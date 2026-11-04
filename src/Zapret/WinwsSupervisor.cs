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
        private const int OutputLimit = 120;

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
        /// </summary>
        public string Start(Strategy strategy, Dictionary<string, string> placeholders)
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

            SetState(ProtectionState.Starting, null);

            List<string> args = strategy.BuildArguments(placeholders);
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
                            if (IsOwn(pid)) continue;
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
