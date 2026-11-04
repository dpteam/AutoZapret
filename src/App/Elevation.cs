using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>
    /// Повышение прав.
    ///
    /// Почему это вообще нужно: winws.exe имеет манифест requireAdministrator,
    /// а WinDivert грузит kernel-драйвер. Без прав администратора запуск
    /// невозможен в принципе — Windows вернёт ERROR_ELEVATION_REQUIRED (740).
    ///
    /// Стратегия: сам процесс стартует asInvoker (чтобы UI, логи и диагностика
    /// работали всегда), а для включения обхода перезапускается с runas.
    /// Так пользователь видит интерфейс и внятное объяснение, а не молчаливый отказ.
    /// </summary>
    public static class Elevation
    {
        public const int ErrorCancelled = 1223;   // ERROR_CANCELLED: пользователь нажал «Нет» в UAC

        /// <summary>Запущен ли процесс с правами администратора.</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Перезапустить себя с правами администратора.
        /// Возвращает true, если новый процесс стартовал (текущий должен выйти).
        /// </summary>
        public static bool RestartElevated(string extraArgs = null)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Application_ExecutablePath(),
                    UseShellExecute = true,
                    Verb = "runas",                 // именно это вызывает UAC
                    WorkingDirectory = Paths.BaseDir,
                    Arguments = string.IsNullOrEmpty(extraArgs) ? "" : extraArgs
                };

                Process.Start(psi);
                Log.Info("APP", "перезапуск с правами администратора");
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled)
                {
                    // Пользователь отказался — это не ошибка программы, а его выбор.
                    Log.Warn("APP", "пользователь отменил запрос прав администратора");
                }
                else
                {
                    Log.Error("APP", "не удалось повысить права: " + ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("APP", "перезапуск с правами", ex);
                return false;
            }
        }

        private static string Application_ExecutablePath()
        {
            try { return System.Windows.Forms.Application.ExecutablePath; }
            catch { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        /// <summary>
        /// Запустить ОДНУ операцию с правами администратора и дождаться её.
        ///
        /// Правило AGENTS.md: повышение прав — только на операцию, а не «весь GUI
        /// теперь администратор». Поэтому тяжёлые действия (создание задачи
        /// планировщика) идут отдельным короткоживущим процессом того же exe.
        ///
        /// Возвращает код возврата дочернего процесса или -1 при отказе в UAC.
        /// </summary>
        public static int RunElevatedOperation(string arguments, int timeoutMs = 60000)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Application_ExecutablePath(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Paths.BaseDir,
                    Arguments = arguments ?? "",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;

                    if (!p.WaitForExit(Math.Max(1000, timeoutMs)))
                    {
                        Log.Warn("APP", "повышенная операция не завершилась за " + timeoutMs + " мс");
                        return -1;
                    }

                    Log.Info("APP", "повышенная операция завершена, код " + p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled)
                    Log.Warn("APP", "пользователь отменил запрос прав администратора");
                else
                    Log.Error("APP", "не удалось запустить повышенную операцию: " + ex.Message);

                return -1;
            }
            catch (Exception ex)
            {
                Log.Exception("APP", "повышенная операция", ex);
                return -1;
            }
        }

        /// <summary>Понятное объяснение, зачем нужны права.</summary>
        public static string ExplainWhy()
        {
            return
                "Для обхода блокировок нужны права администратора.\r\n\r\n" +
                "Причина: драйвер WinDivert, который перехватывает пакеты, " +
                "загружается только с правами администратора. Без них Windows " +
                "не даст запустить движок.\r\n\r\n" +
                "Перезапустить AutoZapret с нужными правами?";
        }
    }
}
