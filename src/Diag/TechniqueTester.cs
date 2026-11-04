using System;
using System.Collections.Generic;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;
using AutoZapret.Zapret.Own;

namespace AutoZapret.Diag
{
    /// <summary>Результат одной техники: сколько целей пробило и за сколько.</summary>
    public sealed class TechniqueScore
    {
        public string Name = "";
        public string Id = "";

        public int OkCount;
        public int Total;
        public int AvgMs;
        public int Score;
        public string Error = "";

        /// <summary>Почему не пробило: «таймаут ×4», «TLS ×2».</summary>
        public string Reasons = "";

        public List<string> Failed = new List<string>();

        public double Rate { get { return Total == 0 ? 0 : (double)OkCount / Total; } }

        public string ToLine()
        {
            string mark = Rate >= 1.0 ? "🟢" : Rate > 0 ? "🟡" : "🔴";

            return mark + " " + Name.PadRight(16) + OkCount + "/" + Total +
                   "  " + AvgMs + "ms  счёт " + Score.ToString().PadLeft(3) +
                   (Error.Length > 0 ? "  (" + Error + ")" : "");
        }

        public string Breakdown()
        {
            return "успех " + Math.Round(Rate * 70) + "/70, задержка " + LatencyBonus + "/20, " +
                   "стабильность " + StabilityBonus + "/10";
        }

        public int LatencyBonus;
        public int StabilityBonus;
    }

    /// <summary>
    /// Сравнение техник desync на реальном DPI.
    ///
    /// Зачем отдельно от подбора стратегий: там сравниваются конфиги winws,
    /// здесь — наши собственные техники. Вопрос один и тот же: «что реально
    /// пробивает мой провайдер», но ответ приходит от другого механизма, и
    /// смешивать их в одном счёте нельзя.
    ///
    /// Порядок важен: перед каждой техникой движок останавливается и ждёт
    /// выгрузки WinDivert. Иначе следующая техника стартует в «занятый»
    /// драйвер и получает чужой перехват — мы уже проходили это с winws.
    /// </summary>
    public static class TechniqueTester
    {
        /// <summary>Все техники, которые имеет смысл сравнивать.</summary>
        public static List<KeyValuePair<string, Technique>> All()
        {
            List<KeyValuePair<string, Technique>> list = new List<KeyValuePair<string, Technique>>();

            list.Add(new KeyValuePair<string, Technique>("split", Technique.Split));
            list.Add(new KeyValuePair<string, Technique>("multisplit", Technique.MultiSplit));
            list.Add(new KeyValuePair<string, Technique>("multidisorder", Technique.MultiDisorder));
            list.Add(new KeyValuePair<string, Technique>("hostfakesplit", Technique.HostFakeSplit));
            list.Add(new KeyValuePair<string, Technique>("fakedsplit", Technique.FakeSplit));
            list.Add(new KeyValuePair<string, Technique>("fake", Technique.Fake));
            list.Add(new KeyValuePair<string, Technique>("syndata", Technique.SynData));
            list.Add(new KeyValuePair<string, Technique>("oob", Technique.Oob));
            list.Add(new KeyValuePair<string, Technique>("wsize", Technique.WindowSize));

            return list;
        }

