using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Orchestrator
{
    /// <summary>Статистика одной стратегии на одной сети.</summary>
    public sealed class StrategyStats
    {
        public string StrategyId = "";
        public string Network = "";

        /// <summary>Успехи и неудачи (Beta-параметры бандита).</summary>
        public double Alpha = 1.0;
        public double Beta = 1.0;

        public int Probes;
        public int Successes;
        public int LastScore;
        public int BestScore;
        public int AvgMs;
        public string LastError = "";
        public DateTime LastProbe = DateTime.MinValue;
        public DateTime LastSuccess = DateTime.MinValue;

        /// <summary>Сколько неудач подряд. Для антифлаппинга.</summary>
        public int ConsecutiveFailures;

        public double Rate { get { return Probes == 0 ? 0 : (double)Successes / Probes; } }

        public void Register(bool ok, int score, int avgMs, string error)
        {
            Probes++;
            LastScore = score;
            AvgMs = avgMs;
            LastProbe = DateTime.Now;

            if (ok)
            {
                Successes++;
                Alpha += score / 100.0;      // дробный вес: 76% и 85% должны различаться
                ConsecutiveFailures = 0;
                LastSuccess = DateTime.Now;
                LastError = "";
                if (score > BestScore) BestScore = score;
            }
            else
            {
                Beta += 1.0;
                ConsecutiveFailures++;
                LastError = error ?? "";
            }
        }
    }

    /// <summary>
    /// Хранилище статистики: (стратегия, сеть) -> счётчики.
    ///
    /// Зачем разделять по сети: одна и та же стратегия работает у одного провайдера
    /// и не работает у другого. Без этого «средний рейтинг» бесполезен —
    /// стратегия с 50% успеха может быть идеальной дома и нулевой в мобильной сети.
    ///
    /// Данные переживают перезапуск: иначе каждое включение начинается с нуля
    /// и автоподбор никогда не накапливает опыт.
    /// </summary>
    public sealed class ScoreStore
    {
        private const int MaxEntries = 500;

        private readonly object _gate = new object();
        private readonly Dictionary<string, StrategyStats> _map =
            new Dictionary<string, StrategyStats>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath
        {
            get { return Path.Combine(Paths.StateDir, "scores.json"); }
        }

        private static string Key(string strategyId, string network)
        {
            return (strategyId ?? "") + "|" + (network ?? "");
        }

        public StrategyStats Get(string strategyId, string network)
        {
            string key = Key(strategyId, network);
            lock (_gate)
            {
                StrategyStats stats;
                if (_map.TryGetValue(key, out stats)) return stats;

                stats = new StrategyStats { StrategyId = strategyId, Network = network };
                _map[key] = stats;
                return stats;
            }
        }

        /// <summary>Все записи для сети (копия).</summary>
        public List<StrategyStats> ForNetwork(string network)
        {
            List<StrategyStats> result = new List<StrategyStats>();
            lock (_gate)
            {
                foreach (StrategyStats s in _map.Values)
                    if (string.Equals(s.Network, network, StringComparison.OrdinalIgnoreCase))
                        result.Add(s);
            }
            return result;
        }

        public List<StrategyStats> All()
        {
            lock (_gate) return new List<StrategyStats>(_map.Values);
        }

        public void Register(string strategyId, string network, bool ok, int score, int avgMs, string error)
        {
            StrategyStats stats = Get(strategyId, network);
            lock (_gate)
            {
                stats.Register(ok, score, avgMs, error);
                Prune();
            }
        }

        /// <summary>Сбросить статистику (например, при смене провайдера вручную).</summary>
        public void Clear(string network = null)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(network)) { _map.Clear(); return; }

                List<string> remove = new List<string>();
                foreach (KeyValuePair<string, StrategyStats> kv in _map)
                    if (string.Equals(kv.Value.Network, network, StringComparison.OrdinalIgnoreCase))
                        remove.Add(kv.Key);

                foreach (string key in remove) _map.Remove(key);
            }
        }

        /// <summary>Держим размер под контролем: старые сети не нужны вечно.</summary>
        private void Prune()
        {
            if (_map.Count <= MaxEntries) return;

            List<StrategyStats> all = new List<StrategyStats>(_map.Values);
            all.Sort((a, b) => a.LastProbe.CompareTo(b.LastProbe));

            int toRemove = _map.Count - MaxEntries;
            for (int i = 0; i < toRemove && i < all.Count; i++)
            {
                foreach (KeyValuePair<string, StrategyStats> kv in new List<KeyValuePair<string, StrategyStats>>(_map))
                {
                    if (ReferenceEquals(kv.Value, all[i])) { _map.Remove(kv.Key); break; }
                }
            }
        }

        // ------------------------------------------------------------- persist

        public void Load()
        {
            string text = AtomicFile.ReadAllTextOrNull(FilePath);
            if (text == null) return;

            Dictionary<string, object> root = Json.ParseObjectSafe(text);
            if (root == null)
            {
                Log.Warn("AUTO", "scores.json не разобрался — начинаю с чистого листа");
                return;
            }

            List<object> items = Json.GetArray(root, "entries");
            if (items == null) return;

            lock (_gate)
            {
                _map.Clear();

                foreach (object item in items)
                {
                    Dictionary<string, object> m = item as Dictionary<string, object>;
                    if (m == null) continue;

                    StrategyStats s = new StrategyStats
                    {
                        StrategyId = Json.GetString(m, "strategy", ""),
                        Network = Json.GetString(m, "network", ""),
                        Alpha = Json.GetDouble(m, "alpha", 1.0),
                        Beta = Json.GetDouble(m, "beta", 1.0),
                        Probes = Json.GetInt(m, "probes", 0),
                        Successes = Json.GetInt(m, "successes", 0),
                        LastScore = Json.GetInt(m, "lastScore", 0),
                        BestScore = Json.GetInt(m, "bestScore", 0),
                        AvgMs = Json.GetInt(m, "avgMs", 0),
                        LastError = Json.GetString(m, "lastError", ""),
                        ConsecutiveFailures = Json.GetInt(m, "fails", 0),
                        LastProbe = ParseDate(Json.GetString(m, "lastProbe", "")),
                        LastSuccess = ParseDate(Json.GetString(m, "lastSuccess", ""))
                    };

                    if (s.StrategyId.Length == 0) continue;
                    _map[Key(s.StrategyId, s.Network)] = s;
                }
            }

            Log.Info("AUTO", "статистика загружена: " + _map.Count + " записей");
        }

        public void Save()
        {
            List<object> items = new List<object>();

            lock (_gate)
            {
                foreach (StrategyStats s in _map.Values)
                {
                    items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "strategy", s.StrategyId },
                        { "network", s.Network },
                        { "alpha", Math.Round(s.Alpha, 4) },
                        { "beta", Math.Round(s.Beta, 4) },
                        { "probes", (long)s.Probes },
                        { "successes", (long)s.Successes },
                        { "lastScore", (long)s.LastScore },
                        { "bestScore", (long)s.BestScore },
                        { "avgMs", (long)s.AvgMs },
                        { "lastError", s.LastError ?? "" },
                        { "fails", (long)s.ConsecutiveFailures },
                        { "lastProbe", s.LastProbe == DateTime.MinValue ? "" : s.LastProbe.ToString("o") },
                        { "lastSuccess", s.LastSuccess == DateTime.MinValue ? "" : s.LastSuccess.ToString("o") }
                    });
                }
            }

            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", 1 },
                { "savedAt", DateTime.Now.ToString("o") },
                { "entries", items }
            };

            try
            {
                AtomicFile.WriteAllText(FilePath, Json.Stringify(root));
            }
            catch (Exception ex)
            {
                Log.Warn("AUTO", "не удалось сохранить статистику: " + ex.Message);
            }
        }

        private static DateTime ParseDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return DateTime.MinValue;

            DateTime parsed;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind, out parsed))
                return parsed;

            return DateTime.MinValue;
        }
    }
}