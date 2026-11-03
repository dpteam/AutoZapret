using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Payloads;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Связка «движок + стратегии + процесс» для UI.
    ///
    /// Держит единственный экземпляр WinwsSupervisor и StrategyStore,
    /// лениво импортирует стратегии из .bat при первом обращении,
    /// кэширует результат в assets\strategies\strategies.v1.json.
    /// </summary>
    public sealed class ZapretService : IDisposable
    {
        private readonly EngineLocator _engine;
        private readonly StrategyStore _store;
        private readonly WinwsSupervisor _supervisor;
        private readonly object _gate = new object();
        private bool _imported;

        public ZapretService()
        {
            _engine = new EngineLocator();
            _store = new StrategyStore();
            _supervisor = new WinwsSupervisor(_engine);

            _supervisor.StateChanged += (state, error) =>
            {
                Action<ProtectionState, string> handler = StateChanged;
                if (handler != null) { try { handler(state, error); } catch { } }
            };
        }

        public event Action<ProtectionState, string> StateChanged;

        public EngineLocator Engine { get { return _engine; } }
        public StrategyStore Strategies { get { return _store; } }
        public WinwsSupervisor Supervisor { get { return _supervisor; } }

        public ProtectionState State { get { return _supervisor.State; } }
        public string LastError { get { return _supervisor.LastError; } }
        public string ActiveStrategyId { get { return _supervisor.StrategyId; } }
        public string ActiveStrategyName { get { return _supervisor.StrategyName; } }
        public TimeSpan Uptime { get { return _supervisor.Uptime; } }

        /// <summary>
        /// Гарантирует, что стратегии загружены.
        /// Порядок: кэш JSON -> импорт из .bat в refs/ (dev) -> встроенный минимум.
        /// </summary>
        public void EnsureStrategies()
        {
            lock (_gate)
            {
                if (_imported) return;
                _imported = true;

                _engine.Refresh();

                // 1) кэш рядом с exe
                string cachePath = Path.Combine(Paths.StrategiesDir, "strategies.v1.json");
                if (_store.Load(cachePath) && _store.Count > 0)
                {
                    Log.Info("WINWS", "стратегии из кэша: " + _store.Count);
                    return;
                }

                // 2) импорт из исходных .bat
                string batDir = FindBatDirectory();
                if (batDir != null)
                {
                    int count = _store.ImportFromDirectory(batDir, _engine.BinDir, _engine.ListsDir);
                    if (count > 0)
                    {
                        try
                        {
                            Directory.CreateDirectory(Paths.StrategiesDir);
                            _store.Save(cachePath, _engine.Version);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("WINWS", "не удалось сохранить кэш стратегий: " + ex.Message);
                        }
                        return;
                    }
                }

                Log.Warn("WINWS", "стратегии не найдены — работает только встроенный минимум");
                _store.ImportFromDirectory(null, _engine.BinDir, _engine.ListsDir);
            }
        }

        /// <summary>
        /// Где искать исходные .bat. В dev-раскладке они в refs/ рядом с репозиторием,
        /// в релизе — рядом с exe (если пользователь положил) или в engine/.
        /// </summary>
        private static string FindBatDirectory()
        {
            string[] candidates =
            {
                Path.Combine(Paths.BaseDir, "bat"),
                Path.Combine(Paths.EngineDir, "bat"),
                Path.Combine(Paths.BaseDir, "..", "refs", "zapret-discord-youtube"),
                Path.Combine(Paths.BaseDir, "..", "..", "refs", "zapret-discord-youtube"),
                Path.Combine(Paths.BaseDir, "..", "..", "..", "refs", "zapret-discord-youtube")
            };

            foreach (string c in candidates)
            {
                try
                {
                    if (Directory.Exists(c) && Directory.GetFiles(c, "general*.bat").Length > 0)
                        return Path.GetFullPath(c);
                }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------------------- control

        public string Start(string strategyId)
        {
            EnsureStrategies();

            Strategy strategy = _store.Find(strategyId);
            if (strategy == null)
            {
                // если id пустой или не найден — берём первую
                IReadOnlyList<Strategy> all = _store.All;
                if (all.Count == 0) return "Нет доступных стратегий.";
                strategy = all[0];
                Log.Warn("WINWS", "стратегия '" + strategyId + "' не найдена, беру '" + strategy.Id + "'");
            }

            Settings settings = Settings.Current;
            Dictionary<string, string> placeholders = _engine.BuildPlaceholders(settings.GameFilter);

            // Проверяем фейки ДО запуска: winws не проверяет содержимое .bin,
            // и битый файл превращается в «стратегия не работает» без объяснений.
            ValidatePayloads(strategy, placeholders);

            return _supervisor.Start(strategy, placeholders);
        }

        /// <summary>Предупреждает в лог о битых фейках. Не блокирует запуск.</summary>
        private static void ValidatePayloads(Strategy strategy, Dictionary<string, string> placeholders)
        {
            try
            {
                List<string> args = strategy.BuildArguments(placeholders);
                foreach (string arg in args)
                {
                    int eq = arg.IndexOf('=');
                    if (eq < 0) continue;

                    string value = arg.Substring(eq + 1);
                    if (!value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!System.IO.File.Exists(value)) continue;

                    Payload payload = PayloadValidator.Inspect(value);
                    if (!payload.Valid)
                    {
                        Log.Warn("FAKE", "битый фейк в стратегии: " +
                                         System.IO.Path.GetFileName(value) + " (" + payload.InvalidReason + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("FAKE", "проверка фейков: " + ex.Message);
            }
        }

        public bool Stop()
        {
            return _supervisor.Stop();
        }

        /// <summary>Список стратегий для UI (плоский, без аргументов).</summary>
        public List<Dictionary<string, object>> DescribeStrategies()
        {
            EnsureStrategies();

            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (Strategy s in _store.All)
            {
                List<object> targets = new List<object>();
                foreach (string t in s.Targets) targets.Add(t);

                result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", s.Id },
                    { "name", s.Name },
                    { "note", s.Note },
                    { "recommended", s.Recommended },
                    { "targets", targets },
                    { "profiles", (long)s.Profiles.Count }
                });
            }
            return result;
        }

        public void Dispose()
        {
            try { _supervisor.Dispose(); } catch { }
        }
    }
}
