using System;
using System.IO;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Доступ к вшитым ресурсам (UI). Ассеты лежат в exe, наружу не распаковываются -
    /// так их нельзя случайно отредактировать и «сломать интерфейс».
    ///
    /// LogicalName заданы в AutoZapret.csproj: AutoZapret.Web.ui.html и т.д.
    /// </summary>
    public static class Resources
    {
        public const string UiHtml = "AutoZapret.Web.ui.html";
        public const string AppJs = "AutoZapret.Web.app.js";
        public const string ThemeCss = "AutoZapret.Web.theme.css";

        /// <summary>Словарь перевода. Русский не нужен: исходный текст и есть русский.</summary>
        public const string EnglishDict = "AutoZapret.Web.i18n.en.json";

        /// <summary>Читает ресурс. При отсутствии возвращает пустую строку (не null).</summary>
        public static string Text(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return string.Empty;
            try
            {
                using (Stream stream = typeof(Resources).Assembly.GetManifestResourceStream(logicalName))
                {
                    if (stream == null) return string.Empty;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        return reader.ReadToEnd();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static bool Exists(string logicalName)
        {
            try { return typeof(Resources).Assembly.GetManifestResourceInfo(logicalName) != null; }
            catch { return false; }
        }

        /// <summary>Список всех вшитых ресурсов - для --selftest и диагностики.</summary>
        public static string[] Names()
        {
            try
            {
                string[] names = typeof(Resources).Assembly.GetManifestResourceNames();
                Array.Sort(names, StringComparer.Ordinal);
                return names;
            }
            catch
            {
                return new string[0];
            }
        }
    }
}
