using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>Состояние службы для интерфейса.</summary>
    public sealed class ServiceStatus
    {
        public bool Installed;
        public bool Running;
        public bool StartPending;
        public bool StopPending;
        public string State = "не установлена";
        public string Problem = "";
        public string Path = "";

        public string Text()
        {
            if (!Installed) return "не установлена";
            if (Problem.Length > 0) return State + " (" + Problem + ")";

            return State;
        }
    }

    /// <summary>Результат операции над службой.</summary>
    public sealed class ServiceResult
    {
        public bool Ok;
        public string Message = "";

        public static ServiceResult Good(string message)
        {
            return new ServiceResult { Ok = true, Message = message };
        }

        public static ServiceResult Bad(string message)
        {
            return new ServiceResult { Ok = false, Message = message };
        }
    }

    /// <summary>
    /// Установка, удаление и управление службой через sc.exe.
    ///
    /// Почему sc.exe, а не P/Invoke: установка — это одна команда, и вызывать
    /// ради неё CreateService/ChangeServiceConfig из управляемого кода значит
    /// повторить уже отлаженную в Windows логику. P/Invoke остался только там,
    /// где без него нельзя — в теле службы (StartServiceCtrlDispatcher из
    /// командной строки не вызвать).
    ///
    /// Кодировки: `chcp 437` перед каждой командой. Без него на русской Windows
    /// вывод приходит в OEM, и разобрать состояние невозможно — та же грабля,
    /// что с netsh в SystemChecks.
    /// </summary>
    public static class ServiceManager
    {
        public const string Name = "AutoZapret";
        public const string DisplayName = "AutoZapret DPI bypass";

        /// <summary>Имя для `sc`: без него SCM не поймёт, о ком речь.</summary>
        public static string ExePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch
                {
                    return Path.Combine(Paths.BaseDir, "AutoZapret.exe");
                }
            }
        }

        // ------------------------------------------------------------------ query

        public static ServiceStatus Query()
        {
            ServiceStatus status = new ServiceStatus();

            string output = Cmd("sc query \"" + Name + "\"");

            if (output.IndexOf("FAILED 1060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("не найдена", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.Installed = false;
                status.State = "не установлена";
                return status;
            }

            status.Installed = true;

            if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.Running = true;
                status.State = "работает";
            }
            else if (output.IndexOf("START_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.StartPending = true;
                status.State = "запускается…";
            }
            else if (output.IndexOf("STOP_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.StopPending = true;
                status.State = "останавливается…";
            }
            else if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.State = "остановлена";
            }
            else
            {
                status.State = FirstLine(output);
            }

            // путь к exe: если служба осталась от старой установки, это надо видеть
            string config = Cmd("sc qc \"" + Name + "\"");
            foreach (string line in config.Split('\n'))
            {
                string t = line.Trim();

                if (t.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = t.IndexOf(':');
                    if (colon > 0) status.Path = t.Substring(colon + 1).Trim();
                    break;
                }
            }

            if (status.Path.Length > 0 && status.Path.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) < 0)
                status.Problem = "установлена из другого места: " + status.Path;

            return status;
        }

        // ----------------------------------------------------------------- install

        /// <summary>
        /// Установить службу. Автозапуск при загрузке — то, ради чего это всё.
        /// </summary>
        public static ServiceResult Install(bool autoStart)
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("нужны права администратора");

            ServiceStatus existing = Query();

            if (existing.Installed)
                return ServiceResult.Bad("служба уже установлена: сначала удалите её");

            string exe = ExePath;

            if (!File.Exists(exe))
                return ServiceResult.Bad("не нашёл собственный exe: " + exe);

            // binPath в кавычках: путь с пробелами иначе развалится на аргументы.
            // Экранируем кавычкой-слэшем внутри уже квотированной строки.
            string binPath = "\"\\\"" + exe + "\\\" --service\"";

            string start = autoStart ? "auto" : "demand";

            string output = Cmd("sc create \"" + Name + "\" binPath= " + binPath +
                                " start= " + start +
                                " obj= LocalSystem DisplayName= \"" + DisplayName + "\"");

            if (!Created(output))
            {
                Log.Warn("SVC", "sc create: " + output.Trim());

                return ServiceResult.Bad("не удалось создать службу: " + FirstLine(output));
            }

            // описание — необязательно, но полезно в services.msc
            Cmd("sc description \"" + Name +
                "\" \"Обход DPI без окна. Управляется из AutoZapret, кнопка «Служба».\"");

            // восстановление при падении: служба должна подниматься сама,
            // иначе «установил и забыл» превращается в «однажды перестало»
            Cmd("sc failure \"" + Name + "\" reset= 86400 actions= restart/5000/restart/10000/restart/30000");

            Log.Info("SVC", "служба установлена: " + exe + ", автозапуск=" + autoStart);

            return ServiceResult.Good("служба установлена" + (autoStart ? ", запускается при загрузке" : ""));
        }

        public static ServiceResult Uninstall()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("нужны права администратора");

            ServiceStatus status = Query();

            if (!status.Installed)
                return ServiceResult.Bad("служба не установлена");

            // Сначала остановить и ДОЖДАТЬСЯ: sc delete у работающей службы
            // помечает её на удаление, и она висит в SCM до перезагрузки.
            if (status.Running || status.StartPending)
            {
                Stop();
                WaitForStopped(10000);
            }

            string output = Cmd("sc delete \"" + Name + "\"");

            if (output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) < 0 &&
                output.IndexOf("УСПЕХ", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // sc delete не всегда печатает SUCCESS в локализованной сборке
                if (Query().Installed)
                {
                    Log.Warn("SVC", "sc delete: " + output.Trim());
                    return ServiceResult.Bad("не удалось удалить: " + FirstLine(output));
                }
            }

            Log.Info("SVC", "служба удалена");

            return ServiceResult.Good("служба удалена");
        }

        public static ServiceResult Start()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("нужны права администратора");

            ServiceStatus status = Query();

            if (!status.Installed)
                return ServiceResult.Bad("служба не установлена");

            if (status.Running)
                return ServiceResult.Good("служба уже работает");

            string output = Cmd("sc start \"" + Name + "\"");

            // 1056 = уже запущена, 1053 = не ответила вовремя
            if (output.IndexOf("1053", StringComparison.Ordinal) >= 0)
            {
                Log.Error("SVC", "служба не ответила SCM: " + output.Trim());

                return ServiceResult.Bad(
                    "служба не ответила SCM (1053). Скорее всего, exe не в режиме --service — " +
                    "удалите и установите службу заново");
            }

            // даём SCM время перевести её в RUNNING, иначе интерфейс покажет
            // «остановлена» сразу после успешного старта
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(250);

                ServiceStatus after = Query();
                if (after.Running) return ServiceResult.Good("служба запущена");
            }

            ServiceStatus final = Query();

            if (final.Running) return ServiceResult.Good("служба запущена");

            Log.Warn("SVC", "sc start: " + output.Trim());

            return ServiceResult.Bad("не удалось запустить: " + final.State);
        }

        public static ServiceResult Stop()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("нужны права администратора");

            ServiceStatus status = Query();

            if (!status.Installed)
                return ServiceResult.Bad("служба не установлена");

            if (!status.Running && !status.StartPending)
                return ServiceResult.Good("служба уже остановлена");

            Cmd("sc stop \"" + Name + "\"");

            // `sc stop` возвращается ДО реальной остановки: движок ещё гасит
            // дерево процессов и ждёт выгрузки WinDivert. Ждём по-настоящему.
            if (!WaitForStopped(15000))
            {
                Log.Warn("SVC", "служба не остановилась за 15 с");

                return ServiceResult.Bad("служба не остановилась за 15 секунд");
            }

            return ServiceResult.Good("служба остановлена");
        }

        private static bool WaitForStopped(int timeoutMs)
        {
            int waited = 0;

            while (waited < timeoutMs)
            {
                System.Threading.Thread.Sleep(500);
                waited += 500;

                ServiceStatus status = Query();
                if (!status.Running && !status.StopPending && !status.StartPending) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ helpers

        private static bool Created(string output)
        {
            if (output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (output.IndexOf("УСПЕХ", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // локальная сборка может не печатать ничего при успехе — проверяем факт
            return Query().Installed;
        }

        private static string Cmd(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // chcp 437: без него sc отвечает на языке системы в OEM,
                    // и разобрать «RUNNING/STOPPED» невозможно
                    Arguments = "/c chcp 437 >nul & " + arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();

                    p.WaitForExit(15000);

                    return output + (error.Length > 0 ? "\n" + error : "");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SVC", "команда не выполнилась: " + ex.Message);
                return "ошибка: " + ex.Message;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 120 ? t.Substring(0, 120) : t;
            }

            return "";
        }

        // -------------------------------------------------------------------- cli

        /// <summary>--service status|install|uninstall|start|stop</summary>
        public static int RunCli(string[] args)
        {
            // ВАЖНО: по умолчанию — "run", а не "status".
            //
            // SCM запускает службу ровно как `binPath`, то есть
            //   "AutoZapret.exe" --service
            // без второго аргумента. Если по умолчанию было бы "status",
            // служба «стартовала» бы, напечатала состояние и вышла — SCM
            // увидел бы мгновенный выход и ошибку 1053/7000.
            // Поэтому «нет действия» = «работай как служба».
            string action = "run";
            bool auto = true;

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--service", StringComparison.OrdinalIgnoreCase)) continue;

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    action = args[i + 1].ToLowerInvariant();

                for (int j = i + 1; j < args.Length; j++)
                    if (string.Equals(args[j], "--demand", StringComparison.OrdinalIgnoreCase)) auto = false;
            }

            switch (action)
            {
                case "install":
                {
                    ServiceResult result = Install(auto);
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "uninstall":
                case "delete":
                case "remove":
                {
                    ServiceResult result = Uninstall();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "start":
                {
                    ServiceResult result = Start();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "stop":
                {
                    ServiceResult result = Stop();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "run":
                    // внутреннее: то, что вызывает SCM. Нужно для отладки —
                    // руками увидишь 1063 и подсказку, что делать
                    return ServiceHost.Run(Name);
            }

            ServiceStatus status = Query();

            Console.WriteLine("== служба " + Name + " ==");
            Console.WriteLine("  состояние: " + status.Text());
            if (status.Path.Length > 0) Console.WriteLine("  exe:       " + status.Path);
            if (status.Problem.Length > 0) Console.WriteLine("  проблема:  " + status.Problem);
            Console.WriteLine("  наш exe:   " + ExePath);

            return 0;
        }
    }
}