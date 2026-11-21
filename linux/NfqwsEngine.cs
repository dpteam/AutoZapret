using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Linux
{
    /// <summary>
    /// Движок обхода на Linux: nfqws + правила iptables в NFQUEUE.
    ///
    /// Почему nfqws, а не свой перехват. На Windows мы держим WinDivert и умеем
    /// разбирать пакеты сами. На Linux то же самое уже написано в zapret и
    /// обкатано годами: nfqws получает пакеты из ядра через netlink, а какие
    /// именно пакеты — решают правила iptables. Писать свой перехват значило бы
    /// повторить чужую работу хуже.
    ///
    /// Главное: nfqws понимает ТЕ ЖЕ опции --dpi-desync, что winws (это один и
    /// тот же код, разные источники пакетов). Поэтому весь каталог стратегий
    /// работает здесь без переделки — проверено через --dry-run на всех 141.
    ///
    /// Безопасность. В правилах стоит --queue-bypass: если nfqws упадёт или его
    /// убьют, ядро пропустит пакеты дальше, и интернет не пропадёт. Без этого
    /// флага смерть процесса означает мёртвую сеть, и человек остаётся без
    /// интернета с «выключить обход не помогает».
    /// </summary>
    public sealed class NfqwsEngine
    {
        public const int QueueNumber = Firewall.QueueNumber;

        private readonly object _gate = new object();
        private Process _process;
        private string _strategyName = "";
        private string _backend = "";

        public event Action<bool, string> StateChanged;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    if (_process == null) return false;

                    try
                    {
                        if (_process.HasExited) return false;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        public string StrategyName
        {
            get { lock (_gate) { return _strategyName; } }
        }

        /// <summary>Где лежит nfqws: рядом с программой или в каталоге данных.</summary>
        public static string BinaryPath
        {
            get
            {
                string local = Path.Combine(Paths.BaseDir, "engine", "bin", "nfqws");
                if (File.Exists(local)) return local;

                string shared = Path.Combine(Paths.EngineBinDir, "nfqws");
                if (File.Exists(shared)) return shared;

                return local;
            }
        }

        public static bool Ready
        {
            get { return File.Exists(BinaryPath); }
        }

        public static string Problem
        {
            get
            {
                if (!Ready) return "нет nfqws: " + BinaryPath + " (запустите tools/fetch-engine.sh)";
                if (!AppInfo.IsAdmin) return "нужны права root: nfqws открывает netlink-сокет к ядру";
                return null;
            }
        }

        /// <summary>Плейсхолдеры для аргументов стратегии. Аналог EngineLocator.BuildPlaceholders.</summary>
        public static Dictionary<string, string> BuildPlaceholders(string gameFilter)
        {
            string tcp = "12";
            string udp = "12";

            string mode = (gameFilter ?? "off").Trim().ToLowerInvariant();

            if (mode == "all" || mode == "tcp+udp" || mode == "both")
            {
                tcp = "1024-65535";
                udp = "1024-65535";
            }
            else if (mode == "tcp")
            {
                tcp = "1024-65535";
            }
            else if (mode == "udp")
            {
                udp = "1024-65535";
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "BIN", Paths.EngineBinDir.TrimEnd('/') },
                { "LISTS", Paths.EngineListsDir.TrimEnd('/') },
                { "LUA", Path.Combine(Paths.EngineDir, "lua").TrimEnd('/') },
                { "FAKES", Paths.FakesDir.TrimEnd('/') },
                { "GAME_TCP", tcp },
                { "GAME_UDP", udp },
                { "WF_TCP", "80,443,2053,2083,2087,2096,8443" },
                { "WF_UDP", "443,19294-19344,50000-50100" }
            };
        }

        /// <summary>
        /// Собрать аргументы nfqws из стратегии.
        ///
        /// --wf-tcp/--wf-udp выбрасываются: это фильтр WinDivert, на Linux его
        /// роль играют правила iptables. Остальное идёт как есть.
        /// </summary>
        public static List<string> BuildArguments(Strategy strategy, string gameFilter)
        {
            List<string> args = new List<string>();

            if (strategy == null) return args;

            Dictionary<string, string> placeholders = BuildPlaceholders(gameFilter);

            foreach (string arg in strategy.BuildArguments(placeholders))
            {
                if (arg.StartsWith("--wf-", StringComparison.Ordinal)) continue;
                args.Add(arg);
            }

            return args;
        }

        /// <summary>
        /// Убить остатки своего nfqws от прошлого запуска.
        ///
        /// Зачем. Если предыдущий экземпляр не успел убраться (упал, его убили
        /// извне, или приложение перезапустили через pkill по имени), его nfqws
        /// продолжает держать очередь 200. Новый тогда падает с
        /// `nfq_create_queue(): Operation not permitted`, и снаружи это
        /// «обход не включается», хотя списки и правила в порядке.
        ///
        /// Убиваем ТОЛЬКО свой: по пути к бинарнику. На машине может стоять
        /// чужой zapret, и трогать его нельзя.
        /// </summary>
        private static void KillStaleInstances()
        {
            string ours = BinaryPath;

            try
            {
                foreach (string dir in Directory.GetDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);

                    int pid;
                    if (!int.TryParse(name, out pid)) continue;
                    if (pid == Process.GetCurrentProcess().Id) continue;

                    string exe;

                    try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget; }
                    catch { continue; }

                    if (exe == null || exe != ours) continue;

                    Log.Warn("NFQWS", "найден остаток прошлого запуска (pid " + pid + "), убираю");

                    try
                    {
                        Process stale = Process.GetProcessById(pid);
                        stale.Kill(true);
                        stale.WaitForExit(2000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("NFQWS", "не убился pid " + pid + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("NFQWS", "поиск остатков: " + ex.Message);
            }
        }

        /// <summary>
        /// Свободна ли очередь NFQUEUE. Проверяем по /proc/net/netfilter:
        /// там есть строка на каждую занятую очередь.
        /// </summary>
        private static bool QueueIsFree()
        {
            try
            {
                if (!File.Exists("/proc/net/netfilter/nfnetlink_queue")) return true;

                foreach (string line in File.ReadAllLines("/proc/net/netfilter/nfnetlink_queue"))
                {
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 0 && parts[0] == QueueNumber.ToString()) return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        public string Start(Strategy strategy, string gameFilter)
        {
            lock (_gate)
            {
                if (IsRunning) return "обход уже включён";

                if (strategy == null) return "стратегия не выбрана";

                string problem = Problem;
                if (problem != null) return problem;

                List<string> args = BuildArguments(strategy, gameFilter);

                if (args.Count == 0) return "у стратегии нет аргументов для nfqws";

                string backend = Firewall.Backend;

                if (backend == null)
                    return "нет ни nftables, ни iptables: пакеты некуда направлять";

                // Остатки прошлого запуска держат очередь — убираем их ДО
                // постановки правил, иначе новый nfqws не сможет подключиться
                // и молча получит «Operation not permitted».
                KillStaleInstances();

                for (int attempt = 0; attempt < 10 && !QueueIsFree(); attempt++)
                    Thread.Sleep(300);

                if (!QueueIsFree())
                    return "очередь " + QueueNumber + " занята чужим процессом: " +
                           "остановите другой zapret или смените номер очереди";

                // Порты берём из самой стратегии: если она слушает только 443,
                // нет смысла гнать через очередь ещё и 80.
                string tcpPorts, udpPorts;
                Firewall.PortsFromArgs(args, out tcpPorts, out udpPorts);

                if (tcpPorts.Length == 0 && udpPorts.Length == 0)
                {
                    // У стратегии нет явных портов — берём стандартные.
                    tcpPorts = "80,443";
                    udpPorts = "443";
                }

                // Правила ставим ДО запуска процесса: иначе первые пакеты уйдут
                // в очередь, которую никто не читает, и соединение зависнет.
                string rulesError = ApplyRules(backend, tcpPorts, udpPorts);
                if (rulesError != null) return rulesError;

                List<string> full = new List<string> { "--qnum=" + Firewall.QueueNumber };
                full.AddRange(args);

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(BinaryPath)
                    {
                        WorkingDirectory = Path.GetDirectoryName(BinaryPath),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    foreach (string a in full) psi.ArgumentList.Add(a);

                    _process = Process.Start(psi);

                    if (_process == null)
                    {
                        RemoveRules(backend);
                        return "nfqws не запустился";
                    }

                    _strategyName = strategy.Name;
                    _backend = backend;

                    // Читаем вывод в фоне: иначе pipe заполнится и процесс встанет.
                    _process.OutputDataReceived += (s, e) => { if (e.Data != null) Log.Info("NFQWS", e.Data); };
                    _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Warn("NFQWS", e.Data); };
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    Log.Info("NFQWS", "запущен: " + strategy.Name +
                                      " (backend " + backend + ", tcp " + tcpPorts + ", udp " + udpPorts + ")");

                    Raise(true, "");
                    return null;
                }
                catch (Exception ex)
                {
                    RemoveRules(backend);
                    Log.Warn("NFQWS", "не запустился: " + ex.Message);
                    return "не запустился: " + ex.Message;
                }
            }
        }

        public bool Stop()
        {
            lock (_gate)
            {
                bool wasRunning = false;

                if (_process != null)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            wasRunning = true;
                            _process.Kill(true);
                            _process.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("NFQWS", "остановка: " + ex.Message);
                    }

                    try { _process.Dispose(); } catch { }
                    _process = null;
                }

                // Правила убираем ВСЕГДА, даже если процесс уже умер: иначе
                // пакеты продолжат уходить в несуществующую очередь.
                RemoveRules(_backend);

                _strategyName = "";

                if (wasRunning) Log.Info("NFQWS", "остановлен");
                Raise(false, "");

                return wasRunning;
            }
        }

        // ------------------------------------------------------------------ правила

        private string ApplyRules(string backend, string tcpPorts, string udpPorts)
        {
            foreach (string[] cmd in Firewall.SetupCommands(backend, tcpPorts, udpPorts))
            {
                string error;

                // Подготовительные команды (удаление остатков, создание цепочек)
                // могут законно падать: удалять нечего. Ошибкой считаем только
                // сами правила — их отсутствие означает, что обход не заработает.
                bool isPreparatory = IsPreparatory(cmd);

                Firewall.Run(cmd, out error);

                if (error != null && !isPreparatory)
                {
                    RemoveRules(backend);
                    return "правило не поставилось (" + string.Join(" ", cmd) + "): " + error;
                }
            }

            Log.Info("NFQWS", "правила " + backend + " поставлены: tcp " + tcpPorts + ", udp " + udpPorts);
            return null;
        }

        /// <summary>
        /// Подготовительная ли команда: удаление остатков или создание цепочки.
        /// Такие команды падают штатно, когда удалять нечего.
        /// </summary>
        private static bool IsPreparatory(string[] cmd)
        {
            for (int i = 0; i < cmd.Length; i++)
            {
                if (cmd[i] == "-D" || cmd[i] == "-F" || cmd[i] == "-X" || cmd[i] == "-N") return true;
                if (cmd[i] == "delete") return true;
                if (cmd[i] == "add" && i + 1 < cmd.Length && cmd[i + 1] == "chain") return true;
            }

            return false;
        }

        private void RemoveRules(string backend)
        {
            if (string.IsNullOrEmpty(backend)) return;

            foreach (string[] cmd in Firewall.TeardownCommands(backend))
            {
                string error;
                Firewall.Run(cmd, out error);

                // Не ругаемся: если правил уже нет — это нормальный случай
                // (например, Stop() после падения процесса).
            }

            Log.Info("NFQWS", "правила " + backend + " убраны");
        }

        private void Raise(bool running, string error)
        {
            Action<bool, string> handler = StateChanged;
            if (handler == null) return;

            try { handler(running, error); } catch { }
        }
    }
}
