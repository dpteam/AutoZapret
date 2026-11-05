using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    public enum ProtectionState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    /// <summary>
    /// Запуск и надзор за winws.exe.
    ///
    /// Принципиальные решения (см. AGENTS.md):
    ///   * запускаем САМИ, не через .bat и не через cmd start — иначе не получим
    ///     ни PID, ни код выхода, ни возможности убить дерево процессов;
    ///   * аргументы передаём массивом (ArgumentList), а не склеенной строкой:
    ///     никаких проблем с кавычками и пробелами в путях;
    ///   * Stop() убивает всё дерево и ЖДЁТ завершения — иначе следующий старт
    ///     упадёт на занятом WinDivert;
    ///   * чужой winws (например, служба zapret от Flowseal) не трогаем молча:
    ///     обнаруживаем и предупреждаем, потому что это частая причина «не работает».
    /// </summary>
    public sealed class WinwsSupervisor : IDisposable
    {
        private readonly EngineLocator _engine;
        private readonly object _gate = new object();

        private Process _process;
        private List<int> _trackedPids = new List<int>();
        private ProtectionState _state = ProtectionState.Stopped;
        private string _lastError;
        private DateTime _startedAt;
        private string _strategyId = "";
        private string _strategyName = "";

        /// <summary>
        /// Последние строки, которые напечатал сам winws (stdout+stderr).
        ///
        /// Зачем: winws — единственный, кто знает, почему обход не работает
        /// («windivert open error», «cannot load blob», «bad argument»...).
        /// Раньше stdout уходил в Log.Debug, то есть при обычном уровне Info
        /// был невидим, и в отчётах оставалось только «winws запущен» — при том
        /// что на деле он мог ничего не перехватывать.
        /// </summary>
        private readonly Queue<string> _output = new Queue<string>();
        private const int OutputLimit = 2000;

        /// <summary>Сколько строк stdout уже выведено в Info: защита от потока отладки.</summary>
        private int _outputLines;
        private const int OutputInfoLimit = 60;

        public WinwsSupervisor(EngineLocator engine)
        {
            _engine = engine;
        }

        public event Action<ProtectionState, string> StateChanged;
        public event Action<int> ProcessExited;

        public ProtectionState State { get { lock (_gate) return _state; } }
        public string LastError { get { lock (_gate) return _lastError; } }
        public string StrategyId { get { lock (_gate) return _strategyId; } }
        public string StrategyName { get { lock (_gate) return _strategyName; } }
        public bool IsRunning { get { lock (_gate) return _state == ProtectionState.Running; } }

        public TimeSpan Uptime
        {
            get
            {
                lock (_gate)
                {
                    if (_state != ProtectionState.Running) return TimeSpan.Zero;
                    return DateTime.Now - _startedAt;
                }
            }
        }

        // --------------------------------------------------------------- start

        /// <summary>
        /// Запустить стратегию. Возвращает null при успехе, иначе текст ошибки.
        ///
        /// extraArgs — служебные параметры поверх стратегии (например `--debug=1`,
        /// чтобы winws рассказал, что он делает с пакетами). Одноимённые параметры
        /// из стратегии при этом убираются: два `--debug` winws не поймёт.
        /// </summary>
        public string Start(Strategy strategy, Dictionary<string, string> placeholders,
                            List<string> extraArgs = null)
        {
            return StartCore(strategy, placeholders, extraArgs, false);
        }

        /// <summary>
        /// Тело запуска. retried — уже была одна попытка после «фильтр занят»;
        /// второй раз не повторяем, иначе на чужом процессе получится цикл.
        /// </summary>
        private string StartCore(Strategy strategy, Dictionary<string, string> placeholders,
                                 List<string> extraArgs, bool retried)
        {
            if (strategy == null) return "Стратегия не выбрана.";

            lock (_gate)
            {
                if (_state == ProtectionState.Running || _state == ProtectionState.Starting)
                    return "Уже запущено.";
            }

            _engine.Refresh();
            if (!_engine.Ready) return _engine.Problem;

            // какой движок нужен этой стратегии
            EngineKind kind = string.Equals(strategy.Engine, "v2", StringComparison.OrdinalIgnoreCase)
                ? EngineKind.V2
                : EngineKind.V1;

            if (!_engine.IsAvailable(kind))
                return _engine.UnavailableReason(kind);

            string exePath = _engine.ExePath(kind);
            if (exePath == null) return _engine.UnavailableReason(kind);

            // чужой winws — частая причина «не работает»
            string foreign = DetectForeignWinws();
            if (foreign != null)
            {
                Log.Warn("WINWS", "обнаружен чужой winws: " + foreign);
                return "Уже работает другой winws (например, служба zapret). Остановите его: sc stop zapret";
            }

            // Остатки НАШЕГО же winws держат фильтр WinDivert и не дают новому
            // стартовать: winws честно отвечает «A copy of winws is already
            // running with the same filter» и выходит с кодом 1.
            //
            // Приходит это от аварийного завершения, двойного старта или
            // убитого процесса, у которого остались дети. Убираем по ПУТИ к
            // exe, а не по имени: чужой winws из установки Flowseal может
            // жить рядом и трогать его нельзя.
            int leftovers = KillOwnWinws("перед запуском");

            if (leftovers > 0)
            {
                Log.Warn("WINWS", "снято своих winws перед стартом: " + leftovers);
                Thread.Sleep(500);      // драйверу нужно мгновение
            }

            SetState(ProtectionState.Starting, null);

            List<string> args = strategy.BuildArguments(placeholders);
            if (extraArgs != null && extraArgs.Count > 0) args = MergeArgs(args, extraArgs);

            Log.Info("WINWS", "старт " + kind + " стратегии '" + strategy.Name + "', аргументов: " + args.Count);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = _engine.BinDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // net48: ArgumentList нет, только строка. Квотируем сами.
                    Arguments = CommandLine.Build(args)
                };

                Log.Debug("WINWS", "cmdline: " + psi.Arguments);

                ClearOutput();

                Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => OnOutput(e.Data, false);
                proc.ErrorDataReceived += (s, e) => OnOutput(e.Data, true);
                proc.Exited += OnProcessExited;

                if (!proc.Start()) return "Не удалось запустить winws.exe.";
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                lock (_gate)
                {
                    _process = proc;
                    _trackedPids = new List<int> { proc.Id };
                    _startedAt = DateTime.Now;
                    _strategyId = strategy.Id;
                    _strategyName = strategy.Name;
                }

                // запоминаем как свой, чтобы проверки не принимали его за чужой
                RememberOwn(proc.Id);

                // winws умирает сразу, если аргументы неверные — даём ему шанс
                if (proc.WaitForExit(700))
                {
                    int code = SafeExitCode(proc);

                    // Отдельный, очень частый случай: фильтр уже занят другим
                    // winws. По коду это тот же «1», что и неверные аргументы,
                    // но лечится не правкой стратегии, а снятием остатка.
                    string output = AllOutputText();

                    if (output.IndexOf("already running with the same filter",
                                       StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !retried)
                    {
                        Log.Warn("WINWS", "фильтр занят другим winws — снимаю свои и пробую ещё раз");

                        lock (_gate) { _process = null; _trackedPids.Clear(); }
                        ForgetOwn(proc.Id);

                        if (KillOwnWinws("фильтр был занят") > 0) Thread.Sleep(600);

                        // повторяем ровно один раз: если и теперь занято,
                        // значит это ЧУЖОЙ процесс, и его трогать нельзя
                        return StartCore(strategy, placeholders, extraArgs, true);
                    }

                    string msg = "winws завершился сразу (код " + code + "). Проверьте аргументы стратегии.";
                    Log.Error("WINWS", msg);
                    lock (_gate) { _process = null; _trackedPids.Clear(); }
                    SetState(ProtectionState.Error, msg);
                    return msg;
                }

                SetState(ProtectionState.Running, null);
                Log.Info("WINWS", "winws запущен, pid=" + proc.Id + ", стратегия=" + strategy.Name);
                return null;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
            {
                // winws.exe имеет манифест requireAdministrator. Без прав Windows
                // отказывает ДО запуска, и пользователь видел сырой текст
                // «Запрошенная операция требует повышения» без единого намёка,
                // что делать. Это не ошибка движка, а нехватка прав.
                string message = "нужны права администратора: без них winws.exe не запускается. " +
                                 "Перезапустите AutoZapret от имени администратора.";

                Log.Warn("WINWS", "старт отклонён: " + message);
                SetState(ProtectionState.Error, message);
                return message;
            }
            catch (Exception ex)
            {
                Log.Exception("WINWS", "старт", ex);
                SetState(ProtectionState.Error, ex.Message);
                return ex.Message;
            }
        }

        /// <summary>
        /// Добавить служебные аргументы к аргументам стратегии.
        ///
        /// `--debug=N` из extra заменяет такой же из стратегии (в v2 он всегда
        /// `--debug=0`, и без замены мы бы не получили ни одной строки). Сами
        /// служебные аргументы ставим в начало: глобальные параметры winws
        /// действуют на все профили независимо от места.
        /// </summary>
        private static List<string> MergeArgs(List<string> args, List<string> extra)
        {
            List<string> result = new List<string>(args);

            foreach (string e in extra)
            {
                int eq = e.IndexOf('=');
                string name = eq > 0 ? e.Substring(0, eq) : e;

                // убираем прежние значения того же параметра
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    string a = result[i];

                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                        a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        result.RemoveAt(i);
                    }
                }
            }

            result.InsertRange(0, extra);
            return result;
        }

        /// <summary>
        /// Вывод winws. Первые строки — в Info (их видно в обычном логе), дальше —
        /// в Debug, чтобы включённый `--debug` не превратил лог в мешанину.
        /// Строки с явной ошибкой всегда идут в Warn.
        /// </summary>
        private void OnOutput(string line, bool stderr)
        {
            if (string.IsNullOrEmpty(line)) return;

            // Log сам склеивает многострочное, но один \r от cygwin ломает вывод
            string clean = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (clean.Length == 0) return;

            if (clean.Length > 300) clean = clean.Substring(0, 300) + "…";

            lock (_gate)
            {
                _output.Enqueue(clean);
                while (_output.Count > OutputLimit) _output.Dequeue();
                _outputLines++;
            }

            bool complaint = LooksLikeComplaint(clean);

            if (stderr || complaint) Log.Warn("WINWS", clean);
            else if (_outputLines <= OutputInfoLimit) Log.Info("WINWS", clean);
            else Log.Debug("WINWS", clean);
        }

        /// <summary>
        /// winws пишет и обычную болтовню, и настоящие причины отказа в один поток.
        /// Эти слова — маркер «здесь проблема», их нельзя прятать в Debug.
        /// </summary>
        private static bool LooksLikeComplaint(string line)
        {
            string[] markers = { "error", "fail", "cannot", "can't", "unable", "denied",
                                 "invalid", "bad ", "not found", "no such", "refused" };

            foreach (string m in markers)
                if (line.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private void ClearOutput()
        {
            lock (_gate)
            {
                _output.Clear();
                _outputLines = 0;
            }
        }

        /// <summary>
        /// Последние строки вывода движка — для отчёта и для объяснения
        /// «стратегия дала 0/4». Пустой список означает, что winws молчал:
        /// это тоже ответ (аргументы приняты, но фильтр ничего не поймал).
        /// </summary>
        public List<string> RecentOutput(int maxLines = 20)
        {
            lock (_gate)
            {
                List<string> all = new List<string>(_output);
                int from = Math.Max(0, all.Count - Math.Max(1, maxLines));
                return all.GetRange(from, all.Count - from);
            }
        }

        /// <summary>
        /// Весь вывод движка одной строкой — для поиска известных сообщений.
        ///
        /// winws говорит «A copy of winws is already running with the same
        /// filter» и выходит с кодом 1: по коду это неотличимо от неверных
        /// аргументов, а лечится совершенно иначе.
        /// </summary>
        private string AllOutputText()
        {
            lock (_gate) return string.Join("\n", _output.ToArray());
        }

        /// <summary>
        /// Слить весь накопленный вывод в файл. Нужно для отладочного прогона:
        /// в логе живут только первые строки, а разбираться приходится по всем.
        /// </summary>
        public bool SaveOutput(string path)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                List<string> all;
                lock (_gate) all = new List<string>(_output);

                System.IO.File.WriteAllLines(path, all.ToArray(), new System.Text.UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("WINWS", "сохранение вывода движка", ex);
                return false;
            }
        }

        /// <summary>Код Windows «требуется повышение прав».</summary>

        /// <summary>Код Windows «требуется повышение прав».</summary>
        public const int ErrorElevationRequired = 740;

        /// <summary>Требует ли ошибка повышения прав. Нужно UI, чтобы предложить перезапуск.</summary>
        public static bool IsElevationError(string error)
        {
            if (string.IsNullOrEmpty(error)) return false;
            return error.IndexOf("прав администратора", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- stop

        /// <summary>Остановить. Ждёт завершения, чтобы WinDivert освободился.</summary>
        public bool Stop()
        {
            Process proc;
            List<int> pids;

            lock (_gate)
            {
                if (_state == ProtectionState.Stopped) return true;
                proc = _process;
                pids = new List<int>(_trackedPids);
                _state = ProtectionState.Stopping;
            }
            RaiseState(ProtectionState.Stopping, null);

            Log.Info("WINWS", "остановка winws");

            try
            {
                if (proc != null && !proc.HasExited)
                {
                    // Process.Kill(entireProcessTree) появился только в .NET Core 3+.
                    // В net48 дерево убиваем через taskkill /T.
                    KillTree(proc.Id);

                    if (!proc.WaitForExit(3000))
                    {
                        Log.Warn("WINWS", "winws не завершился за 3 с, добиваю");
                        try { proc.Kill(); } catch { }
                        proc.WaitForExit(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WINWS", "остановка: " + ex.Message);
            }

            // добиваем всё, что осталось от дерева
            foreach (int pid in pids) TryKillPid(pid);

            // И проверяем по ПУТИ, а не по PID: после аварии или двойного
            // старта могут остаться наши winws, которых нет в списке PID-ов.
            // Они держат фильтр и ломают следующий запуск.
            int leftovers = KillOwnWinws("после остановки");

            if (leftovers > 0)
                Log.Warn("WINWS", "добито своих winws после остановки: " + leftovers);

            // даём драйверу выгрузиться
            Thread.Sleep(300);

            foreach (int pid in pids) ForgetOwn(pid);

            lock (_gate)
            {
                _process = null;
                _trackedPids.Clear();
                _strategyId = "";
                _strategyName = "";
            }

            SetState(ProtectionState.Stopped, null);
            Log.Info("WINWS", "winws остановлен");
            return true;
        }

        // ------------------------------------------------------------- helpers

        private void OnProcessExited(object sender, EventArgs e)
        {
            Process proc = sender as Process;
            int code = SafeExitCode(proc);

            try { if (proc != null) ForgetOwn(proc.Id); } catch { }

            lock (_gate)
            {
                if (_state == ProtectionState.Stopping || _state == ProtectionState.Stopped) return;
                _process = null;
                _trackedPids.Clear();
            }

            Log.Warn("WINWS", "winws завершился сам, код " + code);
            SetState(code == 0 ? ProtectionState.Stopped : ProtectionState.Error,
                     code == 0 ? null : "winws завершился с кодом " + code);

            Action<int> handler = ProcessExited;
            if (handler != null) { try { handler(code); } catch { } }
        }

        private static int SafeExitCode(Process proc)
        {
            try { return proc == null ? -1 : proc.ExitCode; }
            catch { return -1; }
        }

        private void TryKillPid(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (!p.HasExited) p.Kill();
                }
            }
            catch { /* уже умер */ }
        }

        /// <summary>
        /// Убивает процесс вместе со всем деревом.
        /// Process.Kill(entireProcessTree) есть только в .NET Core 3+, поэтому в net48
        /// используем taskkill /T /F — он делает ровно это и не требует P/Invoke.
        /// </summary>
        private static void KillTree(int pid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process killer = Process.Start(psi))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(5000);
                        string output = killer.StandardOutput.ReadToEnd();
                        if (!string.IsNullOrEmpty(output)) Log.Debug("WINWS", "taskkill: " + output.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WINWS", "taskkill /T не сработал: " + ex.Message);
            }
        }

        /// <summary>
        /// Свои PID-ы, запущенные нами же. Нужно, чтобы «чужой winws» не находил
        /// наш собственный: иначе проверка готовности блокирует подбор стратегий,
        /// когда обход уже включён.
        /// </summary>
        private static readonly HashSet<int> OwnPids = new HashSet<int>();
        private static readonly object OwnGate = new object();

        private static void RememberOwn(int pid)
        {
            lock (OwnGate) OwnPids.Add(pid);
        }

        private static void ForgetOwn(int pid)
        {
            lock (OwnGate) OwnPids.Remove(pid);
        }

        private static bool IsOwn(int pid)
        {
            lock (OwnGate) return OwnPids.Contains(pid);
        }

        /// <summary>
        /// Запущен ли процесс из НАШЕГО каталога движка.
        ///
        /// По имени процесса судить нельзя: winws.exe от службы Flowseal
        /// называется так же. Путь к exe — единственный признак, по которому
        /// можно отличить «наш» от «чужого», не убивая чужое.
        /// </summary>
        private static bool IsOurEnginePath(Process p)
        {
            try
            {
                return IsOurEngineExe(p.MainModule.FileName);
            }
            catch
            {
                // нет прав на MainModule — не гадаем, считаем чужим
                return false;
            }
        }

        /// <summary>
        /// Лежит ли этот exe в НАШЕМ каталоге движка.
        ///
        /// Отдельный метод от Process-версии, чтобы правило можно было
        /// проверить без запуска процессов: ошибка здесь стоит дорого —
        /// либо мы не убьём свой зависший winws, либо убьём чужой.
        /// </summary>
        public static bool IsOurEngineExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                string dir = Core.Paths.EngineBinDir;
                if (!dir.EndsWith("\\", StringComparison.Ordinal)) dir += "\\";

                return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Убить НАШИ winws/winws2 по пути к exe, не по PID.
        ///
        /// Зачем не по PID: PID-метка живёт только пока процесс помнит наша
        /// программа. После аварийного завершения, двойного старта или
        /// убийства родителя остаётся процесс, который держит фильтр
        /// WinDivert, но никому не известен. winws тогда отвечает «A copy of
        /// winws is already running with the same filter» и выходит с кодом 1 —
        /// а со стороны это «обход не включается, хотя всё выключено».
        ///
        /// Чужие процессы не трогаем: фильтр по пути к exe.
        /// </summary>
        public static int KillOwnWinws(string reason)
        {
            int killed = 0;

            foreach (string name in new[] { "winws", "winws2" })
            {
                Process[] all;

                try { all = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (Process p in all)
                {
                    try
                    {
                        int pid = p.Id;

                        if (!IsOwn(pid) && !IsOurEnginePath(p)) continue;

                        KillTree(pid);
                        ForgetOwn(pid);

                        killed++;
                        Log.Warn("WINWS", "снят свой " + name + " pid=" + pid + " (" + reason + ")");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("WINWS", "не смог снять " + name + ": " + ex.Message);
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }

            return killed;
        }

        /// <summary>
        /// Ищет ЧУЖОЙ запущенный winws/winws2. Возвращает описание или null.
        /// Нужно, чтобы не гадать, почему «не работает»: чаще всего виновата служба zapret.
        ///
        /// Свои процессы (которые мы сами запустили) не считаются чужими — иначе
        /// при включённом обходе любая проверка пыталась бы его «остановить».
        /// </summary>
        public static string DetectForeignWinws()
        {
            try
            {
                List<string> descr = new List<string>();

                foreach (string name in new[] { "winws", "winws2" })
                {
                    Process[] all = Process.GetProcessesByName(name);
                    foreach (Process p in all)
                    {
                        try
                        {
                            int pid = p.Id;

                            // Свой — по PID-метке ИЛИ по пути к exe. Второе важно:
                            // после аварийного завершения PID-метка теряется, и
                            // остаток нашего же winws выглядел бы «чужим» —
                            // программа отказывалась бы запускаться, ссылаясь на
                            // службу zapret, которой нет.
                            if (IsOwn(pid) || IsOurEnginePath(p)) continue;

                            descr.Add(name + " pid " + pid);
                        }
                        catch { descr.Add(name + " pid ?"); }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }

                return descr.Count == 0 ? null : string.Join(", ", descr.ToArray());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Есть ли ЛЮБОЙ winws/winws2 — включая наш собственный.
        ///
        /// Не через DetectForeignWinws: тот отфильтровывает наши PID-ы, и на
        /// вопросе «работает ли движок вообще» дал бы ложное «нет». Именно на
        /// этой путанице счёт стратегии терял все 20 баллов за процесс.
        /// </summary>
        public static bool AnyWinwsRunning()
        {
            try
            {
                foreach (string name in new[] { "winws", "winws2" })
                {
                    Process[] all = Process.GetProcessesByName(name);
                    bool any = all.Length > 0;
                    foreach (Process p in all) { try { p.Dispose(); } catch { } }
                    if (any) return true;
                }
            }
            catch { }

            return false;
        }

        private void SetState(ProtectionState state, string error)
        {
            lock (_gate)
            {
                _state = state;
                _lastError = error;
            }
            RaiseState(state, error);
        }

        private void RaiseState(ProtectionState state, string error)
        {
            Action<ProtectionState, string> handler = StateChanged;
            if (handler != null) { try { handler(state, error); } catch { } }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
        }
    }
}
