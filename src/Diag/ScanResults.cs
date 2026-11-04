using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    /// <summary>Результат одной стратегии из последнего скана.</summary>
    public sealed class ScanEntry
    {
        public string Id = "";
        public string Name = "";
        public int OkCount;
        public int Total;
        public int Score;
        public int AvgMs;
        public string Reasons = "";
        public string Error = "";
    }

    /// <summary>
    /// Результаты последнего подбора стратегий.
    ///
    /// Зачем отдельно от ScoreStore: там статистика «сколько раз сработало» для
    /// автопилота, а здесь — снимок конкретного скана: когда был, на какой сети,
    /// что получилось у каждой стратегии. Человеку нужно видеть «эта стратегия
    /// проверена, 6 из 6, счёт 98, час назад» прямо у выбора стратегии, а не
    /// лазить на экран диагностики и вспоминать.
    ///
    /// Файл маленький, перезаписывается целиком, читается при старте.
    /// </summary>
    public static class ScanResults
    {
        private sealed class Snapshot
        {
            public DateTime At = DateTime.MinValue;
            public string Network = "";
            public List<ScanEntry> Entries = new List<ScanEntry>();
        }

        /// <summary>Сохранить результаты сравнения техник.</summary>
        public static void SaveTechniques(IEnumerable<TechniqueScore> scores, string network)
        {
            if (scores == null) return;

            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "at", DateTime.Now.ToString("o") },
                    { "network", network ?? "" }
                };

                List<object> items = new List<object>();

                foreach (TechniqueScore score in scores)
                {
                    items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "name", score.Name },
                        { "ok", (long)score.OkCount },
                        { "total", (long)score.Total },
                        { "score", (long)score.Score },
                        { "ms", (long)score.AvgMs },
                        { "reasons", score.Reasons ?? "" }
                    });
                }

                root["techniques"] = items;

                AtomicFile.WriteAllText(TechniquePath, Json.Stringify(root));

                Log.Info("OWN", "результаты техник сохранены: " + items.Count);
            }
            catch (Exception ex)
            {
                Log.Warn("OWN", "не сохранил результаты техник: " + ex.Message);
            }
        }

        /// <summary>Лучшая техника по последнему сравнению, если он был.</summary>
        public static string BestTechnique()
        {
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(TechniquePath);
                if (string.IsNullOrEmpty(text)) return "";

                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                List<object> items = root == null ? null : Json.GetArray(root, "techniques");

                if (items == null || items.Count == 0) return "";

                string best = "";
                int bestScore = -1;

                foreach (object item in items)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map == null) continue;

                    int score = Json.GetInt(map, "score", 0);
                    int ok = Json.GetInt(map, "ok", 0);

                    if (ok <= 0 || score <= bestScore) continue;

                    bestScore = score;
                    best = Json.GetString(map, "name", "");
                }

                return best;
            }
            catch
            {
                return "";
            }
        }

        private static string TechniquePath
        {
            get { return Path.Combine(Paths.StateDir, "last-techniques.json"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(Paths.StateDir, "last-scan.json"); }
        }

        /// <summary>Сохранить результаты подбора.</summary>
        public static void Save(IEnumerable<StrategyScore> scores, string network)
        {
            if (scores == null) return;

            Snapshot snapshot = new Snapshot { At = DateTime.Now, Network = network ?? "" };

            foreach (StrategyScore score in scores)
            {
                snapshot.Entries.Add(new ScanEntry
                {
                    Id = score.Id,
                    Name = score.Name,
                    OkCount = score.OkCount,
                    Total = score.Total,
                    Score = score.Score,
                    AvgMs = score.AvgMs,
                    Reasons = score.ProbeReasons ?? "",
                    Error = score.Error ?? ""
                });
            }

            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "at", snapshot.At.ToString("o") },
                    { "network", snapshot.Network }
                };

                List<object> items = new List<object>();

                foreach (ScanEntry entry in snapshot.Entries)
                {
                    items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "id", entry.Id },
                        { "name", entry.Name },
                        { "ok", (long)entry.OkCount },
                        { "total", (long)entry.Total },
                        { "score", (long)entry.Score },
                        { "ms", (long)entry.AvgMs },
                        { "reasons", entry.Reasons ?? "" },
                        { "error", entry.Error ?? "" }
                    });
                }

                root["items"] = items;

                AtomicFile.WriteAllText(FilePath, Json.Stringify(root));

                Log.Info("DIAG", "результаты скана сохранены: " + snapshot.Entries.Count + " стратегий");
            }
            catch (Exception ex)
            {
                Log.Warn("DIAG", "не сохранил результаты скана: " + ex.Message);
            }
        }

        private static Snapshot Load()
        {
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(FilePath);
                if (string.IsNullOrEmpty(text)) return null;

                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                if (root == null) return null;

                Snapshot snapshot = new Snapshot
                {
                    Network = Json.GetString(root, "network", "")
                };

                DateTime at;
                if (DateTime.TryParse(Json.GetString(root, "at", ""), out at)) snapshot.At = at;

                List<object> items = Json.GetArray(root, "items");
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        Dictionary<string, object> map = item as Dictionary<string, object>;
                        if (map == null) continue;

                        snapshot.Entries.Add(new ScanEntry
                        {
                            Id = Json.GetString(map, "id", ""),
                            Name = Json.GetString(map, "name", ""),
                            OkCount = Json.GetInt(map, "ok", 0),
                            Total = Json.GetInt(map, "total", 0),
                            Score = Json.GetInt(map, "score", 0),
                            AvgMs = Json.GetInt(map, "ms", 0),
                            Reasons = Json.GetString(map, "reasons", ""),
                            Error = Json.GetString(map, "error", "")
                        });
                    }
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                Log.Debug("DIAG", "чтение результатов скана: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Что известно про эту стратегию: строка для интерфейса и признак свежести.
        /// Пустая строка — стратегию ещё не проверяли.
        /// </summary>
        public static string Describe(string strategyId, out bool fresh, out bool good)
        {
            fresh = false;
            good = false;

            Snapshot snapshot = Load();
            if (snapshot == null || snapshot.Entries.Count == 0) return "";

            ScanEntry found = null;

            foreach (ScanEntry entry in snapshot.Entries)
                if (string.Equals(entry.Id, strategyId, StringComparison.OrdinalIgnoreCase)) { found = entry; break; }

            if (found == null) return "";

            TimeSpan age = DateTime.Now - snapshot.At;
            fresh = age.TotalHours < 6;

            // «Хорошо» = пробило всё. Частичный успех — это жёлтый, а не зелёный.
            good = found.Total > 0 && found.OkCount >= found.Total;

            string when = AgeText(age);

            string text = "скан " + when + " · " + found.OkCount + "/" + found.Total +
                          " · счёт " + found.Score;

            if (found.OkCount == 0 && found.Reasons.Length > 0)
                text += " · " + found.Reasons;

            if (!fresh) text += " · устарело";

            return text;
        }

        /// <summary>Сеть, на которой делали скан — чтобы «устарело» не врало при смене сети.</summary>
        public static string NetworkOf()
        {
            Snapshot snapshot = Load();
            return snapshot == null ? "" : snapshot.Network;
        }

        public static int Count()
        {
            Snapshot snapshot = Load();
            return snapshot == null ? 0 : snapshot.Entries.Count;
        }

        private static string AgeText(TimeSpan age)
        {
            if (age.TotalMinutes < 2) return "только что";
            if (age.TotalMinutes < 60) return (int)age.TotalMinutes + " мин назад";
            if (age.TotalHours < 24) return (int)age.TotalHours + " ч назад";

            return (int)age.TotalDays + " дн назад";
        }
    }
}