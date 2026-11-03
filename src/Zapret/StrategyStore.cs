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

        // ----------------------------------------------------------------- import

        /// <summary>
        /// Импортирует все general*.bat из каталога. Возвращает число стратегий.
        /// Служебные файлы (service.bat) пропускаются — в них нет запуска winws.
        /// </summary>
        public int ImportFromDirectory(string dir, string binDir, string listsDir)
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

                Strategy s = StrategyParser.ParseBat(file, binDir, listsDir);
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

            lock (_gate)
            {
                _strategies.Clear();
                _strategies.AddRange(imported);
            }

            Log.Info("WINWS", "импортировано стратегий: " + imported.Count);
            return imported.Count;
        }

        // ------------------------------------------------------------- serialize

        public string ToJson(string sourceVersion)
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", CurrentSchema },
                { "engine", "v1" },
                { "sourceVersion", sourceVersion ?? "" },
                { "importedAt", DateTime.Now.ToString("o") }
            };

            List<object> list = new List<object>();
            lock (_gate)
            {
                foreach (Strategy s in _strategies) list.Add(StrategyToMap(s));
            }
            root["strategies"] = list;

            return Json.Stringify(root);
        }

        public void Save(string path, string sourceVersion)
        {
            AtomicFile.WriteAllText(path, ToJson(sourceVersion));
            Log.Info("WINWS", "стратегии сохранены: " + path);
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

            lock (_gate)
            {
                _strategies.Clear();
                _strategies.AddRange(loaded);
            }
            Log.Info("WINWS", "загружено стратегий: " + loaded.Count);
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
                { "source", s.Source }
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
                Source = Json.GetString(map, "source", "")
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
