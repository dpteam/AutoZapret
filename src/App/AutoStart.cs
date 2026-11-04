using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AutoZapret.Core;
using Microsoft.Win32;

namespace AutoZapret.App
{
    public enum AutoStartMethod
    {
        None,
        /// <summary>Планировщик заданий. Запускает с правами администратора БЕЗ UAC-запроса.</summary>
        Task,
        /// <summary>Ключ HKCU\...\Run. Без прав администратора, но winws потом попросит UAC.</summary>
        RunKey
    }

    public sealed class AutoStartStatus
    {
        public bool Enabled;
        public AutoStartMethod Method = AutoStartMethod.None;

        /// <summary>Где именно прописано: имя задачи или значение реестра.</summary>
        public string Where = "";

        /// <summary>Путь, который реально запускается. Пусто, если ничего не настроено.</summary>
        public string Target = "";

        /// <summary>true — прописан ДРУГОЙ путь (переехала папка, portable-сборка).</summary>
        public bool Stale;

        public string Problem = "";

        public string Text()
        {
            if (!Enabled) return "выключен";

            string method = Method == AutoStartMethod.Task ? "планировщик" : "реестр";
            string text = "включён (" + method + ")";

            if (Stale) text += " — путь устарел: " + Target;

            return text;
        }
    }

    public sealed class AutoStartResult
    {
        public bool Ok;
        public bool NeedsElevation;
        public string Error = "";
        public AutoStartStatus Status;

        public string Text()
        {
            if (Ok) return "готово";
            if (NeedsElevation) return "нужны права администратора";
            return Error;
        }
    }

    /// <summary>
    /// Автозапуск при входе в Windows.
    ///
    /// Два способа, и выбор не случайный:
    ///
    ///   * Планировщик заданий с /RL HIGHEST — запускает приложение СРАЗУ
    ///     с правами администратора, без запроса UAC. Это единственный способ
    ///     сделать автозапуск обхода не раздражающим.
    ///   * HKCU\...\Run — не требует прав при установке, но при старте обхода
    ///     winws всё равно попросит UAC, и человек увидит запрос при каждом входе.
    ///
    /// Обратная сторона планировщика: для создания задачи нужны права.
    /// Поэтому Enable() сначала пробует задачу, а если прав нет — честно говорит
    /// об этом, а не подсовывает молча менее надёжный вариант.
    ///
    /// Отдельная проверка — «путь устарел»: portable-сборку переносят в другую
    /// папку, и задача остаётся указывать в пустоту. Молчаливый неработающий
    /// автозапуск хуже отсутствия автозапуска.
    /// </summary>
    public static class AutoStartManager
    {
        public const string TaskName = "AutoZapret";
        public const string RunValueName = "AutoZapret";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Задержка при входе: сеть и WinDivert должны успеть подняться.</summary>
        private const string TaskDelay = "0000:30";

        /// <summary>Аргументы автозапуска: стартуем в трей, без окна поверх всех.</summary>
        public const string StartArguments = "--minimized";

        /// <summary>Путь к текущему exe. Именно он должен оказаться в задаче.</summary>
        public static string ExePath
        {
            get
            {
                try { return System.Windows.Forms.Application.ExecutablePath; }
                catch
                {
                    try { return Process.GetCurrentProcess().MainModule.FileName; }
                    catch { return ""; }
                }
            }
        }

        /// <summary>Что настроено сейчас. Никогда не бросает.</summary>
        public static AutoStartStatus Query()
        {
            AutoStartStatus status = new AutoStartStatus();

            string exe = ExePath;

            // ---- планировщик: приоритет, потому что он запускает с правами
            string taskPath = TaskTarget();
            if (taskPath != null)
            {
                status.Enabled = true;
                status.Method = AutoStartMethod.Task;
                status.Where = "задача " + TaskName;
                status.Target = taskPath;
                status.Stale = exe.Length > 0 &&
                               taskPath.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0;

                if (status.Stale) status.Problem = "задача указывает на другой файл";
                return status;
            }

            // ---- реестр
            string runValue = ReadRunValue();
            if (runValue != null)
            {
                status.Enabled = true;
                status.Method = AutoStartMethod.RunKey;
                status.Where = @"HKCU\...\Run\" + RunValueName;
                status.Target = ExtractPath(runValue);
                status.Stale = exe.Length > 0 &&
                               status.Target.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0;

                if (status.Stale) status.Problem = "реестр указывает на другой файл";
                return status;
            }

            return status;
        }

