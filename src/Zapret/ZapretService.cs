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
        /// Порядок для каждого движка: кэш JSON -> импорт из .bat -> ничего.
        /// </summary>
        public void EnsureStrategies()
        {
            lock (_gate)
            {
                if (_imported) return;
                _imported = true;

                _engine.Refresh();

                EnsureEngine(EngineKind.V1, "strategies.v1.json");
                EnsureEngine(EngineKind.V2, "strategies.v2.json");
            }
        }

        private void EnsureEngine(EngineKind kind, string cacheName)
        {
            string engineTag = kind == EngineKind.V2 ? "v2" : "v1";

            if (!_engine.IsAvailable(kind))
            {
                Log.Info("WINWS", engineTag + " недоступен: " + _engine.UnavailableReason(kind));
                return;
            }

            // 1) кэш рядом с exe
            string cachePath = Path.Combine(Paths.StrategiesDir, cacheName);
            if (_store.Load(cachePath) && _store.ByEngine(engineTag).Count > 0)
            {
                Log.Info("WINWS", engineTag + " из кэша: " + _store.ByEngine(engineTag).Count);
                return;
            }

            // 2) импорт из исходных .bat
            string batDir = FindBatDirectory(kind);
            if (batDir != null)
            {
                int count = _store.ImportFromDirectory(batDir, _engine.BinDir, _engine.ListsDir,
                                                       kind == EngineKind.V2 ? _engine.LuaDir : null);
                if (count > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(Paths.StrategiesDir);
                        _store.Save(cachePath, _engine.VersionOf(kind), engineTag);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("WINWS", "не удалось сохранить кэш " + engineTag + ": " + ex.Message);
                    }
                    return;
                }
            }

            Log.Warn("WINWS", engineTag + ": стратегии не найдены");
        }

        /// <summary>
        /// Где искать исходные .bat для движка.
        /// В dev-раскладке они в refs/ рядом с репозиторием, в релизе — рядом с exe.
        /// </summary>
        private static string FindBatDirectory(EngineKind kind)
        {
            // v2 живёт в отдельном репозитории (ветка zapret2)
            string[] names = kind == EngineKind.V2
                ? new[] { "snanvl-zapret2", "zapret2" }
                : new[] { "zapret-discord-youtube" };

            string[] roots =
            {
                Path.Combine(Paths.BaseDir, "bat"),
                Path.Combine(Paths.EngineDir, "bat"),
                Path.Combine(Paths.BaseDir, "..", "refs"),
                Path.Combine(Paths.BaseDir, "..", "..", "refs"),
                Path.Combine(Paths.BaseDir, "..", "..", "..", "refs")
            };

            foreach (string root in roots)
            {
                foreach (string name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(root, name);
                        if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "general*.bat").Length > 0)
                            return Path.GetFullPath(candidate);
                    }
                    catch { }
                }
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
                // если id пустой или не найден — берём первую подходящую под настройки
                string wantEngine = Settings.Current.EngineKind;
                List<Strategy> pool = _store.ByEngine(wantEngine);
                if (pool.Count == 0) pool = new List<Strategy>(_store.All);

                if (pool.Count == 0) return "Нет доступных стратегий.";
                strategy = pool[0];
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
                    { "engine", s.Engine },
                    { "note", s.Note },
                    { "recommended", s.Recommended },
                    { "targets", targets },
                    { "profiles", (long)s.Profiles.Count }
                });
            }
            return result;
        }

        /// <summary>Доступность движков для UI.</summary>
        public Dictionary<string, object> DescribeEngines()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "v1", _engine.HasV1 },
                { "v2", _engine.HasV2 },
                { "v1Version", _engine.VersionOf(EngineKind.V1) },
                { "v2Version", _engine.VersionOf(EngineKind.V2) },
                { "v2Reason", _engine.HasV2 ? "" : _engine.UnavailableReason(EngineKind.V2) },
                { "counts", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "v1", (long)_store.ByEngine("v1").Count },
                        { "v2", (long)_store.ByEngine("v2").Count }
                    }
                }
            };
        }

        public void Dispose()
        {
            try { _supervisor.Dispose(); } catch { }
        }
    }
}
