using System;
using System.Collections.Generic;
using AutoZapret.Core;

namespace AutoZapret.Orchestrator
{
    /// <summary>
    /// Выбор стратегии по статистике — Thompson Sampling поверх Beta-распределения.
    ///
    /// Почему не «перебрать всё и взять лучшее»: перебор 44 стратегий занимает
    /// минуты и на каждой пробе выключает обход. Бандит вместо этого тратит
    /// пробы на перспективные варианты и быстро отсеивает плохие.
    ///
    /// Почему Thompson, а не «просто по среднему»: у стратегии с одной удачной
    /// пробой средний балл 100%, но доверия к ней нет. Beta-распределение это
    /// учитывает: широкое при малом числе проб, узкое при большом.
    ///
    /// Ключевые решения:
    ///   * статистика раздельная по сети — у разных провайдеров разные победители;
    ///   * exploration обязателен (по умолчанию 10%): иначе одна случайно
    ///     удачная стратегия навсегда блокирует поиск лучшей;
    ///   * вес успеха дробный (score/100), иначе 76% и 85% неразличимы.
    /// </summary>
    public sealed class Bandit
    {
        private readonly Random _random;

        /// <summary>Доля «исследовательских» выборов, в тысячных (100 = 10%).</summary>
        public int ExplorationPermil = 100;

        public Bandit(int seed = 0)
        {
            _random = seed == 0 ? new Random() : new Random(seed);
        }

        /// <summary>
        /// Выбирает стратегию из списка. null — если список пуст.
        /// </summary>
        public string Pick(IList<string> candidates, ScoreStore store, string network)
        {
            if (candidates == null || candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            // exploration: пробуем наименее изученное
            if (_random.Next(1000) < ExplorationPermil)
                return PickLeastTried(candidates, store, network);

            string best = null;
            double bestSample = double.MinValue;

            foreach (string id in candidates)
            {
                StrategyStats stats = store.Get(id, network);
                double sample = Sample(stats.Alpha, stats.Beta);

                if (sample > bestSample)
                {
                    bestSample = sample;
                    best = id;
                }
            }

            return best ?? candidates[0];
        }

        /// <summary>
        /// Наименее изученная стратегия. Тай-брейк случайный: иначе при равных
        /// счётчиках всегда выбиралась бы первая по порядку, и «холодный старт»
        /// никогда не покрыл бы весь список.
        /// </summary>
        private string PickLeastTried(IList<string> candidates, ScoreStore store, string network)
        {
            int minProbes = int.MaxValue;
            List<string> pool = new List<string>();

            foreach (string id in candidates)
            {
                int probes = store.Get(id, network).Probes;

                if (probes < minProbes)
                {
                    minProbes = probes;
                    pool.Clear();
                    pool.Add(id);
                }
                else if (probes == minProbes)
                {
                    pool.Add(id);
                }
            }

            if (pool.Count == 0) return candidates[0];
            return pool[_random.Next(pool.Count)];
        }

        // ------------------------------------------------------------- sampling

        /// <summary>Сэмпл из Beta(a, b) через два Gamma.</summary>
        public double Sample(double alpha, double beta)
        {
            if (alpha <= 0) alpha = 1;
            if (beta <= 0) beta = 1;

            double x = SampleGamma(alpha);
            double y = SampleGamma(beta);

            double sum = x + y;
            if (sum <= 0) return 0.5;

            return x / sum;
        }

        /// <summary>
        /// Gamma(shape, scale=1) — метод Marsaglia–Tsang.
        /// Без внешних библиотек: в net48 нет ни MathNet, ни System.Numerics нужной формы.
        /// </summary>
        private double SampleGamma(double shape)
        {
            if (shape < 1.0)
            {
                // буст для shape < 1: Gamma(a) = Gamma(a+1) * U^(1/a)
                double u = _random.NextDouble();
                return SampleGamma(shape + 1.0) * Math.Pow(u, 1.0 / shape);
            }

            double d = shape - 1.0 / 3.0;
            double c = 1.0 / Math.Sqrt(9.0 * d);

            while (true)
            {
                double x, v;

                do
                {
                    x = SampleNormal();
                    v = 1.0 + c * x;
                }
                while (v <= 0);

                v = v * v * v;
                double u = _random.NextDouble();

                if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
                if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
            }
        }

        /// <summary>Нормальное распределение (Box–Muller).</summary>
        private double SampleNormal()
        {
            double u1 = 1.0 - _random.NextDouble();   // (0, 1], чтобы избежать log(0)
            double u2 = _random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        // ------------------------------------------------------------- ranking

        /// <summary>
        /// Ранжирование по нижней границе Уилсона: «насколько можно верить успеху».
        /// Одна удачная проба не должна обгонять десять удачных из десяти.
        /// </summary>
        public static double WilsonLowerBound(int successes, int trials, double z = 1.96)
        {
            if (trials <= 0) return 0.0;

            double p = (double)successes / trials;
            double z2 = z * z;
            double denominator = 1.0 + z2 / trials;
            double centre = p + z2 / (2.0 * trials);
            double margin = z * Math.Sqrt(p * (1.0 - p) / trials + z2 / (4.0 * trials * trials));

            double lower = (centre - margin) / denominator;
            return lower < 0 ? 0 : lower;
        }
    }
}