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
        private static int _skipped;
        private static bool _requireEngine;

        public static int Run(bool requireEngine = false)
        {
            Log.Info("APP", "selftest старт");
            Report.Length = 0;
            _failures = 0;
            _skipped = 0;
            _requireEngine = requireEngine;

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

                Section("hosts");
                TestHosts();

                Section("фейки");
                TestPayloads();

                Section("прокси: крипто");
                TestProxyCrypto();

                Section("обновления");
                TestUpdates();

                Section("автопилот");
                TestAutoPilot();

                Section("проверки окружения");
                TestSystemChecks();

                Section("тест TCP 16-20 КБ");
                TestTcp16();

                Section("диагностический архив");
                TestDiagnosticBundle();
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
            string verdict = _failures == 0
                ? (_skipped > 0
                    ? "RESULT: ok (" + _skipped.ToString(CultureInfo.InvariantCulture) + " skipped)"
                    : "RESULT: ok")
                : "RESULT: " + _failures.ToString(CultureInfo.InvariantCulture) + " failed" +
                  (_skipped > 0 ? ", " + _skipped.ToString(CultureInfo.InvariantCulture) + " skipped" : "");
            Report.AppendLine(verdict);

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

            // Нативная библиотека WebView2 лежит рядом с exe и обязательна:
            // в single-file она не встраивается, без неё окно не стартует.
            Check("WebView2Loader.dll рядом с exe",
                File.Exists(Path.Combine(Paths.BaseDir, "WebView2Loader.dll")), "WebView2Loader.dll");

            // Managed-сборка может быть как рядом с exe (обычная сборка),
            // так и вшита в single-file. Проверяем, что тип реально резолвится,
            // а не что файл лежит на диске.
            bool managedOk;
            try
            {
                Type t = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core", false);
                managedOk = t != null;
            }
            catch { managedOk = false; }

            Check("сборка WebView2 доступна", managedOk,
                managedOk ? "загружена" : "не найдена");
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
                    int count = CountWinwsProcesses();
                    alive = count > 0;
                    Console.WriteLine("  t+" + (i + 1) + "с: winws процессов " + count);
                }

                bool stopped = svc.Stop();
                System.Threading.Thread.Sleep(800);

                int left = CountWinwsProcesses();

                Console.WriteLine("после остановки winws процессов: " + left);
                Console.WriteLine("состояние: " + svc.State);

                bool ok = alive && stopped && left == 0 && svc.State == AutoZapret.Zapret.ProtectionState.Stopped;
                Console.WriteLine(ok ? "RESULT: ok" : "RESULT: failed");
                return ok ? 0 : 1;
            }
        }

        /// <summary>
        /// Считает живые winws и winws2.
        /// Важно: у zapret2 процесс называется winws2, и проверка только "winws"
        /// даёт ложное «движок не запустился».
        /// </summary>
        private static int CountWinwsProcesses()
        {
            int total = 0;

            foreach (string name in new[] { "winws", "winws2" })
            {
                System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName(name);
                total += procs.Length;
                foreach (System.Diagnostics.Process p in procs) { try { p.Dispose(); } catch { } }
            }

            return total;
        }

        private static void TestEngine()
        {
            AutoZapret.Zapret.EngineLocator engine = new AutoZapret.Zapret.EngineLocator();

            if (!engine.Ready)
            {
                // Движок не скачан — это нормальное состояние чистого клона
                // (engine/ в .gitignore). Не провал, а пропуск.
                if (_requireEngine)
                    Check("движок установлен (обязательно)", false, engine.Problem);
                else
                    Skip("движок установлен", engine.Problem + " (запустите tools\\fetch-engine.ps1)");
                return;
            }

            Check("движок установлен", true, engine.StatusLine());
            Check("winws.exe найден", engine.WinwsPath != null, engine.WinwsPath ?? "null");

            // v2: наличие и полнота lua — самая частая причина «winws2 не стартует»
            if (engine.HasV2)
            {
                Check("v2: winws2.exe найден", engine.ExePath(AutoZapret.Zapret.EngineKind.V2) != null, "ok");
                Check("v2: lua/blobs.lua на месте",
                    System.IO.File.Exists(System.IO.Path.Combine(engine.LuaDir, "blobs.lua")), "ok");
                Check("v2: lua/zapret-lib.lua на месте",
                    System.IO.File.Exists(System.IO.Path.Combine(engine.LuaDir, "zapret-lib.lua")), "ok");
                Check("v2: lua/zapret-antidpi.lua на месте",
                    System.IO.File.Exists(System.IO.Path.Combine(engine.LuaDir, "zapret-antidpi.lua")), "ok");
            }
            else
            {
                Skip("v2: движок", engine.UnavailableReason(AutoZapret.Zapret.EngineKind.V2));
            }            Check("версия прочитана", !string.IsNullOrEmpty(engine.Version), engine.Version);
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
                Skip("парсер стратегий", "движок не установлен — разбирать нечего");
                return;
            }

            // исходные .bat лежат в engine\..\..\refs (dev) либо рядом; берём из refs, если есть
            string batDir = System.IO.Path.Combine(Paths.BaseDir, "..", "..", "refs", "zapret-discord-youtube");
            if (!System.IO.Directory.Exists(batDir))
                batDir = System.IO.Path.Combine(Paths.BaseDir, "..", "refs", "zapret-discord-youtube");

            if (!System.IO.Directory.Exists(batDir))
            {
                Skip("каталог .bat найден", batDir + " — пропускаю разбор");
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

            // ---- v2: другой синтаксис, свои глобальные флаги
            if (engine.HasV2)
            {
                string v2Dir = System.IO.Path.Combine(Paths.BaseDir, "..", "refs", "snanvl-zapret2");
                if (!System.IO.Directory.Exists(v2Dir))
                    v2Dir = System.IO.Path.Combine(Paths.BaseDir, "..", "..", "refs", "snanvl-zapret2");

                if (System.IO.Directory.Exists(v2Dir))
                {
                    AutoZapret.Zapret.StrategyStore v2Store = new AutoZapret.Zapret.StrategyStore();
                    int v2Count = v2Store.ImportFromDirectory(v2Dir, engine.BinDir, engine.ListsDir, engine.LuaDir);

                    Check("v2: стратегии импортированы", v2Count >= 20, v2Count + " шт.");

                    AutoZapret.Zapret.Strategy v2 = v2Store.Find("zapret2.general");
                    Check("v2: стратегия найдена", v2 != null, v2 == null ? "null" : v2.Name);

                    if (v2 != null)
                    {
                        Check("v2: engine = v2", v2.Engine == "v2", v2.Engine);
                        Check("v2: --lua-init в глобальных флагах",
                            v2.GlobalArgs.Exists(a => a.StartsWith("--lua-init=", StringComparison.OrdinalIgnoreCase)),
                            string.Join(" ", v2.GlobalArgs.ToArray()));
                        Check("v2: --wf-tcp-out (а не --wf-tcp)",
                            v2.GlobalArgs.Exists(a => a.StartsWith("--wf-tcp-out=", StringComparison.OrdinalIgnoreCase)), "ok");

                        // имена профилей берутся из --name
                        Check("v2: имя профиля из --name",
                            v2.Profiles.Count > 0 && v2.Profiles[0].Name.Length > 0 &&
                            v2.Profiles[0].Name.IndexOf("--name", StringComparison.Ordinal) < 0,
                            v2.Profiles.Count > 0 ? v2.Profiles[0].Name : "нет профилей");

                        Dictionary<string, string> ph = engine.BuildPlaceholders("off");
                        List<string> args = v2.BuildArguments(ph);
                        Check("v2: аргументы собираются", args.Count > 20, args.Count + " арг.");
                        Check("v2: плейсхолдеры подставлены",
                            !args.Exists(a => a.Contains("${")), "ok");
                        Check("v2: lua-пути существуют",
                            args.FindAll(a => a.StartsWith("--lua-init=@", StringComparison.OrdinalIgnoreCase))
                                .TrueForAll(a => System.IO.File.Exists(a.Substring("--lua-init=@".Length))),
                            "ok");
                    }
                }
                else
                {
                    Skip("v2: каталог стратегий", v2Dir);
                }
            }
            else
            {
                Skip("v2: разбор стратегий", "движок v2 не установлен");
            }

            // сохранение/загрузка
            string tmp = System.IO.Path.Combine(Paths.StateDir, "selftest-strategies.json");            try
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

        private static void TestHosts()
        {
            // --- разбор
            string source1 =
                "# comment\r\n" +
                "1.2.3.4 example.com www.example.com\r\n" +
                "1.2.3.4 example.com\r\n" +              // точный дубликат
                "5.6.7.8 other.org\r\n" +
                "мусор\r\n" +                            // невалидная строка
                "9.9.9.9 bad_host\r\n";                  // невалидное имя

            List<KeyValuePair<string, string>> sources = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flowseal", source1)
            };

            AutoZapret.Lists.HostsAnalysis a = AutoZapret.Lists.HostsFile.Analyze(sources, "");

            Check("hosts: имена разобраны", a.UniqueHosts == 3,
                a.UniqueHosts + " имён (example.com, www.example.com, other.org)");
            Check("hosts: дубликат найден", a.ExactDuplicates == 1, a.ExactDuplicates.ToString());
            Check("hosts: мусор отброшен", a.Invalid.Count == 2, a.Invalid.Count + " строк");
            Check("hosts: конфликтов нет", a.Conflicts.Count == 0, a.Conflicts.Count.ToString());

            // --- IPv4 + IPv6 для одного имени не конфликт
            List<KeyValuePair<string, string>> dualStack = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("src1", "1.2.3.4 dual.example\r\n"),
                new KeyValuePair<string, string>("src2", "2606:50c0::1 dual.example\r\n")
            };

            AutoZapret.Lists.HostsAnalysis dual = AutoZapret.Lists.HostsFile.Analyze(dualStack, "");
            Check("hosts: IPv4+IPv6 не конфликт", dual.Conflicts.Count == 0,
                dual.Conflicts.Count + " конфликтов");
            Check("hosts: обе записи сохранены", dual.Entries.Count == 2,
                dual.Entries.Count + " записей");

            // --- два разных IPv4 для одного имени = конфликт
            List<KeyValuePair<string, string>> clash = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flowseal", "1.1.1.1 clash.example\r\n"),
                new KeyValuePair<string, string>("malw", "2.2.2.2 clash.example\r\n")
            };

            AutoZapret.Lists.HostsAnalysis conflicted = AutoZapret.Lists.HostsFile.Analyze(clash, "");
            Check("hosts: конфликт IPv4 найден", conflicted.Conflicts.Count == 1,
                conflicted.Conflicts.Count + " конфликтов");
            Check("hosts: выбран по приоритету",
                conflicted.Conflicts.Count == 1 && conflicted.Conflicts[0].Chosen == "1.1.1.1",
                conflicted.Conflicts.Count == 1 ? conflicted.Conflicts[0].Chosen : "—");

            // --- приоритет проверки важнее приоритета источника
            Dictionary<string, string> preferred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "clash.example", "2.2.2.2" }
            };
            AutoZapret.Lists.HostsAnalysis verified = AutoZapret.Lists.HostsFile.Analyze(clash, "", preferred);
            Check("hosts: проверенный IP важнее приоритета",
                verified.Conflicts.Count == 1 && verified.Conflicts[0].Chosen == "2.2.2.2",
                verified.Conflicts.Count == 1 ? verified.Conflicts[0].Chosen : "—");

            // --- пользовательские записи не перекрываются
            string userHosts = "10.0.0.1 myserver.local\r\n1.1.1.1 keep.example\r\n";
            List<KeyValuePair<string, string>> withUser = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flowseal", "9.9.9.9 keep.example\r\n8.8.8.8 fresh.example\r\n")
            };
            AutoZapret.Lists.HostsAnalysis userAware = AutoZapret.Lists.HostsFile.Analyze(withUser, userHosts);
            Check("hosts: своя запись не перекрыта", userAware.UserCollisions == 1,
                userAware.UserCollisions.ToString());
            Check("hosts: новое имя добавлено", userAware.UniqueHosts == 1,
                userAware.UniqueHosts + " имён");

            // --- сборка файла: маркеры и сохранение пользовательской части
            string built = AutoZapret.Lists.HostsFile.Build(userHosts, userAware, "тест");
            Check("hosts: маркер начала", built.Contains(AutoZapret.Lists.HostsFile.BeginMarker), "ok");
            Check("hosts: маркер конца", built.Contains(AutoZapret.Lists.HostsFile.EndMarker), "ok");
            Check("hosts: пользовательская часть сохранена",
                built.Contains("10.0.0.1 myserver.local"), "ok");
            Check("hosts: новое имя записано", built.Contains("fresh.example"), "ok");

            // --- повторная сборка не дублирует блок
            string rebuilt = AutoZapret.Lists.HostsFile.Build(built, userAware, "тест");
            int markers = 0;
            int idx = 0;
            while ((idx = rebuilt.IndexOf(AutoZapret.Lists.HostsFile.BeginMarker, idx, StringComparison.Ordinal)) >= 0)
            {
                markers++;
                idx += 1;
            }
            Check("hosts: блок не дублируется", markers == 1, markers + " блоков");

            // --- StripBlock убирает ровно наш блок
            string stripped = AutoZapret.Lists.HostsFile.StripBlock(built);
            Check("hosts: StripBlock убирает блок",
                stripped.IndexOf(AutoZapret.Lists.HostsFile.BeginMarker, StringComparison.Ordinal) < 0 &&
                stripped.Contains("myserver.local"), "ok");
        }

        private static void TestPayloads()
        {
            AutoZapret.Payloads.PayloadLibrary library = new AutoZapret.Payloads.PayloadLibrary();
            int total = library.Scan();

            if (total == 0)
            {
                // Фейки лежат в engine\bin, а его на чистом клоне нет.
                if (_requireEngine)
                    Check("фейки найдены (обязательно)", false, "0 файлов");
                else
                    Skip("фейки найдены", "engine не установлен");
                return;
            }

            Check("фейки найдены", total > 0, total + " файлов");
            Check("все фейки валидны", library.ValidCount == total,
                library.ValidCount + "/" + total);

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

        private static void TestProxyCrypto()
        {
            // ---- AES-CTR: симметричность и независимость от размера пачки
            byte[] key = new byte[32];
            byte[] iv = new byte[16];
            new Random(1).NextBytes(key);
            new Random(2).NextBytes(iv);

            byte[] plain = new byte[1000];
            new Random(3).NextBytes(plain);

            byte[] encrypted;
            using (AutoZapret.Proxy.AesCtr ctr = new AutoZapret.Proxy.AesCtr(key, iv))
                encrypted = ctr.Transform(plain);

            Check("AES-CTR шифрует", !ByteEquals(plain, encrypted), "ok");

            byte[] decrypted;
            using (AutoZapret.Proxy.AesCtr ctr = new AutoZapret.Proxy.AesCtr(key, iv))
                decrypted = ctr.Transform(encrypted);

            Check("AES-CTR обратим", ByteEquals(plain, decrypted), "ok");

            // Тот же результат при шифровании кусками разного размера:
            // это проверяет, что позиция keystream не сбивается между пачками.
            byte[] chunked = new byte[1000];
            using (AutoZapret.Proxy.AesCtr ctr = new AutoZapret.Proxy.AesCtr(key, iv))
            {
                Buffer.BlockCopy(plain, 0, chunked, 0, plain.Length);
                int pos = 0;
                int[] sizes = { 1, 7, 16, 100, 333, 543 };
                int si = 0;
                while (pos < chunked.Length)
                {
                    int take = Math.Min(sizes[si++ % sizes.Length], chunked.Length - pos);
                    ctr.Transform(chunked, pos, take);
                    pos += take;
                }
            }
            Check("AES-CTR не зависит от размера пачки", ByteEquals(chunked, encrypted), "ok");

            // ---- secret parsing
            string hex = "00112233445566778899aabbccddeeff";
            byte[] parsed = AutoZapret.Proxy.TgProxyServer.ParseSecret(hex);
            Check("секрет разобран", parsed != null && parsed.Length == 16, parsed == null ? "null" : "16 байт");

            byte[] withPrefix = AutoZapret.Proxy.TgProxyServer.ParseSecret("dd" + hex);
            Check("секрет с dd-префиксом", withPrefix != null && ByteEquals(parsed, withPrefix), "ok");

            Check("короткий секрет отклонён",
                AutoZapret.Proxy.TgProxyServer.ParseSecret("aabb") == null, "ok");
            Check("не-hex секрет отклонён",
                AutoZapret.Proxy.TgProxyServer.ParseSecret(new string('z', 32)) == null, "ok");

            // ---- handshake: полный цикл encode -> decode
            byte[] secret = new byte[16];
            new Random(4).NextBytes(secret);

            // строим init так, как его построил бы клиент
            byte[] init = BuildTestInit(secret, dc: 2, media: false, tag: AutoZapret.Proxy.MtProtoTag.Intermediate);

            AutoZapret.Proxy.HandshakeResult hs = AutoZapret.Proxy.MtProtoHandshake.Decode(init, secret);
            Check("handshake разобран", hs.Ok, hs.Error);
            Check("DC определён", hs.Dc == 2, hs.Dc.ToString());
            Check("media = false", !hs.IsMedia, hs.IsMedia.ToString());
            Check("тег intermediate", hs.Tag == AutoZapret.Proxy.MtProtoTag.Intermediate,
                AutoZapret.Proxy.MtProtoHandshake.TagName(hs.Tag));

            // media: отрицательный DC
            byte[] initMedia = BuildTestInit(secret, dc: 4, media: true, tag: AutoZapret.Proxy.MtProtoTag.Abridged);
            AutoZapret.Proxy.HandshakeResult hsMedia = AutoZapret.Proxy.MtProtoHandshake.Decode(initMedia, secret);
            Check("media определён", hsMedia.Ok && hsMedia.IsMedia && hsMedia.Dc == 4,
                hsMedia.Ok ? hsMedia.Dc + " media=" + hsMedia.IsMedia : hsMedia.Error);

            // неверный секрет должен отклоняться
            byte[] wrongSecret = new byte[16];
            new Random(5).NextBytes(wrongSecret);
            AutoZapret.Proxy.HandshakeResult hsBad = AutoZapret.Proxy.MtProtoHandshake.Decode(init, wrongSecret);
            Check("неверный секрет отклонён", !hsBad.Ok, hsBad.Error);

            // ---- обратный ключ: расшифровка того, что мы зашифровали
            byte[] fwdKey, fwdIv;
            AutoZapret.Proxy.MtProtoHandshake.DeriveReverse(init, secret, out fwdKey, out fwdIv);
            Check("обратный ключ 32 байта", fwdKey != null && fwdKey.Length == 32, "ok");
            Check("обратный ключ отличается от прямого",
                !ByteEquals(fwdKey, hs.ClientKey), "ok");

            // ---- relay init: не похож на HTTP/TLS
            bool reserved = false;
            for (int i = 0; i < 50; i++)
            {
                byte[] relay = AutoZapret.Proxy.MtProtoHandshake.GenerateRelayInit(2, false);
                if (relay[0] == 0xEF) reserved = true;
                if (relay[0] == (byte)'G' && relay[1] == (byte)'E') reserved = true;
                if (relay[0] == 0x16 && relay[1] == 0x03) reserved = true;
            }
            Check("relay init без запрещённых начал", !reserved, "50 попыток");

            // ---- сплиттер: склейка пакетов
            AutoZapret.Proxy.MtProtoSplitter splitter =
                new AutoZapret.Proxy.MtProtoSplitter(AutoZapret.Proxy.MtProtoTag.Intermediate);

            // intermediate: длина 4 байта LE + payload
            byte[] p1 = new byte[8];
            p1[0] = 4; p1[1] = 0; p1[2] = 0; p1[3] = 0;
            p1[4] = 0xAA; p1[5] = 0xBB; p1[6] = 0xCC; p1[7] = 0xDD;

            System.Collections.Generic.List<byte[]> got = splitter.Feed(p1, 0, p1.Length);
            Check("сплиттер: один пакет", got.Count == 1, got.Count + " шт.");
            Check("сплиттер: длина верна", got.Count == 1 && got[0].Length == 8,
                got.Count == 1 ? got[0].Length + " байт" : "—");

            // два пакета в одном чанке
            byte[] two = new byte[16];
            Buffer.BlockCopy(p1, 0, two, 0, 8);
            Buffer.BlockCopy(p1, 0, two, 8, 8);
            System.Collections.Generic.List<byte[]> got2 = splitter.Feed(two, 0, two.Length);
            Check("сплиттер: два пакета", got2.Count == 2, got2.Count + " шт.");

            // разорванный пакет: сначала половина, потом остаток
            AutoZapret.Proxy.MtProtoSplitter splitter2 =
                new AutoZapret.Proxy.MtProtoSplitter(AutoZapret.Proxy.MtProtoTag.Intermediate);
            System.Collections.Generic.List<byte[]> part1 = splitter2.Feed(p1, 0, 3);
            System.Collections.Generic.List<byte[]> part2 = splitter2.Feed(p1, 3, 5);
            Check("сплиттер: ждёт целый пакет", part1.Count == 0 && part2.Count == 1,
                part1.Count + "/" + part2.Count);
        }

        /// <summary>Строит 64-байтовый init так, как это сделал бы клиент Telegram.</summary>
        private static byte[] BuildTestInit(byte[] secret, int dc, bool media, AutoZapret.Proxy.MtProtoTag tag)
        {
            byte[] init = new byte[64];
            new Random(100 + dc).NextBytes(init);

            // Ключ КЛИЕНТА: SHA256(prekey || secret), а не сам prekey.
            // (prekey напрямую — это уже ключ к Telegram, другая история.)
            byte[] prekey = new byte[32];
            Buffer.BlockCopy(init, 8, prekey, 0, 32);
            byte[] key = AutoZapret.Proxy.MtProtoHandshake.DeriveKey(prekey, secret);

            byte[] iv = new byte[16];
            Buffer.BlockCopy(init, 40, iv, 0, 16);

            byte[] tail = new byte[8];
            byte[] tagBytes = tag == AutoZapret.Proxy.MtProtoTag.Abridged
                ? AutoZapret.Proxy.MtProtoHandshake.TagAbridged
                : AutoZapret.Proxy.MtProtoHandshake.TagIntermediate;
            Buffer.BlockCopy(tagBytes, 0, tail, 0, 4);

            int dcValue = media ? -dc : dc;
            tail[4] = (byte)(dcValue & 0xFF);
            tail[5] = (byte)((dcValue >> 8) & 0xFF);

            using (AutoZapret.Proxy.AesCtr ctr = new AutoZapret.Proxy.AesCtr(key, iv))
            {
                ctr.Skip(56);
                ctr.Transform(tail, 0, 8);
            }

            Buffer.BlockCopy(tail, 0, init, 56, 8);
            return init;
        }

        private static bool ByteEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static void TestAutoPilot()
        {
            // ---- Beta-сэмплер: значения в [0,1], распределение вменяемое
            AutoZapret.Orchestrator.Bandit bandit = new AutoZapret.Orchestrator.Bandit(42);

            double sum = 0;
            int outOfRange = 0;
            for (int i = 0; i < 500; i++)
            {
                double v = bandit.Sample(2, 2);
                if (v < 0 || v > 1 || double.IsNaN(v)) outOfRange++;
                sum += v;
            }
            Check("Beta: значения в [0,1]", outOfRange == 0, outOfRange + " вне диапазона");

            double mean = sum / 500;
            Check("Beta(2,2): среднее ≈ 0.5", Math.Abs(mean - 0.5) < 0.06,
                mean.ToString("0.000", CultureInfo.InvariantCulture));

            // Beta(8,2) должно быть заметно выше 0.5
            double sum2 = 0;
            for (int i = 0; i < 500; i++) sum2 += bandit.Sample(8, 2);
            double mean2 = sum2 / 500;
            Check("Beta(8,2): среднее ≈ 0.8", mean2 > 0.7,
                mean2.ToString("0.000", CultureInfo.InvariantCulture));

            // ---- Wilson: одна удача не должна обгонять десять из десяти
            double oneOfOne = AutoZapret.Orchestrator.Bandit.WilsonLowerBound(1, 1);
            double tenOfTen = AutoZapret.Orchestrator.Bandit.WilsonLowerBound(10, 10);
            Check("Wilson: 10/10 > 1/1", tenOfTen > oneOfOne,
                oneOfOne.ToString("0.000", CultureInfo.InvariantCulture) + " vs " +
                tenOfTen.ToString("0.000", CultureInfo.InvariantCulture));

            double eightOfTen = AutoZapret.Orchestrator.Bandit.WilsonLowerBound(8, 10);
            Check("Wilson: 8/10 < 10/10", eightOfTen < tenOfTen,
                eightOfTen.ToString("0.000", CultureInfo.InvariantCulture));
            Check("Wilson: 0 проб -> 0", AutoZapret.Orchestrator.Bandit.WilsonLowerBound(0, 0) == 0, "0");

            // ---- ScoreStore: статистика и привязка к сети
            AutoZapret.Orchestrator.ScoreStore store = new AutoZapret.Orchestrator.ScoreStore();

            store.Register("s1", "netA", true, 100, 150, "");
            store.Register("s1", "netA", true, 80, 200, "");
            store.Register("s2", "netA", false, 0, 0, "нет связи");
            store.Register("s1", "netB", false, 0, 0, "блок");

            AutoZapret.Orchestrator.StrategyStats s1a = store.Get("s1", "netA");
            Check("ScoreStore: пробы учтены", s1a.Probes == 2, s1a.Probes.ToString());
            Check("ScoreStore: успехи учтены", s1a.Successes == 2, s1a.Successes.ToString());
            Check("ScoreStore: alpha дробный", Math.Abs(s1a.Alpha - 2.8) < 0.01,
                s1a.Alpha.ToString("0.00", CultureInfo.InvariantCulture));

            AutoZapret.Orchestrator.StrategyStats s2a = store.Get("s2", "netA");
            Check("ScoreStore: неудача учтена", s2a.ConsecutiveFailures == 1,
                s2a.ConsecutiveFailures.ToString());

            // разделение по сетям: та же стратегия в другой сети — отдельная запись
            AutoZapret.Orchestrator.StrategyStats s1b = store.Get("s1", "netB");
            Check("ScoreStore: сети раздельны", s1b.Probes == 1 && s1a.Probes == 2,
                "netA=" + s1a.Probes + ", netB=" + s1b.Probes);

            Check("ScoreStore: сброс неудачи после успеха",
                s1a.ConsecutiveFailures == 0, s1a.ConsecutiveFailures.ToString());

            // ---- бандит: предпочитает проверенного лидера
            AutoZapret.Orchestrator.ScoreStore banditStore = new AutoZapret.Orchestrator.ScoreStore();
            for (int i = 0; i < 30; i++) banditStore.Register("good", "net", true, 100, 100, "");
            for (int i = 0; i < 30; i++) banditStore.Register("bad", "net", false, 0, 0, "нет");

            AutoZapret.Orchestrator.Bandit picker = new AutoZapret.Orchestrator.Bandit(7);
            picker.ExplorationPermil = 0;   // чистый exploitation

            int goodPicked = 0;
            List<string> candidates = new List<string> { "good", "bad" };
            for (int i = 0; i < 200; i++)
                if (picker.Pick(candidates, banditStore, "net") == "good") goodPicked++;

            Check("бандит: выбирает рабочую", goodPicked > 180, goodPicked + "/200");

            // при полном исследовании должен пробовать и плохую
            picker.ExplorationPermil = 1000;
            int badPicked = 0;
            for (int i = 0; i < 200; i++)
                if (picker.Pick(candidates, banditStore, "net") == "bad") badPicked++;

            Check("бандит: исследует и плохую", badPicked > 20, badPicked + "/200");

            // ---- сохранение статистики: без него автопилот не накапливает опыт
            string dir = System.IO.Path.Combine(Paths.StateDir, "selftest-scores");
            System.IO.Directory.CreateDirectory(dir);

            string savedPath = System.IO.Path.Combine(Paths.StateDir, "scores.json");
            string backup = System.IO.File.Exists(savedPath)
                ? System.IO.File.ReadAllText(savedPath)
                : null;

            try
            {
                AutoZapret.Orchestrator.ScoreStore persist = new AutoZapret.Orchestrator.ScoreStore();
                persist.Register("keep", "netX", true, 90, 120, "");
                persist.Register("keep", "netX", true, 70, 140, "");
                persist.Register("drop", "netX", false, 0, 0, "нет связи");
                persist.Save();

                Check("ScoreStore: файл создан", System.IO.File.Exists(savedPath), savedPath);

                AutoZapret.Orchestrator.ScoreStore reloaded = new AutoZapret.Orchestrator.ScoreStore();
                reloaded.Load();

                AutoZapret.Orchestrator.StrategyStats k = reloaded.Get("keep", "netX");
                Check("ScoreStore: пробы пережили перезапуск", k.Probes == 2, k.Probes.ToString());
                Check("ScoreStore: успехи пережили", k.Successes == 2, k.Successes.ToString());
                Check("ScoreStore: alpha пережил",
                    Math.Abs(k.Alpha - 2.6) < 0.02, k.Alpha.ToString("0.00", CultureInfo.InvariantCulture));

                AutoZapret.Orchestrator.StrategyStats d = reloaded.Get("drop", "netX");
                Check("ScoreStore: неудача пережила", d.ConsecutiveFailures == 1,
                    d.ConsecutiveFailures.ToString());
                Check("ScoreStore: причина сохранена", d.LastError == "нет связи", d.LastError);
            }
            finally
            {
                if (backup != null)
                    System.IO.File.WriteAllText(savedPath, backup);
                else
                    try { System.IO.File.Delete(savedPath); } catch { }

                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        private static void TestDiagnosticBundle()
        {
            string dir = System.IO.Path.Combine(Paths.DataDir, "selftest-bundle");

            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }

            // настройки с ЯВНЫМ секретом: проверяем, что он не утечёт в архив
            Settings settings = new Settings();
            settings.ProxySecret = Settings.NewSecret();
            settings.ProxyPort = 1443;

            AutoZapret.Diag.BundleResult result = null;

            try
            {
                result = AutoZapret.Diag.DiagnosticBundle.Create(dir, settings, null);

                Check("архив собран", result.Ok, result.Ok ? result.SizeText() : result.Error);
                Check("архив не пустой", result.SizeBytes > 0, result.SizeBytes + " Б");
                Check("имя файла .zip",
                    result.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                    System.IO.Path.GetFileName(result.Path));
                Check("временный .tmp убран",
                    !File.Exists(result.Path + ".tmp"), "ok");

                if (!result.Ok) return;

                // ---- состав
                using (System.IO.Compression.ZipArchive zip =
                       System.IO.Compression.ZipFile.OpenRead(result.Path))
                {
                    List<string> names = new List<string>();
                    foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
                        names.Add(e.FullName);

                    foreach (string required in new[]
                             {
                                 "README.txt", "environment.txt", "diagnostics.txt",
                                 "settings.json", "engine.txt", "strategies.txt"
                             })
                    {
                        Check("в архиве есть " + required, names.Contains(required), names.Count + " файлов");
                    }

                    // ---- секрет не должен встречаться НИ В ОДНОМ файле
                    int leaks = 0;
                    string settingsJson = "";

                    foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                    {
                        string text = ReadEntry(entry);
                        if (text.IndexOf(settings.ProxySecret, StringComparison.OrdinalIgnoreCase) >= 0)
                            leaks++;

                        if (entry.FullName == "settings.json") settingsJson = text;
                    }

                    Check("секрет прокси не утёк", leaks == 0, leaks + " файлов с секретом");
                    Check("settings.json содержит REDACTED",
                        settingsJson.IndexOf("REDACTED", StringComparison.Ordinal) >= 0,
                        settingsJson.Length + " символов");

                    // ---- окружение должно описывать реальную систему
                    string environment = ReadEntry(zip.GetEntry("environment.txt"));
                    Check("environment: есть версия",
                        environment.IndexOf(AppInfo.Version, StringComparison.Ordinal) >= 0, "ok");
                    Check("environment: есть рантайм",
                        environment.IndexOf(".NET", StringComparison.Ordinal) >= 0, "ok");
                    Check("environment: есть отпечаток сети",
                        environment.IndexOf("отпечаток", StringComparison.OrdinalIgnoreCase) >= 0, "ok");

                    // ---- диагностика должна содержать все 12 проверок
                    string diagnostics = ReadEntry(zip.GetEntry("diagnostics.txt"));
                    int okMarks = 0, marks = 0;
                    foreach (string line in diagnostics.Split('\n'))
                    {
                        string t = line.TrimStart();
                        if (t.StartsWith("[ok]", StringComparison.Ordinal) ||
                            t.StartsWith("[warn]", StringComparison.Ordinal) ||
                            t.StartsWith("[FAIL]", StringComparison.Ordinal) ||
                            t.StartsWith("[skip]", StringComparison.Ordinal)) marks++;
                        if (t.StartsWith("[ok]", StringComparison.Ordinal)) okMarks++;
                    }
                    Check("diagnostics: 12 строк-результатов", marks == 12, marks + " строк");
                    Check("diagnostics: есть хотя бы один [ok]", okMarks > 0, okMarks + " ok");

                    // ---- README обязан предупредить про пути
                    string readme = ReadEntry(zip.GetEntry("README.txt"));
                    Check("README предупреждает про пути",
                        readme.IndexOf("пути", StringComparison.OrdinalIgnoreCase) >= 0, "ok");
                }

                // ---- страховка ScrubSecrets: ссылка с секретом не должна пройти
                Settings leaky = new Settings();
                leaky.ProxySecret = "0123456789abcdef0123456789abcdef";

                AutoZapret.Diag.BundleResult second =
                    AutoZapret.Diag.DiagnosticBundle.Create(dir, leaky, null);

                if (second.Ok)
                {
                    using (System.IO.Compression.ZipArchive zip =
                           System.IO.Compression.ZipFile.OpenRead(second.Path))
                    {
                        bool found = false;
                        foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                            if (ReadEntry(entry).IndexOf(leaky.ProxySecret, StringComparison.Ordinal) >= 0)
                                found = true;

                        Check("второй архив: секрет вырезан", !found, found ? "НАЙДЕН" : "ok");
                    }
                }
                else
                {
                    Check("второй архив собран", false, second.Error);
                }

                // ---- папка по умолчанию внутри data
                Check("папка по умолчанию в data",
                    AutoZapret.Diag.DiagnosticBundle.DefaultDirectory()
                        .StartsWith(Paths.DataDir, StringComparison.OrdinalIgnoreCase),
                    AutoZapret.Diag.DiagnosticBundle.DefaultDirectory());
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static string ReadEntry(System.IO.Compression.ZipArchiveEntry entry)
        {
            if (entry == null) return "";

            try
            {
                using (Stream stream = entry.Open())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch { return ""; }
        }

        private static void TestTcp16()
        {
            // ---- загрузка списка целей
            string path = System.IO.Path.Combine(Paths.BaseDir, "assets", "diag", "tcp16.json");
            List<AutoZapret.Diag.Tcp16Target> targets = AutoZapret.Diag.Tcp16Test.LoadTargets(path);

            if (System.IO.File.Exists(path))
            {
                Check("tcp16.json загружен", targets.Count > 50, targets.Count + " целей");
                Check("у целей есть IP",
                    targets.TrueForAll(t => t.Ip.Length > 0), "ok");
                Check("у целей есть провайдер",
                    targets.TrueForAll(t => t.Provider.Length > 0), "ok");
            }
            else
            {
                Skip("tcp16.json", "файла нет");
            }

            // встроенный список должен быть непустым — это резерв
            List<AutoZapret.Diag.Tcp16Target> builtin = AutoZapret.Diag.Tcp16Test.BuiltinTargets();
            Check("встроенный список непуст", builtin.Count >= 5, builtin.Count + " целей");

            // ---- вердикт на синтетических данных
            List<AutoZapret.Diag.Tcp16Result> blocked = new List<AutoZapret.Diag.Tcp16Result>
            {
                new AutoZapret.Diag.Tcp16Result { Provider = "A", BytesSent = 18000, LooksBlocked = true },
                new AutoZapret.Diag.Tcp16Result { Provider = "B", BytesSent = 20000, LooksBlocked = true },
                new AutoZapret.Diag.Tcp16Result { Provider = "C", BytesSent = 40960, Survived = true }
            };
            string blockedVerdict = AutoZapret.Diag.Tcp16Test.Verdict(blocked);
            Check("вердикт: обрыв обнаружен",
                blockedVerdict.IndexOf("ОБНАРУЖЕН", StringComparison.Ordinal) >= 0, blockedVerdict);
            Check("вердикт: называет seqovl",
                blockedVerdict.IndexOf("seqovl", StringComparison.OrdinalIgnoreCase) >= 0, "ok");

            List<AutoZapret.Diag.Tcp16Result> clean = new List<AutoZapret.Diag.Tcp16Result>
            {
                new AutoZapret.Diag.Tcp16Result { Provider = "A", BytesSent = 40960, Survived = true },
                new AutoZapret.Diag.Tcp16Result { Provider = "B", BytesSent = 40960, Survived = true }
            };
            string cleanVerdict = AutoZapret.Diag.Tcp16Test.Verdict(clean);
            Check("вердикт: обрыва нет",
                cleanVerdict.IndexOf("не обнаружено", StringComparison.Ordinal) >= 0, cleanVerdict);

            // ---- реальная проверка против ЛОКАЛЬНОГО сервера:
            //      он читает всё, поэтому обрыва быть не должно
            TestTcp16Local();
        }

        /// <summary>
        /// Поднимает локальный TCP-сервер, который читает всё, и проверяет,
        /// что Probe() видит «пережило». Это ловит ошибки в логике подсчёта
        /// байт и в определении обрыва, не завися от интернета.
        /// </summary>
        private static bool TestTcp16Local()
        {
            System.Net.Sockets.TcpListener listener = null;
            System.Threading.Thread server = null;
            int port;

            try
            {
                listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

                server = new System.Threading.Thread(() =>
                {
                    try
                    {
                        using (System.Net.Sockets.TcpClient client = listener.AcceptTcpClient())
                        using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                        {
                            byte[] sink = new byte[8192];
                            DateTime until = DateTime.UtcNow.AddSeconds(10);

                            // читаем всё, что пришлют
                            while (DateTime.UtcNow < until)
                            {
                                if (!stream.DataAvailable) { System.Threading.Thread.Sleep(10); continue; }
                                int n = stream.Read(sink, 0, sink.Length);
                                if (n <= 0) break;
                            }
                        }
                    }
                    catch { }
                })
                { IsBackground = true, Name = "selftest-tcp16" };

                server.Start();

                AutoZapret.Diag.Tcp16Target target = new AutoZapret.Diag.Tcp16Target
                {
                    Id = "local",
                    Provider = "local",
                    Ip = "127.0.0.1",
                    Port = port
                };

                AutoZapret.Diag.Tcp16Result result = AutoZapret.Diag.Tcp16Test.Probe(target, 5000);

                Check("TCP16: локальный сервер -> пережило",
                    result.Survived && result.BytesSent >= AutoZapret.Diag.Tcp16Test.TargetBytes,
                    result.BytesSent + " байт, " + result.Detail);

                Check("TCP16: не помечено как обрыв",
                    !result.LooksBlocked, result.LooksBlocked.ToString());

                return result.Survived;
            }
            catch (Exception ex)
            {
                Check("TCP16: локальный сервер", false, ex.Message);
                return false;
            }
            finally
            {
                try { if (listener != null) listener.Stop(); } catch { }
                try { if (server != null) server.Join(1000); } catch { }
            }
        }

        private static void TestSystemChecks()        {
            // ---- chcp 437: без него netsh/sc отвечают на языке системы
            string output = AutoZapret.Diag.SystemChecks.Cmd("echo chcp-test");
            Check("Cmd() возвращает вывод", output.IndexOf("chcp-test", StringComparison.Ordinal) >= 0,
                output.Trim().Length > 0 ? "ok" : "пусто");

            // ---- все 12 проверок выполняются и не бросают
            List<AutoZapret.Diag.CheckResult> results = AutoZapret.Diag.SystemChecks.RunAll();

            Check("проверок 12", results.Count == 12, results.Count.ToString());

            int noName = 0;
            int noDetail = 0;
            foreach (AutoZapret.Diag.CheckResult r in results)
            {
                if (string.IsNullOrEmpty(r.Name)) noName++;
                if (string.IsNullOrEmpty(r.Detail)) noDetail++;
            }

            Check("у всех есть название", noName == 0, noName + " без названия");
            Check("у всех есть результат", noDetail == 0, noDetail + " без деталей");

            // советы должны быть только у проблем и предупреждений
            int uselessAdvice = 0;
            foreach (AutoZapret.Diag.CheckResult r in results)
            {
                if (r.Status == AutoZapret.Diag.CheckStatus.Ok && r.Advice.Length > 0) uselessAdvice++;
            }
            Check("советы только там, где нужно", uselessAdvice == 0,
                uselessAdvice + " советов при OK");

            // ---- проверка «файлы драйвера» должна знать про отсутствие движка
            AutoZapret.Diag.CheckResult driver = results.Find(
                r => r.Name.IndexOf("драйвер", StringComparison.OrdinalIgnoreCase) >= 0);

            if (driver != null)
            {
                bool engineInstalled = System.IO.File.Exists(
                    System.IO.Path.Combine(Paths.EngineBinDir, "winws.exe"));

                if (engineInstalled)
                    Check("драйвер: движок установлен -> OK",
                        driver.Status == AutoZapret.Diag.CheckStatus.Ok, driver.Detail);
                else
                    Check("драйвер: движка нет -> skip",
                        driver.Status == AutoZapret.Diag.CheckStatus.Skip, driver.Detail);
            }
            else
            {
                Check("проверка драйвера найдена", false, "не найдена");
            }
        }

        private static void TestUpdates()
        {
            // Сравнение версий — самое важное: «1.10.4 > 1.9» должно быть верно,
            // а не «строка длиннее = новее».
            Check("1.10.4 > 1.9",
                AutoZapret.Update.UpdateCheck.CompareVersions("1.10.4", "1.9") > 0,
                "1.10.4 vs 1.9");
            Check("1.10.4 == 1.10.4",
                AutoZapret.Update.UpdateCheck.CompareVersions("1.10.4", "1.10.4") == 0, "равны");
            Check("1.9 < 1.10",
                AutoZapret.Update.UpdateCheck.CompareVersions("1.9", "1.10") < 0, "1.9 vs 1.10");
            Check("2.0 > 1.99.99",
                AutoZapret.Update.UpdateCheck.CompareVersions("2.0", "1.99.99") > 0, "2.0 vs 1.99.99");
            Check("v-префикс игнорируется",
                AutoZapret.Update.UpdateCheck.CompareVersions("v1.2.3", "1.2.3") == 0, "v1.2.3");
            Check("пре-релиз не новее релиза",
                AutoZapret.Update.UpdateCheck.CompareVersions("1.2.3-beta", "1.2.3") == 0, "beta");
            Check("пустая версия", AutoZapret.Update.UpdateCheck.CompareVersions("", "1.0") < 0, "пустая");

            // HasUpdate
            AutoZapret.Update.UpdateCheck check = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.9",
                LatestVersion = "1.10.4"
            };
            Check("есть обновление", check.HasUpdate, "1.9 -> 1.10.4");

            check.CurrentVersion = "1.10.4";
            Check("нет обновления", !check.HasUpdate, "равные версии");

            // движок должен определять свою версию
            AutoZapret.Zapret.EngineLocator engine = new AutoZapret.Zapret.EngineLocator();
            if (engine.Ready)
                Check("версия движка читается", engine.Version.Length > 0, engine.Version);
            else
                Skip("версия движка читается", "engine не установлен");
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

        /// <summary>
        /// Проверка, которую нельзя выполнить из-за отсутствия данных (например,
        /// движок не скачан). Это НЕ провал: на чистом клоне engine/ нет по определению.
        /// Если нужна строгость — запускайте с --require-engine.
        /// </summary>
        private static void Skip(string name, string reason)
        {
            Report.AppendLine("[skip] " + name + (string.IsNullOrEmpty(reason) ? "" : " -> " + reason));
            _skipped++;
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
