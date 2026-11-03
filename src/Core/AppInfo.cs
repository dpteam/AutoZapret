using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace AutoZapret.Core
{
    /// <summary>Сведения о приложении и окружении. Всё, что показывается в «О программе» и логах.</summary>
    public static class AppInfo
    {
        public const string Name = "AutoZapret";

        /// <summary>Псевдоним: используется в UI там, где важнее «имя продукта», чем «имя».</summary>
        public const string ProductName = Name;
        public const string Url = "https://github.com/";

        public static string Version
        {
            get
            {
                try
                {
                    Version v = Assembly.GetExecutingAssembly().GetName().Version;
                    return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", v.Major, v.Minor, v.Build);
                }
                catch { return "0.0.0"; }
            }
        }

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using (System.Security.Principal.WindowsIdentity identity =
                           System.Security.Principal.WindowsIdentity.GetCurrent())
                    {
                        System.Security.Principal.WindowsPrincipal principal =
                            new System.Security.Principal.WindowsPrincipal(identity);
                        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    }
                }
                catch { return false; }
            }
        }

        public static string OsDescription
        {
            get
            {
                try
                {
                    Version v = Environment.OSVersion.Version;
                    string name;
                    if (v.Major == 6 && v.Minor == 1) name = "Windows 7";
                    else if (v.Major == 6 && v.Minor == 2) name = "Windows 8";
                    else if (v.Major == 6 && v.Minor == 3) name = "Windows 8.1";
                    else if (v.Major == 10 && v.Build >= 22000) name = "Windows 11";
                    else if (v.Major == 10) name = "Windows 10";
                    else name = "Windows " + v.Major + "." + v.Minor;

                    return string.Format(CultureInfo.InvariantCulture, "{0} ({1}), {2}",
                        name, v, Environment.Is64BitOperatingSystem ? "x64" : "x86");
                }
                catch { return "Windows (?)"; }
            }
        }

        /// <summary>
        /// Проверка разрядности. 32-битного winws.exe не существует, WinDivert64.sys -
        /// единственный драйвер в бандле. См. AGENTS.md.
        /// </summary>
        public static bool IsPlatformSupported()
        {
            return Environment.Is64BitOperatingSystem && Environment.Is64BitProcess;
        }

        public static string PlatformProblem()
        {
            if (!Environment.Is64BitOperatingSystem)
                return "Нужна 64-битная Windows: 32-битной сборки движка zapret не существует.";
            if (!Environment.Is64BitProcess)
                return "AutoZapret запущен как 32-битный процесс. Требуется x64-сборка.";
            return null;
        }

        /// <summary>Версия WebView2 Runtime, если установлен. Иначе null.</summary>
        public static string WebView2RuntimeVersion()
        {
            try
            {
                return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch
            {
                return null;
            }
        }

        public static string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} {1} | {2} | {3} | {4}",
                Name, Version,
                OsDescription,
                IsAdmin ? "admin" : "user",
                Paths.IsPortable ? "portable" : "installed");
        }
    }
}
