using System;
using System.Collections.Generic;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    /// <summary>Один испробованный фейк.</summary>
    public sealed class FakeAttempt
    {
        public string File = "";
        public int Score;
        public int OkCount;
        public int Total;
        public long AvgMs;
        public string Breakdown = "";
        public string Failed = "";
    }

    /// <summary>Итог подбора фейка.</summary>
    public sealed class FakeTuneResult
    {
        public string StrategyId = "";
        public string StrategyName = "";
        public FakeSlot Slot;

        /// <summary>Победитель. null — ни один вариант не прошёл.</summary>
        public FakeAttempt Best;

        /// <summary>
        /// Готовая стратегия с фейком-победителем.
        ///
        /// Возвращается наружу, чтобы вызывающий сохранил её как свою.
        /// Пересобирать её по имени файла снаружи нельзя: в стратегии может
        /// быть несколько слотов, и «тот же файл» в другом слоте — другая
        /// стратегия.
        /// </summary>
        public Strategy BestStrategy;

        public List<FakeAttempt> Attempts = new List<FakeAttempt>();
        public List<string> Warnings = new List<string>();
        public string Error = "";

        /// <summary>
        /// Перебор остановлен человеком, а не провалился.
        ///
        /// Разница важна для интерфейса: «не удалось» означает, что ни один
        /// вариант не работает, а «остановлено» — что человек передумал ждать,
        /// и найденное до остановки остаётся годным. Показать первое вместо
        /// второго — значит соврать про результат.
        /// </summary>
        public bool Cancelled;

        /// <summary>Сколько вариантов успели проверить и сколько было всего.</summary>
        public int Tested;
        public int Planned;

        public bool Ok { get { return Error.Length == 0 && Best != null; } }
    }

    /// <summary>
    /// Оркестрация подбора фейка: перебрать файлы, прогнать стратегию с каждым,
    /// вернуть победителя.
    ///
    /// Почему отдельный класс, а не метод в интерфейсе. Тот же подбор нужен и
    /// из консоли (`--tune-fakes`), и из окна, и selftest-у. Держать его в
    /// AppHost значило бы, что консоль и окно со временем разойдутся: сначала
    /// в одном месте появится проверка, потом в другом забудут.
    ///
    /// Почему по одному слоту за раз. Стратегия может ссылаться на несколько
    /// фейков (TLS + QUIC + STUN). Перебирать их совместно — это произведение
    /// вариантов: 3 слота по 100 файлов дают миллион прогонов. Поэтому слоты
    /// обрабатываются последовательно: подобрали лучший TLS, зафиксировали,
    /// перешли к QUIC.
    /// </summary>
    public static class FakeTuner
    {
        /// <summary>
        /// Подобрать фейк. Блокирующая операция: прогоняет реальные стратегии.
        ///
        /// token — обязательный выход: файлов фейков у нас полторы тысячи, и
        /// полный перебор занимает часы. Без отмены это кнопка «запусти и
        /// жди, пока надоест закрывать программу». Проверяется перед каждым
        /// вариантом и передаётся в пробу, иначе «отмена» ждала бы окончания
        /// текущей стратегии вместе с её таймаутами.
        ///
        /// maxSeconds — страховка сверху (0 = без ограничения). Нужна на
        /// случай, когда человек закрыл окно: мост отменит токен, но если он
        /// этого не сделает, перебор не должен идти сутками.
        /// </summary>
        public static FakeTuneResult Run(ZapretService zapret, string strategyId,
                                         int maxCandidates, int maxSlots,
                                         Action<string> progress,
                                         CancellationToken token,
                                         Action<int, int, string> step = null,
                                         int maxSeconds = 0)
        {
            FakeTuneResult result = new FakeTuneResult { StrategyId = strategyId };

            if (zapret == null) { result.Error = "движок не запущен"; return result; }

            Strategy strategy = zapret.Strategies.Find(strategyId);

            if (strategy == null) { result.Error = "стратегия не найдена: " + strategyId; return result; }

            result.StrategyName = strategy.Name;

            List<FakeSlot> slots = FakeSweeper.Slots(strategy, result.Warnings);

            if (slots.Count == 0)
            {
                result.Error = "в стратегии нет фейков, которые можно перебрать";
                return result;
            }

            if (maxSlots > 0 && slots.Count > maxSlots) slots = slots.GetRange(0, maxSlots);

            result.Slot = slots[0];

            // Что перебирать: кандидаты того же вида, что и текущий файл.
            List<Payloads.Payload> candidates = FakeSweeper.Candidates(slots[0].Kind, maxCandidates);

            if (candidates.Count == 0)
            {
                result.Error = "нет файлов вида " + slots[0].Kind + " для перебора";
                return result;
            }

            result.Planned = candidates.Count;

            // Текущий файл проверяем первым: если он уже лучший, не надо
            // гонять сотню вариантов, чтобы это выяснить.
            candidates.Sort((a, b) =>
            {
                bool aCurrent = string.Equals(a.File, slots[0].File, StringComparison.OrdinalIgnoreCase);
                bool bCurrent = string.Equals(b.File, slots[0].File, StringComparison.OrdinalIgnoreCase);

                if (aCurrent == bCurrent) return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                return aCurrent ? -1 : 1;
            });

            if (progress != null)
                progress("фейк: " + slots[0].Describe() + ", вариантов " + candidates.Count);

            // Бюджет времени считается от начала работы, включая замер сети:
            // человек просил «не дольше N секунд на всё», а не «на перебор
            // после замера». Замер сети сам занимает до 10 секунд.
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            // Снимок сети до перебора: он же задаёт цели проверки. Без него
            // tester берёт цели по умолчанию, и они могут не совпадать с тем,
            // что реально заблокировано у человека.
            Baseline.Snapshot baseline = Baseline.Measure(new ConnectivityProbe(), 10, false, token);

            if (baseline == null)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; return result; }

                result.Error = "не удалось измерить сеть";
                return result;
            }

            StrategyTester tester = new StrategyTester(zapret, new ConnectivityProbe());

            List<string> temporaryIds = new List<string>();

            try
            {
                int index = 0;

                foreach (Payloads.Payload candidate in candidates)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                    if (maxSeconds > 0 && watch.Elapsed.TotalSeconds >= maxSeconds)
                    {
                        result.Cancelled = true;

                        if (progress != null)
                            progress("остановлено по времени (" + maxSeconds + " с)");

                        break;
                    }

                    index++;

                    if (step != null) { try { step(index, candidates.Count, candidate.File); } catch { } }

                    string id = "__faketune." + index;

                    Strategy variant = FakeSweeper.Variant(strategy, slots[0], candidate.File, id);

                    if (variant == null) continue;

                    zapret.Strategies.AddTemporary(new[] { variant });
                    temporaryIds.Add(id);

                    if (progress != null)
                        progress("проверяю " + candidate.File + " (" + index + " из " + candidates.Count + ")");

                    List<StrategyScore> scores;

                    try { scores = tester.Run(new[] { id }, baseline, 4, 8, token); }
                    catch (Exception ex)
                    {
                        result.Warnings.Add(candidate.File + ": " + ex.Message);
                        continue;
                    }

                    if (scores == null || scores.Count == 0)
                    {
                        if (token.IsCancellationRequested) { result.Cancelled = true; break; }

                        continue;
                    }

                    StrategyScore score = scores[0];

                    FakeAttempt attempt = new FakeAttempt
                    {
                        File = candidate.File,
                        Score = score.Score,
                        OkCount = score.OkCount,
                        Total = score.Total,
                        AvgMs = score.AvgMs,
                        Breakdown = score.Breakdown(),
                        Failed = string.Join(", ", score.Failed.ToArray())
                    };

                    result.Attempts.Add(attempt);
                    result.Tested = index;

                    // Счёт 0 значит «ни одна цель не прошла». Такой вариант не
                    // может быть победителем: иначе подбор «удаётся» при
                    // полностью неработающей стратегии, сохраняет её как свою,
                    // и человек получает «мою стратегию», которая не работает
                    // хуже исходной. Лучше честное «не удалось».
                    if (attempt.OkCount > 0 &&
                        (result.Best == null || attempt.Score > result.Best.Score))
                    {
                        result.Best = attempt;
                        result.BestStrategy = variant;
                    }

                    if (progress != null)
                        progress("  " + candidate.File + " → " + score.Score + " (" +
                                 score.OkCount + "/" + score.Total + ")");

                    // Полный успех: дальше искать нечего, все оставшиеся варианты
                    // не могут быть лучше «прошло всё».
                    if (score.OkCount == score.Total && score.Total > 0)
                    {
                        if (progress != null) progress("полный успех, перебор остановлен");
                        break;
                    }
                }
            }
            finally
            {
                zapret.Strategies.RemoveByIds(temporaryIds);
            }

            if (result.Cancelled)
            {
                // Итог печатает вызывающий (ReportTuneResult / окно), поэтому
                // здесь только короткая пометка в журнале — иначе «остановлено:
                // проверено 0 из 30» появляется дважды подряд.
                if (progress != null) progress("перебор прерван");

                return result;
            }

            if (result.Best == null && result.Error.Length == 0)
            {
                result.Error = result.Attempts.Count == 0
                    ? "ни один вариант не проверен"
                    : "ни один вариант не прошёл ни одной цели (лучший счёт 0)";
            }

            return result;
        }
    }
}
