using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Экспорт стратегии обратно в .bat.
    ///
    /// Зачем. Каталог стратегий живёт в JSON, а привычный формат у людей —
    /// .bat. Без экспорта человек, которому нужна одна стратегия, вынужден
    /// вручную переписывать аргументы из интерфейса, а это ровно тот способ
    /// получить опечатку, который мы и убирали.
    ///
    /// Формат — тот же, что читает StrategyParser: `set "BIN=..."` плюс строка
    /// с winws.exe. Поэтому экспорт и импорт обратимы, и это проверяется
    /// selftest-ом: parse(export(s)) должен дать те же аргументы.
    ///
    /// Почему абсолютные пути, а не `%~dp0`. Экспортированный .bat должен
    /// работать сразу после записи, без перекладывания папок. Если человек
    /// захочет перенести — в шапке написано, что менять.
    /// </summary>
    public static class StrategyBatWriter
    {
        /// <summary>Записать стратегию в .bat. Возвращает путь или текст ошибки.</summary>
        public static string Write(Strategy strategy, Dictionary<string, string> placeholders, string path)
        {
            if (strategy == null) return "стратегия не выбрана";
            if (placeholders == null) return "не заданы плейсхолдеры путей";

            string text = Build(strategy, placeholders);

            try
            {
                string dir = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // .bat читает cmd, а он ждёт CRLF и не понимает UTF-8 без BOM
                // в качестве основного шрифта консоли. Пишем UTF-8 с BOM:
                // тогда и кириллица в комментариях не превратится в мусор.
                File.WriteAllText(path, text, new UTF8Encoding(true));
                return null;
            }
            catch (Exception ex)
            {
                return "не записал " + Path.GetFileName(path) + ": " + ex.Message;
            }
        }

        /// <summary>
        /// Собрать текст .bat.
        ///
        /// Отдельным методом, чтобы selftest проверял текст, не трогая диск:
        /// ошибка здесь — это .bat, который молча не запускается.
        /// </summary>
        public static string Build(Strategy strategy, Dictionary<string, string> placeholders)
        {
            string bin = Placeholder(placeholders, "BIN");
            string lists = Placeholder(placeholders, "LISTS");
            string lua = Placeholder(placeholders, "LUA");

            string exe = string.Equals(strategy.Engine, "v2", StringComparison.OrdinalIgnoreCase)
                ? "winws2.exe"
                : "winws.exe";

            // Пути в .bat должны идти через переменные, а не абсолютными
            // значениями: иначе файл перестаёт работать при переносе папки, а
            // шапка обещает обратное. Подставляем `%BIN%` вместо реального
            // пути — ровно то, что делает `set "BIN=..."` ниже.
            //
            // Слэш оставляем прямым: `StrategyParser.ExpandVariables` при
            // обратном разборе заменяет `%BIN%` на `${BIN}/`, и обратный слэш
            // дал бы `${BIN}/\x.bin` — двойной разделитель и расхождение с
            // исходной стратегией.
            Dictionary<string, string> batPlaceholders =
                new Dictionary<string, string>(placeholders, StringComparer.Ordinal);

            batPlaceholders["BIN"] = "%BIN%";
            batPlaceholders["LISTS"] = "%LISTS%";
            batPlaceholders["LUA"] = "%LUA%";
            batPlaceholders["FAKES"] = "%FAKES%";

            List<string> args = strategy.BuildArguments(batPlaceholders);

            bool usesFakes = args.Exists(a => a.IndexOf("%FAKES%", StringComparison.Ordinal) >= 0);

            StringBuilder sb = new StringBuilder();

            sb.Append("@echo off\r\n");
            sb.Append(":: AutoZapret: стратегия \"").Append(strategy.Name).Append("\"\r\n");

            if (!string.IsNullOrEmpty(strategy.Note))
            {
                // Однострочно и без символов, которые cmd истолкует: заметка
                // приходит из каталога и может содержать что угодно.
                sb.Append(":: ").Append(Sanitize(strategy.Note)).Append("\r\n");
            }

            sb.Append("::\r\n");
            sb.Append(":: Если переносите папку с движком — поправьте путь в BIN ниже.\r\n");
            sb.Append(":: Остальные пути относительные (LISTS, LUA), их менять не нужно.\r\n");
            sb.Append("setlocal\r\n");

            // Абсолютный BIN нужен ровно для одного — перейти в каталог движка.
            // Дальше все пути относительные, и это не косметика.
            //
            // cmd.exe обрезает командную строку на 8191 символе, а считает он
            // её ПОСЛЕ подстановки переменных. У длинных стратегий (Shizapret:
            // 51 подстановка) абсолютные пути давали 7178 символов при
            // установке в E:\... — и 9218 при установке в глубокую папку вида
            // C:\Users\Имя\AppData\Local\Programs\...\AutoZapret-portable-0.2.0\.
            // То есть .bat создавался, а winws получал обрубок и не стартовал.
            //
            // С относительными путями длина команды вообще не зависит от того,
            // куда установили программу: `%LISTS%` это `..\lists`, а не
            // 51-символьный путь. Тот же приём, что в service.bat у Flowseal.
            sb.Append("set \"BIN=").Append(bin).Append("\"\r\n");
            sb.Append("cd /d \"%BIN%\"\r\n");
            sb.Append("set \"BIN=.\"\r\n");

            if (!string.IsNullOrEmpty(lists))
                sb.Append("set \"LISTS=").Append(Relative(bin, lists)).Append("\"\r\n");

            if (!string.IsNullOrEmpty(lua))
                sb.Append("set \"LUA=").Append(Relative(bin, lua)).Append("\"\r\n");

            if (usesFakes)
            {
                string fakes = Placeholder(placeholders, "FAKES");

                if (string.IsNullOrEmpty(fakes)) fakes = Path.Combine(bin, "..", "..", "assets", "fakes");

                sb.Append("set \"FAKES=").Append(Relative(bin, fakes)).Append("\"\r\n");
            }

            sb.Append("\r\n");
            sb.Append("start \"\" \"%BIN%\\").Append(exe).Append("\"");

            foreach (string arg in args)
            {
                sb.Append(' ').Append(Quote(arg));
            }

            sb.Append("\r\n");

            // Проверка длины: если стратегия когда-нибудь дорастёт до лимита,
            // лучше узнать об этом в логе при экспорте, чем от человека,
            // у которого «.bat есть, а обхода нет».
            int commandLength = 0;

            foreach (string line in sb.ToString().Split('\n'))
                if (line.StartsWith("start ", StringComparison.Ordinal))
                    commandLength = line.Length;

            if (commandLength > CmdLimitWarn)
            {
                Log.Warn("BAT", "стратегия «" + strategy.Name + "»: командная строка " +
                                commandLength + " символов, лимит cmd.exe 8191 — .bat может не запуститься");
            }

            return sb.ToString();
        }

        /// <summary>Лимит cmd.exe — 8191; предупреждаем заранее, с запасом.</summary>
        private const int CmdLimitWarn = 7800;

        /// <summary>
        /// Путь <paramref name="target"/> относительно <paramref name="from"/>,
        /// с обратными слешами (их ждёт cmd) и без завершающего слеша.
        /// </summary>
        private static string Relative(string from, string target)
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(target)) return target ?? "";

            try
            {
                string rel = Path.GetRelativePath(from, target);

                // Внутри одного каталога GetRelativePath отдаёт ".", и это
                // правильный ответ: `%LISTS%` рядом с bin — это `.\x.txt`.
                return rel.Replace('/', '\\').TrimEnd('\\');
            }
            catch
            {
                // Пути на разных дисках относительными не бывают — оставляем
                // абсолютный: пусть будет длинно, но работоспособно.
                return target;
            }
        }

        /// <summary>
        /// Имя файла для стратегии: только безопасные символы.
        ///
        /// Имя стратегии приходит из каталога и может содержать `/`, `:` и
        /// пробелы. Взять его как есть — значит либо не создать файл, либо
        /// создать его в чужом каталоге.
        /// </summary>
        public static string FileName(Strategy strategy)
        {
            string name = strategy == null ? "" : strategy.Name;

            if (string.IsNullOrEmpty(name)) name = "strategy";

            StringBuilder sb = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ';

                // Кириллица допустима в имени файла на NTFS, но .bat с ней
                // может не найтись при запуске из другой локали — оставляем
                // только латиницу и цифры.
                if (c > 127) { sb.Append('_'); continue; }

                sb.Append(ok ? c : '_');
            }

            string clean = sb.ToString().Trim();

            if (clean.Length == 0) clean = "strategy";
            if (clean.Length > 60) clean = clean.Substring(0, 60).Trim();

            return clean + ".bat";
        }

        /// <summary>
        /// Значение для аргумента командной строки.
        ///
        /// Квотируем только когда есть пробел: лишние кавычки winws принимает
        /// за часть значения (`--hostlist="C:\a b\x.txt"` ломается).
        /// </summary>
        private static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";

            if (arg.IndexOf(' ') < 0 && arg.IndexOf('\t') < 0) return arg;

            return "\"" + arg.Replace("\"", "") + "\"";
        }

        /// <summary>Заметка не должна ломать .bat: убираем переводы строк и `%`.</summary>
        private static string Sanitize(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";

            StringBuilder sb = new StringBuilder(note.Length);

            foreach (char c in note)
            {
                if (c == '\r' || c == '\n') { sb.Append(' '); continue; }

                // `%` в .bat — начало подстановки. В строке `::` его обычно не
                // раскрывают, но полагаться на это не стоит: заметка приходит из
                // каталога и может содержать что угодно. Проще убрать.
                if (c == '%') continue;

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        private static string Placeholder(Dictionary<string, string> placeholders, string key)
        {
            string value;

            if (placeholders.TryGetValue(key, out value)) return value ?? "";

            return "";
        }
    }
}
