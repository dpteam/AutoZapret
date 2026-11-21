using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Lists
{
    /// <summary>
    /// Пак исправления: то, что добавляется к обходу, а не заменяет его.
    ///
    /// Зачем отдельно от hosts-фиксов. Записи в hosts меняют адрес, а есть
    /// случаи, которые адресом не лечатся вовсе: игра, у которой DPI режет
    /// первое UDP-рукопожатие, или домены, которые провайдер режет по имени.
    /// Для них нужны другие действия — профиль движка или строка в списке
    /// обхода. Свалить это в один «hosts-фикс» значит соврать о том, что
    /// произойдёт.
    ///
    /// Виды (Kind):
    ///   * `hostlist` — домены добавляются в list-general-user.txt (личный
    ///     список обхода). Файл виден и обратим: строки можно убрать руками.
    ///   * `strategy` — аргументы становятся отдельным профилем выбранной
    ///     стратегии. Стратегия сохраняется как своя, чтобы это было видно
    ///     в списке и удалялось одной кнопкой.
    /// </summary>
    public sealed class FixPack
    {
        public string Id = "";
        public string Name = "";

        /// <summary>`hostlist` или `strategy`.</summary>
        public string Kind = "";

        public string Note = "";

        /// <summary>Предупреждение, которое показываем до применения.</summary>
        public string Warning = "";

        /// <summary>Что именно проверено на этом компьютере — не «доверьтесь».</summary>
        public string Checked = "";

        /// <summary>Домены для личного списка обхода (Kind = hostlist).</summary>
        public List<string> Entries = new List<string>();

        /// <summary>Аргументы winws для нового профиля (Kind = strategy).</summary>
        public List<string> Args = new List<string>();

        public bool IsHostlist
        {
            get { return string.Equals(Kind, "hostlist", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsStrategy
        {
            get { return string.Equals(Kind, "strategy", StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// Каталог паков исправлений и их применение.
    ///
    /// Файл `assets/fixes/packs.json` обновляется вместе с программой, поэтому
    /// свои паки живут в `fixes.user.json` и читаются ПОСЛЕ поставки — иначе
    /// обновление стирало бы добавленное человеком.
    /// </summary>
    public static class FixPacks
    {
        public static string BundledPath
        {
            get { return Path.Combine(Paths.BaseDir, "assets", "fixes", "packs.json"); }
        }

        public static string UserPath
        {
            get { return Path.Combine(Paths.BaseDir, "assets", "fixes", "fixes.user.json"); }
        }

        public static List<FixPack> Load()
        {
            List<FixPack> result = new List<FixPack>();

            ReadFile(BundledPath, result);
            ReadFile(UserPath, result);

            return result;
        }

        private static void ReadFile(string path, List<FixPack> into)
        {
            if (!File.Exists(path)) return;

            Dictionary<string, object> root;

            try
            {
                root = Json.ParseObjectSafe(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Warn("FIX", "не разобрал " + Path.GetFileName(path) + ": " + ex.Message);
                return;
            }

            if (root == null) return;

            List<object> packs = Json.GetArray(root, "packs");
            if (packs == null) return;

            foreach (object item in packs)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;

                FixPack pack = new FixPack
                {
                    Id = Json.GetString(map, "id", ""),
                    Name = Json.GetString(map, "name", ""),
                    Kind = Json.GetString(map, "kind", ""),
                    Note = Json.GetString(map, "note", ""),
                    Warning = Json.GetString(map, "warning", ""),
                    Checked = Json.GetString(map, "checked", "")
                };

                if (pack.Id.Length == 0 || pack.Name.Length == 0) continue;

                List<object> entries = Json.GetArray(map, "entries");

                if (entries != null)
                    foreach (object e in entries)
                    {
                        string value = e as string;
                        if (!string.IsNullOrEmpty(value)) pack.Entries.Add(value.Trim().ToLowerInvariant());
                    }

                List<object> args = Json.GetArray(map, "args");

                if (args != null)
                    foreach (object a in args)
                    {
                        string value = a as string;
                        if (!string.IsNullOrEmpty(value)) pack.Args.Add(value);
                    }

                // Тот же id из своего файла перекрывает поставку: человек мог
                // поправить пак, и обновление не должно это затирать.
                int existing = into.FindIndex(x => string.Equals(x.Id, pack.Id, StringComparison.OrdinalIgnoreCase));

                if (existing >= 0) into[existing] = pack;
                else into.Add(pack);
            }
        }

        /// <summary>Путь личного списка обхода, куда попадают домены пака.</summary>
        public static string UserHostlistPath
        {
            get { return Path.Combine(Paths.EngineListsDir, "list-general-user.txt"); }
        }

        /// <summary>
        /// Добавить домены пака в личный список обхода.
        ///
        /// Дубли не появляются: сравниваем без учёта регистра и без пробелов.
        /// Файл пишется целиком (атомарно), чтобы обрыв не оставил половину.
        /// </summary>
        public static string ApplyHostlist(FixPack pack, out int added, out int alreadyThere)
        {
            added = 0;
            alreadyThere = 0;

            if (pack == null || !pack.IsHostlist) return "это не пак со списком доменов";
            if (pack.Entries.Count == 0) return "в паке нет доменов";

            string path = UserHostlistPath;
            string dir = Path.GetDirectoryName(path);

            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { return "не создал каталог списков: " + ex.Message; }
            }

            string existing = "";

            if (File.Exists(path))
            {
                try { existing = File.ReadAllText(path, Encoding.UTF8); }
                catch (Exception ex)
                {
                    return "не прочитал " + Path.GetFileName(path) + ": " + ex.Message;
                }
            }

            string merged = MergeHostlist(existing, pack.Entries, out added, out alreadyThere);

            if (added == 0) return null;

            try
            {
                AtomicFile.WriteAllText(path, merged);
            }
            catch (Exception ex)
            {
                return "не записал список: " + ex.Message;
            }

            Log.Info("FIX", "пак «" + pack.Name + "»: добавлено " + added +
                            ", уже было " + alreadyThere);
            return null;
        }

        /// <summary>
        /// Слить домены в текст списка, не создавая дублей.
        ///
        /// Чистая функция: файл не трогает, поэтому её проверяет selftest.
        /// Строки-комментарии и пустые строки сохраняются как были — список
        /// принадлежит человеку, и переписывать его целиком нельзя.
        /// </summary>
        public static string MergeHostlist(string existingText, IEnumerable<string> entries,
                                           out int added, out int alreadyThere)
        {
            added = 0;
            alreadyThere = 0;

            List<string> lines = new List<string>();
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(existingText))
            {
                foreach (string raw in existingText.Replace("\r\n", "\n").Split('\n'))
                {
                    lines.Add(raw.TrimEnd('\r'));

                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    known.Add(line);
                }

                // хвостовые пустые строки не нужны: иначе каждый вызов добавлял бы одну
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
            }

            if (entries != null)
            {
                foreach (string entry in entries)
                {
                    string value = (entry ?? "").Trim().ToLowerInvariant();
                    if (value.Length == 0) continue;

                    if (known.Contains(value)) { alreadyThere++; continue; }

                    known.Add(value);
                    lines.Add(value);
                    added++;
                }
            }

            if (lines.Count == 0) return "";

            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        /// <summary>
        /// Собрать стратегию с профилем из пака.
        ///
        /// Не меняем выбранную стратегию: делаем её копию и дописываем профиль.
        /// Иначе «применил фикс» означало бы «изменил то, что было», и вернуть
        /// прежнее поведение можно было бы только переустановкой.
        /// </summary>
        public static Strategy BuildStrategy(FixPack pack, Strategy baseStrategy, out string error)
        {
            error = null;

            if (pack == null || !pack.IsStrategy) { error = "это не пак со стратегией"; return null; }
            if (baseStrategy == null) { error = "не выбрана стратегия, к которой добавить правило"; return null; }
            if (pack.Args.Count == 0) { error = "в паке нет аргументов"; return null; }

            Strategy copy = new Strategy
            {
                Id = "",
                Name = baseStrategy.Name + " + " + pack.Name,
                Engine = baseStrategy.Engine,
                Note = "к «" + baseStrategy.Name + "» добавлен пак «" + pack.Name + "»",
                Source = "fix:" + pack.Id,
                Custom = true,
                Recommended = false
            };

            copy.Targets.AddRange(baseStrategy.Targets);
            copy.GlobalArgs.AddRange(baseStrategy.GlobalArgs);

            foreach (StrategyProfile profile in baseStrategy.Profiles)
                copy.Profiles.Add(profile.Clone());

            StrategyProfile added = new StrategyProfile
            {
                Name = pack.Name,
                Label = "fix:" + pack.Id
            };

            added.Args.AddRange(pack.Args);
            copy.Profiles.Add(added);

            return copy;
        }
    }
}
