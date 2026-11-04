using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;
using AutoZapret.Diag;
using AutoZapret.Payloads;

namespace AutoZapret.Zapret
{
    public enum PreflightLevel
    {
        /// <summary>Обход не запустится — запускать бессмысленно.</summary>
        Blocker,
        /// <summary>Запустится, но что-то может не работать. Не мешает старту.</summary>
        Warning
    }

    public sealed class PreflightProblem
    {
        public PreflightLevel Level = PreflightLevel.Blocker;
        public string Title = "";
        public string Hint = "";
        public string Code = "";

        public string Text()
        {
            return Hint.Length > 0 ? Title + " — " + Hint : Title;
        }
    }

    /// <summary>
    /// Быстрая проверка «можно ли вообще запускать обход» ДО запуска winws.
    ///
    /// Зачем отдельно от диагностики: диагностика объясняет, почему у человека
    /// не работает интернет, и занимает секунды. Здесь — 200 миллисекунд и один
    /// вопрос: хватает ли файлов и настроек, чтобы winws стартовал.
    ///
    /// Что ловит: отсутствующий движок, отсутствующий драйвер, стратегию с
    /// несуществующим фейком (winws молча шлёт мусор), отсутствующий hostlist
    /// (winws ругается в stderr, а пользователь видит «не работает»), чужой
    /// запущенный winws, который держит WinDivert.
    ///
    /// Ничего не чинит и не меняет: только читает и возвращает список.
    /// </summary>
    public static class Preflight
    {
        /// <summary>
        /// Полная проверка перед стартом. strategyId может быть пустым —
        /// тогда проверится только окружение.
        /// </summary>
        public static List<PreflightProblem> Run(EngineLocator engine, StrategyStore store,
                                                 string strategyId, string gameFilter)
        {
            List<PreflightProblem> problems = new List<PreflightProblem>();

            if (engine == null) return problems;

            try { engine.Refresh(); } catch { }

            Strategy strategy = null;
            try { strategy = store == null ? null : store.Find(strategyId); }
            catch { }

            // ---- 1. сам движок
            if (!engine.Ready)
            {
                problems.Add(Blocker("engine-not-ready",
                    "Движок не установлен",
                    engine.Problem ?? "скачайте движок кнопкой на главной"));
                return problems;    // без движка остальное не имеет смысла
            }

            EngineKind kind = strategy != null &&
                              string.Equals(strategy.Engine, "v2", StringComparison.OrdinalIgnoreCase)
                ? EngineKind.V2
                : EngineKind.V1;

            // ---- 0. DNS-ловушка: трафик завёрнут в туннель VPN
            //
            // Это первое, что нужно проверить: при fake-IP DNS (198.18.x) пакеты
            // уходят в локальный TUN, до провайдера и DPI не доходят, и любая
            // стратегия даёт ровно «0 из 4» с ошибкой TLS. Выглядит как «движок
            // не работает», а на деле ему нечего исправлять.
            string fakeDns = DnsGuard.Detect();
            if (fakeDns.Length > 0)
            {
                problems.Add(Warning("dns-fake-ip",
                    "системный DNS подменяется — проверки идут через DoH", fakeDns));
            }

            if (!engine.IsAvailable(kind))
            {
                problems.Add(Blocker("engine-kind-missing",
                    kind == EngineKind.V2 ? "zapret 2 не установлен" : "zapret 1 не установлен",
                    engine.UnavailableReason(kind)));
            }

            // ---- 2. файлы, без которых winws не стартует
            string bin = Paths.EngineBinDir;

            string exeName = kind == EngineKind.V2 ? "winws2.exe" : "winws.exe";
            RequireFile(problems, bin, exeName, "нет исполняемого файла движка");
            RequireFile(problems, bin, "WinDivert64.sys", "нет драйвера — обход не сможет перехватывать пакеты");
            RequireFile(problems, bin, "WinDivert.dll", "нет библиотеки драйвера");

            if (kind == EngineKind.V1)
                RequireFile(problems, bin, "cygwin1.dll", "нет cygwin1.dll — winws не запустится");

            // ---- 3. чужие процессы держат WinDivert
            try
            {
                string foreign = WinwsSupervisor.DetectForeignWinws();
                if (foreign != null)
                {
                    problems.Add(Blocker("foreign-winws",
                        "уже работает чужой winws",
                        foreign + " — остановите: sc stop zapret"));
                }
            }
            catch { }

            // ---- 4. стратегия и её аргументы
            // Пустой strategyId означает «проверь только окружение»: подбор
            // стратегий перебирает их сам, и конкретная ему не нужна.
            bool needStrategy = !string.IsNullOrEmpty(strategyId);

            if (needStrategy && strategy == null)
            {
                problems.Add(Blocker("strategy-missing",
                    "стратегия не выбрана",
                    "выберите стратегию на главном экране"));
                return problems;
            }

            if (!needStrategy) return problems;

            List<string> args;

            try
            {
                Dictionary<string, string> placeholders = engine.BuildPlaceholders(gameFilter ?? "off");
                args = strategy.BuildArguments(placeholders);
            }
            catch (Exception ex)
            {
                problems.Add(Blocker("strategy-args",
                    "не удалось собрать аргументы стратегии",
                    ex.Message));
                return problems;
            }

            if (args == null || args.Count == 0)
            {
                problems.Add(Blocker("strategy-empty",
                    "у стратегии нет аргументов",
                    "это битая запись в каталоге стратегий"));
                return problems;
            }

            // ---- 5. фейки, списки и lua, на которые ссылается стратегия
            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;

                int eq = arg.IndexOf('=');
                if (eq <= 0) continue;

                string option = arg.Substring(0, eq);
                string value = arg.Substring(eq + 1);

                if (value.Length == 0) continue;

                // фейки: winws не проверяет содержимое .bin и молча шлёт мусор
                if (value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    string fakePath = NormalizePath(value);

                    if (!File.Exists(fakePath))
                    {
                        problems.Add(Blocker("fake-missing",
                            "нет файла фейка",
                            Path.GetFileName(fakePath) + " — перекачайте движок"));
                        continue;
                    }

                    Payload payload = PayloadValidator.Inspect(fakePath);
                    if (!payload.Valid)
                    {
                        problems.Add(Blocker("fake-invalid",
                            "битый файл фейка",
                            Path.GetFileName(fakePath) + ": " + payload.InvalidReason));
                    }

                    continue;
                }

                // списки доменов и IP: без них winws ругается и не фильтрует.
                //
                // ВАЖНО: только точные имена опций. `--hostlist-domains=discord.media`
                // — это ДОМЕН в аргументе, а не путь к файлу. Проверка по префиксу
                // ловила его и блокировала ALT12 у всех подряд.
                bool isListFile =
                    option == "--hostlist" ||
                    option == "--hostlist-exclude" ||
                    option == "--ipset" ||
                    option == "--ipset-exclude";

                if (isListFile)
                {
                    string listPath = NormalizePath(value);

                    if (!File.Exists(listPath))
                    {
                        // пустой ipset-файл — не блокер, hostlist — блокер
                        bool hostlistMissing = option.StartsWith("--hostlist", StringComparison.Ordinal);

                        problems.Add(hostlistMissing
                            ? Blocker("hostlist-missing", "нет файла списка доменов", Path.GetFileName(listPath))
                            : Warning("ipset-missing", "нет файла списка IP", Path.GetFileName(listPath)));

                        continue;
                    }

                    // Файл есть, но пустой — это ХУЖЕ отсутствующего: winws
                    // запускается, фильтр не совпадает ни с чем, и все стратегии
                    // выглядят «нерабочими» (0/4 у каждой). Встречалось вживую.
                    //
                    // Но не для всех файлов: `*-user.txt` — личные дополнения
                    // пользователя, пустой файл там НОРМА, а `ipset-exclude.txt` —
                    // список локальных сетей, у него 11 подсетей это 135 байт.
                    // Раньше мы ругались на них при каждом запуске, и это выглядело
                    // как «списков нет, хотя я их скачивал».
                    if (!IsSupposedToGrow(Path.GetFileName(listPath)))
                        continue;

                    try
                    {
                        long size = new FileInfo(listPath).Length;

                        // list-*.txt с одним доменом — это ~10 байт, живой список — сотни
                        long minSize = option.StartsWith("--hostlist", StringComparison.Ordinal) ? 32 : 1024;

                        if (size < minSize)
                        {
                            problems.Add(Warning("list-empty",
                                "список почти пуст: " + Path.GetFileName(listPath),
                                size + " байт — списки обновятся сами при запуске (или кнопкой на экране «Списки»)"));
                        }
                    }
                    catch { }

                    continue;
                }

                // lua для zapret2. Путь пишется как `--lua-init=@<файл>`: собака —
// это префикс «читать из файла» в zapret2, а не часть пути. Без её
// отбрасывания File.Exists всегда false и preflight врал «нет lua-скрипта».
                if (option.IndexOf("lua", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    value.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    string luaPath = NormalizePath(value);

                    if (!File.Exists(luaPath))
                        problems.Add(Blocker("lua-missing", "нет lua-скрипта", Path.GetFileName(luaPath)));
                }
            }

            // ---- 6. права администратора: запуск невозможен, но это не «поломка»
            if (!ElevationRequiredButMissing())
            {
                // права есть — проверять нечего
            }
            else
            {
                problems.Add(Blocker("elevation",
                    "нет прав администратора",
                    "winws.exe и WinDivert запускаются только от администратора"));
            }

            // ---- 7. пустая библиотека фейков (не блокер: часть стратегий без них)
            try
            {
                if (!Directory.Exists(Paths.FakesDir) || Directory.GetFiles(Paths.FakesDir, "*.bin").Length == 0)
                {
                    bool usesFakes = false;
                    foreach (string arg in args)
                        if (arg.IndexOf(".bin", StringComparison.OrdinalIgnoreCase) >= 0) { usesFakes = true; break; }

                    if (usesFakes)
                        problems.Add(Warning("fakes-empty", "библиотека фейков пуста",
                            "стратегия ссылается на фейки, но папка assets\\fakes пуста"));
                }
            }
            catch { }

            return problems;
        }

