using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AutoZapret.Core;
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

        public double Rate { get { return Total == 0 ? 0 : (double)OkCount / Total; } }

        public string ToLine()
        {
            string mark = Rate >= 1.0 ? "🟢" : Rate > 0 ? "🟡" : "🔴";
            return mark + " " + Name.PadRight(28) + OkCount + "/" + Total +
                   "  " + AvgMs + "ms" + (Error.Length > 0 ? "  (" + Error + ")" : "");
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
                scores.Add(score);

                _zapret.Stop();

                // пауза на выгрузку драйвера: без неё следующая стратегия стартует в пустоту
                Thread.Sleep(1200);

                Log.Info("DIAG", score.ToLine());
                Report(score.ToLine());
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

            return 0;
        }

        /// <summary>
        /// --test-all [лимит] [секунд_удержания]: перебор стратегий против заблокированных целей.
        /// </summary>
        public static int RunTestAll(string[] args)
        {
            int limit = 6;
            int hold = 4;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--test-all", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) int.TryParse(args[i + 1], out limit);
                    if (i + 2 < args.Length) int.TryParse(args[i + 2], out hold);
                }
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
                foreach (Strategy s in svc.Strategies.All)
                {
                    ids.Add(s.Id);
                    if (ids.Count >= limit) break;
                }

                StrategyTester tester = new StrategyTester(svc, new ConnectivityProbe());
                tester.Progress += line => Console.WriteLine(line);

                List<StrategyScore> scores = tester.Run(ids, baseline, hold, 8);

                Console.WriteLine();
                Console.WriteLine("== рейтинг ==");
                foreach (StrategyScore s in scores) Console.WriteLine(s.ToLine());

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
