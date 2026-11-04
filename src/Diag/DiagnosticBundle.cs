using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    public sealed class DeepReportOptions
{
    /// <summary>Добавлять расширенные разделы: preflight, hosts, аргументы стратегий.</summary>
    public bool Deep;

    public Action<string> Progress;

    public static DeepReportOptions Fast()
    {
        return new DeepReportOptions { Deep = false };
    }

    public static DeepReportOptions Extended(Action<string> progress)
    {
        return new DeepReportOptions { Deep = true, Progress = progress };
    }
}

/// <summary>Итог сборки бандла: путь, состав, ошибка (если была).</summary>
    public sealed class BundleResult
    {
        public string Path = "";
        public bool Ok;
        public string Error = "";
        public long SizeBytes;
        public List<string> Entries = new List<string>();

        public string SizeText()
        {
            if (SizeBytes < 1024) return SizeBytes + " Б";
            if (SizeBytes < 1024 * 1024) return (SizeBytes / 1024) + " КБ";
            return (SizeBytes / (1024 * 1024)) + " МБ";
        }
    }

    /// <summary>
    /// Диагностический ZIP: один файл, который можно отправить в поддержку.
    ///
    /// Зачем: «не работает» без данных — это разговор на неделю. Бандл отвечает
    /// на 90% вопросов сразу: версии, окружение, все 14 проверок, хвост лога,
    /// настройки, состав движка, список стратегий.
    ///
    /// Что НЕ попадает внутрь:
    ///   * секрет прокси — заменяется на REDACTED (AGENTS.md: в лог не пишем);
    ///   * любые строки вида `secret=dd<32 hex>` — вычищаются регуляркой;
    ///   * содержимое WebView2-профиля и пользовательские файлы.
    ///
    /// Пути внутри бандла остаются настоящими: без них диагностика бесполезна
    /// (не понять, куда распаковался движок). Это осознанно и написано в README
    /// внутри архива, чтобы человек знал, что отправляет.
    /// </summary>
    public static class DiagnosticBundle
    {
        /// <summary>Сколько последних строк лога кладём. Больше — архив раздувается без пользы.</summary>
        public const int LogTailLines = 3000;

        /// <summary>Предел размера лога в бандле.</summary>
        private const int LogMaxBytes = 1024 * 1024;

        /// <summary>
        /// Собирает архив. targetDir создаётся при необходимости.
        /// Возвращает результат; исключений не бросает.
        /// </summary>
        public static BundleResult Create(string targetDir, Settings settings, StrategyStore store)
        {
            return Create(targetDir, settings, store, DeepReportOptions.Fast());
        }

        /// <summary>
        /// Собирает архив. options.Deep добавляет разделы для разбора «почему не работает»:
        /// preflight, состояние hosts, полные аргументы стратегий, снимок базовой линии.
        /// </summary>
        public static BundleResult Create(string targetDir, Settings settings, StrategyStore store,
                                          DeepReportOptions options)
        {
            BundleResult result = new BundleResult();

            if (options == null) options = DeepReportOptions.Fast();

            try
            {
                if (string.IsNullOrEmpty(targetDir))
                    targetDir = DefaultDirectory();

                Directory.CreateDirectory(targetDir);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string name = "autozapret-diag-" + stamp + ".zip";
                string finalPath = System.IO.Path.Combine(targetDir, name);

                // Временный файл — рядом с целевым, а не в %TEMP%:
                // на другой диск File.Move не работает атомарно. См. AGENTS.md.
                string tempPath = finalPath + ".tmp";

                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(zip, "README.txt", ReadmeText(settings), result);
                    AddText(zip, "environment.txt", EnvironmentText(), result);
                    AddText(zip, "diagnostics.txt", DiagnosticsText(), result);
                    AddText(zip, "settings.json", RedactedSettings(settings), result);
                    AddText(zip, "engine.txt", EngineText(), result);
                    AddText(zip, "strategies.txt", StrategiesText(store), result);

                    if (options.Deep)
                    {
                        Report(options, "проверяю готовность…");
                        AddText(zip, "preflight.txt", PreflightText(settings, store), result);

                        Report(options, "читаю hosts…");
                        AddText(zip, "hosts.txt", HostsText(), result);

                        Report(options, "собираю аргументы стратегий…");
                        AddText(zip, "strategies-full.txt", StrategiesFullText(settings, store), result);

                        AddText(zip, "network.txt", NetworkText(), result);
                        AddText(zip, "engine-files.txt", EngineFilesText(), result);

                        // Что вшито в поставку и совпадает ли с тем, что лежит
                        // в engine\lists. По этому видно, работает человек на
                        // бандле или на скачанном, и не испорчен ли список.
                        AddText(zip, "lists.txt", ListsText(), result);
                    }

                    string logText = LogTail();
                    if (logText.Length > 0) AddText(zip, "app.log", logText, result);
                    else result.Entries.Add("app.log — пусто");

                    // Вывод winws с --debug=1: если его собирали, он важнее всего
                    // остального. Кладём как есть, вместе с аргументами и ошибками.
                    try
                    {
                        string winws = StrategyDebugger.OutputPath;
                        if (File.Exists(winws))
                            AddText(zip, "winws-debug.txt", File.ReadAllText(winws), result);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("DIAG", "winws-debug.txt: " + ex.Message);
                    }
                }

                if (File.Exists(finalPath))
                {
                    try { File.Delete(finalPath); } catch { }
                }

                File.Move(tempPath, finalPath);

                FileInfo info = new FileInfo(finalPath);

                result.Path = finalPath;
                result.SizeBytes = info.Length;
                result.Ok = true;

                Log.Info("DIAG", "диагностический бандл собран: " + info.Length + " Б, файлов " + result.Entries.Count);

                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.GetType().Name + ": " + ex.Message;
                Log.Exception("DIAG", "не удалось собрать бандл", ex);
                return result;
            }
        }

        /// <summary>Куда сохраняем по умолчанию: data\diag (рядом с настройками).</summary>
        public static string DefaultDirectory()
        {
            return System.IO.Path.Combine(Paths.DataDir, "diag");
        }

        // ------------------------------------------------------------- content

        private static string ReadmeText(Settings settings)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("AutoZapret — диагностический архив");
            sb.AppendLine("==================================");
            sb.AppendLine();
            sb.AppendLine("Собран: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("Состав:");
            sb.AppendLine("  environment.txt  — версии приложения, .NET, WebView2, Windows, отпечаток сети");
            sb.AppendLine("  diagnostics.txt  — 14 проверок окружения с советами");
            sb.AppendLine("  settings.json    — настройки (секрет прокси вырезан)");
            sb.AppendLine("  engine.txt       — файлы движка zapret и их размеры");
            sb.AppendLine("  lists.txt        — списки: что в движке и что вшито в поставку");
            sb.AppendLine("  strategies.txt   — список стратегий");
            sb.AppendLine("  app.log          — последние " + LogTailLines + " строк лога");
            sb.AppendLine();
            sb.AppendLine("ЧТО ВЫРЕЗАНО:");
            sb.AppendLine("  * секрет Telegram-прокси заменён на REDACTED;");
            sb.AppendLine("  * строки вида secret=dd<32 hex> вычищены из лога.");
            sb.AppendLine();
            sb.AppendLine("ЧТО ОСТАЛОСЬ:");
            sb.AppendLine("  * пути к файлам (нужны, чтобы понять, где лежит движок);");
            sb.AppendLine("  * имя компьютера и локальные IP — они есть в логе работы winws.");
            sb.AppendLine();
            sb.AppendLine("Если это нежелательно — просмотрите файлы перед отправкой.");

            return sb.ToString();
        }

        private static string EnvironmentText()
        {
            StringBuilder sb = new StringBuilder();

            Line(sb, "приложение", AppInfo.Name + " " + AppInfo.Version);
            Line(sb, "сборка", BuildStamp());
            Line(sb, "ОС", AppInfo.OsDescription);
            Line(sb, "платформа", Environment.Is64BitOperatingSystem ? "x64" : "x86");
            Line(sb, "процесс", Environment.Is64BitProcess ? "64-bit" : "32-bit");
            Line(sb, "права", AppInfo.IsAdmin ? "администратор" : "пользователь");
            Line(sb, "режим", Paths.IsPortable ? "portable" : "installed");
            Line(sb, "каталог", Paths.BaseDir);
            Line(sb, "данные", Paths.DataDir);

            string runtime = ".NET " + Environment.Version;
            try
            {
                // точная версия рантайма: Environment.Version в .NET 9 неполный
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            }
            catch { }
            Line(sb, "рантайм", runtime);

            string webview = AppInfo.WebView2RuntimeVersion();
            Line(sb, "WebView2", webview ?? "не найден");
            Line(sb, "язык системы", CultureInfo.CurrentUICulture.Name);
            Line(sb, "кодировка консоли", "CP437 для netsh/sc/tasklist");
            Line(sb, "часовой пояс", TimeZoneInfo.Local.DisplayName);
            Line(sb, "сеть (отпечаток)", NetworkFingerprint.Hash());
            Line(sb, "пользователь", Environment.UserName);

            return sb.ToString();
        }

        /// <summary>
        /// Когда собрана программа. В single-file `Assembly.Location` пустой,
        /// поэтому берём время файла по пути запущенного процесса.
        /// </summary>
        private static string BuildStamp()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    exe = Path.Combine(AppContext.BaseDirectory, "AutoZapret.exe");

                if (!File.Exists(exe)) return "?";

                return File.GetLastWriteTimeUtc(exe)
                    .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            }
            catch { return "?"; }
        }

        private static string DiagnosticsText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Проверки окружения");
            sb.AppendLine("==================");
            sb.AppendLine();

            List<CheckResult> results = SystemChecks.RunAll();

            int problems = 0, warnings = 0;

            foreach (CheckResult r in results)
            {
                string mark;
                switch (r.Status)
                {
                    case CheckStatus.Ok: mark = "[ok]  "; break;
                    case CheckStatus.Warn: mark = "[warn]"; warnings++; break;
                    case CheckStatus.Fail: mark = "[FAIL]"; problems++; break;
                    default: mark = "[skip]"; break;
                }

                sb.AppendLine(mark + " " + r.Name.PadRight(30) + r.Detail);

                if (!string.IsNullOrEmpty(r.Advice))
                    sb.AppendLine("       -> " + r.Advice);
            }

            sb.AppendLine();
            sb.AppendLine("проблем: " + problems + ", предупреждений: " + warnings);

            return sb.ToString();
        }

        private static string EngineText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Движок zapret");
            sb.AppendLine("=============");
            sb.AppendLine();

            Line(sb, "каталог", Paths.EngineDir);
            Line(sb, "bin", Paths.EngineBinDir);
            Line(sb, "установлен", Directory.Exists(Paths.EngineDir) ? "да" : "нет");

            if (!Directory.Exists(Paths.EngineDir))
            {
                sb.AppendLine();
                sb.AppendLine("Движок не установлен: tools\\fetch-engine.ps1");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("Файлы bin:");

            try
            {
                string[] files = Directory.GetFiles(Paths.EngineBinDir);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(file);
                        sb.AppendLine("  " + fi.Name.PadRight(24) + fi.Length.ToString(CultureInfo.InvariantCulture).PadLeft(12) +
                                      "  " + fi.LastWriteTimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  не удалось прочитать: " + ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine("Каталоги:");

            try
            {
                foreach (string dir in Directory.GetDirectories(Paths.EngineDir))
                {
                    int count = 0;
                    try { count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
                    catch { }
                    sb.AppendLine("  " + System.IO.Path.GetFileName(dir).PadRight(24) + count + " файлов");
                }
            }
            catch { }

            return sb.ToString();
        }

        private static string StrategiesText(StrategyStore store)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Стратегии");
            sb.AppendLine("=========");
            sb.AppendLine();

            if (store == null)
            {
                sb.AppendLine("хранилище недоступно");
                return sb.ToString();
            }

            sb.AppendLine("всего: " + store.Count);
            sb.AppendLine();

            try
            {
                foreach (Strategy s in store.All)
                {
                    sb.AppendLine("  " + s.Id.PadRight(28) + " [" + s.Engine + "] " +
                                  (s.Recommended ? "* " : "  ") + s.Name);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("не удалось перечислить: " + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Настройки с вырезанным секретом. Простая замена значения, а не
        /// пересборка JSON: ToJson — единственный источник формата, дублировать
        /// его здесь значит разойтись при первом же новом поле.
        /// </summary>
        private static string RedactedSettings(Settings settings)
        {
            if (settings == null) return "{\n  \"error\": \"настройки недоступны\"\n}\n";

            string json;
            try { json = settings.ToJson(); }
            catch (Exception ex) { return "{\n  \"error\": \"" + ex.Message.Replace('"', '\'') + "\"\n}\n"; }

            string secret = settings.ProxySecret;
            if (!string.IsNullOrEmpty(secret))
                json = json.Replace(secret, "REDACTED");

            return ScrubSecrets(json);
        }

        /// <summary>Хвост текущего лога. Файл читается с FileShare.ReadWrite — лог может писаться прямо сейчас.</summary>
        private static string LogTail()
        {
            string path = Log.CurrentFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";

            try
            {
                List<string> lines = new List<string>();

                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);

                        // держим окно в памяти: файл за сутки бывает большим
                        if (lines.Count > LogTailLines * 2)
                            lines.RemoveRange(0, LogTailLines);
                    }
                }

                int skip = Math.Max(0, lines.Count - LogTailLines);
                StringBuilder sb = new StringBuilder();

                for (int i = skip; i < lines.Count; i++)
                {
                    if (sb.Length > LogMaxBytes) break;
                    sb.AppendLine(lines[i]);
                }

                return ScrubSecrets(sb.ToString());
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Страховка: даже если секрет утёк в лог каким-то путём, вырезаем его
        /// по форме. Формат ссылки прокси: secret=dd&lt;32 hex&gt;.
        /// </summary>
        private static string ScrubSecrets(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            try
            {
                return Regex.Replace(text, @"(secret=dd)[0-9a-fA-F]{16,}",
                    "$1REDACTED", RegexOptions.IgnoreCase);
            }
            catch
            {
                return text;
            }
        }

        // ------------------------------------------------------------- helpers

        private static void Report(DeepReportOptions options, string message)
        {
            if (options == null || options.Progress == null) return;

            try { options.Progress(message); } catch { }
        }

        /// <summary>
        /// Preflight: что мешает запуску прямо сейчас, с кодами и подсказками.
        /// Самый полезный раздел расширенного отчёта — здесь видно, чего не хватает,
        /// а не «не работает».
        /// </summary>
        private static string PreflightText(Settings settings, StrategyStore store)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Проверка готовности к запуску");
            sb.AppendLine("=============================");
            sb.AppendLine();

            try
            {
                Zapret.EngineLocator engine = new Zapret.EngineLocator();
                engine.Refresh();

                List<Zapret.PreflightProblem> problems = Zapret.Preflight.Run(
                    engine, store, settings == null ? null : settings.StrategyId,
                    settings == null ? "off" : settings.GameFilter);

                if (problems.Count == 0)
                {
                    sb.AppendLine("[ok] проблем не найдено — запускать можно");
                }
                else
                {
                    foreach (Zapret.PreflightProblem p in problems)
                    {
                        sb.AppendLine((p.Level == Zapret.PreflightLevel.Blocker ? "[БЛОК] " : "[warn] ") +
                                      p.Title + " (код " + p.Code + ")");
                        if (!string.IsNullOrEmpty(p.Hint)) sb.AppendLine("       -> " + p.Hint);
                    }

                    sb.AppendLine();
                    sb.AppendLine(Zapret.Preflight.Summary(problems));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("не удалось выполнить проверку: " + ex.GetType().Name + ": " + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>
        /// hosts: есть ли наш блок, сколько строк, и не перенаправлены ли сайты,
        /// которые обходит движок. Именно это ломает обход чаще всего.
        /// </summary>
        private static string HostsText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Файл hosts");
            sb.AppendLine("==========");
            sb.AppendLine();

            try
            {
                sb.AppendLine("путь: " + Lists.HostsFile.SystemPath);
                sb.AppendLine("резервная копия: " + Lists.HostsFile.BackupPath +
                              (System.IO.File.Exists(Lists.HostsFile.BackupPath) ? " (есть)" : " (нет)"));

                string text = Lists.HostsFile.ReadSystem();

                if (text == null)
                {
                    sb.AppendLine("не удалось прочитать (нужны права администратора)");
                    return sb.ToString();
                }

                int blockLines;
                bool hasBlock = Lists.HostsFile.HasBlock(text, out blockLines);

                sb.AppendLine("наш блок: " + (hasBlock ? "есть, " + blockLines + " строк" : "нет"));
                sb.AppendLine("всего строк: " + text.Split('\n').Length);
                sb.AppendLine();

                sb.AppendLine("Сайты, которые перенаправлены (это ломает обход):");

                string[] risky =
                {
                    "youtube.com", "googlevideo.com", "ytimg.com",
                    "discord.com", "discordapp.com",
                    "telegram.org", "web.telegram.org", "t.me"
                };

                int found = 0;
                foreach (string site in risky)
                {
                    if (text.IndexOf(" " + site, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sb.AppendLine("  " + site);
                        found++;
                    }
                }

                if (found == 0) sb.AppendLine("  нет");
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("Что делать: Списки -> Hosts -> «Почистить» (убрать наш блок)");
                    sb.AppendLine("или «Пересоздать» (оставить только localhost).");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("ошибка: " + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Полные аргументы каждой стратегии с раскрытыми плейсхолдерами.
        /// Нужно, чтобы увидеть, на какой именно файл ссылается стратегия
        /// и существует ли он.
        /// </summary>
        private static string StrategiesFullText(Settings settings, StrategyStore store)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Стратегии: полные аргументы");
            sb.AppendLine("===========================");
            sb.AppendLine();

            if (store == null)
            {
                sb.AppendLine("хранилище недоступно");
                return sb.ToString();
            }

            Dictionary<string, string> placeholders = null;

            try
            {
                Zapret.EngineLocator engine = new Zapret.EngineLocator();
                placeholders = engine.BuildPlaceholders(settings == null ? "off" : settings.GameFilter);
            }
            catch (Exception ex)
            {
                sb.AppendLine("плейсхолдеры недоступны: " + ex.Message);
                sb.AppendLine();
            }

            int filesChecked = 0, filesMissing = 0;

            foreach (Strategy s in store.All)
            {
                sb.AppendLine("--- " + s.Id + " [" + s.Engine + "] " + s.Name +
                              (s.Recommended ? " *рекомендуется" : ""));
                if (!string.IsNullOrEmpty(s.Note)) sb.AppendLine("    " + s.Note);

                foreach (StrategyProfile profile in s.Profiles)
                {
                    sb.AppendLine("  профиль '" + profile.Name + "' (" + profile.Args.Count + " аргументов):");

                    foreach (string raw in profile.Args)
                    {
                        string arg = raw;

                        try
                        {
                            if (placeholders != null)
                                arg = Strategy.Substitute(raw, placeholders);
                        }
                        catch { }

                        string marker = "";

                        // ссылки на файлы: сразу видно, есть он или нет
                        int eq = arg.IndexOf('=');
                        if (eq > 0)
                        {
                            string value = arg.Substring(eq + 1);

                            if (value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                                value.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
                                value.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                            {
                                filesChecked++;
                                bool exists = false;
                                try { exists = File.Exists(value); } catch { }

                                if (!exists) { filesMissing++; marker = "   <-- НЕТ ФАЙЛА"; }
                            }
                        }

                        sb.AppendLine("    " + arg + marker);
                    }
                }

                sb.AppendLine();
            }

            sb.AppendLine("проверено ссылок на файлы: " + filesChecked + ", отсутствует: " + filesMissing);

            return sb.ToString();
        }

        /// <summary>Снимок базовой линии: что было заблокировано на момент замера.</summary>
        private static string NetworkText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Сеть: сохранённый замер");
            sb.AppendLine("=======================");
            sb.AppendLine();
            sb.AppendLine("Живой замер не выполняется: он занимает до минуты и меняет сеть.");
            sb.AppendLine("Свежие данные: экран «Диагностика» -> «Проверить сеть».");
            sb.AppendLine();

            try
            {
                Baseline.Snapshot snapshot = Baseline.Load();

                if (snapshot == null)
                {
                    sb.AppendLine("замера нет");
                    return sb.ToString();
                }

                sb.AppendLine("отпечаток сети: " + snapshot.Network);
                sb.AppendLine("когда: " + snapshot.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("доступно: " + string.Join(", ", snapshot.Working.ToArray()));
                sb.AppendLine("заблокировано: " + string.Join(", ", snapshot.Blocked.ToArray()));
            }
            catch (Exception ex)
            {
                sb.AppendLine("ошибка чтения: " + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>Полный список файлов движка с размерами: видно недокачанные и лишние.</summary>
        private static string ListsText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Списки: движок и вшитая копия");
            sb.AppendLine("=============================");
            sb.AppendLine();
            sb.AppendLine("каталог движка : " + Paths.EngineListsDir);
            sb.AppendLine("вшитая копия   : " + Lists.BundledLists.SourceDir);
            sb.AppendLine("в поставке     : " + Lists.BundledLists.Describe());
            sb.AppendLine();

            try
            {
                List<string> names = new List<string>();

                foreach (Lists.ListKind kind in Lists.ListCatalog.All())
                    if (!names.Contains(kind.File)) names.Add(kind.File);

                sb.AppendLine("файл                      движок  вшито  строк  примечание");

                foreach (string name in names)
                {
                    string live = Path.Combine(Paths.EngineListsDir, name);
                    string bundled = Path.Combine(Lists.BundledLists.SourceDir, name);

                    long liveSize = 0, bundledSize = 0;

                    try { if (File.Exists(live)) liveSize = new FileInfo(live).Length; } catch { }
                    try { if (File.Exists(bundled)) bundledSize = new FileInfo(bundled).Length; } catch { }

                    int lines = 0;

                    try
                    {
                        if (File.Exists(live))
                            foreach (string line in File.ReadLines(live))
                                if (line.Trim().Length > 0) lines++;
                    }
                    catch { }

                    string note = "";

                    if (liveSize == 0) note = "НЕТ (движок не найдёт)";
                    else if (bundledSize == 0) note = "нет в поставке";
                    else if (liveSize < bundledSize / 2) note = "меньше вшитой — вероятно испорчен";
                    else if (liveSize == bundledSize) note = "совпадает с вшитой";

                    sb.AppendLine(name.PadRight(26) +
                                  liveSize.ToString(CultureInfo.InvariantCulture).PadLeft(7) +
                                  bundledSize.ToString(CultureInfo.InvariantCulture).PadLeft(7) +
                                  lines.ToString(CultureInfo.InvariantCulture).PadLeft(7) +
                                  "  " + note);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("ошибка чтения списков: " + ex.Message);
            }

            return sb.ToString();
        }

        private static string EngineFilesText()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("Движок: все файлы");
            sb.AppendLine("=================");
            sb.AppendLine();

            try
            {
                if (!Directory.Exists(Paths.EngineDir))
                {
                    sb.AppendLine("каталог движка отсутствует: " + Paths.EngineDir);
                    return sb.ToString();
                }

                string[] files = Directory.GetFiles(Paths.EngineDir, "*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                long total = 0;

                foreach (string file in files)
                {
                    int slash = file.IndexOf("engine", StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        FileInfo info = new FileInfo(file);
                        total += info.Length;

                        sb.AppendLine(info.Length.ToString(CultureInfo.InvariantCulture).PadLeft(11) +
                                      "  " + file.Substring(slash));
                    }
                    catch { }
                }

                sb.AppendLine();
                sb.AppendLine("всего файлов: " + files.Length + ", размер: " + (total / (1024 * 1024)) + " МБ");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ошибка: " + ex.Message);
            }

            return sb.ToString();
        }

        private static void AddText(ZipArchive zip, string name, string content, BundleResult result)
        {
            try
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);

                using (Stream stream = entry.Open())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content ?? "");
                }

                result.Entries.Add(name + " — " + (content == null ? 0 : content.Length) + " символов");
            }
            catch (Exception ex)
            {
                result.Entries.Add(name + " — ОШИБКА: " + ex.Message);
            }
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.AppendLine(key.PadRight(18) + ": " + value);
        }
    }
}