        /// <summary>Есть ли среди проблем блокеры. Предупреждения старт не отменяют.</summary>
        public static bool HasBlockers(List<PreflightProblem> problems)
        {
            if (problems == null) return false;

            foreach (PreflightProblem p in problems)
                if (p.Level == PreflightLevel.Blocker) return true;

            return false;
        }

        /// <summary>Короткая строка для лога и уведомления.</summary>
        public static string Summary(List<PreflightProblem> problems)
        {
            if (problems == null || problems.Count == 0) return "";

            int blockers = 0;
            foreach (PreflightProblem p in problems)
                if (p.Level == PreflightLevel.Blocker) blockers++;

            if (blockers > 0) return problems.Count == 1 ? problems[0].Text() : "проблем: " + problems.Count;
            return "предупреждений: " + problems.Count;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>
        /// Приводит значение аргумента к реальному пути.
        ///
        /// zapret2 пишет `--lua-init=@C:\...\blobs.lua`: `@` — префикс «читать из
        /// файла», а не часть пути. Без его отбрасывания File.Exists всегда false,
        /// и проверка ругалась на отсутствующие lua при наличии всех файлов.
        /// </summary>
        public static string NormalizePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string path = value.Trim();

            while (path.StartsWith("@", StringComparison.Ordinal)) path = path.Substring(1);

            return path;
        }

