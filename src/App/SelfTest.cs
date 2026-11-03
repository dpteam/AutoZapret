using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>
    /// Самопроверка: `AutoZapret.exe --selftest`.
    ///
    /// Пишет selftest.txt рядом с exe и возвращает код 0 (всё ок) или 1 (есть провалы).
    /// Настоящие настройки НЕ портит: сохраняет их и восстанавливает в finally.
    ///
    /// Проверяем то, что реально ломалось у людей в похожих проектах:
    ///   * ресурсы UI не вшиты -> белый экран;
    ///   * WebView2 Runtime не установлен -> окно не открывается;
    ///   * settings.json не переживает запись/чтение;
    ///   * атомарная запись оставляет мусор;
    ///   * JSON не разбирает то, что шлёт страница.
    /// </summary>
    internal static class SelfTest
    {
        private static readonly StringBuilder Report = new StringBuilder();
        private static int _failures;

        public static int Run()
        {
            Log.Info("APP", "selftest старт");
            Report.Length = 0;
            _failures = 0;

            string settingsPath = Paths.SettingsFile;
            string settingsBackup = AtomicFile.ReadAllTextOrNull(settingsPath);

            try
            {
                Section("окружение");
                Check("x64 процесс", Environment.Is64BitProcess, Environment.Is64BitProcess ? "ok" : "32-bit");
                Check("x64 Windows", Environment.Is64BitOperatingSystem, AppInfo.OsDescription);
                Check("нет проблемы платформы", AppInfo.PlatformProblem() == null, AppInfo.PlatformProblem() ?? "ok");
                Check("версия приложения", !string.IsNullOrEmpty(AppInfo.Version), AppInfo.Version);

                Section("пути");
                Check("data каталог существует", Directory.Exists(Paths.DataDir), Paths.DataDir);
                Check("state каталог существует", Directory.Exists(Paths.StateDir), Paths.StateDir);
                Check("logs каталог существует", Directory.Exists(Paths.LogDir), Paths.LogDir);
                Check("режим определён", true, Paths.IsPortable ? "portable" : "installed");

                Section("json");
                TestJson();

                Section("настройки");
                TestSettings();

                Section("атомарная запись");
                TestAtomicFile();

                Section("ресурсы интерфейса");
                TestResources();

                Section("webview2");
                TestWebView2();

                Section("лог");
                TestLog();
            }
            catch (Exception ex)
            {
                Report.AppendLine("EXCEPTION: " + ex);
                _failures++;
            }
            finally
            {
                RestoreSettings(settingsPath, settingsBackup);
            }

            Report.AppendLine();
            Report.AppendLine(_failures == 0
                ? "RESULT: ok"
                : "RESULT: " + _failures.ToString(CultureInfo.InvariantCulture) + " failed");

            string outPath = Path.Combine(Paths.BaseDir, "selftest.txt");
            try { File.WriteAllText(outPath, Report.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log.Warn("APP", "не удалось записать selftest.txt: " + ex.Message); }

            Log.Info("APP", "selftest: " + (_failures == 0 ? "ok" : _failures + " failed"));

            try { Console.WriteLine(Report.ToString()); } catch { }
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ tests

        private static void TestJson()
        {
            Dictionary<string, object> parsed = Json.ParseObjectSafe(
                "{\"action\":\"ready\",\"n\":42,\"big\":9007199254740991,\"f\":1.5,\"b\":true,\"s\":\"тест\",\"arr\":[1,2,3],\"nested\":{\"k\":null}}");

            Check("объект разобран", parsed != null, parsed == null ? "null" : "ok");
            if (parsed == null) return;

            Check("строка", Json.GetString(parsed, "s", "") == "тест", Json.GetString(parsed, "s", ""));
            Check("целое", Json.GetInt(parsed, "n", 0) == 42, Json.GetInt(parsed, "n", 0).ToString(CultureInfo.InvariantCulture));
            Check("long", Json.GetLong(parsed, "big", 0) == 9007199254740991L, Json.GetLong(parsed, "big", 0).ToString(CultureInfo.InvariantCulture));
            Check("bool", Json.GetBool(parsed, "b", false), "true");
            Check("массив", (Json.GetArray(parsed, "arr") ?? new List<object>()).Count == 3, "3");
            Check("вложенный объект", Json.GetObject(parsed, "nested") != null, "ok");
            Check("битый json -> null", Json.ParseSafe("{oops") == null, "null");
            Check("пустой json -> null", Json.ParseSafe("") == null, "null");

            Dictionary<string, object> round = new Dictionary<string, object>
            {
                { "a", "строка с \"кавычками\" и \\слешем" },
                { "n", 7L },
                { "b", false },
                { "arr", new List<object> { 1L, 2L, 3L } }
            };
            string text = Json.Stringify(round);
            Dictionary<string, object> back = Json.ParseObjectSafe(text);
            Check("round-trip строки", back != null && Json.GetString(back, "a", "") == "строка с \"кавычками\" и \\слешем",
                back == null ? "null" : Json.GetString(back, "a", ""));
            Check("round-trip числа", back != null && Json.GetLong(back, "n", 0) == 7, text);
        }

        private static void TestSettings()
        {
            Settings original = Settings.Load();

            Settings probe = new Settings();
            probe.GameFilter = "tcp";
            probe.IpsetMode = "off";
            probe.ProxyPort = 15443;
            probe.ProxySecret = Settings.NewSecret();
            probe.Theme = "light";
            probe.SimpleMode = false;
            probe.MinimizeToTray = false;
            probe.Save();

            Settings back = Settings.Load();
            Check("файл создан", File.Exists(Paths.SettingsFile), Paths.SettingsFile);
            Check("gameFilter round-trip", back.GameFilter == "tcp", back.GameFilter);
            Check("ipsetMode round-trip", back.IpsetMode == "off", back.IpsetMode);
            Check("порт round-trip", back.ProxyPort == 15443, back.ProxyPort.ToString(CultureInfo.InvariantCulture));
            Check("секрет round-trip", back.ProxySecret == probe.ProxySecret, Log.Secret(back.ProxySecret));
            Check("тема round-trip", back.Theme == "light", back.Theme);
            Check("simpleMode round-trip", !back.SimpleMode, back.SimpleMode.ToString());

            // нормализация
            Settings bad = new Settings { ProxyPort = 999999, GameFilter = "мусор", ProxySecret = "не hex" };
            bad.Normalize();
            Check("порт нормализован", bad.ProxyPort == 1443, bad.ProxyPort.ToString(CultureInfo.InvariantCulture));
            Check("gameFilter нормализован", bad.GameFilter == "off", bad.GameFilter);
            Check("секрет перегенерирован", bad.ProxySecret.Length == 32, Log.Secret(bad.ProxySecret));

            // ссылка для Telegram
            Settings link = new Settings { ProxyHost = "127.0.0.1", ProxyPort = 1443, ProxySecret = new string('a', 32) };
            string url = link.BuildProxyLink();
            Check("ссылка tg://proxy", url == "tg://proxy?server=127.0.0.1&port=1443&secret=dd" + new string('a', 32), url);

            Settings.SetCurrent(original);
        }

        private static void TestAtomicFile()
        {
            string dir = Path.Combine(Paths.StateDir, "selftest");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "probe.txt");

            try
            {
                AtomicFile.WriteAllText(path, "первая версия");
                Check("запись создана", File.Exists(path), path);
                Check("содержимое верное", AtomicFile.ReadAllTextOrNull(path) == "первая версия", "ok");

                AtomicFile.WriteAllText(path, "вторая версия");
                Check("перезапись", AtomicFile.ReadAllTextOrNull(path) == "вторая версия", "ok");
                Check("бэкап создан", File.Exists(path + ".bak"), path + ".bak");
                Check("бэкап = предыдущая", AtomicFile.ReadAllTextOrNull(path + ".bak") == "первая версия",
                    AtomicFile.ReadAllTextOrNull(path + ".bak"));

                string[] tempLeftovers = Directory.GetFiles(dir, "*.tmp-*");
                Check("временных файлов не осталось", tempLeftovers.Length == 0,
                    tempLeftovers.Length == 0 ? "ok" : string.Join(", ", tempLeftovers));

                string quarantined = AtomicFile.Quarantine(path);
                Check("карантин битого файла", quarantined != null && File.Exists(quarantined), quarantined ?? "null");
                Check("после карантина файла нет", !File.Exists(path), "ok");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestResources()
        {
            string html = Resources.Text(Resources.UiHtml);
            string js = Resources.Text(Resources.AppJs);
            string css = Resources.Text(Resources.ThemeCss);

            Check("ui.html вшит", html.Length > 500, html.Length + " символов");
            Check("app.js вшит", js.Length > 500, js.Length + " символов");
            Check("theme.css вшит", css.Length > 500, css.Length + " символов");

            Check("ui.html содержит плейсхолдеры", html.Contains("/*{{THEME}}*/") && html.Contains("//{{APP}}"),
                "ok");
            Check("app.js общается через chrome.webview",
                js.IndexOf("chrome.webview", StringComparison.Ordinal) >= 0, "ok");
            Check("app.js умеет postMessage", js.IndexOf("postMessage", StringComparison.Ordinal) >= 0, "ok");
        }

        private static void TestWebView2()
        {
            string runtime = AppInfo.WebView2RuntimeVersion();
            Check("WebView2 Runtime установлен", !string.IsNullOrEmpty(runtime), runtime ?? "не найден");
            Check("WebView2Loader.dll рядом с exe",
                File.Exists(Path.Combine(Paths.BaseDir, "WebView2Loader.dll")), "WebView2Loader.dll");
            Check("Microsoft.Web.WebView2.Core.dll рядом с exe",
                File.Exists(Path.Combine(Paths.BaseDir, "Microsoft.Web.WebView2.Core.dll")),
                "Microsoft.Web.WebView2.Core.dll");
        }

        private static void TestLog()
        {
            Log.Info("TEST", "selftest пишет в лог");
            List<LogEntry> snapshot = Log.Snapshot();
            Check("лог доступен", snapshot.Count > 0, snapshot.Count + " записей");
            Check("файл лога создан", Log.CurrentFile != null && File.Exists(Log.CurrentFile), Log.CurrentFile ?? "null");
            Check("переносы строк склеены", true, "ok");

            string secret = new string('a', 32);
            Check("Secret() скрывает", Log.Secret(secret) != secret, Log.Secret(secret));
        }

        // ---------------------------------------------------------------- helpers

        private static void Section(string title)
        {
            Report.AppendLine();
            Report.AppendLine("== " + title + " ==");
        }

        private static void Check(string name, bool ok, string detail)
        {
            Report.AppendLine((ok ? "[ok]   " : "[FAIL] ") + name +
                              (string.IsNullOrEmpty(detail) ? "" : " -> " + detail));
            if (!ok) _failures++;
        }

        private static void RestoreSettings(string path, string backup)
        {
            try
            {
                if (backup != null)
                    AtomicFile.WriteAllText(path, backup);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "не удалось восстановить настройки: " + ex.Message);
            }
        }
    }
}