        /// <summary>
        /// Включает автозапуск. Сначала планировщик: он не показывает UAC при входе.
        /// Если прав нет — NeedsElevation = true, и вызывающий решает, повышать ли их.
        /// </summary>
        public static AutoStartResult Enable(bool preferTask = true)
        {
            AutoStartResult result = new AutoStartResult();

            string exe = ExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                result.Error = "не удалось определить путь к программе";
                return result;
            }

            if (preferTask)
            {
                if (!Elevation.IsElevated)
                {
                    result.NeedsElevation = true;
                    result.Error = "для задачи планировщика нужны права администратора";
                    result.Status = Query();
                    return result;
                }

                string error = CreateTask(exe);
                if (error == null)
                {
                    // задача создана — значение в реестре больше не нужно, иначе двойной запуск
                    DeleteRunValue();

                    result.Ok = true;
                    result.Status = Query();
                    Log.Info("APP", "автозапуск включён через планировщик: " + exe);
                    return result;
                }

                Log.Warn("APP", "не удалось создать задачу автозапуска: " + error + ", пробую реестр");
            }

            string runError = WriteRunValue(exe);
            if (runError != null)
            {
                result.Error = runError;
                result.Status = Query();
                return result;
            }

            result.Ok = true;
            result.Status = Query();
            Log.Info("APP", "автозапуск включён через реестр: " + exe);
            return result;
        }

        /// <summary>Выключает автозапуск обоими способами: не должно остаться половин.</summary>
        public static AutoStartResult Disable()
        {
            AutoStartResult result = new AutoStartResult();

            bool hadTask = TaskTarget() != null;

            if (hadTask)
            {
                string error = DeleteTask();
                if (error != null && !Elevation.IsElevated)
                {
                    result.NeedsElevation = true;
                    result.Error = "задача создана с повышенными правами, нужен администратор";
                    result.Status = Query();
                    return result;
                }
            }

            string runError = DeleteRunValue();

            if (runError != null)
            {
                result.Error = runError;
                result.Status = Query();
                return result;
            }

            result.Ok = true;
            result.Status = Query();

            if (result.Status.Enabled)
            {
                result.Ok = false;
                result.Error = "автозапуск всё ещё активен: " + result.Status.Text();
            }
            else
            {
                Log.Info("APP", "автозапуск выключен");
            }

            return result;
        }

        // ------------------------------------------------------------- task

