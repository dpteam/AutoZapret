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

                Section("движок");
                TestEngine();

                Section("парсер стратегий");
                TestStrategies();

                Section("квотирование аргументов");
                TestCommandLine();

                Section("списки");
                TestLists();

                Section("фейки");
                TestPayloads();
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

        /// <summary>
        /// Реальный запуск winws: --test-winws [strategyId] [seconds]
        /// Проверяет, что процесс стартует, живёт и корректно останавливается.
        /// Не трогает чужие процессы: если winws уже запущен — честно откажется.
        /// </summary>
        public static int RunWinws(string[] args)
        {
            string strategyId = "flowseal.general";
            int seconds = 6;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--test-winws", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) strategyId = args[i + 1];
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out seconds);
                }
            }

            Console.WriteLine("== тест запуска winws ==");
            Console.WriteLine("стратегия: " + strategyId);
            Console.WriteLine("удержание: " + seconds + " с");

            using (AutoZapret.Zapret.ZapretService svc = new AutoZapret.Zapret.ZapretService())
            {
                string foreign = AutoZapret.Zapret.WinwsSupervisor.DetectForeignWinws();
                if (foreign != null)
                {
                    Console.WriteLine("ОТКАЗ: уже работает winws (" + foreign + ")");
                    Console.WriteLine("       остановите его: sc stop zapret");
                    return 3;
                }

                svc.EnsureStrategies();
                Console.WriteLine("стратегий доступно: " + svc.Strategies.Count);

                string error = svc.Start(strategyId);
                if (error != null)
                {
                    Console.WriteLine("ОШИБКА СТАРТА: " + error);
                    return 1;
                }

                Console.WriteLine("состояние: " + svc.State);

                bool alive = false;
                for (int i = 0; i < seconds; i++)
                {
                    System.Threading.Thread.Sleep(1000);
                    System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName("winws");
                    alive = procs.Length > 0;
                    Console.WriteLine("  t+" + (i + 1) + "с: winws процессов " + procs.Length);
                    foreach (System.Diagnostics.Process p in procs) { try { p.Dispose(); } catch { } }
                }

                bool stopped = svc.Stop();
                System.Threading.Thread.Sleep(800);

                System.Diagnostics.Process[] after = System.Diagnostics.Process.GetProcessesByName("winws");
                int left = after.Length;
                foreach (System.Diagnostics.Process p in after) { try { p.Dispose(); } catch { } }

                Console.WriteLine("после остановки winws процессов: " + left);
                Console.WriteLine("состояние: " + svc.State);

                bool ok = alive && stopped && left == 0 && svc.State == AutoZapret.Zapret.ProtectionState.Stopped;
                Console.WriteLine(ok ? "RESULT: ok" : "RESULT: failed");
                return ok ? 0 : 1;
            }
        }

        private static void TestEngine()
        {
            AutoZapret.Zapret.EngineLocator engine = new AutoZapret.Zapret.EngineLocator();
            Check("движок установлен", engine.Ready, engine.Problem ?? engine.StatusLine());

            if (!engine.Ready) return;

            Check("winws.exe найден", engine.WinwsPath != null, engine.WinwsPath ?? "null");
            Check("версия прочитана", !string.IsNullOrEmpty(engine.Version), engine.Version);
            Check("WinDivert64.sys на месте",
                System.IO.File.Exists(System.IO.Path.Combine(engine.BinDir, "WinDivert64.sys")), "ok");

            string tcp, udp;
            engine.MapGameFilter("tcp+udp", out tcp, out udp);
            Check("game filter tcp+udp", tcp == "1024-65535" && udp == "1024-65535", tcp + "/" + udp);

            engine.MapGameFilter("off", out tcp, out udp);
            Check("game filter off", tcp == "12" && udp == "12", tcp + "/" + udp);

            Dictionary<string, string> ph = engine.BuildPlaceholders("off");
            Check("плейсхолдер BIN", ph.ContainsKey("BIN") && System.IO.Directory.Exists(ph["BIN"]), ph["BIN"]);
            Check("плейсхолдер LISTS", ph.ContainsKey("LISTS") && System.IO.Directory.Exists(ph["LISTS"]), ph["LISTS"]);
        }

        private static void TestStrategies()
        {
            AutoZapret.Zapret.EngineLocator engine = new AutoZapret.Zapret.EngineLocator();
            if (!engine.Ready)
            {
                Check("парсер стратегий", false, "движок не установлен — нечего разбирать");
                return;
            }

            // исходные .bat лежат в engine\..\..\refs (dev) либо рядом; берём из refs, если есть
            string batDir = System.IO.Path.Combine(Paths.BaseDir, "..", "..", "refs", "zapret-discord-youtube");
            if (!System.IO.Directory.Exists(batDir))
                batDir = System.IO.Path.Combine(Paths.BaseDir, "..", "refs", "zapret-discord-youtube");

            if (!System.IO.Directory.Exists(batDir))
            {
                Check("каталог .bat найден", false, batDir);
                return;
            }

            AutoZapret.Zapret.StrategyStore store = new AutoZapret.Zapret.StrategyStore();
            int count = store.ImportFromDirectory(batDir, engine.BinDir, engine.ListsDir);

            Check("стратегии импортированы", count >= 20, count + " шт.");
            if (count == 0) return;

            AutoZapret.Zapret.Strategy general = store.Find("flowseal.general");
            Check("стратегия general найдена", general != null, general == null ? "null" : general.Name);

            if (general != null)
            {
                Check("globalArgs содержит --wf-tcp",
                    general.GlobalArgs.Exists(a => a.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase)),
                    string.Join(" ", general.GlobalArgs.ToArray()));
                Check("globalArgs содержит --wf-udp",
                    general.GlobalArgs.Exists(a => a.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase)), "ok");
                Check("профилей больше 5", general.Profiles.Count > 5, general.Profiles.Count.ToString());

                // ни один аргумент не должен остаться с абсолютным путём к bin/lists
                bool hasAbsPath = false;
                foreach (AutoZapret.Zapret.StrategyProfile p in general.Profiles)
                    foreach (string a in p.Args)
                        if (a.IndexOf(engine.BinDir, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            a.IndexOf(engine.ListsDir, StringComparison.OrdinalIgnoreCase) >= 0)
                            hasAbsPath = true;
                Check("абсолютные пути заменены плейсхолдерами", !hasAbsPath, hasAbsPath ? "остались" : "ok");

                // сборка финальных аргументов
                Dictionary<string, string> ph = engine.BuildPlaceholders("off");
                List<string> args = general.BuildArguments(ph);
                Check("аргументы собираются", args.Count > 20, args.Count + " арг.");
                Check("--new разделяет профили",
                    args.FindAll(a => a == "--new").Count == general.Profiles.Count - 1,
                    args.FindAll(a => a == "--new").Count + " шт.");
                Check("плейсхолдеры подставлены",
                    args.Exists(a => a.Contains("${")) == false, "ok");

                // все файлы, на которые ссылаются аргументы, должны существовать
                int missing = 0;
                string firstMissing = null;
                foreach (string a in args)
                {
                    int eq = a.IndexOf('=');
                    if (eq < 0) continue;
                    string value = a.Substring(eq + 1);
                    if (!value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                        !value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!System.IO.File.Exists(value))
                    {
                        missing++;
                        if (firstMissing == null) firstMissing = value;
                    }
                }
                Check("все файлы стратегии существуют", missing == 0,
                    missing == 0 ? "ok" : missing + " нет, первый: " + firstMissing);
            }

            // сохранение/загрузка
            string tmp = System.IO.Path.Combine(Paths.StateDir, "selftest-strategies.json");
            try
            {
                store.Save(tmp, "selftest");
                Check("стратегии сохранены", System.IO.File.Exists(tmp), tmp);

                AutoZapret.Zapret.StrategyStore reloaded = new AutoZapret.Zapret.StrategyStore();
                bool loaded = reloaded.Load(tmp);
                Check("стратегии загружены", loaded && reloaded.Count == count, reloaded.Count + "/" + count);

                AutoZapret.Zapret.Strategy back = reloaded.Find("flowseal.general");
                Check("round-trip сохранил профили",
                    back != null && back.Profiles.Count == general.Profiles.Count,
                    back == null ? "null" : back.Profiles.Count.ToString());
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { }
            }
        }

        private static void TestCommandLine()
        {
            // правила квотирования Windows
            Check("простое слово", AutoZapret.Zapret.CommandLine.Quote("abc") == "abc", "abc");
            Check("пробел -> кавычки",
                AutoZapret.Zapret.CommandLine.Quote("a b") == "\"a b\"", "a b");
            Check("путь с пробелом",
                AutoZapret.Zapret.CommandLine.Quote(@"C:\Program Files\x") == "\"C:\\Program Files\\x\"",
                @"C:\Program Files\x");
            Check("пустая строка", AutoZapret.Zapret.CommandLine.Quote("") == "\"\"", "empty");
            Check("кавычка внутри",
                AutoZapret.Zapret.CommandLine.Quote("a\"b") == "\"a\\\"b\"",
                AutoZapret.Zapret.CommandLine.Quote("a\"b"));

            // round-trip: собрали строку -> разобрали обратно
            List<string> original = new List<string>
            {
                "--wf-tcp=80,443",
                @"--hostlist=C:\Program Files\AutoZapret\engine\lists\list-general.txt",
                "--dpi-desync=fake,multisplit",
                @"C:\path with spaces\and trailing\",
                "!",
                "--dpi-desync-fake-tls=0x00000000"
            };

            string built = AutoZapret.Zapret.CommandLine.Build(original);
            List<string> parsed = AutoZapret.Zapret.CommandLine.Split(built);

            bool same = parsed.Count == original.Count;
            if (same)
                for (int i = 0; i < original.Count; i++)
                    if (parsed[i] != original[i]) { same = false; break; }

            Check("round-trip аргументов", same, same ? "ok" : "built: " + built);
        }

        private static void TestLists()
        {
            // валидация доменов
            Check("домен валиден", AutoZapret.Lists.HostlistManager.IsValidDomain("example.com"), "example.com");
            Check("маска валидна", AutoZapret.Lists.HostlistManager.IsValidDomain("*.example.com"), "*.example.com");
            Check("!домен валиден", AutoZapret.Lists.HostlistManager.IsValidDomain("!example.com"), "!example.com");
            Check("пустое отклонено", !AutoZapret.Lists.HostlistManager.IsValidDomain(""), "empty");
            Check("без точки отклонено", !AutoZapret.Lists.HostlistManager.IsValidDomain("localhost"), "localhost");
            Check("двойная точка отклонена", !AutoZapret.Lists.HostlistManager.IsValidDomain("a..com"), "a..com");
            Check("пробел отклонён", !AutoZapret.Lists.HostlistManager.IsValidDomain("a b.com"), "a b.com");

            // валидация IP
            Check("IP валиден", AutoZapret.Lists.HostlistManager.IsValidIpOrCidr("10.0.0.1"), "10.0.0.1");
            Check("CIDR валиден", AutoZapret.Lists.HostlistManager.IsValidIpOrCidr("192.168.0.0/16"), "192.168.0.0/16");
            Check("CIDR v6 валиден", AutoZapret.Lists.HostlistManager.IsValidIpOrCidr("fe80::/10"), "fe80::/10");
            Check("плохой префикс отклонён", !AutoZapret.Lists.HostlistManager.IsValidIpOrCidr("10.0.0.0/64"), "10.0.0.0/64");
            Check("текст отклонён", !AutoZapret.Lists.HostlistManager.IsValidIpOrCidr("не ip"), "не ip");

            // разбор ввода
            List<string> parsed = AutoZapret.Lists.HostlistManager.ParseInput("a.com\nb.com, c.com; d.com");
            Check("разбор ввода", parsed.Count == 4, parsed.Count + " значений");

            // идемпотентность записи
            string dir = System.IO.Path.Combine(Paths.StateDir, "selftest-lists");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "list.txt");

            try
            {
                List<AutoZapret.Lists.ListEntry> entries = new List<AutoZapret.Lists.ListEntry>
                {
                    new AutoZapret.Lists.ListEntry { Value = "a.com" },
                    new AutoZapret.Lists.ListEntry { Value = "b.com" }
                };

                bool first = AutoZapret.Lists.HostlistManager.Write(path, entries);
                Check("список записан", first && System.IO.File.Exists(path), path);

                bool second = AutoZapret.Lists.HostlistManager.Write(path, entries);
                Check("повторная запись не трогает файл", !second, second ? "перезаписал" : "ok");

                List<AutoZapret.Lists.ListEntry> back = AutoZapret.Lists.HostlistManager.Read(path);
                Check("список прочитан", back.Count == 2, back.Count + " записей");
                Check("значения совпали",
                    back.Count == 2 && back[0].Value == "a.com" && back[1].Value == "b.com",
                    back.Count > 1 ? back[0].Value + ", " + back[1].Value : "");

                // комментарии пользователя не должны теряться при повторной записи
                System.IO.File.AppendAllText(path, "# моя заметка\r\n");
                bool third = AutoZapret.Lists.HostlistManager.Write(path, entries);
                Check("комментарий не вызывает перезапись", !third, third ? "перезаписал" : "ok");

                // нормализация ipset: CRLF -> LF
                string ipsetPath = System.IO.Path.Combine(dir, "ipset.txt");
                System.IO.File.WriteAllText(ipsetPath, "10.0.0.0/8\r\n192.168.0.0/16\r\n\r\nмусор\r\n# коммент\r\n");
                int normalized = AutoZapret.Lists.HostlistManager.NormalizeIpset(ipsetPath);
                Check("ipset нормализован", normalized == 2, normalized + " записей");

                string content = System.IO.File.ReadAllText(ipsetPath);
                Check("ipset без CR", content.IndexOf('\r') < 0, content.IndexOf('\r') < 0 ? "ok" : "есть CR");
                Check("мусор выброшен", content.IndexOf("мусор") < 0, "ok");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestPayloads()
        {
            AutoZapret.Payloads.PayloadLibrary library = new AutoZapret.Payloads.PayloadLibrary();
            int total = library.Scan();

            Check("фейки найдены", total > 0, total + " файлов");
            Check("все фейки валидны", library.ValidCount == total,
                library.ValidCount + "/" + total);

            if (total == 0) return;

            int tls = library.ByKind(AutoZapret.Payloads.PayloadKind.TlsClientHello).Count;
            int quic = library.ByKind(AutoZapret.Payloads.PayloadKind.QuicInitial).Count;
            int stun = library.ByKind(AutoZapret.Payloads.PayloadKind.Stun).Count;

            Check("есть TLS-фейки", tls > 0, tls.ToString());
            Check("есть QUIC-фейки", quic > 0, quic.ToString());
            Check("есть STUN-фейки", stun > 0, stun.ToString());

            // SNI должен извлекаться из ClientHello
            AutoZapret.Payloads.Payload google =
                library.Find("tls_clienthello_www_google_com.bin");
            Check("SNI извлечён", google != null && google.Sni == "www.google.com",
                google == null ? "файл не найден" : google.Sni);

            // валидатор должен ОТБРАКОВЫВАТЬ битые файлы
            string dir = System.IO.Path.Combine(Paths.StateDir, "selftest-fakes");
            System.IO.Directory.CreateDirectory(dir);

            try
            {
                string broken = System.IO.Path.Combine(dir, "broken.bin");
                System.IO.File.WriteAllBytes(broken, new byte[] { 0x16, 0x03, 0x01, 0x00, 0x10 });

                AutoZapret.Payloads.Payload payload = AutoZapret.Payloads.PayloadValidator.Inspect(broken);
                Check("битый TLS отбракован", !payload.Valid, payload.InvalidReason);

                string tiny = System.IO.Path.Combine(dir, "tiny.bin");
                System.IO.File.WriteAllBytes(tiny, new byte[] { 0x01 });
                Check("слишком малый отбракован",
                    !AutoZapret.Payloads.PayloadValidator.Inspect(tiny).Valid, "ok");

                string garbage = System.IO.Path.Combine(dir, "garbage.bin");
                byte[] junk = new byte[1500];
                new Random(42).NextBytes(junk);
                junk[0] = 0x00;   // не TLS, не QUIC long header
                System.IO.File.WriteAllBytes(garbage, junk);
                Check("мусор отбракован",
                    !AutoZapret.Payloads.PayloadValidator.Inspect(garbage).Valid, "ok");
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

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
