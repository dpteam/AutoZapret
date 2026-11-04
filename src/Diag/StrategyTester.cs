using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Orchestrator;
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

        /// <summary>
        /// Почему пробы не прошли: «таймаут ×4», «DNS ×2, TLS ×2».
        /// Без этого «0/4» не отличить от «engine вообще не влияет» и
        /// «не работает DNS, стратегия тут ни при чём».
        /// </summary>
        public string ProbeReasons = "";

        /// <summary>+20: процесс winws жив после всех проб. Мёртвый движок не обходит ничего.</summary>
        public int ProcessBonus;

        /// <summary>+15: задержки успешных проб предсказуемы (нет «то 200 мс, то 5 с»).</summary>
        public int StabilityBonus;

        /// <summary>+10: бонус за низкую задержку.</summary>
        public int LatencyBonus;

        /// <summary>Итог 0-100: 20 процесс + 15 стабильность + 55 × successRate + 10 задержка.</summary>
        public int Score;

        public double Rate { get { return Total == 0 ? 0 : (double)OkCount / Total; } }

        /// <summary>
        /// Разбивка счёта. Без неё «78» ничего не объясняет: непонятно,
        /// стратегия медленная или просто нестабильная.
        /// </summary>
        public string Breakdown()
        {
            return "процесс " + ProcessBonus + "/20, стабильность " + StabilityBonus + "/15, " +
                   "успех " + Math.Round(Rate * 55) + "/55, задержка " + LatencyBonus + "/10";
        }

        public string ToLine()
        {
            string mark = Rate >= 1.0 ? "🟢" : Rate > 0 ? "🟡" : "🔴";
            string reasons = ProbeReasons.Length > 0 ? "  " + ProbeReasons : "";

            return mark + " " + Name.PadRight(28) + OkCount + "/" + Total +
                   "  " + AvgMs + "ms  счёт " + Score.ToString().PadLeft(3) +
                   (Error.Length > 0 ? "  (" + Error + ")" : reasons);
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

        /// <summary>
        /// Началась очередная стратегия: (номер с 1, всего, имя).
        /// Отдельное событие, потому что Progress — это свободный текст для лога,
        /// а тут нужны числа: по ним UI рисует полосу и «сколько осталось».
        /// </summary>
        public event Action<int, int, string> StrategyStarted;

        public List<StrategyScore> Run(IEnumerable<string> strategyIds, Baseline.Snapshot baseline,
                                       int holdSeconds, int timeoutSec,
                                       CancellationToken cancel = default(CancellationToken))
        {
            List<ProbeTarget> targets = baseline != null ? baseline.BlockedTargets()
                                                         : ConnectivityProbe.DefaultTargets();

            List<StrategyScore> scores = new List<StrategyScore>();
            List<string> ids = new List<string>(strategyIds);

            Log.Info("DIAG", "перебор стратегий: " + ids.Count + ", целей: " + targets.Count);

            int position = 0;

            foreach (string id in ids)
            {
                // Отмена: человек нажал «Отменить» — не запускаем следующую
                // стратегию, а гасим движок и выходим. Проверка до старта,
                // потому что каждая итерация занимает 10-20 секунд.
                if (cancel.IsCancellationRequested)
                {
                    Report("отменено");
                    break;
                }

                StrategyScore score = new StrategyScore { Id = id };
                Strategy strategy = _zapret.Strategies.Find(id);
                score.Name = strategy != null ? strategy.Name : id;

                position++;

                Action<int, int, string> started = StrategyStarted;
                if (started != null) { try { started(position, ids.Count, score.Name); } catch { } }

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

                foreach (ProbeResult r in report.Results)
                    if (!r.Ok) score.Failed.Add(r.Name);

                // почему не пробило — до остановки движка, пока его вывод ещё наш
                score.ProbeReasons = report.FailureSummary();

                // ---- счёт: считается ДО остановки, иначе «процесс жив» всегда false.
                // OkCount/Total/AvgMs заполняет сам ComputeScore — из отчёта.
                bool alive = WinwsSupervisor.AnyWinwsRunning();

                ComputeScore(score, report, alive);

                // Ни одна проба не прошла — показываем, что говорил сам движок.
                // Его молчание тоже информация: аргументы приняты, но фильтр
                // не поймал ни одного пакета (не тот порт/список/движок).
                if (score.OkCount == 0)
                {
                    List<string> fromEngine = _zapret.EngineOutput(6);

                    if (fromEngine.Count > 0)
                    {
                        Log.Warn("DIAG", "движок сказал: " + string.Join(" | ", fromEngine.ToArray()));
                        Report("   движок: " + string.Join(" | ", fromEngine.ToArray()));
                    }
                    else
                    {
                        Report("   движок молчит: аргументы приняты, но ни один пакет не перехвачен");
                    }
                }

                scores.Add(score);

                _zapret.Stop();

                // пауза на выгрузку драйвера: без неё следующая стратегия стартует в пустоту
                Thread.Sleep(1200);

                Log.Info("DIAG", score.ToLine());
                Report(score.ToLine());
                Report("   " + score.Breakdown());
                if (score.Failed.Count > 0) Report(score.FailedLine());
            }

            // возвращаем «выключено» — тестер не должен оставлять обход включённым
            try { _zapret.Stop(); } catch { }

            scores.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int byRate = b.Rate.CompareTo(a.Rate);
                if (byRate != 0) return byRate;
                int byCount = b.OkCount.CompareTo(a.OkCount);
                if (byCount != 0) return byCount;
                return a.AvgMs.CompareTo(b.AvgMs);
            });

            return scores;
        }

        /// <summary>
        /// Счёт 0-100. Почему не «только successRate»: стратегия, которая пробивает
        /// всё, но с задержкой 4 секунды или с процессом, падающим через минуту,
        /// на практике хуже, чем 4/5 за 300 мс. Одного процента для этого мало.
        ///
        ///   20 — процесс жив после проб (мёртвый winws не обходит ничего);
        ///   15 — стабильность: разброс задержек успешных проб не больше 2x;
        ///   55 × successRate — основная часть;
        ///   10 — задержка (полный бонус до 400 мс, ноль от 3 с).
        ///
        /// Чистая функция без побочных эффектов — её проверяет selftest.
        /// </summary>
        public static void ComputeScore(StrategyScore score, ProbeReport report, bool alive)
        {
            // ---- все цифры берём ИЗ ОТЧЁТА, а не из score: иначе функция молча
            // зависит от того, что вызывающий уже успел заполнить, и на синтетике
            // в тестах выдаёт ноль. Один источник правды.
            score.OkCount = report.OkCount;
            score.Total = report.Total;
            score.AvgMs = report.AvgMs;

            score.ProcessBonus = alive ? 20 : 0;

            // ---- стабильность: по разбросу задержек среди УСПЕШНЫХ проб.
            // Упавшие пробы сюда не идут: они уже наказаны через successRate,
            // иначе одна и та же проблема штрафуется дважды.
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

                if (spread <= 2.0) score.StabilityBonus = 15;
                else if (spread >= 6.0) score.StabilityBonus = 0;
                else score.StabilityBonus = (int)Math.Round(15.0 * (6.0 - spread) / 4.0);
            }
            else if (successes == 1)
            {
                // одна проба: разброс не измерить, но и повода штрафовать нет
                score.StabilityBonus = 7;
            }

            // ---- задержка: до 400 мс полный бонус, от 3 с нулевой
            score.LatencyBonus = 0;

            if (score.OkCount > 0)
            {
                int ms = score.AvgMs;
                if (ms <= 400) score.LatencyBonus = 10;
                else if (ms >= 3000) score.LatencyBonus = 0;
                else score.LatencyBonus = (int)Math.Round(10.0 * (3000 - ms) / 2600.0);
            }

            int total = score.ProcessBonus + score.StabilityBonus +
                        (int)Math.Round(55.0 * score.Rate) + score.LatencyBonus;

            score.Score = Math.Max(0, Math.Min(100, total));
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
        /// <summary>--update-engine: скачать и применить движок (с остановкой обхода).</summary>
        public static int RunUpdateEngine(string[] args)
        {
            Console.WriteLine("== обновление движка ==");
            Console.WriteLine();

            Update.EngineUpdater updater = new Update.EngineUpdater();
            updater.Progress += line => Console.WriteLine("  " + line);

            Update.UpdateCheck check = updater.Check();
            Console.WriteLine("  " + check.Message);

            if (!check.HasUpdate && !HasFlagLocal(args, "--force"))
            {
                Console.WriteLine();
                Console.WriteLine("обновление не требуется (используйте --force для переустановки)");
                return 0;
            }

            Console.WriteLine();
            string error = updater.Install(delegate
            {
                // останавливаем чужой winws/winws2, если он есть
                foreach (string name in new[] { "winws", "winws2" })
                {
                    System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName(name);
                    foreach (System.Diagnostics.Process p in procs)
                    {
                        try { p.Kill(); } catch { }
                        try { p.Dispose(); } catch { }
                    }
                }
                System.Threading.Thread.Sleep(1500);
                return true;
            });

            Console.WriteLine();
            if (error != null)
            {
                Console.WriteLine("ОШИБКА: " + error);
                return 1;
            }

            Console.WriteLine("готово, версия: " + updater.Check().CurrentVersion);
            return 0;
        }

        private static bool HasFlagLocal(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>--check-updates: проверить версии движка и приложения.</summary>
        public static int RunCheckUpdates(string[] args)
        {
            Console.WriteLine("== проверка обновлений ==");
            Console.WriteLine();

            Update.EngineUpdater engine = new Update.EngineUpdater();
            engine.Progress += line => Console.WriteLine("  " + line);
            Update.UpdateCheck engineCheck = engine.Check();

            Console.WriteLine("  движок:      " + (engineCheck.Ok ? "[ok] " : "[FAIL] ") + engineCheck.Message);
            if (engineCheck.Ok)
                Console.WriteLine("               сейчас " +
                                  (engineCheck.CurrentVersion.Length > 0 ? engineCheck.CurrentVersion : "—") +
                                  " → " + engineCheck.LatestVersion +
                                  (engineCheck.HasUpdate ? "  (есть обновление)" : "  (актуально)"));

            Update.SelfUpdater app = new Update.SelfUpdater();
            app.Progress += line => Console.WriteLine("  " + line);
            Update.UpdateCheck appCheck = app.Check(true);

            Console.WriteLine("  приложение:  " + (appCheck.Ok ? "[ok] " : "[FAIL] ") + appCheck.Message);
            if (appCheck.Ok)
                Console.WriteLine("               сейчас " + appCheck.CurrentVersion + " → " + appCheck.LatestVersion);

            Console.WriteLine();
            Console.WriteLine("версии сравниваются по числам: 1.10.4 > 1.9 (а не как строки)");
            return 0;
        }

        /// <summary>
        /// --tcp16 [count]: тест обрыва по объёму (16-20 КБ).
        /// Самый частый тип блокировки в РФ: handshake проходит, поток рвётся.
        /// </summary>
        public static int RunTcp16(string[] args)
        {
            int count = 8;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--tcp16", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    int.TryParse(args[i + 1], out count);

            Console.WriteLine("== тест обрыва по объёму (TCP 16-20 КБ) ==");

            string path = System.IO.Path.Combine(Paths.BaseDir, "assets", "diag", "tcp16.json");
            List<Tcp16Target> targets = Tcp16Test.LoadTargets(path);

            string source = "assets\\diag\\tcp16.json";
            if (targets.Count == 0)
            {
                targets = Tcp16Test.BuiltinTargets();
                source = "встроенный список";
            }

            Console.WriteLine("источник: " + source + " (" + targets.Count + " целей)");

            // берём разные провайдеры, а не первые N подряд
            List<Tcp16Target> picked = PickDiverse(targets, Math.Max(1, count));

            Console.WriteLine("проверяю " + picked.Count + " разных провайдеров, по " +
                              Tcp16Test.TargetBytes / 1024 + " КБ на соединение");
            Console.WriteLine();

            List<Tcp16Result> results = Tcp16Test.ProbeAll(picked, 6000, 4);

            foreach (Tcp16Result r in results)
                Console.WriteLine("  " + r.ToLine());

            Console.WriteLine();
            Console.WriteLine(Tcp16Test.Verdict(results));

            int blocked = 0;
            foreach (Tcp16Result r in results) if (r.LooksBlocked) blocked++;

            return blocked == 0 ? 0 : 2;
        }

        /// <summary>Берёт цели от разных провайдеров, чтобы выборка была осмысленной.</summary>
        private static List<Tcp16Target> PickDiverse(List<Tcp16Target> targets, int count)
        {
            List<Tcp16Target> result = new List<Tcp16Target>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Tcp16Target t in targets)
            {
                string key = t.Provider;
                if (seen.Contains(key)) continue;

                seen.Add(key);
                result.Add(t);
                if (result.Count >= count) break;
            }

            // если провайдеров меньше, чем нужно, добираем остальными
            if (result.Count < count)
            {
                foreach (Tcp16Target t in targets)
                {
                    if (result.Contains(t)) continue;
                    result.Add(t);
                    if (result.Count >= count) break;
                }
            }

            return result;
        }

        /// <summary>
        /// --gen-fake --sni домен [--type tls|quic] [--size N] [--out каталог] [--all]
        /// Свой генератор фейков: собрать payload под конкретный домен.
        /// </summary>
        public static int RunGenFake(string[] args)
        {
            string sni = null;
            string type = "tls";
            string outDir = Paths.FakesDir;
            int size = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--sni", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    sni = args[i + 1];

                if (string.Equals(args[i], "--type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    type = args[i + 1].ToLowerInvariant();

                if (string.Equals(args[i], "--size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out size);

                if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outDir = args[i + 1];
            }

            if (string.IsNullOrEmpty(sni))
            {
                Console.WriteLine("использование: AutoZapret.exe --gen-fake --sni <домен> [--type tls|quic] [--size N]");
                Console.WriteLine("               [--out <каталог>] [--all]");
                return 1;
            }

            Console.WriteLine("== генератор фейков ==");
            Console.WriteLine("домен:  " + sni);
            Console.WriteLine("каталог: " + outDir);
            Console.WriteLine();

            int created = 0;

            if (type == "tls" || type == "all")
            {
                GeneratedFake fake = FakeGenerator.TlsClientHello(sni, size > 0 ? size : FakeGenerator.DefaultTlsSize);
                created += Report(fake, outDir);
            }

            if (type == "quic" || type == "all")
            {
                GeneratedFake fake = FakeGenerator.QuicInitial(size > 0 ? size : FakeGenerator.QuicSize, sni);
                created += Report(fake, outDir);
            }

            bool allTypes = type == "all";
            if (!allTypes && type != "tls" && type != "quic")
            {
                Console.WriteLine("неизвестный тип: " + type + " (ожидалось tls | quic | all)");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine(created == 0 ? "ничего не создано" : "создано: " + created);

            return created > 0 ? 0 : 1;
        }

        private static int Report(GeneratedFake fake, string outDir)
        {
            if (!fake.Valid)
            {
                Console.WriteLine("  ОШИБКА: " + fake.Error);
                return 0;
            }

            string error;
            string path = FakeGenerator.Save(fake, outDir, out error);

            if (path == null)
            {
                Console.WriteLine("  не удалось сохранить: " + error);
                return 0;
            }

            Console.WriteLine("  " + Path.GetFileName(path));
            Console.WriteLine("     " + fake.Describe());

            // перечитываем с диска: файл должен проходить валидатор, а не только память
            Payload check = PayloadValidator.Inspect(path);
            Console.WriteLine("     проверка с диска: " +
                              (check.Valid ? "ok" : "ПРОВАЛ — " + check.InvalidReason) +
                              (check.Kind == PayloadKind.TlsClientHello
                                  ? (check.Sni.Length > 0 ? ", SNI читается: " + check.Sni : ", SNI не найден")
                                  : ", SNI в открытом виде отсутствует (так и должно быть)"));

            return check.Valid ? 1 : 0;
        }

        /// <summary>--sha256 &lt;файл&gt;: посчитать хеш и сверить с checksums.txt рядом, если он есть.</summary>
        public static int RunSha256(string[] args)
        {
            string path = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--sha256", StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) path = args[i + 1];
            }

            if (string.IsNullOrEmpty(path))
            {
                Console.WriteLine("использование: AutoZapret.exe --sha256 <файл>");
                return 1;
            }

            string full = System.IO.Path.GetFullPath(path);

            if (!System.IO.File.Exists(full))
            {
                Console.WriteLine("файл не найден: " + full);
                return 1;
            }

            string hash = AutoZapret.Update.Checksums.Sha256File(full);

            if (hash.Length == 0)
            {
                Console.WriteLine("не удалось посчитать SHA-256");
                return 1;
            }

            Console.WriteLine("файл:   " + System.IO.Path.GetFileName(full));
            Console.WriteLine("размер: " + new System.IO.FileInfo(full).Length + " байт");
            Console.WriteLine("sha256: " + hash);

            // если рядом лежит checksums.txt — сразу сверяем
            string checksumsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full), "checksums.txt");

            if (System.IO.File.Exists(checksumsPath))
            {
                try
                {
                    System.Collections.Generic.Dictionary<string, string> map =
                        AutoZapret.Update.Checksums.Parse(System.IO.File.ReadAllText(checksumsPath));

                    AutoZapret.Update.ChecksumResult result =
                        AutoZapret.Update.Checksums.Verify(full, map);

                    Console.WriteLine("сверка: " + result.Text());
                    return result.Ok ? 0 : 2;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("checksums.txt не прочитан: " + ex.Message);
                }
            }

            return 0;
        }

        /// <summary>
        /// --netcap [секунды]: посмотреть, что реально идёт по TCP/443.
        ///
        /// Работает в режиме SNIFF: WinDivert отдаёт КОПИИ пакетов, не задерживая
        /// трафик. Это принципиально: первый слой своего движка не должен уметь
        /// ломать сеть. Перехват и модификация — следующий этап, когда видно,
        /// что копирование работает.
        /// </summary>
        public static int RunNetCap(string[] args)
        {
            int seconds = 5;

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--netcap", StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    int.TryParse(args[i + 1], out seconds);
            }

            if (seconds <= 0) seconds = 5;

            Console.WriteLine("== netcap: sniff TCP " + seconds + " с ==");

            SniffResult result = TrafficSniffer.Run(seconds);

            if (!result.Ok)
            {
                Console.WriteLine(result.Error);
                return 1;
            }

            Console.WriteLine(result.Summary());

            if (result.Snis.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("домены в SNI:");
                foreach (string sni in result.Snis) Console.WriteLine("  " + sni);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(result.Verdict());
            }

            return result.Total > 0 ? 0 : 2;
        }

        /// <summary>
        /// --stages [host...]: пошагово DNS -> TCP -> TLS по целям.
        ///
        /// Отвечает на вопрос, который проба через curl не различает: не работает
        /// DNS, не соединяется TCP (блокировка по IP) или рвётся TLS (DPI).
        /// Без движка и без прав — это чистая проверка сети.
        /// </summary>
        public static int RunStages(string[] args)
        {
            List<string> hosts = new List<string>();

            foreach (string a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (a.Length > 3 && a.IndexOf('.') > 0) hosts.Add(a);
            }

            if (hosts.Count == 0)
            {
                hosts.Add("www.youtube.com");
                hosts.Add("discord.com");
                hosts.Add("www.google.com");
            }

            Console.WriteLine("== по шагам: DNS -> TCP -> TLS ==");
            Console.WriteLine();

            List<ProbeTarget> targets = new List<ProbeTarget>();
            foreach (string h in hosts) targets.Add(new ProbeTarget(h, "https://" + h));

            List<TargetStage> stages = TargetDiagnosis.Run(targets, 6000);

            foreach (TargetStage s in stages)
            {
                Console.WriteLine(s.Name);
                Console.WriteLine("  DNS: " + s.Dns);
                Console.WriteLine("  TCP: " + s.Tcp);
                Console.WriteLine("  TLS: " + s.Tls);
                Console.WriteLine("  итог: " + s.Verdict());

                if (!string.IsNullOrEmpty(s.SubstitutionNote))
                    Console.WriteLine("  ! " + s.SubstitutionNote);

                Console.WriteLine();
            }

            int broken = 0;
            foreach (TargetStage s in stages) if (!s.TlsOk) broken++;

            return broken == 0 ? 0 : 2;
        }

        /// <summary>
        /// --own [секунды] [--split N] [--dry]: свой движок, техника split.
        ///
        /// Первый живой прогон P10.3: перехват TCP/443, разрез TLS ClientHello,
        /// пересчёт сумм, отправка двух частей. `--dry` только показывает, что
        /// было бы сделано — безопасный первый запуск.
        /// </summary>
        public static int RunOwn(string[] args)
        {
            AutoZapret.Zapret.Own.OwnEngineOptions options = new AutoZapret.Zapret.Own.OwnEngineOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (string.Equals(a, "--dry", StringComparison.OrdinalIgnoreCase))
                {
                    options.DryRun = true;
                    continue;
                }

                if (string.Equals(a, "--split", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out options.Technique.Position);
                    continue;
                }

                // --tech split|multisplit|fake|fakedsplit
                if (string.Equals(a, "--tech", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string name = args[i + 1].Trim().ToLowerInvariant();

                    if (name == "multisplit") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.MultiSplit;
                    else if (name == "multidisorder") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.MultiDisorder;
                    else if (name == "hostfakesplit") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.HostFakeSplit;
                    else if (name == "syndata") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.SynData;
                    else if (name == "oob") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.Oob;
                    else if (name == "wsize") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.WindowSize;
                    else if (name == "udplen") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.UdpLen;
                    else if (name == "fake") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.Fake;
                    else if (name == "fakedsplit") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.FakeSplit;
                    else if (name == "off") options.Technique.Kind = AutoZapret.Zapret.Own.Technique.Off;
                    else options.Technique.Kind = AutoZapret.Zapret.Own.Technique.Split;

                    continue;
                }

                // --overlap N — перекрытие кусков для hostfakesplit (seqovl)
                if (string.Equals(a, "--overlap", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out options.Technique.Overlap);
                    continue;
                }

                // --pos 1,5,10 — несколько позиций для multisplit
                if (string.Equals(a, "--pos", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Technique.Positions.Clear();

                    foreach (string part in args[i + 1].Split(','))
                    {
                        int p;
                        if (int.TryParse(part.Trim(), out p) && p > 0) options.Technique.Positions.Add(p);
                    }

                    continue;
                }

                // --repeats N — сколько раз повторить фейк
                if (string.Equals(a, "--repeats", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out options.Technique.Repeats);
                    continue;
                }

                // --fake-file path.bin
                if (string.Equals(a, "--fake-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Technique.FakeFile = args[i + 1];
                    continue;
                }

                // --badsum — испортить сумму у фейка
                if (string.Equals(a, "--badsum", StringComparison.OrdinalIgnoreCase))
                {
                    options.Technique.BadSum = true;
                    continue;
                }

                if (string.Equals(a, "--own", StringComparison.OrdinalIgnoreCase)) continue;

                int seconds;
                if (int.TryParse(a, out seconds) && seconds > 0) options.Seconds = seconds;
            }

            if (options.Seconds <= 0) options.Seconds = 20;

            if (!AutoZapret.App.Elevation.IsElevated)
            {
                Console.WriteLine("нужны права администратора: WinDivert без них не открывается");
                return 1;
            }

            Console.WriteLine("== свой движок: " + options.Technique.Describe() +
                              ", " + options.Seconds + " с" + (options.DryRun ? ", показ" : "") + " ==");
            Console.WriteLine("откройте https://www.youtube.com в браузере во время работы");
            Console.WriteLine();

            AutoZapret.Zapret.Own.OwnEngine engine = new AutoZapret.Zapret.Own.OwnEngine(options);

            System.Threading.CancellationTokenSource cancel =
                new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds + 5));

            string error = engine.Run(cancel.Token);

            if (error != null)
            {
                Console.WriteLine(error);
                return 1;
            }

            Console.WriteLine(engine.Stats.Summary());
            return engine.Stats.Applied > 0 ? 0 : 2;
        }

        /// <summary>
        /// --flushdns: сбросить кэш DNS. Права не нужны.
        ///
        /// Отдельная команда, потому что это первое, что нужно после правки
        /// hosts: без сброса Windows продолжает отдавать старый адрес, и правка
        /// выглядит несработавшей.
        /// </summary>
        public static int RunFlushDns()
        {
            string error = AutoZapret.Core.DnsCache.Flush();

            if (error == null)
            {
                Console.WriteLine("кэш DNS сброшен");
                return 0;
            }

            Console.WriteLine(error);
            return 1;
        }

        /// <summary>
        /// --browser-doh [status|enable|disable]: Secure DNS в браузерах.
        ///
        /// Провайдер подменяет DNS, и браузер через системный резолв приходит на
        /// заглушку — сайт не открывается, хотя обход работает. Здесь мы включаем
        /// браузерам их собственный DoH (политики Chromium / user.js Firefox).
        /// </summary>
        public static int RunBrowserDoh(string[] args)
        {
            string action = "status";

            foreach (string a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;

                string v = a.Trim().ToLowerInvariant();
                if (v == "enable" || v == "disable" || v == "status") action = v;
            }

            if (action == "enable")
            {
                Console.WriteLine(AutoZapret.Core.BrowserDoh.Enable());
                return 0;
            }

            if (action == "disable")
            {
                Console.WriteLine(AutoZapret.Core.BrowserDoh.Disable());
                return 0;
            }

            Console.WriteLine("== Secure DNS в браузерах ==");
            Console.WriteLine("шаблон: " + AutoZapret.Core.BrowserDoh.Template);
            Console.WriteLine();

            bool any = false;

            foreach (AutoZapret.Core.BrowserInfo b in AutoZapret.Core.BrowserDoh.Detect())
            {
                if (!b.Installed) continue;

                any = true;
                Console.WriteLine("  " + b.Name.PadRight(18) + (b.DohOn ? "включён" : "выключен") +
                                  (b.Detail.Length > 0 ? "  (" + b.Detail + ")" : ""));
            }

            if (!any) Console.WriteLine("  браузеры не найдены");

            return 0;
        }

        /// <summary>
        /// --sysdns [status|enable|disable|serve]: системный DNS через DoH.
        ///
        /// `enable` поднимает локальный резолвер и переключает на него адаптер,
        /// `serve` только держит резолвер запущенным (отладка), `disable` возвращает.
        /// Windows 10 своего DoH не имеет — это единственный способ дать его всем.
        /// </summary>
        public static int RunSystemDns(string[] args)
        {
            string action = "status";
            int port = 53;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                    continue;
                }

                if (a.StartsWith("--", StringComparison.Ordinal)) continue;

                string v = a.Trim().ToLowerInvariant();
                if (v == "enable" || v == "disable" || v == "status" || v == "serve") action = v;
            }

            if (port <= 0) port = 53;

            // Смена системного DNS требует прав; «serve» на высоком порту — нет,
            // это режим отладки самого резолвера.
            if ((action == "enable" || action == "disable" || (action == "serve" && port == 53)) &&
                !AutoZapret.App.Elevation.IsElevated)
            {
                Console.WriteLine("нужны права администратора");
                return 1;
            }

            if (action == "status")
            {
                Console.WriteLine("== системный DNS ==");
                Console.WriteLine("  состояние: " + AutoZapret.Core.SystemDns.StatusText());

                string adapter;
                string localIp = "";
                adapter = AutoZapret.Core.SystemDns.DefaultRouteAdapter(out localIp);

                Console.WriteLine("  интернет:  " + (adapter == null ? "нет маршрута" : adapter + " (" + localIp + ")"));
                Console.WriteLine("  DoH:       " + AutoZapret.Core.DohServers.Recommended());

                return 0;
            }

            if (action == "disable")
            {
                string off = AutoZapret.Core.SystemDns.Disable();
                Console.WriteLine(off ?? "системный DNS возвращён");
                return off == null ? 0 : 1;
            }

            AutoZapret.Core.LocalDns dns = new AutoZapret.Core.LocalDns();
            dns.Port = port;

            string error = dns.Start();

            if (error != null)
            {
                Console.WriteLine(error);
                return 1;
            }

            if (action == "serve")
            {
                Console.WriteLine("локальный DNS работает. Ctrl+C — выход.");

                try
                {
                    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                }
                catch { }

                dns.Stop();
                return 0;
            }

            string dnsError = AutoZapret.Core.SystemDns.Enable();

            if (dnsError != null)
            {
                dns.Stop();
                Console.WriteLine(dnsError);
                return 1;
            }

            Console.WriteLine("системный DNS включён: " + AutoZapret.Core.SystemDns.StatusText());
            Console.WriteLine("резолвер: " + dns.StatusLine());
            Console.WriteLine();
            Console.WriteLine("Проверьте: --stages www.youtube.com  (DNS должен стать настоящим)");

            dns.Stop();
            return 0;
        }

        /// <summary>
        /// --preflight [стратегия]: проверить готовность перед запуском.
        /// Без аргумента проверяется окружение без привязки к стратегии.
        /// </summary>
        public static int RunPreflight(string[] args)
        {
            string strategyId = null;

            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--preflight", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    strategyId = args[i + 1];

            Console.WriteLine("== проверка готовности ==");
            Console.WriteLine();

            using (ZapretService svc = new ZapretService())
            {
                svc.EnsureStrategies();

                List<PreflightProblem> problems = Preflight.Run(
                    svc.Engine, svc.Strategies, strategyId, Settings.Current.GameFilter);

                if (problems.Count == 0)
                {
                    Console.WriteLine("проблем нет — можно запускать");
                    return 0;
                }

                foreach (PreflightProblem p in problems)
                {
                    Console.WriteLine((p.Level == PreflightLevel.Blocker ? "[БЛОК] " : "[warn] ") + p.Text());
                }

                Console.WriteLine();
                Console.WriteLine(Preflight.Summary(problems));

                return Preflight.HasBlockers(problems) ? 1 : 0;
            }
        }

        /// <summary>
        /// --install-engine: скачать движок из приложения (без git и без консоли).
        /// </summary>
        public static int RunInstallEngine(string[] args)
        {
            Console.WriteLine("== установка движка ==");
            Console.WriteLine();

            EngineInstaller installer = new EngineInstaller();
            installer.Progress += line => Console.WriteLine("  " + line);

            EngineInstallResult result = installer.Install();

            Console.WriteLine();

            if (!result.Ok)
            {
                Console.WriteLine("не удалось: " + result.Error);
                return 1;
            }

            Console.WriteLine("движок установлен, версия " +
                              (result.Version.Length > 0 ? result.Version : "неизвестна"));

            if (result.Note.Length > 0) Console.WriteLine("примечание: " + result.Note);

            // проверяем, что EngineLocator видит установленный движок
            EngineLocator locator = new EngineLocator();
            locator.Refresh();

            Console.WriteLine("v1 доступен: " + (locator.HasV1 ? "да" : "НЕТ"));
            Console.WriteLine("v2 доступен: " + (locator.HasV2 ? "да" : "нет"));

            if (!locator.Ready)
            {
                Console.WriteLine("движок не готов: " + locator.Problem);
                return 1;
            }

            return 0;
        }

        /// <summary>--presets: показать пресеты по процессам и что совпадает сейчас.</summary>
        public static int RunPresets(string[] args)
        {
            Console.WriteLine("== пресеты по процессам ==");
            Console.WriteLine("файл: " + ProcessPresets.FilePath);
            Console.WriteLine();

            List<ProcessPreset> presets = ProcessPresets.Load(ProcessPresets.FilePath);
            string source = "файл";

            if (presets.Count == 0)
            {
                presets = ProcessPresets.Builtin();
                source = "встроенный набор";
            }

            Console.WriteLine("источник: " + source + ", пресетов: " + presets.Count);
            Console.WriteLine("включено глобально: " + (Settings.Current.ProcessPresets ? "да" : "нет"));
            Console.WriteLine();

            foreach (ProcessPreset preset in presets)
            {
                string mark = preset.Enabled ? "[on] " : "[off]";
                Console.WriteLine(mark + " " + preset.Describe());

                foreach (string process in preset.Processes)
                    if (ProcessPresets.IsRunning(process))
                        Console.WriteLine("       запущен: " + process);

                if (!preset.ChangesSomething)
                    Console.WriteLine("       ВНИМАНИЕ: пресет ничего не меняет");
            }

            Console.WriteLine();

            ProcessPreset active = ProcessPresets.Match(presets, ProcessPresets.IsRunning);
            Console.WriteLine(active == null
                ? "сейчас не совпадает ни один пресет"
                : "сейчас совпадает: " + active.Name);

            return 0;
        }

        /// <summary>--diagnose: 12 проверок окружения (почему не работает).</summary>
        public static int RunDiagnose(string[] args)
        {
            Console.WriteLine("== проверки окружения ==");
            Console.WriteLine();

            List<CheckResult> results = SystemChecks.RunAll(name => Console.WriteLine("  проверяю " + name + "…"));

            Console.WriteLine();

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

                Console.WriteLine(mark + " " + r.Name.PadRight(28) + r.Detail);

                if (r.Advice.Length > 0)
                    Console.WriteLine("       → " + r.Advice);
            }

            Console.WriteLine();
            if (problems == 0 && warnings == 0)
                Console.WriteLine("всё в порядке");
            else
                Console.WriteLine("проблем: " + problems + ", предупреждений: " + warnings);

            return problems == 0 ? 0 : 1;
        }

        /// <summary>--diag-zip [путь]: собрать диагностический архив для поддержки.</summary>
        public static int RunDiagZip(string[] args)
        {
            string target = null;
            bool deep = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--deep", StringComparison.OrdinalIgnoreCase)) deep = true;

                // путь может стоять в любом порядке относительно --deep
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) target = args[i];
            }

            Console.WriteLine("== диагностический архив" + (deep ? " (расширенный)" : "") + " ==");
            Console.WriteLine();

            Settings settings = Settings.Load();

            StrategyStore store = null;
            try
            {
                using (ZapretService svc = new ZapretService())
                {
                    svc.EnsureStrategies();
                    store = svc.Strategies;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("стратегии недоступны: " + ex.Message);
            }

            BundleResult result = DiagnosticBundle.Create(
                target ?? DiagnosticBundle.DefaultDirectory(), settings, store,
                deep
                    ? DeepReportOptions.Extended(line => Console.WriteLine("  " + line))
                    : DeepReportOptions.Fast());

            if (!result.Ok)
            {
                Console.WriteLine("не удалось: " + result.Error);
                return 1;
            }

            Console.WriteLine("файл:  " + result.Path);
            Console.WriteLine("размер: " + result.SizeText());
            Console.WriteLine();
            Console.WriteLine("содержимое:");

            foreach (string entry in result.Entries)
                Console.WriteLine("  " + entry);

            Console.WriteLine();
            Console.WriteLine("секрет прокси вырезан, пути оставлены — см. README.txt внутри архива");

            return 0;
        }

        /// <summary>--import-fakes: импортировать набор TLSpack в assets\fakes.</summary>
        public static int RunImportFakes(string[] args)
        {
            string source = null;
            string target = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--import-fakes", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    source = args[i + 1];

                if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                    target = args[i + 1];
            }

            if (string.IsNullOrEmpty(source)) source = Payloads.PayloadImporter.FindSource();

            Console.WriteLine("== импорт библиотеки фейков ==");
            Console.WriteLine("источник: " + (source ?? "не найден"));
            Console.WriteLine("куда:     " + (target ?? Paths.FakesDir));
            Console.WriteLine();

            if (source == null)
            {
                Console.WriteLine("ОШИБКА: набор не найден.");
                Console.WriteLine("        Ожидается refs\\TLSpack (HatabaSS/zapret-custom-binaries).");
                Console.WriteLine("        Скачать: git clone https://github.com/HatabaSS/zapret-custom-binaries refs/TLSpack");
                return 1;
            }

            Payloads.PayloadImportResult result =
                Payloads.PayloadImporter.Import(source, target, line => Console.WriteLine("  " + line));

            Console.WriteLine();
            if (!result.Ok)
            {
                Console.WriteLine("ОШИБКА: " + result.Message);
                return 1;
            }

            Console.WriteLine("  просканировано: " + result.Scanned);
            Console.WriteLine("  валидных:      " + result.Valid);
            Console.WriteLine("  отбраковано:   " + result.SkippedInvalid);
            Console.WriteLine("  дубликатов:    " + result.Duplicates);
            Console.WriteLine("  импортировано: " + result.Imported);
            Console.WriteLine();

            // показываем по типам
            int tls = 0, quic = 0, stun = 0;
            foreach (string name in result.ImportedNames)
            {
                if (name.StartsWith("tls_", StringComparison.OrdinalIgnoreCase)) tls++;
                else if (name.StartsWith("quic_", StringComparison.OrdinalIgnoreCase)) quic++;
                else stun++;
            }
            Console.WriteLine("  TLS: " + tls + ", QUIC: " + quic + ", прочее: " + stun);

            if (result.Problems.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  проблемы:");
                foreach (string p in result.Problems) Console.WriteLine("    " + p);
            }

            // проверяем, что библиотека теперь видит новые файлы
            Payloads.PayloadLibrary library = new Payloads.PayloadLibrary();
            int total = library.Scan();
            Console.WriteLine();
            Console.WriteLine("  библиотека видит: " + total + " фейков, валидных " + library.ValidCount);

            return 0;
        }

        /// <summary>
        /// --auto-once [strategyId]: один цикл автопилота (запустить обход, проверить,
        /// при провале переключиться). Нужны права администратора.
        /// </summary>
        public static int RunAutoOnce(string[] args)
        {
            string strategyId = "flowseal.general-alt12";

            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--auto-once", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    strategyId = args[i + 1];

            Console.WriteLine("== один цикл автопилота ==");
            Console.WriteLine("стратегия: " + strategyId);

            using (ZapretService svc = new ZapretService())
            {
                AutoZapret.Orchestrator.ScoreStore store = new AutoZapret.Orchestrator.ScoreStore();
                store.Load();
                svc.EnsureStrategies();

                string error = svc.Start(strategyId);
                if (error != null)
                {
                    Console.WriteLine("не удалось запустить обход: " + error);
                    return 1;
                }

                Console.WriteLine("обход запущен, ждём 5 с…");
                System.Threading.Thread.Sleep(5000);

                using (Orchestrator.AutoPilot pilot = new Orchestrator.AutoPilot(svc, store))
                {
                    pilot.StatusChanged += status =>
                        Console.WriteLine("  [" + status.State + "] " + status.Message);

                    pilot.RunOnce(strategyId);
                }

                Orchestrator.StrategyStats stats = store.Get(strategyId, Diag.NetworkFingerprint.Hash());

                Console.WriteLine();
                Console.WriteLine("  проб:       " + stats.Probes);
                Console.WriteLine("  успехов:    " + stats.Successes);
                Console.WriteLine("  балл:       " + stats.LastScore);
                Console.WriteLine("  неудач:     " + stats.ConsecutiveFailures);
                Console.WriteLine("  сеть:       " + stats.Network);

                store.Save();
                svc.Stop();
                return 0;
            }
        }

        /// <summary>--scores: накопленная статистика стратегий по текущей сети.</summary>
        public static int RunScores(string[] args)
        {
            string network = Diag.NetworkFingerprint.Hash();

            Orchestrator.ScoreStore store = new Orchestrator.ScoreStore();
            store.Load();

            Console.WriteLine("== статистика автопилота ==");
            Console.WriteLine("сеть: " + network);
            Console.WriteLine();

            List<Orchestrator.StrategyStats> forNetwork = store.ForNetwork(network);
            if (forNetwork.Count == 0)
            {
                Console.WriteLine("для этой сети данных ещё нет — включите автопилот и дайте ему поработать");
                return 0;
            }

            forNetwork.Sort((a, b) =>
            {
                double wa = Orchestrator.Bandit.WilsonLowerBound(a.Successes, a.Probes);
                double wb = Orchestrator.Bandit.WilsonLowerBound(b.Successes, b.Probes);
                return wb.CompareTo(wa);
            });

            Console.WriteLine("  {0,-32} {1,7} {2,7} {3,6} {4}", "стратегия", "пробы", "успехи", "вера", "последний балл");
            foreach (Orchestrator.StrategyStats s in forNetwork)
            {
                double wilson = Orchestrator.Bandit.WilsonLowerBound(s.Successes, s.Probes);
                Console.WriteLine("  {0,-32} {1,7} {2,7} {3,6} {4}{5}",
                    s.StrategyId,
                    s.Probes,
                    s.Successes,
                    wilson.ToString("0.00", CultureInfo.InvariantCulture),
                    s.LastScore,
                    s.ConsecutiveFailures > 0 ? "  (неудач подряд: " + s.ConsecutiveFailures + ")" : "");
            }

            int total = store.All().Count;
            Console.WriteLine();
            Console.WriteLine("всего записей (по всем сетям): " + total);
            return 0;
        }

        /// <summary>--scores-reset: очистить статистику (все сети или текущую).</summary>
        public static int RunScoresReset(string[] args)
        {
            Orchestrator.ScoreStore store = new Orchestrator.ScoreStore();
            store.Load();

            bool all = HasFlagLocal(args, "--all");
            string network = all ? null : Diag.NetworkFingerprint.Hash();

            store.Clear(network);
            store.Save();

            Console.WriteLine(all
                ? "статистика очищена для всех сетей"
                : "статистика очищена для сети " + network);

            return 0;
        }

        /// <summary>
        /// --hosts [--verify] [--apply] [--check]
        /// Собрать hosts из источников, показать конфликты/дубликаты, применить.
        /// </summary>
        public static int RunHosts(string[] args)
        {
            bool verify = HasFlagLocal(args, "--verify");
            bool apply = HasFlagLocal(args, "--apply");
            bool check = HasFlagLocal(args, "--check");

            Console.WriteLine("== hosts ==");
            Console.WriteLine("системный файл: " + Lists.HostsFile.SystemPath);
            Console.WriteLine();

            if (check)
            {
                Lists.HostsUpdateResult inspected = Lists.HostsUpdater.VerifyCurrent(msg => Console.WriteLine("  " + msg));
                Console.WriteLine("  " + inspected.Message);
                return inspected.Ok ? 0 : 1;
            }

            Lists.HostsUpdateResult built = Lists.HostsUpdater.Build(true, true, verify,
                msg => Console.WriteLine("  " + msg));

            if (!built.Ok)
            {
                Console.WriteLine("ОШИБКА: " + built.Message);
                return 1;
            }

            Lists.HostsAnalysis a = built.Analysis;

            Console.WriteLine();
            Console.WriteLine("  имён:            " + a.UniqueHosts);
            Console.WriteLine("  дубликатов:      " + a.ExactDuplicates);
            Console.WriteLine("  конфликтов:      " + a.Conflicts.Count);
            Console.WriteLine("  мусорных строк:  " + a.Invalid.Count);
            Console.WriteLine("  ваших записей:   " + a.UserCollisions);

            if (a.Conflicts.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  конфликты (одно имя — разные IP):");
                int shown = 0;
                foreach (Lists.HostsConflict c in a.Conflicts)
                {
                    if (shown++ >= 15) { Console.WriteLine("    … и ещё " + (a.Conflicts.Count - 15)); break; }
                    Console.WriteLine("    " + c.Describe());
                }

                if (!verify)
                    Console.WriteLine("\n    (запустите с --verify, чтобы проверить адреса по TCP)");
            }

            if (a.UserCollisionNames.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  не тронуты (уже есть в вашем hosts): " +
                                  string.Join(", ", a.UserCollisionNames.GetRange(0,
                                      Math.Min(10, a.UserCollisionNames.Count)).ToArray()) +
                                  (a.UserCollisionNames.Count > 10 ? " …" : ""));
            }

            if (a.Invalid.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  примеры мусора: " + string.Join(" | ", a.Invalid.GetRange(0,
                    Math.Min(3, a.Invalid.Count)).ToArray()));
            }

            if (!apply)
            {
                Console.WriteLine();
                Console.WriteLine("ничего не записано (добавьте --apply)");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("записываю…");
            string error = Lists.HostsFile.Apply(built.Preview);
            if (error != null)
            {
                Console.WriteLine("ОШИБКА: " + error);
                return 1;
            }

            Console.WriteLine("готово. Откат: --hosts --check? нет — используйте кнопку «Восстановить» или --hosts-restore");
            return 0;
        }

        /// <summary>--hosts-restore: вернуть hosts из резервной копии.</summary>
        public static int RunHostsRestore(string[] args)
        {
            string error = Lists.HostsFile.Restore();
            if (error != null)
            {
                Console.WriteLine("ОШИБКА: " + error);
                return 1;
            }
            Console.WriteLine("hosts восстановлен из " + Lists.HostsFile.BackupPath);
            return 0;
        }

        /// <summary>--test-proxy [dc]: проверить путь до Telegram и работу прокси.</summary>
        public static int RunTestProxy(string[] args)
        {
            int dc = 2;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "--test-proxy", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out dc);

            Console.WriteLine("== проверка Telegram-прокси (DC " + dc + ") ==");
            Console.WriteLine();

            System.Collections.Generic.List<Proxy.ProxySelfTest.Step> steps =
                Proxy.ProxySelfTest.Run(dc, true);

            Console.WriteLine();
            bool allOk = true;
            foreach (Proxy.ProxySelfTest.Step step in steps)
            {
                Console.WriteLine("  " + (step.Ok ? "[ok]  " : "[FAIL]") + " " +
                                  step.Name.PadRight(24) + step.Detail);
                if (!step.Ok) allOk = false;
            }

            Console.WriteLine();
            if (allOk)
            {
                Console.WriteLine("всё работает: включите прокси в интерфейсе и откройте ссылку в Telegram");
            }
            else
            {
                Proxy.ProxySelfTest.Step first = null;
                foreach (Proxy.ProxySelfTest.Step s in steps) { if (!s.Ok) { first = s; break; } }

                Console.WriteLine("первая проблема: " + first.Name);
                if (first.Name.IndexOf("Telegram", StringComparison.Ordinal) >= 0)
                    Console.WriteLine("  сеть не пускает до kws*.web.telegram.org — нужен обход DPI (zapret)");
                else if (first.Name.IndexOf("WebSocket", StringComparison.Ordinal) >= 0)
                    Console.WriteLine("  эндпоинт /apiws не отвечает — возможно, изменился у Telegram");
            }

            return allOk ? 0 : 1;
        }

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
                    // только стратегии текущего движка: у v2 свой exe и lua
                    foreach (Strategy s in svc.Strategies.ByEngine(Settings.Current.EngineKind))
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
                    Console.WriteLine("   " + s.Breakdown());
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