        public static string TitleOf(Technique kind)
        {
            switch (kind)
            {
                case Technique.Split: return "split";
                case Technique.MultiSplit: return "multisplit";
                case Technique.MultiDisorder: return "multidisorder";
                case Technique.HostFakeSplit: return "hostfakesplit";
                case Technique.FakeSplit: return "fakedsplit";
                case Technique.Fake: return "fake";
                case Technique.SynData: return "syndata";
                case Technique.Oob: return "oob";
                case Technique.WindowSize: return "wsize";
                default: return kind.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Прогнать выбранные техники против заблокированных целей.
        ///
        /// Baseline нужен, чтобы не тратить время на то, что и так открывается:
        /// если youtube работает без обхода, его успех ничего не говорит о
        /// технике. Именно на этом «всё зелёное» и получалось раньше.
        /// </summary>
        public static List<TechniqueScore> Run(IEnumerable<Technique> techniques, Baseline.Snapshot baseline,
                                               int holdSeconds, int timeoutSec,
                                               Action<int, int, string> started,
                                               Action<string> progress,
                                               CancellationToken cancel)
        {
            List<TechniqueScore> scores = new List<TechniqueScore>();
            List<Technique> list = new List<Technique>(techniques);

            List<ProbeTarget> targets = baseline != null
                ? baseline.BlockedTargets()
                : ConnectivityProbe.ZapretTargets();

            Log.Info("OWN", "сравнение техник: " + list.Count + ", целей: " + targets.Count);

            ConnectivityProbe probe = new ConnectivityProbe();
            int position = 0;

            foreach (Technique kind in list)
            {
                if (cancel.IsCancellationRequested) break;

                position++;

                TechniqueScore score = new TechniqueScore
                {
                    Id = TitleOf(kind),
                    Name = TitleOf(kind)
                };

                if (started != null) { try { started(position, list.Count, score.Name); } catch { } }
                if (progress != null) { try { progress("проверяю " + score.Name); } catch { } }

                TechniqueOptions technique = new TechniqueOptions
                {
                    Kind = kind,
                    Position = 1,
                    Repeats = 6,
                    Overlap = 40
                };

                // Много позиций для мультитехник: одна позиция не показывает
                // разницы между split и multisplit вовсе.
                technique.Positions.Add(1);
                technique.Positions.Add(120);
                technique.Positions.Add(300);

                OwnEngineOptions options = new OwnEngineOptions
                {
                    Technique = technique,
                    Seconds = 0    // управляем отменой, а не временем
                };

                OwnEngine engine = new OwnEngine(options);
                CancellationTokenSource engineCancel =
                    CancellationTokenSource.CreateLinkedTokenSource(cancel);

                Thread runner = new Thread(delegate ()
                {
                    try { engine.Run(engineCancel.Token); }
                    catch (Exception ex) { Log.Exception("OWN", "техника " + score.Name, ex); }
                })
                { IsBackground = true, Name = "own-test-" + score.Name };

                try
                {
                    runner.Start();

                    // даём драйверу встать и правилам загрузиться
                    Thread.Sleep(Math.Max(1, holdSeconds) * 1000);

                    if (engine.Stats.Errors > 0 && engine.Stats.ClientHellos == 0)
                    {
                        score.Error = "движок не увидел трафика";
                        score.Total = targets.Count;
                        scores.Add(score);

                        continue;
                    }

                    ProbeReport report = probe.CheckAll(targets, timeoutSec, 4, cancel);

                    foreach (ProbeResult r in report.Results)
                        if (!r.Ok) score.Failed.Add(r.Name);

                    score.Reasons = report.FailureSummary();

                    ComputeScore(score, report, engine.Stats.Applied);

                    Log.Info("OWN", score.ToLine());
                    if (progress != null) { try { progress(score.ToLine()); } catch { } }
                }
                finally
                {
                    // Останавливаем и ЖДЁМ: без ожидания следующая техника
                    // стартует в занятый WinDivert и меряет чужой перехват.
                    try { engineCancel.Cancel(); } catch { }
                    try { runner.Join(2500); } catch { }
                    try { engineCancel.Dispose(); } catch { }

                    Thread.Sleep(1200);
                }

                scores.Add(score);
            }

            scores.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;

                return a.AvgMs.CompareTo(b.AvgMs);
            });

            return scores;
        }

        /// <summary>
        /// Счёт 0-100 для техники. Отличается от счёта стратегий намеренно:
        /// у техники нет процесса, который может умереть, зато важнее скорость
        /// и ровность — техника, которая «иногда пробивает», на практике хуже
        /// предсказуемой.
        ///
        ///   70 × successRate — основа;
        ///   20 — задержка (до 400 мс полный бонус, от 3 с ноль);
        ///   10 — стабильность (разброс не больше 2x).
        ///
        /// applied — сколько ClientHello техника реально обработала. Ноль значит,
        /// что она не сработала ни разу, и тогда даже успешные пробы нельзя
        /// записывать ей в заслугу: скорее всего, цель открылась сама.
        /// </summary>
        public static void ComputeScore(TechniqueScore score, ProbeReport report, int applied)
        {
            score.OkCount = report.OkCount;
            score.Total = report.Total;
            score.AvgMs = report.AvgMs;

            int minMs = int.MaxValue;
            int maxMs = 0;
            int successes = 0;

            foreach (ProbeResult r in report.Results)
            {
                if (!r.Ok) continue;

                successes++;
                if (r.ElapsedMs < minMs) minMs = r.ElapsedMs;
                if (r.ElapsedMs > maxMs) maxMs = r.ElapsedMs;
            }

            score.StabilityBonus = 0;

            if (successes >= 2 && minMs > 0)
            {
                double spread = (double)maxMs / minMs;

                if (spread <= 2.0) score.StabilityBonus = 10;
                else if (spread >= 6.0) score.StabilityBonus = 0;
                else score.StabilityBonus = (int)Math.Round(10.0 * (6.0 - spread) / 4.0);
            }
            else if (successes == 1)
            {
                score.StabilityBonus = 5;
            }

            score.LatencyBonus = 0;

            if (score.OkCount > 0)
            {
                int ms = score.AvgMs;

                if (ms <= 400) score.LatencyBonus = 20;
                else if (ms >= 3000) score.LatencyBonus = 0;
                else score.LatencyBonus = (int)Math.Round(20.0 * (3000 - ms) / 2600.0);
            }

            int success = (int)Math.Round(score.Rate * 70);

            // Техника не сработала ни разу — успех не её. Иначе «0 применений,
            // 4/4 пробы» давало бы 100 баллов и врало в самую опасную сторону.
            if (applied <= 0 && success > 0) success = 0;

            score.Score = Math.Max(0, Math.Min(100, success + score.LatencyBonus + score.StabilityBonus));
        }
    }
}