using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Payloads;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    public sealed class StrategyScore
    {
        public string Id = "";
        public string Name = "";
        public int OkCount;
        public int Total;
        public int AvgMs;
        public string Error = "";
        public List<string> Failed = new List<string>();

        public double Rate { get { return Total == 0 ? 0 : (double)OkCount / Total; } }

        public string ToLine()
        {
            string mark = Rate >= 1.0 ? "🟢" : Rate > 0 ? "🟡" : "🔴";
            return mark + " " + Name.PadRight(28) + OkCount + "/" + Total +
                   "  " + AvgMs + "ms" + (Error.Length > 0 ? "  (" + Error + ")" : "");
        }

        /// <summary>Что именно не пробило — без этого «4/5» ничего не говорит.</summary>
        public string FailedLine()
        {
            if (Failed.Count == 0) return "";
            return "   не прошло: " + string.Join(", ", Failed.ToArray());
        }
    }

    /// <summary>
    /// Перебор стратегий: запускаем каждую, проверяем ТОЛЬКО заблокированные цели,
    /// останавливаем, идём дальше.
    ///
    /// Ключевые решения:
    ///   * тестируем против базовой линии (что реально сломано), а не «всё подряд» —
    ///     это экономит десятки секунд на стратегию;
    ///   * между стратегиями ждём, чтобы WinDivert успел выгрузиться;
    ///   * всегда возвращаем систему в исходное состояние (обход выключен),
    ///     даже если пользователь прервал;
    ///   * результаты сохраняются, чтобы не перебирать одно и то же каждый раз.
    /// </summary>
    public sealed class StrategyTester
    {
        private readonly ZapretService _zapret;
        private readonly ConnectivityProbe _probe;

        public StrategyTester(ZapretService zapret, ConnectivityProbe probe)
        {
            _zapret = zapret;
            _probe = probe;
        }

        public event Action<string> Progress;

        public List<StrategyScore> Run(IEnumerable<string> strategyIds, Baseline.Snapshot baseline,
                                       int holdSeconds, int timeoutSec)
        {
            List<ProbeTarget> targets = baseline != null ? baseline.BlockedTargets()
                                                         : ConnectivityProbe.DefaultTargets();

            List<StrategyScore> scores = new List<StrategyScore>();
            List<string> ids = new List<string>(strategyIds);

            Log.Info("DIAG", "перебор стратегий: " + ids.Count + ", целей: " + targets.Count);

            foreach (string id in ids)
            {
                StrategyScore score = new StrategyScore { Id = id };
                Strategy strategy = _zapret.Strategies.Find(id);
                score.Name = strategy != null ? strategy.Name : id;

                Report("проверяю " + score.Name);

                string error = _zapret.Start(id);
                if (error != null)
                {
                    score.Error = error;
                    score.Total = targets.Count;
                    scores.Add(score);
                    Log.Warn("DIAG", "стратегия " + id + " не запустилась: " + error);
                    Report(score.ToLine());
                    continue;
                }

                // даём winws подняться и WinDivert встать на место
                Thread.Sleep(Math.Max(1, holdSeconds) * 1000);

                ProbeReport report = _probe.CheckAll(targets, timeoutSec, 4);

                score.OkCount = report.OkCount;
                score.Total = report.Total;
                score.AvgMs = report.AvgMs;

                foreach (ProbeResult r in report.Results)
                    if (!r.Ok) score.Failed.Add(r.Name);

                scores.Add(score);

                _zapret.Stop();

                // пауза на выгрузку драйвера: без неё следующая стратегия стартует в пустоту
                Thread.Sleep(1200);

                Log.Info("DIAG", score.ToLine());
                Report(score.ToLine());
                if (score.Failed.Count > 0) Report(score.FailedLine());
            }

            // возвращаем «выключено» — тестер не должен оставлять обход включённым
            try { _zapret.Stop(); } catch { }

            scores.Sort((a, b) =>
            {
                int byRate = b.Rate.CompareTo(a.Rate);
                if (byRate != 0) return byRate;
                int byCount = b.OkCount.CompareTo(a.OkCount);
                if (byCount != 0) return byCount;
                return a.AvgMs.CompareTo(b.AvgMs);
            });

            return scores;
        }

        private void Report(string line)
        {
            Action<string> handler = Progress;
            if (handler != null) { try { handler(line); } catch { } }
        }
    }

    /// <summary>Консольные команды диагностики: --probe, --test-all.</summary>
    public static class DiagCommand
    {
        /// <summary>--fakes: список .bin-фейков с проверкой формата.</summary>
        public static int RunFakes(string[] args)
        {
            PayloadLibrary library = new PayloadLibrary();
            int total = library.Scan();

            Console.WriteLine("== фейки (" + total + ", валидных " + library.ValidCount + ") ==");
            Console.WriteLine();

            foreach (Payload p in library.All)
            {
                string mark = p.Valid ? "[ok]  " : "[БРАК]";
                string kind = Payload.KindName(p.Kind).PadRight(5);

                Console.WriteLine("  " + mark + " " + kind + " " +
                                  p.File.PadRight(38) +
                                  p.Size.ToString().PadLeft(6) + " б  " +
                                  (p.Valid ? p.Sni : p.InvalidReason));
            }

            int broken = total - library.ValidCount;
            if (broken > 0)
            {
                Console.WriteLine();
                Console.WriteLine("битых файлов: " + broken + " — они не должны попадать в аргументы winws");
            }

            return broken == 0 ? 0 : 1;
        }

        /// <summary>--update-lists: скачать ipset и исключения, показать результат.</summary>
        public static int RunUpdateLists(string[] args)
        {
            Console.WriteLine("== обновление списков ==");

            Lists.UpdateResult ipset = Lists.ListsUpdater.UpdateIpset();
            Console.WriteLine("  ipset:      " + (ipset.Ok ? "[ok] " : "[FAIL] ") + ipset.Message);

            Lists.UpdateResult exclude = Lists.ListsUpdater.UpdateExcludeDomains();
            Console.WriteLine("  исключения: " + (exclude.Ok ? "[ok] " : "[FAIL] ") + exclude.Message);

            Lists.UpdateResult ipsetExclude = Lists.ListsUpdater.UpdateIpsetExclude();
            Console.WriteLine("  ipset-excl: " + (ipsetExclude.Ok ? "[ok] " : "[FAIL] ") + ipsetExclude.Message);

            string path = Lists.ListsUpdater.IpsetPath;
            if (System.IO.File.Exists(path))
            {
                string content = System.IO.File.ReadAllText(path);
                Console.WriteLine();
                Console.WriteLine("  файл:   " + path);
                Console.WriteLine("  размер: " + (content.Length / 1024) + " КБ, CR: " +
                                  (content.IndexOf('\r') >= 0 ? "ЕСТЬ (плохо)" : "нет (хорошо)"));
            }

            return ipset.Ok ? 0 : 1;
        }

        /// <summary>
        /// --strategies: список стратегий с идентификаторами (для отладки и для --only).
        /// Заодно создаёт кэш strategies.v1.json.
        /// </summary>
        public static int RunStrategies(string[] args)
        {
            using (ZapretService svc = new ZapretService())
            {
                svc.EnsureStrategies();

                Console.WriteLine("== стратегии (" + svc.Strategies.Count + ") ==");
                foreach (Strategy s in svc.Strategies.All)
                {
                    Console.WriteLine("  " + s.Id.PadRight(32) +
                                      s.Name.PadRight(30) +
                                      "профилей: " + s.Profiles.Count.ToString().PadRight(3) +
                                      s.Note);
                }

                string cache = System.IO.Path.Combine(Paths.StrategiesDir, "strategies.v1.json");
                Console.WriteLine();
                Console.WriteLine("кэш: " + cache + (System.IO.File.Exists(cache) ? " (есть)" : " (нет)"));
                return 0;
            }
        }

        /// <summary>--probe [seconds]: базовый замер, что заблокировано без обхода.</summary>
        public static int RunProbe(string[] args)
        {
            int timeout = 10;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out timeout);

            Console.WriteLine("== базовый замер (обход выключен) ==");
            Console.WriteLine("таймаут на цель: " + timeout + " с");

            ConnectivityProbe probe = new ConnectivityProbe();
            Baseline.Snapshot snapshot = Baseline.Measure(probe, timeout, true);

            Console.WriteLine();
            Console.WriteLine("работает:      " + (snapshot.Working.Count == 0 ? "—" : string.Join(", ", snapshot.Working.ToArray())));
            Console.WriteLine("ЗАБЛОКИРОВАНО: " + (snapshot.Blocked.Count == 0 ? "—" : string.Join(", ", snapshot.Blocked.ToArray())));
            Console.WriteLine("сеть:          " + snapshot.Network);
            Console.WriteLine();

            if (snapshot.Blocked.Count == 0)
            {
                Console.WriteLine("Всё открывается — обход не нужен (или блокировок нет в этой сети).");
                return 0;
            }

            Console.WriteLine("Дальше имеет смысл тестировать только эти цели:");
            foreach (ProbeTarget t in snapshot.BlockedTargets())
                Console.WriteLine("  · " + t.Name + "  " + t.Url);

            List<string> proxyOnly = snapshot.ProxyOnly();
            if (proxyOnly.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("zapret'ом не лечится (нужен Telegram-прокси, P6): " +
                                  string.Join(", ", proxyOnly.ToArray()));
            }

            return 0;
        }

        /// <summary>
        /// --deep: ПОЧЕМУ не открывается (тип блокировки) и что с этим делать.
        /// Отвечает на вопрос «какую стратегию вообще имеет смысл пробовать».
        /// </summary>
        public static int RunDeep(string[] args)
        {
            int timeout = 8;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--deep", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out timeout);

            Baseline.Snapshot baseline = Baseline.Load();
            if (baseline == null)
            {
                Console.WriteLine("Нет базовой линии — сначала выполните: --probe");
                return 2;
            }

            Console.WriteLine("== тип блокировки ==");
            Console.WriteLine("сеть: " + baseline.Network + ", заблокировано: " + baseline.Blocked.Count);
            Console.WriteLine();

            DeepProbe probe = new DeepProbe();

            // сначала учим заглушки провайдера на заблокированных доменах
            List<string> hosts = new List<string>();
            foreach (ProbeTarget t in ConnectivityProbe.DefaultTargets())
                if (baseline.Blocked.Contains(t.Name)) hosts.Add(HostOf(t.Url));

            probe.LearnStubIps(hosts);
            if (probe.StubIpCount > 0)
                Console.WriteLine("найдено IP-заглушек провайдера: " + probe.StubIpCount);
            Console.WriteLine();

            Dictionary<BlockKind, List<string>> byKind = new Dictionary<BlockKind, List<string>>();

            foreach (ProbeTarget target in ConnectivityProbe.DefaultTargets())
            {
                bool blocked = baseline.Blocked.Contains(target.Name);
                if (!blocked && !baseline.Working.Contains(target.Name)) continue;

                string host = HostOf(target.Url);
                Console.WriteLine("  проверяю " + target.Name + " (" + host + ")...");

                string detail;
                BlockKind kind = probe.Classify(host, timeout, out detail);

                string mark = BlockKindText.IsOk(kind) ? "[ok]  " : "[БЛОК]";
                Console.WriteLine("  " + mark + " " + target.Name.PadRight(14) +
                                  BlockKindText.Short(kind).PadRight(11) +
                                  BlockKindText.Russian(kind) +
                                  (detail.Length > 0 ? "  (" + detail + ")" : ""));

                List<string> list;
                if (!byKind.TryGetValue(kind, out list)) { list = new List<string>(); byKind[kind] = list; }
                list.Add(target.Name);
            }

            Console.WriteLine();
            Console.WriteLine("== что пробовать ==");
            foreach (KeyValuePair<BlockKind, List<string>> kv in byKind)
            {
                if (BlockKindText.IsOk(kv.Key)) continue;

                string[] suggest = BlockKindText.Suggest(kv.Key);
                Console.WriteLine();
                Console.WriteLine(BlockKindText.Short(kv.Key) + " (" + BlockKindText.Russian(kv.Key) + "): " +
                                  string.Join(", ", kv.Value.ToArray()));
                if (suggest.Length == 0)
                    Console.WriteLine("   подходящих стратегий нет — нужна диагностика сети");
                else
                    foreach (string s in suggest) Console.WriteLine("   → " + s);
            }

            return 0;
        }

        private static string[] ToArray(HashSet<string> set)
        {
            List<string> list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        private static string HostOf(string url)        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        /// <summary>
        /// --test-all [лимит] [секунд_удержания] [--only id1,id2]
        /// Перебор стратегий против заблокированных целей.
        /// </summary>
        public static int RunTestAll(string[] args)
        {
            int limit = 6;
            int hold = 4;
            string only = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--test-all", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) int.TryParse(args[i + 1], out limit);
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out hold);
                }
                if (string.Equals(args[i], "--only", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    only = args[i + 1];
            }

            string foreign = WinwsSupervisor.DetectForeignWinws();
            if (foreign != null)
            {
                Console.WriteLine("ОТКАЗ: уже работает winws (" + foreign + ").");
                Console.WriteLine("       Остановите: sc stop zapret");
                return 3;
            }

            Baseline.Snapshot baseline = Baseline.Load();
            if (baseline == null || baseline.Blocked.Count == 0)
            {
                Console.WriteLine("Нет базовой линии — сначала замеряю.");
                ConnectivityProbe p = new ConnectivityProbe();
                baseline = Baseline.Measure(p, 10, true);
                Console.WriteLine();
            }

            List<ProbeTarget> targets = baseline.BlockedTargets();
            Console.WriteLine("== перебор стратегий ==");
            Console.WriteLine("заблокировано: " + string.Join(", ", baseline.Blocked.ToArray()));
            Console.WriteLine("тестирую: " + limit + " стратегий, удержание " + hold + " с");
            Console.WriteLine();

            using (ZapretService svc = new ZapretService())
            {
                svc.EnsureStrategies();

                List<string> ids = new List<string>();
                if (!string.IsNullOrEmpty(only))
                {
                    // явный список: --only flowseal.faketlsauto,flowseal.alt12
                    foreach (string raw in only.Split(','))
                    {
                        string id = raw.Trim();
                        if (id.Length == 0) continue;
                        if (svc.Strategies.Find(id) == null)
                        {
                            Console.WriteLine("нет такой стратегии: " + id);
                            continue;
                        }
                        ids.Add(id);
                    }
                }
                else
                {
                    foreach (Strategy s in svc.Strategies.All)
                    {
                        ids.Add(s.Id);
                        if (ids.Count >= limit) break;
                    }
                }

                if (ids.Count == 0)
                {
                    Console.WriteLine("нечего тестировать");
                    return 1;
                }

                StrategyTester tester = new StrategyTester(svc, new ConnectivityProbe());
                tester.Progress += line => Console.WriteLine(line);

                List<StrategyScore> scores = tester.Run(ids, baseline, hold, 8);

                Console.WriteLine();
                Console.WriteLine("== рейтинг ==");
                foreach (StrategyScore s in scores)
                {
                    Console.WriteLine(s.ToLine());
                    if (s.Failed.Count > 0) Console.WriteLine(s.FailedLine());
                }

                // что не пробивает вообще ни одна — это отдельная проблема, не выбор стратегии
                HashSet<string> stillBlocked = new HashSet<string>(StringComparer.Ordinal);
                if (scores.Count > 0)
                {
                    foreach (string f in scores[0].Failed) stillBlocked.Add(f);
                    foreach (StrategyScore s in scores)
                    {
                        HashSet<string> own = new HashSet<string>(s.Failed, StringComparer.Ordinal);
                        stillBlocked.IntersectWith(own);
                    }
                }

                if (stillBlocked.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("не пробивает НИ ОДНА стратегия: " + string.Join(", ", ToArray(stillBlocked)));
                    Console.WriteLine("   для этих целей нужен другой подход (DoH, hosts, прокси)");
                }

                StrategyScore best = scores.Count > 0 ? scores[0] : null;
                if (best != null && best.OkCount > 0)
                    Console.WriteLine("\nлучшая: " + best.Name + " (" + best.OkCount + "/" + best.Total + ")");
                else
                    Console.WriteLine("\nни одна не пробила — нужны другие цели или другой набор стратегий");

                return 0;
            }
        }
    }
}
