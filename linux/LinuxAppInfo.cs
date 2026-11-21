using System;
using System.Diagnostics;
using System.Reflection;

namespace AutoZapret.Core
{
    /// <summary>
    /// Сведения о программе для Linux.
    ///
    /// Заменяет src/Core/AppInfo.cs, который знает про WebView2 и UAC. Здесь
    /// то же самое, но честно про эту систему: права проверяются через euid,
    /// а WebView2 не существует — интерфейс открывается в браузере.
    /// </summary>
    public static class AppInfo
    {
        public const string Name = "AutoZapret";

        public static string Version
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    if (v == null) return "0.0.0";
                    return v.Major + "." + v.Minor + "." + v.Build;
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }

        /// <summary>
        /// Работаем ли с правами root.
        ///
        /// nfqws нужно открыть netlink-сокет к ядру и менять правила iptables —
        /// без root это невозможно. Проверяем euid, а не «есть ли sudo»:
        /// sudo может быть, а программа всё равно запущена обычным пользователем.
        /// </summary>
        public static bool IsAdmin
        {
            get
            {
                try
                {
                    string id = Environment.GetEnvironmentVariable("EUID");
                    if (!string.IsNullOrEmpty(id)) return id == "0";

                    // EUID не всегда выставлен в окружении — спрашиваем ядро через id.
                    ProcessStartInfo psi = new ProcessStartInfo("id", "-u")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process p = Process.Start(psi))
                    {
                        if (p == null) return false;
                        string output = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(2000);
                        return output == "0";
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string OsDescription
        {
            get
            {
                try
                {
                    string pretty = ReadOsRelease();
                    if (!string.IsNullOrEmpty(pretty)) return pretty + " (" + Environment.OSVersion.VersionString + ")";
                    return Environment.OSVersion.VersionString;
                }
                catch
                {
                    return "Linux";
                }
            }
        }

        private static string ReadOsRelease()
        {
            try
            {
                if (!System.IO.File.Exists("/etc/os-release")) return null;

                foreach (string line in System.IO.File.ReadAllLines("/etc/os-release"))
                {
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    {
                        string value = line.Substring("PRETTY_NAME=".Length).Trim();
                        return value.Trim('"');
                    }
                }
            }
            catch
            {
                // нет файла или нет прав — не важно, вернём версию ядра
            }

            return null;
        }

        /// <summary>
        /// WebView2 на Linux не существует. Метод оставлен, потому что его
        /// вызывает общий код: пустая строка означает «не применимо», и
        /// интерфейс не будет искать несуществующий компонент.
        /// </summary>
        public static string WebView2RuntimeVersion()
        {
            return "";
        }

        public static bool IsPlatformSupported()
        {
            return Environment.Is64BitProcess;
        }

        public static string PlatformProblem()
        {
            if (!Environment.Is64BitProcess)
                return "нужна 64-битная система: 32-битного nfqws не существует";

            return null;
        }

        public static string Summary()
        {
            return Name + " " + Version + " · " + OsDescription +
                   (IsAdmin ? " · root" : " · без root");
        }
    }
}
