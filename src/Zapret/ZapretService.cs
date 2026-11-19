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

        /// <summary>Последние строки вывода winws — «что сказал движок».</summary>
        public List<string> EngineOutput(int maxLines = 20)
        {
            return _supervisor.RecentOutput(maxLines);
        }

        /// <summary>Слить весь вывод движка в файл (для отладочного прогона).</summary>
        public bool SaveEngineOutput(string path)
        {
            return _supervisor.SaveOutput(path);
        }

        /// <summary>
        /// Запуск стратегии с отладочным выводом winws.
        ///
        /// Зачем: когда «движок запущен, аргументы правильные, а не работает»,
        /// остаётся один вопрос — применяет ли winws правила к нашим пакетам.
        /// `--debug=1` заставляет его рассказывать это в stdout, а stdout мы
        /// теперь читаем целиком.
        /// </summary>
        public string StartWithDebug(string strategyId, int level)
        {
            return Start(strategyId, null, new List<string> { "--debug=" + Math.Max(1, level) });
        }

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

                // Свои стратегии — последними: кэш движка их тоже содержит, и
                // если загрузить их раньше, он перезапишет файл своим набором.
                try { _store.LoadCustom(Paths.CustomStrategiesFile); }
                catch (Exception ex)
                {
                    Log.Warn("WINWS", "свои стратегии не загрузились: " + ex.Message);
                }
            }
        }

        private void EnsureEngine(EngineKind kind, string cacheName)
        {
            string engineTag = kind == EngineKind.V2 ? "v2" : "v1";

            bool available = _engine.IsAvailable(kind);

            if (!available)
            {
                // Стратегии — это ДАННЫЕ, а не движок. Их надо показывать и
                // тогда, когда движок ещё не скачан: иначе на первом запуске
                // (и в CI, где движок качается отдельным шагом) список пуст,
                // и человек видит «нет доступных стратегий» без причины.
                Log.Info("WINWS", engineTag + " не установлен: " + _engine.UnavailableReason(kind) +
                                 " (стратегии загружаем всё равно)");
            }

            // 1) кэш рядом с exe — не зависит от движка
            string cachePath = Path.Combine(Paths.StrategiesDir, cacheName);
            if (_store.Load(cachePath) && _store.ByEngine(engineTag).Count > 0)
            {
                Log.Info("WINWS", engineTag + " из кэша: " + _store.ByEngine(engineTag).Count);
                return;
            }

            // 2) импорт из исходных .bat — только когда движок есть: нужны его
            //    пути для подстановки ${BIN}/${LISTS}/${LUA}
            if (!available) return;

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
            return Start(strategyId, null);
        }

        /// <summary>
        /// Запуск с явным фильтром портов. gameFilterOverride = null означает «взять из настроек».
        /// Нужно пресетам по процессам: игра требует широкий диапазон, браузинг — нет,
        /// и менять глобальные настройки ради этого было бы неправильно.
        /// </summary>
        public string Start(string strategyId, string gameFilterOverride)
        {
            return Start(strategyId, gameFilterOverride, null);
        }

        /// <summary>
        /// Полный вариант запуска: плюс служебные аргументы поверх стратегии.
        /// Отдельный метод, чтобы обычные вызовы не могли случайно передать лишнее.
        /// </summary>
        public string Start(string strategyId, string gameFilterOverride, List<string> extraArgs)
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

            string filter = string.IsNullOrEmpty(gameFilterOverride)
                ? settings.GameFilter
                : gameFilterOverride;

            Dictionary<string, string> placeholders = _engine.BuildPlaceholders(filter);

            // Проверяем фейки ДО запуска: winws не проверяет содержимое .bin,
            // и битый файл превращается в «стратегия не работает» без объяснений.
            ValidatePayloads(strategy, placeholders);

            return _supervisor.Start(strategy, placeholders, extraArgs);
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

        /// <summary>
        /// Список стратегий для UI (плоский, без аргументов).
        ///
        /// engine фильтрует по движку: у v1 и v2 названия стратегий совпадают
        /// («general (ALT12)» есть в обоих), и общий список превращался в кашу
        /// из 44 почти одинаковых строк. Хуже: можно было выбрать стратегию
        /// чужого движка — она запускала не тот exe.
        /// </summary>
        /// <summary>
        /// Импорт своих .bat-ов.
        ///
        /// Принимает текст, а не пути: файлы выбирает человек в окне, и до
        /// моста доезжает содержимое. Это же делает импорт возможным на
        /// Android, где понятия «путь к папке» в браузерном поле нет.
        ///
        /// Возвращает число добавленных. Всё, что не разобралось, попадает в
        /// warnings — молча пропущенный .bat выглядит как «импорт не сработал».
        /// </summary>
        public int ImportCustomBats(IList<KeyValuePair<string, string>> files,
                                    string prefix, List<string> warnings)
        {
            EnsureStrategies();

            if (files == null || files.Count == 0)
            {
                if (warnings != null) warnings.Add("не передано ни одного файла");
                return 0;
            }

            if (string.IsNullOrEmpty(prefix)) prefix = "custom.";

            int added = 0;

            foreach (KeyValuePair<string, string> file in files)
            {
                string name = file.Key ?? "";

                if (!name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    if (warnings != null) warnings.Add(name + ": не .bat");
                    continue;
                }

                Strategy strategy;

                try
                {
                    strategy = StrategyParser.ParseBatText(
                        file.Value, Path.GetFileName(name), null,
                        _engine.BinDir, _engine.ListsDir,
                        _engine.IsAvailable(EngineKind.V2) ? _engine.LuaDir : null,
                        prefix);
                }
                catch (Exception ex)
                {
                    if (warnings != null) warnings.Add(name + ": " + ex.Message);
                    continue;
                }

                if (AddCustom(strategy, name, warnings)) added++;
            }

            PersistCustom(added);

            return added;
        }

        /// <summary>
        /// Импорт своих .bat-ов из каталога — для консоли.
        ///
        /// Отличается от ImportCustomBats только способом чтения: здесь файл
        /// читается с диска, и `StrategyParser` определяет кодировку сам
        /// (cp1251/cp866/UTF-8). В окне кодировку определяет страница.
        /// </summary>
        public int ImportCustomBatsFromDirectory(string dir, string prefix, List<string> warnings)
        {
            EnsureStrategies();

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                if (warnings != null) warnings.Add("каталог не найден: " + dir);
                return 0;
            }

            if (string.IsNullOrEmpty(prefix)) prefix = "custom.";

            string[] files = Directory.GetFiles(dir, "*.bat");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            int added = 0;

            foreach (string path in files)
            {
                Strategy strategy;

                try
                {
                    strategy = StrategyParser.ParseBat(path, _engine.BinDir, _engine.ListsDir,
                                                       _engine.IsAvailable(EngineKind.V2) ? _engine.LuaDir : null,
                                                       prefix);
                }
                catch (Exception ex)
                {
                    if (warnings != null) warnings.Add(Path.GetFileName(path) + ": " + ex.Message);
                    continue;
                }

                if (AddCustom(strategy, Path.GetFileName(path), warnings)) added++;
            }

            PersistCustom(added);

            return added;
        }

        /// <summary>
        /// Проверить разобранный .bat и положить его в список.
        ///
        /// Отдельно от импорта, потому что «не .bat», «служебный», «нет winws»,
        /// «нет профилей» — это четыре разные причины, и все четыре должны
        /// попасть в отчёт: молча пропущенный файл выглядит как «импорт не
        /// сработал».
        /// </summary>
        private bool AddCustom(Strategy strategy, string name, List<string> warnings)
        {
            // service.bat и подобные — служебные: в них нет запуска winws,
            // и импортировать из них нечего.
            if (Path.GetFileName(name).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                return false;

            if (strategy == null)
            {
                if (warnings != null) warnings.Add(name + ": не нашёл запуск winws");
                return false;
            }

            if (strategy.Profiles.Count == 0)
            {
                if (warnings != null) warnings.Add(name + ": ни одного профиля");
                return false;
            }

            strategy.Custom = true;

            if (string.IsNullOrEmpty(strategy.Note))
                strategy.Note = "свой .bat: " + Path.GetFileName(name);

            _store.AddOrReplace(strategy);

            return true;
        }

        private void PersistCustom(int added)
        {
            if (added <= 0) return;

            try
            {
                Directory.CreateDirectory(Paths.StrategiesDir);
                _store.SaveCustom(Paths.CustomStrategiesFile);
            }
            catch (Exception ex)
            {
                Log.Warn("WINWS", "свои стратегии не сохранились: " + ex.Message);
            }

            Log.Info("WINWS", "импортировано своих стратегий: " + added);
        }

        /// <summary>
        /// Экспорт стратегий обратно в .bat.
        ///
        /// engine == null — все свои; иначе набор указанного движка. onlyId
        /// ограничивает одной стратегией (кнопка «в .bat» рядом со списком).
        /// </summary>
        public int ExportBats(string engine, string onlyId, string dir,
                              List<string> written, List<string> warnings)
        {
            EnsureStrategies();

            if (string.IsNullOrEmpty(dir)) dir = Paths.ExportedStrategiesDir;

            List<Strategy> list = new List<Strategy>();

            if (!string.IsNullOrEmpty(onlyId))
            {
                Strategy one = _store.Find(onlyId);

                if (one == null)
                {
                    if (warnings != null) warnings.Add("стратегия не найдена: " + onlyId);
                    return 0;
                }

                list.Add(one);
            }
            else if (!string.IsNullOrEmpty(engine))
            {
                list.AddRange(_store.ByEngine(engine));
            }
            else
            {
                list.AddRange(_store.All);
            }

            if (list.Count == 0) return 0;

            Dictionary<string, string> placeholders = _engine.BuildPlaceholders("off");

            int count = 0;

            foreach (Strategy s in list)
            {
                // Файлы разных движков в одну папку — это два general.bat с
                // разными командами, и второй молча затрёт первый. Поэтому
                // каталог на движок.
                string target = Path.Combine(dir, s.Engine);

                string path = Path.Combine(target, StrategyBatWriter.FileName(s));

                string error = StrategyBatWriter.Write(s, placeholders, path);

                if (error != null)
                {
                    if (warnings != null) warnings.Add(error);
                    continue;
                }

                if (written != null) written.Add(path);
                count++;
            }

            if (count > 0) Log.Info("WINWS", "экспортировано .bat: " + count + " в " + dir);

            return count;
        }

        /// <summary>
        /// Сохранить стратегию как свою. Возвращает id или текст ошибки.
        ///
        /// Нужно подбору фейка: победитель должен остаться в списке, иначе
        /// следующий запуск вернёт исходную стратегию с чужим фейком, и вся
        /// работа пропадёт.
        /// </summary>
        public string SaveCustomStrategy(Strategy strategy, string id, string name, string note)
        {
            if (strategy == null) return "стратегия не задана";

            EnsureStrategies();

            Strategy copy = new Strategy
            {
                Id = string.IsNullOrEmpty(id) ? "custom.tuned" : id,
                Name = string.IsNullOrEmpty(name) ? (strategy.Name + " (свой)") : name,
                Engine = strategy.Engine,
                Note = note ?? "",
                Recommended = false,
                Source = strategy.Source,
                Custom = true,
                Targets = new List<string>(strategy.Targets),
                GlobalArgs = new List<string>(strategy.GlobalArgs)
            };

            foreach (StrategyProfile p in strategy.Profiles)
            {
                copy.Profiles.Add(new StrategyProfile
                {
                    Name = p.Name,
                    Label = p.Label,
                    Args = new List<string>(p.Args)
                });
            }

            _store.AddOrReplace(copy);

            try
            {
                Directory.CreateDirectory(Paths.StrategiesDir);
                _store.SaveCustom(Paths.CustomStrategiesFile);
            }
            catch (Exception ex)
            {
                Log.Warn("WINWS", "своя стратегия не сохранилась: " + ex.Message);
                return "не сохранил: " + ex.Message;
            }

            return copy.Id;
        }

        public List<Dictionary<string, object>> DescribeStrategies(string engine = null)
        {
            EnsureStrategies();

            IEnumerable<Strategy> source = string.IsNullOrEmpty(engine)
                ? _store.All
                : _store.ByEngine(engine);

            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (Strategy s in source)
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
                    { "profiles", (long)s.Profiles.Count },
                    { "custom", s.Custom }
                });
            }
            return result;
        }

        /// <summary>
        /// Какой движок исполняет стратегии этого режима.
        ///
        /// Свой движок исполняет ТЕ ЖЕ стратегии, что zapret 1: v2 написан на
        /// Lua и не исполним. Поэтому список стратегий у «zapret 1» и «свой»
        /// общий, и слот запоминания тоже общий — иначе переключение движка
        /// меняло бы выбранную стратегию, и сравнивать было бы нечего.
        ///
        /// Отдельный статический метод, а не проверка на месте: одно и то же
        /// правило нужно и интерфейсу, и selftest, и разошедшиеся копии здесь
        /// означали бы «список пуст» у половины людей.
        /// </summary>
        public static string StrategyEngineFor(string kind)
        {
            return string.Equals(kind, "v2", StringComparison.OrdinalIgnoreCase) ? "v2" : "v1";
        }

        /// <summary>Доступность движков для UI.</summary>
        public Dictionary<string, object> DescribeEngines()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "v1", _engine.HasV1 },
                { "v2", _engine.HasV2 },
                { "own", _engine.IsAvailable(EngineKind.Own) },
                { "v1Version", _engine.VersionOf(EngineKind.V1) },
                { "v2Version", _engine.VersionOf(EngineKind.V2) },
                { "ownVersion", _engine.VersionOf(EngineKind.Own) },
                { "v2Reason", _engine.HasV2 ? "" : _engine.UnavailableReason(EngineKind.V2) },
                { "ownReason", _engine.IsAvailable(EngineKind.Own) ? "" : _engine.UnavailableReason(EngineKind.Own) },
                { "counts", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "v1", (long)_store.ByEngine("v1").Count },
                        { "v2", (long)_store.ByEngine("v2").Count },
                        // свой движок исполняет те же стратегии v1, поэтому счётчик
                        // общий: человек видит, что список не «другой», а тот же
                        { "own", (long)_store.ByEngine("v1").Count }
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
