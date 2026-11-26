using System;
using System.Reflection;

namespace AutoZapret.Core
{
    /// <summary>
    /// Сведения о сборке для Android.
    ///
    /// Заменяет `src/Core/AppInfo.cs`, который тянет WinForms и реестр. Из
    /// переносимого кода (прокси, кэш) нужен только `Version` — он уходит в
    /// User-Agent при скачивании списков.
    ///
    /// `IsAdmin` намеренно false: root на телефоне не запрашиваем, а права
    /// VPN даются через системный диалог, а не через «администратора».
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

        /// <summary>На Android «администратор» не используется — права даёт VpnService.</summary>
        public static bool IsAdmin { get { return false; } }

        /// <summary>Одна строка для журнала: что за сборка запущена.</summary>
        public static string Summary()
        {
            return Name + " " + Version + " (Android)";
        }
    }
}
