using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Хранилище стратегий: импорт из .bat, загрузка/сохранение в JSON.
    ///
    /// Ключевое решение: стратегии — это ДАННЫЕ (assets\strategies\strategies.v1.json),
    /// а не код. Обновление конфигов апстрима = перезапуск импорта, а не правка
    /// C#-файлов и пересборка.
    /// </summary>
    public sealed class StrategyStore
    {
        public const int CurrentSchema = 1;

        /// <summary>
        /// Версия формата идентификаторов. Меняется, когда меняется NormalizeId,
        /// иначе старый кэш принесёт id, которых больше нет.
        /// </summary>
        public const int IdFormat = 2;

        private readonly List<Strategy> _strategies = new List<Strategy>();
        private readonly object _gate = new object();

        public IReadOnlyList<Strategy> All
        {
            get { lock (_gate) return _strategies.ToArray(); }
        }

        public int Count
        {
            get { lock (_gate) return _strategies.Count; }
        }

        public Strategy Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_gate)
            {
                foreach (Strategy s in _strategies)
                    if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        /// <summary>
        /// Добавить стратегии без сохранения на диск.
        ///
        /// Нужно перебору фейков: он подставляет варианты одной стратегии,
        /// прогоняет их и убирает. Если такие варианты попадут в кэш, человек
        /// увидит десяток почти одинаковых строк, из которых работает одна.
        /// </summary>
        public void AddTemporary(IEnumerable<Strategy> items)
        {
            if (items == null) return;

            lock (_gate)
            {
                foreach (Strategy s in items)
                {
                    if (s == null) continue;

                    // Одноимённый id не должен остаться от предыдущего прогона:
                    // иначе tester найдёт старый вариант с другим фейком.
                    _strategies.RemoveAll(x =>
                        string.Equals(x.Id, s.Id, StringComparison.OrdinalIgnoreCase));

                    _strategies.Add(s);
                }
            }
        }

        /// <summary>Убрать стратегии по id (временные варианты перебора).</summary>
        public void RemoveByIds(IEnumerable<string> ids)
        {
            if (ids == null) return;

            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string id in ids) if (id != null) set.Add(id);

            if (set.Count == 0) return;

            lock (_gate)
            {
                _strategies.RemoveAll(s => set.Contains(s.Id));
            }
        }

        // ---------------------------------------------------------------- custom

        /// <summary>
        /// Добавить или заменить стратегию по id, не трогая остальные.
        ///
        /// Нужно и импорту своих .bat, и подбору фейка: результат должен
        /// появиться в списке сразу, а не после перезапуска.
        /// </summary>
        public void AddOrReplace(Strategy strategy)
        {
            if (strategy == null || string.IsNullOrEmpty(strategy.Id)) return;

            lock (_gate)
            {
                _strategies.RemoveAll(s =>
                    string.Equals(s.Id, strategy.Id, StringComparison.OrdinalIgnoreCase));

                _strategies.Add(strategy);

                _strategies.Sort((a, b) =>
                    string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Свои стратегии в JSON — отдельный файл, независимый от движка.</summary>
        public string ToCustomJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", CurrentSchema },
                { "idFormat", IdFormat },
                { "kind", "custom" },
                { "savedAt", DateTime.Now.ToString("o") }
            };

            List<object> list = new List<object>();

            lock (_gate)
            {
                foreach (Strategy s in _strategies)
                    if (s.Custom) list.Add(StrategyToMap(s));
            }

            root["strategies"] = list;

            return Json.Stringify(root);
        }

        public int CustomCount
        {
            get
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach (Strategy s in _strategies) if (s.Custom) count++;
                    return count;
                }
            }
        }

        public void SaveCustom(string path)
        {
            AtomicFile.WriteAllText(path, ToCustomJson());
        }

        /// <summary>
        /// Догрузить свои стратегии.
        ///
        /// Вызывается ПОСЛЕ загрузки наборов движка: иначе кэш движка
        /// (в нём свои стратегии тоже лежат) перезапишет их, и человек
        /// увидит «мои стратегии пропали после обновления».
        ///
        /// Возвращает число добавленных.
        /// </summary>
        public int LoadCustom(string path)
        {
            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return 0;

            Dictionary<string, object> root = Json.ParseObjectSafe(text);
            if (root == null)
            {
                Log.Warn("WINWS", "свои стратегии не разобрались: " + path);
                return 0;
            }

            int idFormat = Json.GetInt(root, "idFormat", IdFormat);

            if (idFormat != IdFormat)
            {
                Log.Warn("WINWS", "свои стратегии от старого формата id (" + idFormat + "), пропуск");
                return 0;
            }

            List<object> list = Json.GetArray(root, "strategies");
            if (list == null) return 0;

            List<Strategy> loaded = new List<Strategy>();

            foreach (object item in list)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;

                Strategy s = StrategyFromMap(map);
                if (s == null || s.Profiles.Count == 0) continue;

                s.Custom = true;
                loaded.Add(s);
            }

            if (loaded.Count == 0) return 0;

            lock (_gate)
            {
                foreach (Strategy s in loaded)
                {
                    _strategies.RemoveAll(x =>
                        string.Equals(x.Id, s.Id, StringComparison.OrdinalIgnoreCase));

                    _strategies.Add(s);
                }

                _strategies.Sort((a, b) =>
                    string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            }

            Log.Info("WINWS", "своих стратегий загружено: " + loaded.Count);
            return loaded.Count;
        }

        // ----------------------------------------------------------------- import

        /// <summary>
        /// Импортирует все general*.bat из каталога. Возвращает число стратегий.
        /// Служебные файлы (service.bat) пропускаются — в них нет запуска winws.
        /// </summary>
        public int ImportFromDirectory(string dir, string binDir, string listsDir)
        {
            return ImportFromDirectory(dir, binDir, listsDir, null, null, false);
        }

        public int ImportFromDirectory(string dir, string binDir, string listsDir, string luaDir)
        {
            return ImportFromDirectory(dir, binDir, listsDir, luaDir, null, false);
        }

        /// <summary>
        /// Импорт с явным префиксом источника и, при желании, без очистки
        /// стратегий того же движка.
        ///
        /// keepExisting нужен, когда наборов несколько: импорт Flowseal не
        /// должен стирать Zapret-Mods и наоборот. Обычный импорт движка
        /// по-прежнему заменяет свой набор целиком — иначе после обновления
        /// движка остались бы стратегии, которых в нём уже нет.
        /// </summary>
        public int ImportFromDirectory(string dir, string binDir, string listsDir, string luaDir,
                                       string sourcePrefix, bool keepExisting)
        {
            List<Strategy> imported = new List<Strategy>();

            if (!Directory.Exists(dir))
            {
                Log.Warn("WINWS", "каталог стратегий не найден: " + dir);
                return 0;
            }

            string[] files = Directory.GetFiles(dir, "*.bat");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("service", StringComparison.OrdinalIgnoreCase)) continue;

                Strategy s = StrategyParser.ParseBat(file, binDir, listsDir, luaDir, sourcePrefix);
                if (s == null)
                {
                    Log.Debug("WINWS", "пропущен (нет winws): " + name);
                    continue;
                }
                if (s.Profiles.Count == 0)
                {
                    Log.Debug("WINWS", "пропущен (нет профилей): " + name);
                    continue;
                }
                imported.Add(s);
            }

            if (imported.Count == 0)
            {
                // ВАЖНО: выходим ДО RemoveAll. Иначе при пустом импорте
                // engine = "v1" (значение по умолчанию), и мы сотрём стратегии
                // другого движка. Именно так v2 однажды удалил все v1.
                Log.Warn("WINWS", "в " + dir + " не найдено ни одной стратегии");
                return 0;
            }

            string engine = imported[0].Engine;

            lock (_gate)
            {
                if (!keepExisting)
                {
                    // заменяем только стратегии того же движка: v1 и v2 живут вместе
                    _strategies.RemoveAll(s => string.Equals(s.Engine, engine, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    // Дополнительный набор: убираем только одноимённые id этого
                    // же источника, чтобы повторный импорт не плодил дубликаты,
                    // но и чужие наборы не трогал.
                    _strategies.RemoveAll(s => imported.Exists(i =>
                        string.Equals(i.Id, s.Id, StringComparison.OrdinalIgnoreCase)));
                }

                _strategies.AddRange(imported);
                _strategies.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            }

            Log.Info("WINWS", "импортировано стратегий (" + engine + "): " + imported.Count);
            return imported.Count;
        }

        /// <summary>Стратегии конкретного движка.</summary>
        public List<Strategy> ByEngine(string engine)
        {
            List<Strategy> result = new List<Strategy>();
            lock (_gate)
            {
                foreach (Strategy s in _strategies)
                    if (string.Equals(s.Engine, engine, StringComparison.OrdinalIgnoreCase)) result.Add(s);
            }
            return result;
        }

        // ------------------------------------------------------------- serialize

        public string ToJson(string sourceVersion)
        {
            return ToJson(sourceVersion, "v1");
        }

        public string ToJson(string sourceVersion, string engine)
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", CurrentSchema },
                { "idFormat", IdFormat },
                { "engine", engine ?? "v1" },
                { "sourceVersion", sourceVersion ?? "" },
                { "importedAt", DateTime.Now.ToString("o") }
            };

            List<object> list = new List<object>();
            lock (_gate)
            {
                foreach (Strategy s in _strategies)
                    if (string.Equals(s.Engine, engine ?? "v1", StringComparison.OrdinalIgnoreCase))
                        list.Add(StrategyToMap(s));
            }
            root["strategies"] = list;

            return Json.Stringify(root);
        }

        public void Save(string path, string sourceVersion)
        {
            Save(path, sourceVersion, "v1");
        }

        public void Save(string path, string sourceVersion, string engine)
        {
            AtomicFile.WriteAllText(path, ToJson(sourceVersion, engine));
            Log.Info("WINWS", "стратегии сохранены (" + engine + "): " + path);
        }

        public bool Load(string path)
        {
            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return false;

            Dictionary<string, object> root = Json.ParseObjectSafe(text);
            if (root == null)
            {
                Log.Warn("WINWS", "strategies.json не разобрался");
                return false;
            }

            // кэш от старой версии парсера: id другие, доверять нельзя
            int idFormat = Json.GetInt(root, "idFormat", 0);
            if (idFormat != IdFormat)
            {
                Log.Info("WINWS", "кэш стратегий устарел (idFormat " + idFormat + " != " + IdFormat + "), переимпорт");
                return false;
            }

            List<object> list = Json.GetArray(root, "strategies");
            if (list == null) return false;

            List<Strategy> loaded = new List<Strategy>();
            foreach (object item in list)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;
                Strategy s = StrategyFromMap(map);
                if (s != null && s.Profiles.Count > 0) loaded.Add(s);
            }

            // кэш одного движка не должен стирать стратегии другого
            string engine = Json.GetString(root, "engine", "v1");

            lock (_gate)
            {
                _strategies.RemoveAll(s => string.Equals(s.Engine, engine, StringComparison.OrdinalIgnoreCase));
                _strategies.AddRange(loaded);
                _strategies.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            }

            Log.Info("WINWS", "загружено стратегий (" + engine + "): " + loaded.Count);
            return loaded.Count > 0;
        }

        private static Dictionary<string, object> StrategyToMap(Strategy s)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", s.Id },
                { "name", s.Name },
                { "engine", s.Engine },
                { "note", s.Note },
                { "recommended", s.Recommended },
                { "source", s.Source },
                { "custom", s.Custom }
            };

            List<object> targets = new List<object>();
            foreach (string t in s.Targets) targets.Add(t);
            map["targets"] = targets;

            List<object> globals = new List<object>();
            foreach (string a in s.GlobalArgs) globals.Add(a);
            map["globalArgs"] = globals;

            List<object> profiles = new List<object>();
            foreach (StrategyProfile p in s.Profiles)
            {
                List<object> args = new List<object>();
                foreach (string a in p.Args) args.Add(a);
                profiles.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", p.Name },
                    { "label", p.Label },
                    { "args", args }
                });
            }
            map["profiles"] = profiles;
            return map;
        }

        private static Strategy StrategyFromMap(Dictionary<string, object> map)
        {
            Strategy s = new Strategy
            {
                Id = Json.GetString(map, "id", ""),
                Name = Json.GetString(map, "name", ""),
                Engine = Json.GetString(map, "engine", "v1"),
                Note = Json.GetString(map, "note", ""),
                Recommended = Json.GetBool(map, "recommended", false),
                Source = Json.GetString(map, "source", ""),
                Custom = Json.GetBool(map, "custom", false)
            };
            if (s.Id.Length == 0) return null;

            List<object> targets = Json.GetArray(map, "targets");
            if (targets != null)
                foreach (object t in targets)
                {
                    string str = t as string;
                    if (str != null) s.Targets.Add(str);
                }

            List<object> globals = Json.GetArray(map, "globalArgs");
            if (globals != null)
                foreach (object g in globals)
                {
                    string str = g as string;
                    if (str != null) s.GlobalArgs.Add(str);
                }

            List<object> profiles = Json.GetArray(map, "profiles");
            if (profiles != null)
            {
                foreach (object item in profiles)
                {
                    Dictionary<string, object> pm = item as Dictionary<string, object>;
                    if (pm == null) continue;
                    StrategyProfile p = new StrategyProfile
                    {
                        Name = Json.GetString(pm, "name", ""),
                        Label = Json.GetString(pm, "label", "")
                    };
                    List<object> args = Json.GetArray(pm, "args");
                    if (args != null)
                        foreach (object a in args)
                        {
                            string str = a as string;
                            if (str != null) p.Args.Add(str);
                        }
                    if (p.Args.Count > 0) s.Profiles.Add(p);
                }
            }

            return s;
        }
    }
}
