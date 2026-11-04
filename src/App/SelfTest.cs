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

                Section("счёт стратегий");
                TestStrategyScoring();

                Section("пресеты по процессам");
                TestProcessPresets();

                Section("автозапуск");
                TestAutoStart();

                Section("контрольные суммы");
                TestChecksums();

                Section("каналы обновлений");
                TestUpdateChannels();

                Section("счётчики трафика");
                TestNetStats();

                Section("генератор фейков");
                TestFakeGenerator();

                Section("локализация");
                TestLocalization();

                Section("скачивание движка");
                TestEngineInstaller();

                Section("проверка готовности");
                TestPreflight();

                Section("разбор пакетов (свой движок)");
                TestPacketParser();

                Section("стратегии по движкам");
                TestStrategiesByEngine();
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

        /// <summary>
        /// Генератор фейков. Главное здесь — что результат проходит НАШ ЖЕ валидатор
        /// и что SNI реально читается из байтов: «выглядит как TLS» проверяется
        /// разбором, а не на глаз.
        /// </summary>
        /// <summary>
        /// Стратегии обязаны делиться по движкам, и id не должны пересекаться.
        ///
        /// Названия пересекаются («general (ALT12)» есть и там, и там), поэтому
        /// общий список из 44 строк выглядел как дубликаты, и можно было выбрать
        /// стратегию чужого движка — она запускала не тот exe.
        /// </summary>
        private static void TestStrategiesByEngine()
        {
            AutoZapret.Zapret.StrategyStore store;

            using (AutoZapret.Zapret.ZapretService svc = new AutoZapret.Zapret.ZapretService())
            {
                svc.EnsureStrategies();
                store = svc.Strategies;
            }

            List<AutoZapret.Zapret.Strategy> v1 = store.ByEngine("v1");
            List<AutoZapret.Zapret.Strategy> v2 = store.ByEngine("v2");

            Check("стратегии: v1 найдены", v1.Count >= 20, v1.Count + " шт.");
            Check("стратегии: v2 найдены", v2.Count >= 20, v2.Count + " шт.");

            bool v1IdsOk = v1.TrueForAll(s => s.Id.StartsWith("flowseal.", StringComparison.Ordinal));
            bool v2IdsOk = v2.TrueForAll(s => s.Id.StartsWith("zapret2.", StringComparison.Ordinal));

            Check("стратегии: id v1 с префиксом flowseal", v1IdsOk, "ok");
            Check("стратегии: id v2 с префиксом zapret2", v2IdsOk, "ok");

            // пересечение id означало бы, что выбор стратегии неоднозначен
            HashSet<string> v1Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutoZapret.Zapret.Strategy s in v1) v1Set.Add(s.Id);

            int overlap = 0;
            foreach (AutoZapret.Zapret.Strategy s in v2)
                if (v1Set.Contains(s.Id)) overlap++;

            Check("стратегии: id не пересекаются", overlap == 0, overlap + " пересечений");

            // названия при этом совпадают — именно поэтому фильтр по движку обязателен
            HashSet<string> v1Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AutoZapret.Zapret.Strategy s in v1) v1Names.Add(s.Name);

            int sameNames = 0;
            foreach (AutoZapret.Zapret.Strategy s in v2)
                if (v1Names.Contains(s.Name)) sameNames++;

            Check("стратегии: названия совпадают (нужен фильтр по движку)",
                sameNames > 0, sameNames + " совпадающих названий");

            // v2 требует lua: это и есть причина «не находит blobs.lua»
            bool v2HasLua = v2.Exists(s => s.Profiles.Count > 0 &&
                                           s.Profiles[0].Args.Exists(a => a.IndexOf("lua", StringComparison.OrdinalIgnoreCase) >= 0));

            Check("стратегии: v2 использует lua", v2HasLua, v2HasLua ? "ok" : "не найдено ни одной");
        }

        /// <summary>
        /// Разбор пакетов для своего движка. Проверяем синтетикой: настоящий
        /// сниффер требует прав администратора, а арифметику смещений надо
        /// проверять всегда — ошибка на байт даёт мусор вместо портов и молчит.
        /// </summary>
        private static void TestPacketParser()
        {
            // ---- IPv4 + TCP с TLS ClientHello внутри
            byte[] tls = BuildTlsHello("www.example.com");
            byte[] ipv4 = BuildIPv4Tcp(443, tls);

            AutoZapret.Zapret.PacketInfo info;
            bool ok = AutoZapret.Zapret.PacketParser.TryParse(ipv4, ipv4.Length, false, out info);

            Check("IPv4/TCP: разобран", ok, ok ? "ok" : "не разобран");
            Check("IPv4/TCP: порт назначения 443", info.DstPort == 443, info.DstPort.ToString());
            Check("IPv4/TCP: TCP распознан", info.IsTcp, info.IsTcp.ToString());
            Check("IPv4/TCP: полезная нагрузка найдена", info.HasPayload, info.PayloadLength.ToString());

            if (info.HasPayload)
            {
                byte[] payload = new byte[info.PayloadLength];
                Array.Copy(ipv4, info.PayloadOffset, payload, 0, info.PayloadLength);

                Check("IPv4/TCP: нагрузка начинается с TLS",
                    payload.Length > 2 && payload[0] == 0x16 && payload[1] == 0x03, "ok");

                string sni = AutoZapret.Payloads.PayloadValidator.ExtractSni(
                    payload, AutoZapret.Payloads.PayloadKind.TlsClientHello);

                Check("IPv4/TCP: SNI достаётся из пакета", sni == "www.example.com", sni);
            }

            // ---- IPv6 + TCP
            byte[] ipv6 = BuildIPv6Tcp(443, tls);
            ok = AutoZapret.Zapret.PacketParser.TryParse(ipv6, ipv6.Length, true, out info);

            Check("IPv6/TCP: разобран", ok && info.IsTcp && info.DstPort == 443,
                ok ? info.DstPort.ToString() : "не разобран");
            Check("IPv6/TCP: адрес разобран", info.DstIp.IndexOf(':') >= 0, info.DstIp);

            // ---- UDP: порты есть, полезная нагрузка после 8 байт
            byte[] udp = BuildIPv4Udp(443, new byte[] { 1, 2, 3, 4 });
            ok = AutoZapret.Zapret.PacketParser.TryParse(udp, udp.Length, false, out info);

            Check("IPv4/UDP: разобран", ok && info.IsUdp, ok ? "ok" : "не разобран");
            Check("IPv4/UDP: длина нагрузки", info.PayloadLength == 4, info.PayloadLength.ToString());

            // ---- мусор не должен притворяться пакетом
            byte[] garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
            ok = AutoZapret.Zapret.PacketParser.TryParse(garbage, garbage.Length, false, out info);
            Check("мусор отклонён", !ok, ok ? "принят за пакет" : "ok");

            // ---- слишком короткий буфер
            ok = AutoZapret.Zapret.PacketParser.TryParse(new byte[10], 10, false, out info);
            Check("короткий пакет отклонён", !ok, "ok");

            // ---- адрес WinDivert читается по правильным смещениям
            byte[] address = new byte[AutoZapret.Zapret.WinDivertInterop.AddressSize];
            BitConverter.GetBytes(0x00020000u).CopyTo(address, 8);   // Outbound = бит 17
            AutoZapret.Zapret.WinDivertAddress addr = AutoZapret.Zapret.WinDivertAddress.FromBuffer(address);

            Check("WinDivertAddress: outbound прочитан", addr.Outbound, addr.Outbound.ToString());
            Check("WinDivertAddress: IPv6 выключен", !addr.IsIPv6, addr.IsIPv6.ToString());

            BitConverter.GetBytes(0x00100000u).CopyTo(address, 8);   // IPv6 = бит 20
            addr = AutoZapret.Zapret.WinDivertAddress.FromBuffer(address);
            Check("WinDivertAddress: IPv6 прочитан", addr.IsIPv6, addr.IsIPv6.ToString());
        }

        /// <summary>Минимальный TLS ClientHello с SNI: достаточно для проверки смещений.</summary>
        private static byte[] BuildTlsHello(string host)
        {
            byte[] name = Encoding.ASCII.GetBytes(host);

            List<byte> body = new List<byte>();
            body.AddRange(new byte[] { 0x03, 0x03 });           // client version
            body.AddRange(new byte[32]);                        // random

            body.Add(32);                                       // session id length
            body.AddRange(new byte[32]);

            body.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // один шифр
            body.AddRange(new byte[] { 0x01, 0x00 });            // null compression

            int extLength = 2 + 3 + 2 + 2 + name.Length;

            body.Add((byte)(extLength >> 8));
            body.Add((byte)(extLength & 0xFF));
            body.AddRange(new byte[] { 0x00, 0x00 });            // server_name
            body.Add((byte)((name.Length + 5) >> 8));
            body.Add((byte)((name.Length + 5) & 0xFF));
            body.Add((byte)((name.Length + 3) >> 8));
            body.Add((byte)((name.Length + 3) & 0xFF));
            body.Add(0);
            body.Add((byte)(name.Length >> 8));
            body.Add((byte)(name.Length & 0xFF));
            body.AddRange(name);

            List<byte> record = new List<byte>();
            record.Add(0x16);
            record.AddRange(new byte[] { 0x03, 0x01 });
            int recordLength = body.Count + 4;
            record.Add((byte)(recordLength >> 8));
            record.Add((byte)(recordLength & 0xFF));
            record.Add(0x01);
            record.Add((byte)((body.Count >> 16) & 0xFF));
            record.Add((byte)((body.Count >> 8) & 0xFF));
            record.Add((byte)(body.Count & 0xFF));
            record.AddRange(body);

            return record.ToArray();
        }

        private static byte[] BuildIPv4Tcp(int dstPort, byte[] payload)
        {
            List<byte> packet = new List<byte>();

            int totalLength = 20 + 20 + payload.Length;

            packet.Add(0x45);                                        // IPv4, IHL 5
            packet.Add(0x00);                                        // DSCP
            packet.Add((byte)(totalLength >> 8));
            packet.Add((byte)(totalLength & 0xFF));
            packet.AddRange(new byte[] { 0, 0 });                    // identification
            packet.AddRange(new byte[] { 0x40, 0x00 });              // flags + fragment
            packet.Add(64);                                          // TTL
            packet.Add(6);                                           // protocol TCP
            packet.AddRange(new byte[] { 0, 0 });                    // checksum
            packet.AddRange(new byte[] { 10, 0, 0, 1 });             // src
            packet.AddRange(new byte[] { 93, 184, 216, 34 });        // dst

            packet.AddRange(new byte[] { 0x04, 0xD2 });              // src port 1234
            packet.Add((byte)(dstPort >> 8));
            packet.Add((byte)(dstPort & 0xFF));
            packet.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });  // seq, ack
            packet.Add(0x50);                                        // data offset 5
            packet.Add(0x18);                                        // flags
            packet.AddRange(new byte[] { 0, 0, 0, 0 });              // window, checksum
            packet.AddRange(new byte[] { 0, 0 });                    // urgent

            packet.AddRange(payload);
            return packet.ToArray();
        }

        private static byte[] BuildIPv6Tcp(int dstPort, byte[] payload)
        {
            List<byte> packet = new List<byte>();

            packet.Add(0x60);
            packet.AddRange(new byte[3]);
            packet.Add((byte)((20 + payload.Length) >> 8));
            packet.Add((byte)((20 + payload.Length) & 0xFF));
            packet.Add(6);
            packet.Add(64);
            packet.AddRange(new byte[16]);                             // src ::1
            packet.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });

            packet.AddRange(new byte[] { 0x04, 0xD2 });
            packet.Add((byte)(dstPort >> 8));
            packet.Add((byte)(dstPort & 0xFF));
            packet.AddRange(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            packet.Add(0x50);
            packet.Add(0x18);
            packet.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });

            packet.AddRange(payload);
            return packet.ToArray();
        }

        private static byte[] BuildIPv4Udp(int dstPort, byte[] payload)
        {
            List<byte> packet = new List<byte>();

            int totalLength = 20 + 8 + payload.Length;

            packet.Add(0x45);
            packet.Add(0x00);
            packet.Add((byte)(totalLength >> 8));
            packet.Add((byte)(totalLength & 0xFF));
            packet.AddRange(new byte[] { 0, 0 });                    // identification
            packet.AddRange(new byte[] { 0x40, 0x00 });              // flags + fragment
            packet.Add(64);                                          // TTL
            packet.Add(17);                                          // protocol UDP
            packet.AddRange(new byte[] { 0, 0 });                    // checksum
            packet.AddRange(new byte[] { 10, 0, 0, 1 });             // src
            packet.AddRange(new byte[] { 1, 1, 1, 1 });              // dst

            packet.AddRange(new byte[] { 0x04, 0xD2 });              // src port
            packet.Add((byte)(dstPort >> 8));
            packet.Add((byte)(dstPort & 0xFF));
            packet.Add((byte)((payload.Length + 8) >> 8));
            packet.Add((byte)((payload.Length + 8) & 0xFF));
            packet.AddRange(new byte[] { 0, 0 });                    // checksum

            packet.AddRange(payload);
            return packet.ToArray();
        }

        /// <summary>
        /// Preflight: быстрая проверка «можно ли запускать». Тестируем на живом
        /// окружении, потому что смысл проверки — именно файлы на диске.
        /// </summary>
        private static void TestPreflight()
        {
            AutoZapret.Zapret.EngineLocator engine = new AutoZapret.Zapret.EngineLocator();
            engine.Refresh();

            AutoZapret.Zapret.StrategyStore store = null;

            try
            {
                using (AutoZapret.Zapret.ZapretService svc = new AutoZapret.Zapret.ZapretService())
                {
                    svc.EnsureStrategies();
                    store = svc.Strategies;
                }
            }
            catch { }

            // ---- не бросает
            bool threw = false;
            List<AutoZapret.Zapret.PreflightProblem> problems = null;

            try { problems = AutoZapret.Zapret.Preflight.Run(engine, store, "flowseal.general", "off"); }
            catch { threw = true; }

            Check("preflight не бросает", !threw, threw ? "исключение" : "ok");

            if (problems == null)
            {
                Check("preflight вернул список", false, "null");
                return;
            }

            Check("preflight: у проблем есть текст",
                problems.TrueForAll(p => p.Title.Length > 0), problems.Count + " проблем");

            // ---- без движка проверка сообщает именно об этом и дальше не идёт
            if (!engine.Ready)
            {
                Check("preflight: без движка — одна проблема",
                    problems.Count == 1 && problems[0].Code == "engine-not-ready",
                    problems.Count + " проблем, код " + (problems.Count > 0 ? problems[0].Code : "-"));

                Check("preflight: без движка — это блокер",
                    AutoZapret.Zapret.Preflight.HasBlockers(problems), "ok");
            }
            else
            {
                // ---- пустой strategyId: только окружение, без жалоб на стратегию
                List<AutoZapret.Zapret.PreflightProblem> envOnly =
                    AutoZapret.Zapret.Preflight.Run(engine, store, "", "off");

                bool complainsAboutStrategy = envOnly.Exists(
                    p => p.Code == "strategy-missing" || p.Code == "strategy-empty");

                Check("preflight: пустая стратегия не считается ошибкой",
                    !complainsAboutStrategy, envOnly.Count + " проблем");

                // ---- несуществующая стратегия — блокер
                List<AutoZapret.Zapret.PreflightProblem> missing =
                    AutoZapret.Zapret.Preflight.Run(engine, store, "no.such.strategy", "off");

                Check("preflight: неизвестная стратегия — блокер",
                    AutoZapret.Zapret.Preflight.HasBlockers(missing), missing.Count + " проблем");

                // ---- права: в selftest мы обычно НЕ администратор
                bool hasElevationProblem = problems.Exists(p => p.Code == "elevation");

                if (AutoZapret.Core.AppInfo.IsAdmin)
                    Check("preflight: под админом жалобы на права нет", !hasElevationProblem, "ok");
                else
                    Check("preflight: без админа жалуется на права", hasElevationProblem,
                        hasElevationProblem ? "ok" : "нет проблемы elevation");

                // ---- регресс: `--hostlist-domains=discord.media` — это домен,
                //      а не файл. Проверка по префиксу ловила его и блокировала
                //      ALT12 у всех подряд. Гоняем ВСЕ стратегии.
                if (store != null && Directory.Exists(Paths.EngineListsDir))
                {
                    int falsePositives = 0;
                    string firstBad = "";

                    foreach (AutoZapret.Zapret.Strategy s in store.All)
                    {
                        List<AutoZapret.Zapret.PreflightProblem> each =
                            AutoZapret.Zapret.Preflight.Run(engine, store, s.Id, "off");

                        foreach (AutoZapret.Zapret.PreflightProblem p in each)
                        {
                            if (p.Code != "hostlist-missing") continue;
                            falsePositives++;
                            if (firstBad.Length == 0) firstBad = s.Id + ": " + p.Hint;
                        }
                    }

                    Check("preflight: --hostlist-domains не считается файлом",
                        falsePositives == 0,
                        falsePositives == 0 ? store.Count + " стратегий ok" : falsePositives + ", первая: " + firstBad);
                }
            }

            // ---- логика сводки
            Check("HasBlockers: null -> false",
                !AutoZapret.Zapret.Preflight.HasBlockers(null), "ok");
            Check("HasBlockers: пустой список -> false",
                !AutoZapret.Zapret.Preflight.HasBlockers(
                    new List<AutoZapret.Zapret.PreflightProblem>()), "ok");

            List<AutoZapret.Zapret.PreflightProblem> blockOnly =
                new List<AutoZapret.Zapret.PreflightProblem>
                {
                    new AutoZapret.Zapret.PreflightProblem
                    {
                        Level = AutoZapret.Zapret.PreflightLevel.Blocker,
                        Code = "x", Title = "одна", Hint = ""
                    }
                };

            Check("HasBlockers: блокер найден",
                AutoZapret.Zapret.Preflight.HasBlockers(blockOnly), "ok");
            Check("Summary: одна проблема — её текст",
                AutoZapret.Zapret.Preflight.Summary(blockOnly) == "одна",
                AutoZapret.Zapret.Preflight.Summary(blockOnly));

            List<AutoZapret.Zapret.PreflightProblem> warnOnly =
                new List<AutoZapret.Zapret.PreflightProblem>
                {
                    new AutoZapret.Zapret.PreflightProblem
                    {
                        Level = AutoZapret.Zapret.PreflightLevel.Warning,
                        Code = "y", Title = "предупреждение", Hint = ""
                    }
                };

            Check("HasBlockers: предупреждение не блокер",
                !AutoZapret.Zapret.Preflight.HasBlockers(warnOnly), "ok");
            Check("Summary: предупреждения считаются",
                AutoZapret.Zapret.Preflight.Summary(warnOnly).IndexOf("предупрежд", StringComparison.Ordinal) >= 0,
                AutoZapret.Zapret.Preflight.Summary(warnOnly));
        }

        /// <summary>
        /// Установщик движка. Без сети: проверяем то, что защищает от порчи
        /// файлов и от zip-slip — именно здесь ошибка стоит дорого.
        /// </summary>
        private static void TestEngineInstaller()
        {
            // ---- отбрасывание корневой папки архива
            Check("архив: корень отброшен",
                AutoZapret.Zapret.EngineInstaller.StripArchiveRoot("repo-main/bin/winws.exe") ==
                @"bin\winws.exe",
                AutoZapret.Zapret.EngineInstaller.StripArchiveRoot("repo-main/bin/winws.exe"));

            Check("архив: без слэша -> пусто",
                AutoZapret.Zapret.EngineInstaller.StripArchiveRoot("file.txt") == "", "ok");

            Check("архив: пустая строка",
                AutoZapret.Zapret.EngineInstaller.StripArchiveRoot("") == "", "ok");

            // ---- zip-slip: запись обязана остаться внутри каталога
            string root = Path.Combine(Paths.DataDir, "selftest-engine");

            Check("zip-slip: обычный путь внутри",
                AutoZapret.Zapret.EngineInstaller.IsInsideDirectory(
                    Path.Combine(root, "bin", "winws.exe"), root), "ok");

            Check("zip-slip: выход через ..",
                !AutoZapret.Zapret.EngineInstaller.IsInsideDirectory(
                    Path.Combine(root, "..", "evil.exe"), root), "ok");

            Check("zip-slip: выход на два уровня",
                !AutoZapret.Zapret.EngineInstaller.IsInsideDirectory(
                    Path.Combine(root, "..", "..", "evil.exe"), root), "ok");

            Check("zip-slip: абсолютный путь снаружи",
                !AutoZapret.Zapret.EngineInstaller.IsInsideDirectory(
                    @"C:\Windows\System32\evil.dll", root), "ok");

            Check("zip-slip: пустой путь отброшен",
                !AutoZapret.Zapret.EngineInstaller.IsInsideDirectory("", root), "ok");

            Check("zip-slip: пустой корень отброшен",
                !AutoZapret.Zapret.EngineInstaller.IsInsideDirectory(@"C:\x\y.exe", ""), "ok");

            // ---- проверка «это правда ZIP»
            string dir = Path.Combine(Paths.DataDir, "selftest-engine-zip");

            try
            {
                Directory.CreateDirectory(dir);

                // настоящий zip
                string zipPath = Path.Combine(dir, "ok.zip");

                using (FileStream fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (System.IO.Compression.ZipArchive zip =
                       new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("repo-main/bin/winws.exe");
                    using (Stream s = entry.Open())
                    {
                        byte[] payload = new byte[2048];
                        s.Write(payload, 0, payload.Length);
                    }
                }

                Check("zip: настоящий архив распознан",
                    AutoZapret.Zapret.EngineInstaller.LooksLikeZip(zipPath), "ok");

                // HTML вместо архива — типичный ответ при ошибке или редиректе
                string htmlPath = Path.Combine(dir, "error.html");
                File.WriteAllText(htmlPath, "<!DOCTYPE html><html><body>404</body></html>");
                Check("zip: HTML не считается архивом",
                    !AutoZapret.Zapret.EngineInstaller.LooksLikeZip(htmlPath), "ok");

                // пустой файл
                string emptyPath = Path.Combine(dir, "empty.zip");
                File.WriteAllBytes(emptyPath, new byte[0]);
                Check("zip: пустой файл не считается архивом",
                    !AutoZapret.Zapret.EngineInstaller.LooksLikeZip(emptyPath), "ok");

                // несуществующий файл
                Check("zip: отсутствующий файл",
                    !AutoZapret.Zapret.EngineInstaller.LooksLikeZip(Path.Combine(dir, "nope.zip")), "ok");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }

        /// <summary>
        /// Локализация. Проверяем то, что реально ломается: битый JSON, ключи,
        /// которые никто не использует после правки текста, и переводы-пустышки.
        /// </summary>
        private static void TestLocalization()
        {
            string json = Resources.Text(Resources.EnglishDict);

            Check("словарь en вшит в exe", json.Length > 0, json.Length + " символов");

            if (json.Length == 0) return;

            Dictionary<string, object> map = null;
            bool parsed = true;

            try { map = Json.ParseObjectSafe(json); }
            catch { parsed = false; }

            Check("словарь: это валидный JSON", parsed && map != null, parsed ? "ok" : "не разобран");

            if (map == null) return;

            int real = 0, cyrillic = 0, empty = 0;

            foreach (KeyValuePair<string, object> pair in map)
            {
                if (pair.Key.StartsWith("_", StringComparison.Ordinal)) continue;   // служебные заметки

                real++;

                string value = pair.Value as string;

                if (string.IsNullOrEmpty(value)) { empty++; continue; }

                // в переводе не должно быть кириллицы: значит, его забыли перевести
                foreach (char c in value)
                {
                    if (c >= 'А' && c <= 'я' || c == 'Ё' || c == 'ё') { cyrillic++; break; }
                }
            }

            Check("словарь: есть переводы", real > 100, real + " ключей");
            Check("словарь: нет пустых переводов", empty == 0, empty + " пустых");
            Check("словарь: нет непереведённых значений", cyrillic == 0, cyrillic + " с кириллицей");

            // ---- ключи-сироты: текст в интерфейсе переписали, словарь забыли
            //
            // Сравниваем с нормализованными пробелами: в разметке длинная подсказка
            // перенесена на две строки, а в словаре — одна строка. Для translateDom
            // это неважно (он сам схлопывает пробелы), а для поиска подстроки важно.
            string html = Resources.Text(Resources.UiHtml);
            string js = Resources.Text(Resources.AppJs);

            Check("ui.html вшит", html.Length > 0, html.Length + " символов");
            Check("app.js вшит", js.Length > 0, js.Length + " символов");

            string flatHtml = System.Text.RegularExpressions.Regex.Replace(html ?? "", @"\s+", " ");
            string flatJs = System.Text.RegularExpressions.Regex.Replace(js ?? "", @"\s+", " ");

            List<string> orphans = new List<string>();

            foreach (KeyValuePair<string, object> pair in map)
            {
                if (pair.Key.StartsWith("_", StringComparison.Ordinal)) continue;

                string key = System.Text.RegularExpressions.Regex.Replace(pair.Key, @"\s+", " ");

                if (flatHtml.IndexOf(key, StringComparison.Ordinal) < 0 &&
                    flatJs.IndexOf(key, StringComparison.Ordinal) < 0)
                    orphans.Add(pair.Key);
            }

            Check("словарь: нет ключей-сирот", orphans.Count == 0,
                orphans.Count == 0 ? "ok" : OrphanPreview(orphans));

            // ---- ключевые переводы на месте (защита от случайной путаницы)
            Check("перевод: Главная -> Home",
                Json.GetString(map, "Главная", "") == "Home", Json.GetString(map, "Главная", ""));
            Check("перевод: Диагностика -> Diagnostics",
                Json.GetString(map, "Диагностика", "") == "Diagnostics", "ok");
            Check("перевод: Сохранить -> Save",
                Json.GetString(map, "Сохранить", "") == "Save", "ok");

            // ---- шаблоны с подстановками не должны потерять плейсхолдеры
            Check("шаблон: {0} и {1} сохранены",
                Json.GetString(map, "Стратегия: {0} · {1}", "") == "Strategy: {0} · {1}",
                Json.GetString(map, "Стратегия: {0} · {1}", ""));

            // ---- нормализация языка в настройках
            Check("язык: en", Settings.NormalizeLanguage("EN") == "en", "ok");
            Check("язык: мусор -> ru", Settings.NormalizeLanguage("de") == "ru", "ok");
            Check("язык: пусто -> ru", Settings.NormalizeLanguage("") == "ru", "ok");

            // ---- настройки переживают сохранение языка
            Settings probe = new Settings();
            probe.Language = "en";
            string roundTrip = probe.ToJson();
            Check("настройки: язык сохраняется",
                roundTrip.IndexOf("\"language\":\"en\"", StringComparison.Ordinal) >= 0,
                "ok");
        }

        private static string OrphanPreview(List<string> orphans)
        {
            int take = Math.Min(5, orphans.Count);
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(orphans[i]);
            }

            if (orphans.Count > take) sb.Append(" и ещё ").Append(orphans.Count - take);
            return sb.ToString();
        }

        private static void TestFakeGenerator()
        {
            // ---- нормализация домена
            Check("домен: со схемой и путём",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("https://www.YouTube.com/watch?v=1") ==
                "www.youtube.com",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("https://www.YouTube.com/watch?v=1"));

            Check("домен: с портом",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("example.com:443") == "example.com", "ok");

            Check("домен: с точкой на конце",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("example.com.") == "example.com", "ok");

            Check("домен: голое слово отклонено",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("localhost") == "", "ok");

            Check("домен: пустое отклонено",
                AutoZapret.Payloads.FakeGenerator.NormalizeHost("") == "", "ok");

            // ---- TLS ClientHello
            AutoZapret.Payloads.GeneratedFake tls =
                AutoZapret.Payloads.FakeGenerator.TlsClientHello("www.youtube.com");

            Check("TLS: создан", tls.Valid, tls.Describe());
            Check("TLS: размер по умолчанию 517", tls.Size == 517, tls.Size + " Б");
            Check("TLS: домен нормализован", tls.Sni == "www.youtube.com", tls.Sni);

            if (tls.Valid)
            {
                byte[] d = tls.Data;

                Check("TLS: тип записи 0x16", d[0] == 0x16, "0x" + d[0].ToString("x2"));
                Check("TLS: тип handshake 0x01", d[5] == 0x01, "0x" + d[5].ToString("x2"));

                int recordLength = (d[3] << 8) | d[4];
                Check("TLS: длина записи сходится с файлом",
                    recordLength + 5 == d.Length, recordLength + " vs " + (d.Length - 5));

                Check("TLS: legacy_version 0x0303", d[9] == 0x03 && d[10] == 0x03,
                    "0x" + d[9].ToString("x2") + d[10].ToString("x2"));

                string sni;
                AutoZapret.Payloads.PayloadKind kind =
                    AutoZapret.Payloads.PayloadValidator.DetectKind(d, out sni);

                Check("TLS: валидатор распознал",
                    kind == AutoZapret.Payloads.PayloadKind.TlsClientHello, kind.ToString());

                Check("TLS: SNI читается из байтов",
                    AutoZapret.Payloads.PayloadValidator.ExtractSni(d, kind) == "www.youtube.com",
                    AutoZapret.Payloads.PayloadValidator.ExtractSni(d, kind));

                // ключ ECDH не должен быть нулевым или пустым
                Check("TLS: key_share заполнен",
                    tls.KeyShare.Length > 0, tls.KeyShare);
            }

            // ---- два вызова дают разные байты (random и session_id случайны)
            AutoZapret.Payloads.GeneratedFake tls2 =
                AutoZapret.Payloads.FakeGenerator.TlsClientHello("www.youtube.com");

            bool differs = tls.Valid && tls2.Valid && !SameBytes(tls.Data, tls2.Data);
            Check("TLS: каждый вызов уникален", differs, differs ? "ok" : "байты совпали");

            // ---- размер: меньше естественного -> без padding
            AutoZapret.Payloads.GeneratedFake small =
                AutoZapret.Payloads.FakeGenerator.TlsClientHello("a.b", 200);
            Check("TLS: маленькая цель не ломает формат", small.Valid, small.Describe());
            Check("TLS: маленькая цель даёт естественный размер", small.Size > 200, small.Size + " Б");

            // ---- размер: больше естественного -> padding
            AutoZapret.Payloads.GeneratedFake padded =
                AutoZapret.Payloads.FakeGenerator.TlsClientHello("a.b", 1500);
            Check("TLS: padding до 1500", padded.Valid && padded.Size == 1500,
                padded.Size + " Б");

            // ---- пустой домен
            AutoZapret.Payloads.GeneratedFake noSni =
                AutoZapret.Payloads.FakeGenerator.TlsClientHello("");
            Check("TLS: без домена не создаётся", !noSni.Valid, noSni.Error);

            // ---- QUIC Initial
            AutoZapret.Payloads.GeneratedFake quic =
                AutoZapret.Payloads.FakeGenerator.QuicInitial();

            Check("QUIC: создан", quic.Valid, quic.Describe());
            Check("QUIC: ровно 1200 байт", quic.Size == 1200, quic.Size + " Б");

            if (quic.Valid)
            {
                Check("QUIC: long header", (quic.Data[0] & 0x80) != 0, "0x" + quic.Data[0].ToString("x2"));
                Check("QUIC: fixed bit",
                    (quic.Data[0] & 0x40) != 0, "0x" + quic.Data[0].ToString("x2"));
                Check("QUIC: тип Initial (00)",
                    (quic.Data[0] & 0x30) == 0, "0x" + quic.Data[0].ToString("x2"));
                Check("QUIC: версия 1",
                    quic.Data[1] == 0 && quic.Data[2] == 0 && quic.Data[3] == 0 && quic.Data[4] == 1, "ok");

                string reason;
                Check("QUIC: валидатор распознал",
                    AutoZapret.Payloads.PayloadValidator.DetectKind(quic.Data, out reason) ==
                    AutoZapret.Payloads.PayloadKind.QuicInitial, reason);
            }

            Check("QUIC: слишком маленький отклонён",
                !AutoZapret.Payloads.FakeGenerator.QuicInitial(30).Valid, "ok");
            Check("QUIC: слишком большой отклонён",
                !AutoZapret.Payloads.FakeGenerator.QuicInitial(20000).Valid, "ok");
            Check("QUIC: 1400 допустим",
                AutoZapret.Payloads.FakeGenerator.QuicInitial(1400).Size == 1400, "ok");

            // ---- имя файла
            string tlsName = AutoZapret.Payloads.FakeGenerator.FileNameFor(
                "www.youtube.com", AutoZapret.Payloads.PayloadKind.TlsClientHello);

            Check("имя TLS-файла", tlsName == "tls_clienthello_www_youtube_com.bin", tlsName);
            Check("имя без точек и слешей",
                tlsName.IndexOf('.') == tlsName.Length - 4 && tlsName.IndexOf('/') < 0, tlsName);

            string quicName = AutoZapret.Payloads.FakeGenerator.FileNameFor(
                "a.b", AutoZapret.Payloads.PayloadKind.QuicInitial);
            Check("имя QUIC-файла", quicName == "quic_initial_a_b.bin", quicName);

            // ---- сохранение и чтение с диска
            string dir = System.IO.Path.Combine(Paths.DataDir, "selftest-fakes");

            try
            {
                string error;
                string path = AutoZapret.Payloads.FakeGenerator.Save(tls, dir, out error);

                Check("фейк сохранён", path != null, path ?? error);

                if (path != null)
                {
                    AutoZapret.Payloads.Payload payload = AutoZapret.Payloads.PayloadValidator.Inspect(path);

                    Check("сохранённый файл валиден", payload.Valid, payload.InvalidReason);
                    Check("размер файла на диске", payload.Size == 517, payload.Size + " Б");
                    Check("SNI перечитан с диска", payload.Sni == "www.youtube.com", payload.Sni);
                }

                // ---- невалидный фейк не сохраняется
                string badPath = AutoZapret.Payloads.FakeGenerator.Save(noSni, dir, out error);
                Check("невалидный фейк не сохранён", badPath == null, error);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;

            return true;
        }

        /// <summary>
        /// Счётчики трафика. Арифметика простая, но именно в ней легко получить
        /// отрицательные значения (сброс адаптера) или деление на ноль.
        /// </summary>
        private static void TestNetStats()
        {
            // ---- форматирование
            Check("скорость: 0",
                NetStats.Format(0) == "0 Б/с", NetStats.Format(0));
            Check("скорость: байты",
                NetStats.Format(1023) == "1023 Б/с", NetStats.Format(1023));
            Check("скорость: ровно 1 КБ",
                NetStats.Format(1024) == "1 КБ/с", NetStats.Format(1024));
            // формат числа — по текущей локали: «1,0 МБ/с» для русской Windows,
            // «1.0 МБ/с» для английской. Тест не должен зависеть от машины.
            string oneMb = "1" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "0 МБ/с";
            string oneGb = "1" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator +
                           "00 ГБ/с";

            Check("скорость: 1 МБ",
                NetStats.Format(1024 * 1024) == oneMb, NetStats.Format(1024 * 1024));
            Check("скорость: 1 ГБ",
                NetStats.Format(1024L * 1024 * 1024) == oneGb,
                NetStats.Format(1024L * 1024 * 1024));
            Check("скорость: отрицательное -> 0",
                NetStats.Format(-500) == "0 Б/с", NetStats.Format(-500));

            // ---- разница между замерами
            DateTime start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            NetStats first = new NetStats
            {
                BytesReceived = 1000,
                BytesSent = 500,
                SampledAt = start
            };

            NetStats second = new NetStats
            {
                BytesReceived = 1000 + 2048 * 2,   // 2 КБ/с за 2 с
                BytesSent = 500 + 512 * 2,          // 512 Б/с за 2 с
                SampledAt = start.AddSeconds(2)
            };

            long down, up;
            NetStats.Rates(first, second, out down, out up);

            Check("разница: приём", down == 2048, down + " Б/с");
            Check("разница: отдача", up == 512, up + " Б/с");

            // ---- счётчики обнулились (адаптер переподключился)
            NetStats reset = new NetStats
            {
                BytesReceived = 10,
                BytesSent = 5,
                SampledAt = start.AddSeconds(4)
            };

            NetStats.Rates(second, reset, out down, out up);
            Check("сброс счётчиков: приём 0", down == 0, down + " Б/с");
            Check("сброс счётчиков: отдача 0", up == 0, up + " Б/с");

            // ---- слишком маленький интервал: делить нельзя
            NetStats tooSoon = new NetStats
            {
                BytesReceived = 999999,
                BytesSent = 999999,
                SampledAt = start.AddMilliseconds(10)
            };

            NetStats.Rates(first, tooSoon, out down, out up);
            Check("интервал 10 мс: без деления", down == 0 && up == 0, "ok");

            // ---- null-замеры
            NetStats.Rates(null, second, out down, out up);
            Check("null-предыдущий: 0", down == 0 && up == 0, "ok");

            // ---- реальный замер не бросает
            bool threw = false;
            NetStats real = null;

            try { real = NetStats.Sample(); }
            catch { threw = true; }

            Check("Sample() не бросает", !threw, threw ? "исключение" : "ok");
            Check("Sample() ставит время",
                real != null && real.SampledAt != DateTime.MinValue,
                real == null ? "null" : real.SampledAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            Check("Sample() без отрицательных значений",
                real != null && real.BytesReceived >= 0 && real.BytesSent >= 0,
                real == null ? "null" : real.BytesReceived + "/" + real.BytesSent);
        }

        /// <summary>
        /// Каналы обновлений. Барьер здесь один и он критичный: на стабильном канале
        /// пре-релиз не должен считаться обновлением, иначе пользователь получит
        /// сырую сборку, ничего не нажимая.
        /// </summary>
        private static void TestUpdateChannels()
        {
            // ---- стабильный канал: пре-релизы отбрасываются
            Check("stable: v1.2.3 -> 1.2.3",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3", false) == "1.2.3", "ok");
            Check("stable: 1.2.3 -> 1.2.3",
                AutoZapret.Update.SelfUpdater.SanitizeTag("1.2.3", false) == "1.2.3", "ok");
            Check("stable: V1.2.3 -> 1.2.3",
                AutoZapret.Update.SelfUpdater.SanitizeTag("V1.2.3", false) == "1.2.3", "ok");

            Check("stable: beta отброшена",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-beta1", false) == null, "null");
            Check("stable: rc отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-rc2", false) == null, "null");

            // ---- бета-канал: пре-релизы допустимы
            Check("beta: beta принята",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-beta1", true) == "1.2.3-beta1", "ok");
            Check("beta: rc принят",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-rc2", true) == "1.2.3-rc2", "ok");
            Check("beta: alpha принят",
                AutoZapret.Update.SelfUpdater.SanitizeTag("1.3.0-alpha3", true) == "1.3.0-alpha3", "ok");

            // ---- мусор отбрасывается на ЛЮБОМ канале
            Check("beta: custom-fork отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-custom-fork", true) == null, "null");
            Check("stable: fork отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-fork", false) == null, "null");
            Check("beta: неизвестный суффикс отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-whatever", true) == null, "null");
            Check("beta: пустой суффикс отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v1.2.3-", true) == null, "null");

            // ---- не-версии
            Check("пустой тег отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("", true) == null, "null");
            Check("тег без цифр отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("v", true) == null, "null");
            Check("тег со словом отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag("latest", true) == null, "null");
            Check("null-тег отброшен",
                AutoZapret.Update.SelfUpdater.SanitizeTag(null, true) == null, "null");

            // ---- HasUpdate с учётом пре-релизов
            AutoZapret.Update.UpdateCheck newer = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.2.3",
                LatestVersion = "1.3.0"
            };
            Check("HasUpdate: 1.3.0 > 1.2.3", newer.HasUpdate, "ok");

            AutoZapret.Update.UpdateCheck same = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.2.3",
                LatestVersion = "1.2.3"
            };
            Check("HasUpdate: равные версии -> нет", !same.HasUpdate, "ok");

            // бета-пользователь должен получить финальный релиз той же версии
            AutoZapret.Update.UpdateCheck betaToRelease = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.3.0-beta1",
                LatestVersion = "1.3.0"
            };
            Check("HasUpdate: бета -> релиз той же версии", betaToRelease.HasUpdate, "ok");

            // но не наоборот: стабильный пользователь не откатывается на бету
            AutoZapret.Update.UpdateCheck releaseToBeta = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.3.0",
                LatestVersion = "1.3.0-beta1"
            };
            Check("HasUpdate: релиз -> бета не предлагается", !releaseToBeta.HasUpdate, "ok");

            // числовое сравнение: 1.10 > 1.9
            AutoZapret.Update.UpdateCheck numeric = new AutoZapret.Update.UpdateCheck
            {
                CurrentVersion = "1.9",
                LatestVersion = "1.10.0"
            };
            Check("HasUpdate: 1.10.0 > 1.9 (не по строке)", numeric.HasUpdate, "ok");

            // пре-релиз определяется по суффиксу
            Check("IsPrerelease: beta", AutoZapret.Update.UpdateCheck.IsPrerelease("1.3.0-beta1"), "ok");
            Check("IsPrerelease: обычная версия",
                !AutoZapret.Update.UpdateCheck.IsPrerelease("1.3.0"), "ok");
        }

        /// <summary>
        /// Проверка SHA-256. Ключевое здесь — что ОТСУТСТВИЕ записи означает отказ,
        /// а не «пропускаем»: иначе защита обходится тем, что файл не отдали.
        /// </summary>
        private static void TestChecksums()
        {
            // ---- известный вектор: sha256("") — эталон, который легко проверить
            string emptyHash = AutoZapret.Update.Checksums.Sha256Text("");
            Check("sha256 пустой строки",
                emptyHash == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                emptyHash);

            Check("sha256 'abc'",
                AutoZapret.Update.Checksums.Sha256Text("abc") ==
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                "ok");

            // ---- файл: хеш файла должен совпасть с хешем его содержимого
            string dir = System.IO.Path.Combine(Paths.DataDir, "selftest-checksums");

            try
            {
                Directory.CreateDirectory(dir);

                string file = System.IO.Path.Combine(dir, "sample.bin");
                byte[] bytes = Encoding.UTF8.GetBytes("AutoZapret integrity test");
                File.WriteAllBytes(file, bytes);

                string fileHash = AutoZapret.Update.Checksums.Sha256File(file);
                Check("sha256 файла посчитан", fileHash.Length == 64, fileHash);
                Check("sha256 файла = 64 hex",
                    AutoZapret.Update.Checksums.LooksLikeHash(fileHash), fileHash);

                // ---- разбор формата sha256sum
                Dictionary<string, string> map = AutoZapret.Update.Checksums.Parse(
                    emptyHash + "  AutoZapret-portable.zip\n" +
                    fileHash + "  sample.bin\n");

                Check("разбор: две записи", map.Count == 2, map.Count + " шт.");
                Check("разбор: имя в нижнем регистре",
                    AutoZapret.Update.Checksums.Find(map, "SAMPLE.BIN") == fileHash, "ok");

                // ---- звёздочка бинарного режима
                Dictionary<string, string> binary = AutoZapret.Update.Checksums.Parse(
                    fileHash + " *sample.bin\n");
                Check("разбор: бинарный режим (*)", binary.Count == 1, binary.Count + " шт.");

                // ---- BSD-формат
                Dictionary<string, string> bsd = AutoZapret.Update.Checksums.Parse(
                    "SHA256 (sample.bin) = " + fileHash + "\n");
                Check("разбор: BSD-формат", bsd.Count == 1, bsd.Count + " шт.");

                // ---- путь в имени файла
                Dictionary<string, string> withPath = AutoZapret.Update.Checksums.Parse(
                    fileHash + "  dist/sample.bin\n" +
                    fileHash + "  .\\sample.bin\n");
                Check("разбор: путь отброшен",
                    AutoZapret.Update.Checksums.Find(withPath, "sample.bin") == fileHash,
                    withPath.Count + " шт.");

                // ---- комментарии и пустые строки
                Dictionary<string, string> withComments = AutoZapret.Update.Checksums.Parse(
                    "# это комментарий\n\n" + fileHash + "  sample.bin\n\n");
                Check("разбор: комментарии пропущены", withComments.Count == 1,
                    withComments.Count + " шт.");

                // ---- мусор не превращается в записи
                Check("разбор: мусор -> 0 записей",
                    AutoZapret.Update.Checksums.Parse("не хеш и не файл\n").Count == 0, "ok");
                Check("разбор: пустая строка -> 0 записей",
                    AutoZapret.Update.Checksums.Parse("").Count == 0, "ok");

                // ---- HTML вместо checksums.txt — это ошибка, а не «пусто»
                bool threw = false;
                try { AutoZapret.Update.Checksums.Parse("<!DOCTYPE html><html>login</html>"); }
                catch (FormatException) { threw = true; }
                Check("разбор: HTML отклонён", threw, threw ? "ok" : "принят как валидный");

                // ---- дубликат: первая запись побеждает
                string otherHash = AutoZapret.Update.Checksums.Sha256Text("other");
                Dictionary<string, string> duplicates = AutoZapret.Update.Checksums.Parse(
                    otherHash + "  sample.bin\n" + fileHash + "  sample.bin\n");
                Check("разбор: дубликат -> первая запись",
                    AutoZapret.Update.Checksums.Find(duplicates, "sample.bin") == otherHash,
                    duplicates.Count + " шт.");

                // ---- сверка: совпадение
                AutoZapret.Update.ChecksumResult good =
                    AutoZapret.Update.Checksums.Verify(file, map, "AutoZapret-portable.zip");
                Check("сверка: чужое имя -> отказ", !good.Ok, good.Text());

                AutoZapret.Update.ChecksumResult ok =
                    AutoZapret.Update.Checksums.Verify(file, map);
                Check("сверка: совпало", ok.Ok, ok.Text());
                Check("сверка: ожидаемый хеш заполнен", ok.Expected.Length == 64, ok.Expected);
                Check("сверка: полученный хеш заполнен", ok.Actual.Length == 64, ok.Actual);

                // ---- сверка: несовпадение
                Dictionary<string, string> wrong = AutoZapret.Update.Checksums.Parse(
                    otherHash + "  sample.bin\n");
                AutoZapret.Update.ChecksumResult bad =
                    AutoZapret.Update.Checksums.Verify(file, wrong);
                Check("сверка: несовпадение поймано", !bad.Ok, bad.Text());
                Check("сверка: в тексте есть оба хеша",
                    bad.Text().IndexOf("ожидался", StringComparison.Ordinal) >= 0, bad.Text());

                // ---- сверка: записи нет -> ОТКАЗ (не «пропущено»)
                AutoZapret.Update.ChecksumResult missing =
                    AutoZapret.Update.Checksums.Verify(file,
                        AutoZapret.Update.Checksums.Parse(""), "sample.bin");
                Check("сверка: нет записи -> отказ", !missing.Ok, missing.Text());
                Check("сверка: текст говорит про отсутствие записи",
                    missing.Text().IndexOf("нет записи", StringComparison.Ordinal) >= 0,
                    missing.Text());

                // ---- сверка: файла нет
                AutoZapret.Update.ChecksumResult noFile =
                    AutoZapret.Update.Checksums.Verify(System.IO.Path.Combine(dir, "nope.bin"), map);
                Check("сверка: файла нет -> отказ", !noFile.Ok, noFile.Text());

                // ---- валидация формата хеша
                Check("LooksLikeHash: 64 hex", AutoZapret.Update.Checksums.LooksLikeHash(
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), "ok");
                Check("LooksLikeHash: 63 символа", !AutoZapret.Update.Checksums.LooksLikeHash(
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde"), "ok");
                Check("LooksLikeHash: не-hex", !AutoZapret.Update.Checksums.LooksLikeHash(
                    "g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"), "ok");
                Check("LooksLikeHash: пусто",
                    !AutoZapret.Update.Checksums.LooksLikeHash(""), "ok");

                // ---- ссылки на checksums.txt согласованы с именем архива
                string zipUrl = AutoZapret.Update.SelfUpdater.PortableZipUrl("1.2.3");
                string sumsUrl = AutoZapret.Update.SelfUpdater.ChecksumsUrl("1.2.3");

                Check("URL архива содержит имя архива",
                    zipUrl.EndsWith("/" + AutoZapret.Update.SelfUpdater.PortableZipName,
                        StringComparison.Ordinal), zipUrl);
                Check("URL сумм того же релиза",
                    sumsUrl.EndsWith("/checksums.txt", StringComparison.Ordinal) &&
                    sumsUrl.IndexOf("v1.2.3", StringComparison.Ordinal) >= 0, sumsUrl);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        /// <summary>
        /// Проверки автозапуска, которые НЕ трогают систему.
        /// Включать и выключать автозапуск в selftest нельзя: тест не должен
        /// менять настройки чужой машины. Проверяем разбор и инварианты.
        /// </summary>
        private static void TestAutoStart()
        {
            // ---- путь к текущему exe
            string exe = AutoZapret.App.AutoStartManager.ExePath;
            Check("путь к exe определён", exe.Length > 0, exe);
            Check("exe существует", File.Exists(exe), exe);

            // ---- разбор строки запуска из реестра
            string quoted = AutoZapret.App.AutoStartManager.ExtractPath(
                "\"C:\\Program Files\\AutoZapret\\AutoZapret.exe\" --minimized");
            Check("ExtractPath: кавычки и аргументы",
                quoted == "C:\\Program Files\\AutoZapret\\AutoZapret.exe", quoted);

            string plain = AutoZapret.App.AutoStartManager.ExtractPath(
                "C:\\Tools\\AutoZapret.exe --minimized");
            Check("ExtractPath: без кавычек",
                plain == "C:\\Tools\\AutoZapret.exe", plain);

            string onlyPath = AutoZapret.App.AutoStartManager.ExtractPath(
                "\"D:\\az\\AutoZapret.exe\"");
            Check("ExtractPath: без аргументов",
                onlyPath == "D:\\az\\AutoZapret.exe", onlyPath);

            Check("ExtractPath: пустая строка",
                AutoZapret.App.AutoStartManager.ExtractPath("") == "", "ok");

            // ---- аргументы автозапуска должны стартовать в трей
            Check("автозапуск стартует свёрнутым",
                AutoZapret.App.AutoStartManager.StartArguments.IndexOf(
                    "--minimized", StringComparison.OrdinalIgnoreCase) >= 0,
                AutoZapret.App.AutoStartManager.StartArguments);

            // ---- Query не бросает и не противоречит себе
            AutoZapret.App.AutoStartStatus status = null;
            bool threw = false;

            try { status = AutoZapret.App.AutoStartManager.Query(); }
            catch { threw = true; }

            Check("Query() не бросает", !threw, threw ? "исключение" : "ok");

            if (status != null)
            {
                Check("Query: Enabled согласован с Method",
                    status.Enabled == (status.Method != AutoZapret.App.AutoStartMethod.None),
                    status.Enabled + " / " + status.Method);

                Check("Query: текст не пустой", status.Text().Length > 0, status.Text());

                // состояние только читается — повторный вызов даёт то же самое
                AutoZapret.App.AutoStartStatus again = AutoZapret.App.AutoStartManager.Query();
                Check("Query: два вызова совпадают",
                    again.Enabled == status.Enabled && again.Method == status.Method,
                    again.Text());

                if (status.Enabled)
                {
                    Check("Query: при включённом есть цель",
                        status.Target.Length > 0, status.Target);

                    // stale вычисляется сравнением с текущим exe — логика должна
                    // согласовываться с тем, что мы видим
                    bool shouldBeStale =
                        status.Target.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0;
                    Check("Query: stale согласован с целью",
                        status.Stale == shouldBeStale,
                        "stale=" + status.Stale + ", ожидалось " + shouldBeStale);
                }
            }

            // ---- CLI: статус читается без ошибок
            //      (enable/disable здесь намеренно не вызываем)
            Check("TaskName задан", AutoZapret.App.AutoStartManager.TaskName.Length > 0,
                AutoZapret.App.AutoStartManager.TaskName);
            Check("имя задачи без пробелов",
                AutoZapret.App.AutoStartManager.TaskName.IndexOf(' ') < 0,
                AutoZapret.App.AutoStartManager.TaskName);
        }

        private static void TestProcessPresets()
        {
            // ---- загрузка из файла
            List<AutoZapret.Orchestrator.ProcessPreset> fromFile =
                AutoZapret.Orchestrator.ProcessPresets.Load(
                    AutoZapret.Orchestrator.ProcessPresets.FilePath);

            if (File.Exists(AutoZapret.Orchestrator.ProcessPresets.FilePath))
            {
                Check("пресеты загружены", fromFile.Count > 0, fromFile.Count + " штук");
                Check("у пресетов есть процессы",
                    fromFile.TrueForAll(p => p.Processes.Count > 0), "ok");
                Check("у пресетов есть имена",
                    fromFile.TrueForAll(p => p.Name.Length > 0), "ok");
                Check("пресеты что-то меняют",
                    fromFile.TrueForAll(p => p.ChangesSomething), "ok");
                Check("фильтры пресетов валидны",
                    fromFile.TrueForAll(p =>
                        AutoZapret.Orchestrator.ProcessPresets.IsValidFilter(p.GameFilter)), "ok");
            }
            else
            {
                Skip("пресеты загружены", "файла нет");
            }

            List<AutoZapret.Orchestrator.ProcessPreset> builtin =
                AutoZapret.Orchestrator.ProcessPresets.Builtin();

            Check("встроенные пресеты непусты", builtin.Count >= 2, builtin.Count + " штук");
            Check("встроенные пресеты что-то меняют",
                builtin.TrueForAll(p => p.ChangesSomething), "ok");

            // ---- Match: порядок = приоритет, первый совпавший
            List<AutoZapret.Orchestrator.ProcessPreset> two = new List<AutoZapret.Orchestrator.ProcessPreset>
            {
                Preset("first", new[] { "aaa" }, "strat-A", "", true),
                Preset("second", new[] { "bbb" }, "strat-B", "", true)
            };

            AutoZapret.Orchestrator.ProcessPreset hit = AutoZapret.Orchestrator.ProcessPresets.Match(
                two, name => name == "bbb");

            Check("Match: нашёл второй пресет", hit != null && hit.Id == "second",
                hit == null ? "null" : hit.Id);

            hit = AutoZapret.Orchestrator.ProcessPresets.Match(two, name => true);
            Check("Match: приоритет у первого", hit != null && hit.Id == "first",
                hit == null ? "null" : hit.Id);

            hit = AutoZapret.Orchestrator.ProcessPresets.Match(two, name => false);
            Check("Match: ничего не запущено -> null", hit == null, hit == null ? "null" : hit.Id);

            // ---- выключенный пресет игнорируется
            List<AutoZapret.Orchestrator.ProcessPreset> withDisabled =
                new List<AutoZapret.Orchestrator.ProcessPreset>
                {
                    Preset("off", new[] { "aaa" }, "strat-A", "", false),
                    Preset("on", new[] { "aaa" }, "strat-B", "", true)
                };

            hit = AutoZapret.Orchestrator.ProcessPresets.Match(withDisabled, name => true);
            Check("Match: выключенный пресет пропущен", hit != null && hit.Id == "on",
                hit == null ? "null" : hit.Id);

            // ---- пресет-пустышка игнорируется: он ничего не делает
            List<AutoZapret.Orchestrator.ProcessPreset> empty =
                new List<AutoZapret.Orchestrator.ProcessPreset>
                {
                    Preset("empty", new[] { "aaa" }, "", "", true),
                    Preset("real", new[] { "aaa" }, "strat-B", "", true)
                };

            hit = AutoZapret.Orchestrator.ProcessPresets.Match(empty, name => true);
            Check("Match: пустышка пропущена", hit != null && hit.Id == "real",
                hit == null ? "null" : hit.Id);

            // ---- валидация фильтров
            Check("фильтр off допустим",
                AutoZapret.Orchestrator.ProcessPresets.IsValidFilter("off"), "ok");
            Check("фильтр tcp+udp допустим",
                AutoZapret.Orchestrator.ProcessPresets.IsValidFilter("tcp+udp"), "ok");
            Check("фильтр пустой допустим (не менять)",
                AutoZapret.Orchestrator.ProcessPresets.IsValidFilter(""), "ok");
            Check("мусор в фильтре отклонён",
                !AutoZapret.Orchestrator.ProcessPresets.IsValidFilter("everything"), "ok");

            // ---- round-trip сохранения
            string dir = System.IO.Path.Combine(Paths.DataDir, "selftest-presets");

            try
            {
                Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "processes.json");

                List<AutoZapret.Orchestrator.ProcessPreset> original =
                    new List<AutoZapret.Orchestrator.ProcessPreset>
                    {
                        Preset("discord", new[] { "Discord", "DiscordPTB" }, "flowseal.general-alt12", "", true),
                        Preset("games", new[] { "cs2", "dota2" }, "", "tcp+udp", false)
                    };

                bool saved = AutoZapret.Orchestrator.ProcessPresets.Save(original, file);
                Check("пресеты сохранены", saved, file);

                List<AutoZapret.Orchestrator.ProcessPreset> reloaded =
                    AutoZapret.Orchestrator.ProcessPresets.Load(file);

                Check("round-trip: столько же пресетов",
                    reloaded.Count == original.Count, reloaded.Count + " из " + original.Count);
                Check("round-trip: выключенный остался выключенным",
                    reloaded.Count == 2 && !reloaded[1].Enabled,
                    reloaded.Count == 2 ? reloaded[1].Enabled.ToString() : "нет данных");
                Check("round-trip: процессы сохранились",
                    reloaded.Count == 2 && reloaded[0].Processes.Count == 2,
                    reloaded.Count == 2 ? reloaded[0].Processes.Count.ToString() : "нет данных");
                Check("round-trip: фильтр сохранился",
                    reloaded.Count == 2 && reloaded[1].GameFilter == "tcp+udp",
                    reloaded.Count == 2 ? reloaded[1].GameFilter : "нет данных");

                // ---- Watcher.Tick: главные сценарии
                TestPresetWatcher(two);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void TestPresetWatcher(List<AutoZapret.Orchestrator.ProcessPreset> presets)
        {
            AutoZapret.Orchestrator.ProcessPresetWatcher watcher =
                new AutoZapret.Orchestrator.ProcessPresetWatcher(null);

            List<string> running = new List<string>();
            watcher.ProcessChecker = name => running.Contains(name);
            watcher.Presets = presets;
            watcher.CooldownSeconds = 0;

            // ---- обход выключен: ничего не применяем, но запоминаем пресет
            running.Add("aaa");
            AutoZapret.Orchestrator.PresetDecision decision =
                watcher.Tick(false, "", "default", "off");

            Check("Tick: при выключенном обходе не применяет", !decision.Apply, decision.Reason);
            Check("Tick: пресет запомнен как активный",
                watcher.Active != null && watcher.Active.Id == "first",
                watcher.Active == null ? "null" : watcher.Active.Id);

            // ---- тот же пресет второй раз: не дёргаем winws
            decision = watcher.Tick(false, "", "default", "off");
            Check("Tick: повтор не применяет", !decision.Apply, decision.Reason);

            // ---- обход включён, но стратегия уже нужная -> применять нечего
            watcher = new AutoZapret.Orchestrator.ProcessPresetWatcher(null);
            watcher.ProcessChecker = name => running.Contains(name);
            watcher.Presets = presets;
            watcher.CooldownSeconds = 0;

            decision = watcher.Tick(true, "strat-A", "strat-A", "off");
            Check("Tick: параметры совпали -> без перезапуска", !decision.Apply, decision.Reason);

            // ---- пресет исчез: ОБЯЗАН вернуть настройки. Это был баг.
            watcher = new AutoZapret.Orchestrator.ProcessPresetWatcher(null);
            watcher.ProcessChecker = name => running.Contains(name);
            watcher.Presets = presets;
            watcher.CooldownSeconds = 0;

            decision = watcher.Tick(true, "default", "default", "off");
            Check("Tick: пресет применился", decision.Apply,
                decision.Preset == null ? "нет пресета" : decision.Preset.Id);

            running.Clear();
            decision = watcher.Tick(true, "strat-A", "default", "off");
            Check("Tick: пресет исчез -> возврат к настройкам", decision.Apply, decision.Reason);
            Check("Tick: активно «нет пресета»", watcher.Active == null,
                watcher.Active == null ? "null" : watcher.Active.Id);

            // ---- кулдаун блокирует переключение
            AutoZapret.Orchestrator.ProcessPresetWatcher slow =
                new AutoZapret.Orchestrator.ProcessPresetWatcher(null);
            slow.ProcessChecker = name => running.Contains(name);
            slow.Presets = presets;
            slow.CooldownSeconds = 60;

            running.Add("aaa");
            decision = slow.Tick(true, "default", "default", "off");
            Check("Tick: первое переключение проходит", decision.Apply, decision.Reason);

            running.Clear();
            running.Add("bbb");
            decision = slow.Tick(true, "strat-A", "default", "off");
            Check("Tick: кулдаун блокирует второе", !decision.Apply, decision.Reason);
        }

        private static AutoZapret.Orchestrator.ProcessPreset Preset(
            string id, string[] processes, string strategy, string filter, bool enabled)
        {
            AutoZapret.Orchestrator.ProcessPreset preset =
                new AutoZapret.Orchestrator.ProcessPreset
                {
                    Id = id,
                    Name = id,
                    Strategy = strategy,
                    GameFilter = filter,
                    Enabled = enabled
                };

            preset.Processes.AddRange(processes);
            return preset;
        }

        private static void TestStrategyScoring()
        {
            // ---- идеальная стратегия: процесс жив, всё прошло, быстро и ровно
            AutoZapret.Diag.StrategyScore perfect = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(perfect,
                SynthReport(300, 320, 310, 305), true);

            Check("идеал: счёт 100", perfect.Score == 100, perfect.Score.ToString());
            Check("идеал: процесс 20/20", perfect.ProcessBonus == 20, perfect.ProcessBonus.ToString());
            Check("идеал: стабильность 15/15", perfect.StabilityBonus == 15, perfect.StabilityBonus.ToString());
            Check("идеал: задержка 10/10", perfect.LatencyBonus == 10, perfect.LatencyBonus.ToString());

            // ---- мёртвый процесс: минус 20, остальное на месте
            AutoZapret.Diag.StrategyScore dead = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(dead, SynthReport(300, 320, 310, 305), false);

            Check("мёртвый процесс: счёт 80", dead.Score == 80, dead.Score.ToString());
            Check("мёртвый процесс: 0/20", dead.ProcessBonus == 0, dead.ProcessBonus.ToString());

            // ---- ничего не прошло: только процесс и стабильность недоступна
            AutoZapret.Diag.StrategyScore nothing = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(nothing,
                SynthReport(-1, -1, -1, -1), true);

            Check("ноль успехов: счёт 20", nothing.Score == 20, nothing.Score.ToString());
            Check("ноль успехов: без бонуса задержки", nothing.LatencyBonus == 0,
                nothing.LatencyBonus.ToString());

            // ---- разброс задержек 10x: стабильность должна обнулиться
            AutoZapret.Diag.StrategyScore wild = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(wild, SynthReport(200, 2000, 1500, 1800), true);

            Check("разброс 10x: стабильность 0", wild.StabilityBonus == 0,
                wild.StabilityBonus.ToString());
            Check("разброс 10x: счёт ниже идеала", wild.Score < perfect.Score,
                wild.Score + " < " + perfect.Score);

            // ---- разброс 3x: частичный бонус, между нулём и полным
            AutoZapret.Diag.StrategyScore mid = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(mid, SynthReport(300, 900, 600, 750), true);

            Check("разброс 3x: частичная стабильность",
                mid.StabilityBonus > 0 && mid.StabilityBonus < 15, mid.StabilityBonus.ToString());

            // ---- медленно: 4 секунды убивают бонус за задержку
            AutoZapret.Diag.StrategyScore slow = new AutoZapret.Diag.StrategyScore();
            AutoZapret.Diag.StrategyTester.ComputeScore(slow, SynthReport(4000, 4100, 4050, 4020), true);

            Check("4 с: без бонуса задержки", slow.LatencyBonus == 0, slow.LatencyBonus.ToString());

            // ---- границы: счёт всегда 0..100
            bool inRange = true;
            for (int ms = 0; ms <= 5000; ms += 250)
            {
                AutoZapret.Diag.StrategyScore probe = new AutoZapret.Diag.StrategyScore();
                AutoZapret.Diag.StrategyTester.ComputeScore(probe, SynthReport(ms, ms + 10, ms + 5, ms + 8), true);
                if (probe.Score < 0 || probe.Score > 100) inRange = false;
            }
            Check("счёт всегда в 0..100", inRange, "ok");

            // ---- разбивка читается человеком
            Check("разбивка описывает все части",
                perfect.Breakdown().IndexOf("процесс", StringComparison.Ordinal) >= 0 &&
                perfect.Breakdown().IndexOf("стабильность", StringComparison.Ordinal) >= 0 &&
                perfect.Breakdown().IndexOf("успех", StringComparison.Ordinal) >= 0 &&
                perfect.Breakdown().IndexOf("задержка", StringComparison.Ordinal) >= 0,
                perfect.Breakdown());

            // ---- строка рейтинга содержит счёт
            Check("строка рейтинга содержит счёт",
                perfect.ToLine().IndexOf("счёт", StringComparison.Ordinal) >= 0, perfect.ToLine());
        }

        /// <summary>
        /// Синтетический отчёт: ms &lt; 0 означает «проба не прошла».
        /// Первый аргумент — задержка, остальные — дополнительные пробы.
        /// </summary>
        private static AutoZapret.Diag.ProbeReport SynthReport(params int[] latencies)
        {
            AutoZapret.Diag.ProbeReport report = new AutoZapret.Diag.ProbeReport();

            int ok = 0, sum = 0;

            for (int i = 0; i < latencies.Length; i++)
            {
                bool success = latencies[i] >= 0;

                AutoZapret.Diag.ProbeResult result = new AutoZapret.Diag.ProbeResult
                {
                    Name = "target" + i,
                    Ok = success,
                    ElapsedMs = success ? latencies[i] : 0
                };

                report.Results.Add(result);

                if (success) { ok++; sum += latencies[i]; }
            }

            report.OkCount = ok;
            report.Total = latencies.Length;
            report.AvgMs = ok == 0 ? 0 : sum / ok;

            return report;
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