        /// <summary>
        /// Путь из задачи или null, если задачи нет.
        /// Определяется по коду возврата: текст `schtasks` ЛОКАЛИЗОВАН, парсить его нельзя.
        /// </summary>
        private static string TaskTarget()
        {
            RunResult query = Run("schtasks /query /tn \"" + TaskName + "\" /fo LIST /v");
            if (query.ExitCode != 0) return null;

            // «Задача должна выполнить» / «Task To Run» — значение зависит от локали,
            // но путь в нём всегда литеральный: ищем строку с .exe
            foreach (string line in query.Output.Split('\n'))
            {
                string trimmed = line.Trim();
                int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeIndex < 0) continue;

                // отбрасываем служебные строки winws/движка
                if (trimmed.IndexOf("winws", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                return trimmed;
            }

            return "";
        }

        private static string CreateTask(string exe)
        {
            // /RL HIGHEST — запуск от администратора без UAC-запроса;
            // /DELAY — сеть и драйвер успевают подняться после входа.
            string args =
                "/create /tn \"" + TaskName + "\" " +
                "/tr \"\\\"" + exe + "\\\" " + StartArguments + "\" " +
                "/sc onlogon /rl HIGHEST /f /delay " + TaskDelay;

            RunResult create = Run("schtasks " + args);

            if (create.ExitCode != 0)
                return FirstLine(create.Output, "код " + create.ExitCode);

            // проверяем, что задача реально появилась, а не «команда не упала»
            return TaskTarget() == null ? "задача не появилась после создания" : null;
        }

        private static string DeleteTask()
        {
            RunResult delete = Run("schtasks /delete /tn \"" + TaskName + "\" /f");
            if (delete.ExitCode != 0) return FirstLine(delete.Output, "код " + delete.ExitCode);
            return null;
        }

        // ------------------------------------------------------------- registry

        private static string ReadRunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null) return null;
                    return key.GetValue(RunValueName) as string;
                }
            }
            catch { return null; }
        }

        private static string WriteRunValue(string exe)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return "не удалось открыть ключ Run";
                    key.SetValue(RunValueName, "\"" + exe + "\" " + StartArguments, RegistryValueKind.String);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string DeleteRunValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return null;
                    if (key.GetValue(RunValueName) != null) key.DeleteValue(RunValueName, false);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Достаёт путь из строки вида "\"C:\...\AutoZapret.exe\" --minimized".</summary>
        public static string ExtractPath(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";

            string value = commandLine.Trim();

            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int close = value.IndexOf('"', 1);
                if (close > 1) return value.Substring(1, close - 1);
            }

            int exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) return value.Substring(0, exe + 4).Trim('"');

            return value;
        }

        // ------------------------------------------------------------- helpers

        private struct RunResult
        {
            public int ExitCode;
            public string Output;
        }

        /// <summary>
        /// Запуск внешней команды. chcp 437 — чтобы вывод не зависел от языка системы;
        /// но решения принимаются по КОДУ ВОЗВРАТА, а не по тексту.
        /// </summary>
        private static RunResult Run(string command)
        {
            RunResult result = new RunResult { ExitCode = -1, Output = "" };

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c chcp 437 >nul & " + command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return result;

                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);

                    result.ExitCode = p.HasExited ? p.ExitCode : -1;
                    result.Output = stdout + (stderr.Length > 0 ? "\n" + stderr : "");
                }
            }
            catch (Exception ex)
            {
                result.Output = ex.Message;
            }

            return result;
        }

        private static string FirstLine(string text, string fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;

            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                return t.Length > 120 ? t.Substring(0, 117) + "…" : t;
            }

            return fallback;
        }

        // ------------------------------------------------------------- cli

        /// <summary>
        /// `--autostart status|enable|disable` — отдельный режим, в том числе для
        /// запуска с правами администратора на одну операцию.
        ///
        /// Коды возврата: 0 — успех, 1 — ошибка, 2 — нужны права администратора.
        /// По коду, а не по тексту: консоль локализована и парсить её бессмысленно.
        /// </summary>
        public static int RunCli(string[] args)
        {
            string action = "status";

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--autostart", StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) action = args[i + 1].ToLowerInvariant();
            }

            AutoStartStatus before = Query();

            switch (action)
            {
                case "status":
                    Console.WriteLine("автозапуск: " + before.Text());
                    if (before.Target.Length > 0) Console.WriteLine("цель: " + before.Target);
                    if (before.Problem.Length > 0) Console.WriteLine("проблема: " + before.Problem);
                    Console.WriteLine("текущий exe: " + ExePath);
                    return 0;

                case "enable":
                {
                    AutoStartResult result = Enable();

                    if (result.Ok)
                    {
                        Console.WriteLine("автозапуск: " + result.Status.Text());
                        return 0;
                    }

                    Console.WriteLine("не удалось: " + result.Text());
                    return result.NeedsElevation ? 2 : 1;
                }

                case "enable-run":
                {
                    // явно через реестр: без прав, но Windows будет спрашивать UAC
                    AutoStartResult result = Enable(false);

                    if (result.Ok)
                    {
                        Console.WriteLine("автозапуск: " + result.Status.Text());
                        return 0;
                    }

                    Console.WriteLine("не удалось: " + result.Text());
                    return 1;
                }

                case "disable":
                {
                    AutoStartResult result = Disable();

                    if (result.Ok)
                    {
                        Console.WriteLine("автозапуск: " + result.Status.Text());
                        return 0;
                    }

                    Console.WriteLine("не удалось: " + result.Text());
                    return result.NeedsElevation ? 2 : 1;
                }

                default:
                    Console.WriteLine("неизвестное действие: " + action);
                    Console.WriteLine("используйте: --autostart status|enable|enable-run|disable");
                    return 1;
            }
        }
    }
}