        /// <summary>
        /// Должен ли файл списка быть большим.
        ///
        /// `*-user.txt` — личные дополнения человека: пустой файл там нормальное
        /// состояние. `ipset-exclude*.txt` — список локальных сетей, он по смыслу
        /// короткий (10/8, 192.168/16 и т.д., ~135 байт). Пустыми «по-настоящему»
        /// бывают только скачиваемые списки, о них и предупреждаем.
        /// </summary>
        public static bool IsSupposedToGrow(string fileName)
        {
            string name = (fileName ?? "").ToLowerInvariant();

            if (name.EndsWith("-user.txt", StringComparison.Ordinal)) return false;
            if (name.StartsWith("ipset-exclude", StringComparison.Ordinal)) return false;

            return true;
        }

        private static bool ElevationRequiredButMissing()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                       System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal =
                        new System.Security.Principal.WindowsPrincipal(identity);
                    return !principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        private static void RequireFile(List<PreflightProblem> problems, string dir, string name, string hint)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return;
            }
            catch { }

            problems.Add(Blocker("file-" + name, "не хватает файла: " + name, hint));
        }

        private static PreflightProblem Blocker(string code, string title, string hint)
        {
            return new PreflightProblem
            {
                Level = PreflightLevel.Blocker,
                Code = code,
                Title = title,
                Hint = hint ?? ""
            };
        }

        private static PreflightProblem Warning(string code, string title, string hint)
        {
            return new PreflightProblem
            {
                Level = PreflightLevel.Warning,
                Code = code,
                Title = title,
                Hint = hint ?? ""
            };
        }
    }
}