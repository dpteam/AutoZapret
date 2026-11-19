using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>Результат разбора `.azconf.json`.</summary>
    public sealed class ConfImportResult
    {
        public Strategy Strategy;

        /// <summary>Что не критично, но человек должен знать.</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>Почему файл отклонён. Пусто — принят.</summary>
        public string Error = "";

        public bool Ok { get { return Error.Length == 0 && Strategy != null; } }
    }

    /// <summary>
    /// Обмен одной стратегией файлом `.azconf.json`.
    ///
    /// Зачем, если уже есть экспорт в `.bat`. `.bat` — это программа: он
    /// содержит `start`, `setlocal`, абсолютные или относительные пути и
    /// запускается сам по себе. Отдать его другу — значит отдать исполняемый
    /// файл. `.azconf.json` — это ДАННЫЕ: описание стратегии, которое нельзя
    /// выполнить, можно прочитать глазами и можно проверить до добавления.
    ///
    /// Главное правило формата: **никаких абсолютных путей и никаких URL**.
    /// Всё, что указывает на файл, идёт через `${BIN}`/`${LISTS}`/`${LUA}`/
    /// `${FAKES}`. Иначе «поделиться конфигом» превращается в «загрузи и
    /// выполни»: чужой конфиг с `--hostlist=C:\Windows\System32\...` заставит
    /// движок читать произвольный файл, а с `\\чужой-сервер\share` — ходить
    /// в сеть за содержимым.
    ///
    /// Проверка идёт при импорте, а не только при экспорте: файл мог быть
    /// отредактирован руками или прийти из другой программы.
    /// </summary>
    public static class StrategyConf
    {
        public const int Schema = 1;
        public const string Kind = "azconf";

        /// <summary>Расширение файла. Двойное намеренно: `.json` виден редакторам.</summary>
        public const string Extension = ".azconf.json";

        // ------------------------------------------------------------- export

        /// <summary>
        /// Собрать текст файла. Отдельно от записи, чтобы selftest проверял
        /// формат, не трогая диск.
        /// </summary>
        public static string Build(Strategy strategy, string author, string appVersion)
        {
            if (strategy == null) return null;

            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", Schema },
                { "kind", Kind },
                { "app", "AutoZapret" },
                { "appVersion", appVersion ?? "" },
                { "author", string.IsNullOrEmpty(author) ? "" : author.Trim() },
                { "createdAt", DateTime.Now.ToString("o") }
            };

            Dictionary<string, object> s = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", strategy.Name ?? "" },
                { "engine", string.IsNullOrEmpty(strategy.Engine) ? "v1" : strategy.Engine },
                { "note", strategy.Note ?? "" }
            };

            List<object> targets = new List<object>();
            foreach (string t in strategy.Targets) targets.Add(t);
            s["targets"] = targets;

            List<object> globals = new List<object>();
            foreach (string a in strategy.GlobalArgs) globals.Add(Sanitize(a));
            s["globalArgs"] = globals;

            List<object> profiles = new List<object>();
            foreach (StrategyProfile p in strategy.Profiles)
            {
                List<object> args = new List<object>();
                foreach (string a in p.Args) args.Add(Sanitize(a));

                profiles.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", p.Name ?? "" },
                    { "label", p.Label ?? "" },
                    { "args", args }
                });
            }
            s["profiles"] = profiles;

            root["strategy"] = s;

            return Json.Stringify(root);
        }

        /// <summary>
        /// Записать файл. Возвращает путь или текст ошибки.
        ///
        /// Путь собираем сами, из имени стратегии: имя приходит из каталога и
        /// может содержать `/`, `:` и `..`, то есть создать файл в чужом
        /// каталоге.
        /// </summary>
        public static string Write(Strategy strategy, string author, string appVersion, string dir)
        {
            if (strategy == null) return "стратегия не выбрана";

            string text = Build(strategy, author, appVersion);

            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, FileName(strategy));

                // JSON в UTF-8 без BOM: файл читают чужие редакторы и программы,
                // а BOM часть из них считает частью первого ключа.
                File.WriteAllText(path, text, new UTF8Encoding(false));

                return path;
            }
            catch (Exception ex)
            {
                return "не записал файл: " + ex.Message;
            }
        }

        /// <summary>
        /// Безопасное имя файла: только латиница, цифры и дефис.
        ///
        /// Если от имени ничего не осталось (целиком кириллица), добавляем
        /// короткий хеш исходного имени. Без него две разные стратегии с
        /// русскими названиями дали бы ОДИН файл `strategy.azconf.json`, и
        /// второй экспорт молча затёр бы первый — а человек этого не увидит,
        /// потому что имя файла он не читает.
        /// </summary>
        public static string FileName(Strategy strategy)
        {
            string name = strategy == null ? "" : strategy.Name;

            if (string.IsNullOrEmpty(name)) name = "strategy";

            StringBuilder sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                if (c > 127) { sb.Append('-'); continue; }

                bool ok = char.IsLetterOrDigit(c) || c == '-' || c == '_';

                sb.Append(ok ? c : '-');
            }

            string clean = sb.ToString().Trim('-');

            // Повторные дефисы от «general (ALT12)» -> «general--ALT12-»
            while (clean.IndexOf("--", StringComparison.Ordinal) >= 0)
                clean = clean.Replace("--", "-");

            if (clean.Length == 0) clean = "strategy-" + ShortHash(name);
            if (clean.Length > 60) clean = clean.Substring(0, 60).Trim('-');

            return clean + Extension;
        }

        /// <summary>Шесть hex-символов от имени: различимы и не мешают читать.</summary>
        private static string ShortHash(string value)
        {
            uint hash = 2166136261;

            foreach (char c in value ?? "")
                hash = (hash ^ c) * 16777619;

            return hash.ToString("x6");
        }

        // ------------------------------------------------------------- import

        /// <summary>
        /// Разобрать и проверить файл. Стратегия возвращается уже готовой к
        /// добавлению: с id, помеченная как своя.
        ///
        /// prefix — чтобы свои конфиги не сталкивались с набором движка.
        /// </summary>
        public static ConfImportResult Parse(string text, string prefix)
        {
            ConfImportResult result = new ConfImportResult();

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Error = "файл пуст";
                return result;
            }

            Dictionary<string, object> root = Json.ParseObjectSafe(text);

            if (root == null)
            {
                result.Error = "не JSON или повреждён";
                return result;
            }

            // Вид файла: без этого `.azconf.json` не отличить от любого
            // другого JSON, и чужой файл будет разобран «как получится».
            string kind = Json.GetString(root, "kind", "");

            if (!string.Equals(kind, Kind, StringComparison.OrdinalIgnoreCase))
            {
                result.Error = "это не конфиг AutoZapret (kind = \"" + kind + "\")";
                return result;
            }

            int schema = Json.GetInt(root, "schema", 0);

            if (schema <= 0 || schema > Schema)
            {
                result.Error = "версия формата " + schema + " не поддерживается (эта программа знает " + Schema + ")";
                return result;
            }

            Dictionary<string, object> s = Json.GetObject(root, "strategy");

            if (s == null)
            {
                result.Error = "в файле нет стратегии";
                return result;
            }

            string name = Json.GetString(s, "name", "").Trim();

            if (name.Length == 0)
            {
                result.Error = "у стратегии нет имени";
                return result;
            }

            string engine = Json.GetString(s, "engine", "v1");

            if (engine != "v1" && engine != "v2")
            {
                result.Error = "неизвестный движок: " + engine;
                return result;
            }

            Strategy strategy = new Strategy
            {
                Name = name,
                Engine = engine,
                Note = Json.GetString(s, "note", ""),
                Custom = true,
                Source = "azconf"
            };

            // Автор — в заметку, а не в отдельное поле: человек должен видеть,
            // откуда стратегия, не открывая файл.
            string author = Json.GetString(root, "author", "").Trim();

            if (author.Length > 0)
            {
                if (strategy.Note.Length > 0) strategy.Note += " · ";
                strategy.Note += "от " + author;
            }

            List<object> targets = Json.GetArray(s, "targets");

            if (targets != null)
                foreach (object t in targets)
                {
                    string str = t as string;
                    if (!string.IsNullOrEmpty(str)) strategy.Targets.Add(str);
                }

            List<string> bad = new List<string>();

            List<object> globals = Json.GetArray(s, "globalArgs");

            if (globals != null)
                foreach (object g in globals)
                {
                    string arg = g as string;
                    if (arg == null) continue;

                    string why = Unsafe(arg);

                    if (why != null) { bad.Add(arg + " — " + why); continue; }

                    strategy.GlobalArgs.Add(arg);
                }

            List<object> profiles = Json.GetArray(s, "profiles");

            if (profiles != null)
            {
                foreach (object item in profiles)
                {
                    Dictionary<string, object> pm = item as Dictionary<string, object>;
                    if (pm == null) continue;

                    StrategyProfile profile = new StrategyProfile
                    {
                        Name = Json.GetString(pm, "name", ""),
                        Label = Json.GetString(pm, "label", "")
                    };

                    List<object> args = Json.GetArray(pm, "args");

                    if (args != null)
                        foreach (object a in args)
                        {
                            string arg = a as string;
                            if (arg == null) continue;

                            string why = Unsafe(arg);

                            if (why != null) { bad.Add(arg + " — " + why); continue; }

                            profile.Args.Add(arg);
                        }

                    if (profile.Args.Count > 0) strategy.Profiles.Add(profile);
                }
            }

            // Опасный аргумент — это отказ, а не «выкинем и продолжим».
            //
            // Иначе человек получит половину стратегии, которая молча не
            // работает: профиль есть, а списка, на который он ссылался, нет.
            // Пусть лучше файл не примут и скажут, что в нём не так.
            if (bad.Count > 0)
            {
                result.Error = "в файле недопустимые пути (" + bad.Count + "): " + bad[0];
                return result;
            }

            if (strategy.Profiles.Count == 0)
            {
                result.Error = "в стратегии нет ни одного профиля";
                return result;
            }

            if (string.IsNullOrEmpty(prefix)) prefix = "custom.";

            strategy.Id = prefix + Slug(name);

            result.Strategy = strategy;
            return result;
        }

        // --------------------------------------------------------------- checks

        /// <summary>
        /// Почему аргумент нельзя принимать. null — можно.
        ///
        /// Проверяем три вещи, каждая из которых превращает «конфиг» в способ
        /// заставить чужой компьютер сделать что-то:
        ///   * абсолютный путь (`C:\...`, `\\сервер\...`) — чтение чужого файла;
        ///   * `..` — выход за пределы разрешённого каталога;
        ///   * URL — обращение в сеть по адресу, который выбрал автор файла.
        /// </summary>
        public static string Unsafe(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;

            if (arg.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0 ||
                arg.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ссылка в сеть";

            if (arg.IndexOf("..", StringComparison.Ordinal) >= 0)
                return "выход из каталога через ..";

            // `%BIN%\x` — это переменная .bat, а не наш плейсхолдер. Такой
            // аргумент не развернётся, и движок получит путь с процентами.
            if (arg.IndexOf('%') >= 0)
                return "переменная .bat вместо ${BIN}/${LISTS}";

            string value = arg;

            int eq = arg.IndexOf('=');

            if (eq > 0) value = arg.Substring(eq + 1);

            if (LooksAbsolute(value)) return "абсолютный путь";

            // Путь может быть и без `=` — отдельным токеном после опции.
            if (LooksAbsolute(arg)) return "абсолютный путь";

            return null;
        }

        private static bool LooksAbsolute(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            // \\сервер\share
            if (value.Length >= 2 && value[0] == '\\' && value[1] == '\\') return true;

            // C:\... или C:/...
            if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
                (value[2] == '\\' || value[2] == '/'))
                return true;

            // file:///C:/...
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Часть id из имени. Публичная, чтобы копия стратегии получала такой
        /// же читаемый id, как импортированный файл, а не `custom.strategy-1a2b3c`.
        /// </summary>
        public static string IdPart(string name)
        {
            return Slug(name);
        }

        /// <summary>
        /// Привести имя к безопасному виду для id.
        ///
        /// id попадает в JSON и в список; пробелы и скобки в нём допустимы,
        /// но `general (ALT12)` рядом с `general-ALT12` — это два разных id,
        /// которые человек не отличит. Поэтому одинаковые правила для всех.
        /// </summary>
        private static string Slug(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }

                if (c == '.' || c == '_' || c == '-') { sb.Append(c); continue; }

                sb.Append('-');
            }

            string clean = sb.ToString().Trim('-');

            while (clean.IndexOf("--", StringComparison.Ordinal) >= 0)
                clean = clean.Replace("--", "-");

            if (clean.Length == 0) clean = "shared";
            if (clean.Length > 48) clean = clean.Substring(0, 48).Trim('-');

            return clean;
        }

        /// <summary>
        /// Убрать из аргумента то, чего не должно быть в файле.
        ///
        /// На экспорте это страховка: в каталоге могут оказаться стратегии,
        /// разобранные из чужого `.bat` с абсолютными путями (импорт `.bat`
        /// такие допускает — там это осознанно). В файл для друга они попасть
        /// не должны.
        /// </summary>
        private static string Sanitize(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "";

            string value = arg;
            int eq = arg.IndexOf('=');

            if (eq > 0)
            {
                string option = arg.Substring(0, eq);
                string rest = arg.Substring(eq + 1);

                if (LooksAbsolute(rest))
                {
                    // Оставляем имя файла: без каталога аргумент остаётся
                    // осмысленным, если файл лежит в ${BIN}.
                    string file = Path.GetFileName(rest.Replace('/', '\\'));

                    return option + "=${BIN}/" + file;
                }

                return value;
            }

            if (LooksAbsolute(value)) return "${BIN}/" + Path.GetFileName(value.Replace('/', '\\'));

            return value;
        }
    }
}
