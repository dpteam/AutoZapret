using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>Один профиль (секция между --new) внутри стратегии.</summary>
    public sealed class StrategyProfile
    {
        public string Name = "";
        public string Label = "";
        public List<string> Args = new List<string>();

        public StrategyProfile Clone()
        {
            return new StrategyProfile { Name = Name, Label = Label, Args = new List<string>(Args) };
        }
    }

    /// <summary>Стратегия целиком: набор профилей + глобальные аргументы winws.</summary>
    public sealed class Strategy
    {
        public string Id = "";
        public string Name = "";
        public string Engine = "v1";
        public List<string> Targets = new List<string>();
        public string Note = "";
        public bool Recommended;
        public string Source = "";

        /// <summary>Глобальные флаги до первого --new: --wf-tcp=, --wf-udp=</summary>
        public List<string> GlobalArgs = new List<string>();

        public List<StrategyProfile> Profiles = new List<StrategyProfile>();

        /// <summary>Полный список аргументов для winws.exe (global + профили через --new).</summary>
        public List<string> BuildArguments(Dictionary<string, string> placeholders)
        {
            List<string> result = new List<string>();
            foreach (string a in GlobalArgs) result.Add(Substitute(a, placeholders));

            for (int i = 0; i < Profiles.Count; i++)
            {
                if (i > 0) result.Add("--new");
                foreach (string a in Profiles[i].Args) result.Add(Substitute(a, placeholders));
            }
            return result;
        }

        public static string Substitute(string arg, Dictionary<string, string> placeholders)
        {
            if (string.IsNullOrEmpty(arg) || placeholders == null || placeholders.Count == 0) return arg;
            string result = arg;
            foreach (KeyValuePair<string, string> kv in placeholders)
                result = result.Replace("${" + kv.Key + "}", kv.Value);
            return result;
        }
    }

    /// <summary>
    /// Разбор .bat-файлов стратегий в модель Strategy.
    ///
    /// Зачем парсер, а не «просто запустить .bat»: нам нужно владеть процессом winws
    /// (PID-дерево, код выхода, аккуратный Stop), а для этого аргументы должны быть
    /// массивом строк, а не строкой для cmd.
    ///
    /// Что делаем:
    ///   1) читаем файл, склеиваем продолжения строк через ^
    ///   2) собираем переменные из set "X=Y"
    ///   3) находим строку, где встречается winws.exe
    ///   4) раскрываем %BIN%, %LISTS%, %GameFilterTCP% и т.п.
    ///   5) токенизируем по пробелам с учётом кавычек
    ///   6) режем по --new на профили
    ///   7) пути внутри bin/lists заменяем на плейсхолдеры ${BIN}/${LISTS},
    ///      чтобы стратегия была переносима и не зависела от абсолютных путей
    /// </summary>
    public static class StrategyParser
    {
        private static readonly Regex SetRegex = new Regex(
            @"^\s*set\s+""?([A-Za-z_][A-Za-z0-9_]*)=""?(.*?)""?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Разбирает один .bat. Возвращает null, если это не стратегия (нет winws).</summary>
        public static Strategy ParseBat(string path, string binDir, string listsDir)
        {
            return ParseBat(path, binDir, listsDir, null);
        }

        /// <summary>Разбирает один .bat. luaDir нужен только для v2 (winws2).</summary>
        public static Strategy ParseBat(string path, string binDir, string listsDir, string luaDir)
        {
            string text;
            try { text = File.ReadAllText(path, DetectEncoding(path)); }
            catch (Exception ex)
            {
                Log.Warn("WINWS", "не прочитал " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }

            List<string> logicalLines = JoinContinuations(text);

            Dictionary<string, string> vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            vars["~dp0"] = Path.GetDirectoryName(path) + "\\";

            string commandLine = null;
            string exeName = null;

            foreach (string line in logicalLines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("::", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("@echo", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("chcp", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("call ", StringComparison.OrdinalIgnoreCase)) continue;

                Match m = SetRegex.Match(trimmed);
                if (m.Success)
                {
                    vars[m.Groups[1].Value] = m.Groups[2].Value;
                    continue;
                }

                // winws2.exe проверяем ПЕРВЫМ: «winws.exe» не является его подстрокой,
                // но порядок всё равно фиксируем явно, чтобы не зависеть от случая.
                if (trimmed.IndexOf("winws2.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    commandLine = trimmed;
                    exeName = "winws2.exe";
                    continue;
                }
                if (trimmed.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    commandLine = trimmed;
                    exeName = "winws.exe";
                }
            }

            if (commandLine == null) return null;

            bool isV2 = exeName == "winws2.exe";

            // отрезаем всё до имени exe включительно
            int exeIdx = commandLine.IndexOf(exeName, StringComparison.OrdinalIgnoreCase);
            string argPart = commandLine.Substring(exeIdx + exeName.Length).Trim();
            argPart = argPart.TrimStart('"').Trim();

            string expanded = ExpandVariables(argPart, vars);
            List<string> tokens = Tokenize(expanded);
            if (tokens.Count == 0) return null;

            Strategy strategy = new Strategy
            {
                Id = (isV2 ? "zapret2." : "flowseal.") + NormalizeId(Path.GetFileNameWithoutExtension(path)),
                Name = Path.GetFileNameWithoutExtension(path),
                Engine = isV2 ? "v2" : "v1",
                Source = Path.GetFileName(path)
            };

            // нормализуем пути к плейсхолдерам
            tokens = NormalizePaths(tokens, binDir, listsDir, luaDir);

            // режем по --new
            List<string> current = new List<string>();
            List<List<string>> sections = new List<List<string>>();
            foreach (string token in tokens)
            {
                if (string.Equals(token, "--new", StringComparison.OrdinalIgnoreCase))
                {
                    sections.Add(current);
                    current = new List<string>();
                    continue;
                }
                current.Add(token);
            }
            sections.Add(current);

            // первый блок до --new содержит глобальные флаги и первый профиль
            if (sections.Count > 0)
            {
                List<string> first = sections[0];
                List<string> globals = new List<string>();
                List<string> profileArgs = new List<string>();
                foreach (string a in first)
                {
                    if (IsGlobalArg(a, isV2)) globals.Add(a);
                    else profileArgs.Add(a);
                }
                strategy.GlobalArgs = globals;
                if (profileArgs.Count > 0)
                    strategy.Profiles.Add(new StrategyProfile
                    {
                        Name = GuessProfileName(profileArgs),
                        Label = GuessProfileLabel(profileArgs),
                        Args = profileArgs
                    });
            }

            for (int i = 1; i < sections.Count; i++)
            {
                if (sections[i].Count == 0) continue;
                strategy.Profiles.Add(new StrategyProfile
                {
                    Name = GuessProfileName(sections[i]),
                    Label = GuessProfileLabel(sections[i]),
                    Args = sections[i]
                });
            }

            strategy.Targets = GuessTargets(strategy);
            strategy.Note = Describe(strategy);
            return strategy;
        }

        /// <summary>
        /// Глобальные флаги (действуют на весь процесс, а не на профиль).
        ///
        /// v1: --wf-tcp, --wf-udp
        /// v2: --wf-tcp-out, --wf-udp-out, --debug, --lua-init, --blob
        /// </summary>
        private static bool IsGlobalArg(string arg, bool isV2)
        {
            if (string.IsNullOrEmpty(arg)) return false;

            if (arg.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase)) return true;

            if (isV2)
            {
                if (arg.StartsWith("--debug=", StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith("--lua-init=", StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith("--blob=", StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith("--lua-gc=", StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith("--intercept=", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Склеивает строки, заканчивающиеся на ^ (продолжение команды).
        ///
        /// Тонкость: в конфигах zapret2 после ^ идёт ПУСТАЯ строка (для читаемости),
        /// и cmd её проглатывает, продолжая команду. Если пустые строки не глотать,
        /// команда обрывается на первом же ^, и все профили теряются —
        /// именно так v2 сначала давал 0 стратегий.
        /// </summary>
        private static List<string> JoinContinuations(string text)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            bool continuing = false;

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimEnd();

                // пустая строка после ^ — часть продолжения, пропускаем
                if (line.Length == 0)
                {
                    if (continuing) continue;
                    if (current.Length == 0) continue;
                    result.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                if (line.EndsWith("^", StringComparison.Ordinal))
                {
                    current.Append(line.Substring(0, line.Length - 1)).Append(' ');
                    continuing = true;
                    continue;
                }

                current.Append(line);
                result.Add(current.ToString());
                current.Length = 0;
                continuing = false;
            }

            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        /// <summary>
        /// Раскрывает переменные .bat.
        ///
        /// КЛЮЧЕВОЕ РЕШЕНИЕ: %BIN%, %LISTS%, %LUA% заменяются сразу на плейсхолдеры
        /// (${BIN}/, ${LISTS}/, ${LUA}/), а не на реальные пути. Причина: эти
        /// переменные указывают на подкаталоги того каталога, где лежит .bat
        /// (например refs\snanvl-zapret2\lists\), а движок живёт совсем в другом
        /// месте (bin\engine\lists). Если подставлять реальные пути, потом
        /// приходится угадывать их обратной заменой — и жадный regex съедает
        /// имя опции вместе с путём: "--hostlist=C:\...\lists\x.txt" превращается
        /// в "${LISTS}/x.txt", теряя --hostlist=.
        ///
        /// Спец-случаи: %GameFilterTCP%/%GameFilterUDP% — тоже плейсхолдеры,
        /// их значение считаем мы, а не .bat.
        /// </summary>
        private static string ExpandVariables(string input, Dictionary<string, string> vars)
        {
            string result = input;

            // служебные переменные Flowseal, которые мы считаем сами
            result = result.Replace("%GameFilterTCP%", "${GAME_TCP}");
            result = result.Replace("%GameFilterUDP%", "${GAME_UDP}");

            // каталоги движка -> плейсхолдеры (см. комментарий выше)
            result = result.Replace("%BIN%", "${BIN}/");
            result = result.Replace("%LISTS%", "${LISTS}/");
            result = result.Replace("%LUA%", "${LUA}/");

            // прочие %VAR% из set (кроме служебных ~-переменных)
            foreach (KeyValuePair<string, string> kv in vars)
            {
                if (kv.Key.StartsWith("~", StringComparison.Ordinal)) continue;
                if (string.Equals(kv.Key, "BIN", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "LISTS", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(kv.Key, "LUA", StringComparison.OrdinalIgnoreCase)) continue;
                result = result.Replace("%" + kv.Key + "%", kv.Value);
            }

            // %~dp0 и родственные раскрываются ПОСЛЕ %VAR%: они часто вложены внутрь
            // значений (set "LISTS=%~dp0lists\"), поэтому на этом шаге уже видны как текст.
            string batDir = vars.ContainsKey("~dp0") ? vars["~dp0"] : "";
            result = result.Replace("%~dp0", batDir);
            result = result.Replace("%~f0", batDir);
            result = result.Replace("%~n0", "");

            return result;
        }

        /// <summary>
        /// Токенизация по пробелам с учётом двойных кавычек. Кавычки убираются.
        ///
        /// Отдельно обрабатывается cmd-экранирование: вне кавычек `^X` означает
        /// литеральный `X`. В конфигах Flowseal это встречается как `--dpi-desync-fake-tls=^!`
        /// (экранирование `!`, чтобы отключить delayed expansion). Если не разэкранировать,
        /// winws получает `^!` и падает с кодом 1 — именно так и ломался FAKE TLS AUTO.
        /// </summary>
        public static List<string> Tokenize(string line)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(line)) return tokens;

            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                // вне кавычек ^ экранирует следующий символ
                if (!inQuotes && c == '^' && i + 1 < line.Length)
                {
                    char next = line[i + 1];
                    // ^ в конце строки - продолжение команды, до сюда уже не доходит
                    if (next == ' ' || next == '\t')
                    {
                        // "^ " - литеральный пробел внутри аргумента
                        sb.Append(' ');
                        i++;
                        continue;
                    }
                    sb.Append(next);
                    i++;
                    continue;
                }

                if (!inQuotes && (c == ' ' || c == '\t'))
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>
        /// Заменяет абсолютные пути bin/lists/lua на плейсхолдеры.
        ///
        /// Основная работа уже сделана в ExpandVariables (переменные сразу дают
        /// плейсхолдеры). Здесь — только подстраховка для случаев, когда в .bat
        /// путь прописан буквально.
        ///
        /// ВАЖНО: заменяем ТОЛЬКО когда токен начинается с пути (буква диска или
        /// два слеша), иначе жадный regex съест имя опции:
        /// "--hostlist=C:\...\lists\x.txt" не должен стать "${LISTS}/x.txt".
        /// </summary>
        private static List<string> NormalizePaths(List<string> tokens, string binDir, string listsDir, string luaDir)
        {
            List<string> result = new List<string>(tokens.Count);

            foreach (string token in tokens)
            {
                string t = token;

                if (IsAbsolutePath(t))
                {
                    t = Regex.Replace(t, @"^([A-Za-z]:[\\/]|\\\\)(.+[\\/])?lua[\\/]", "${LUA}/", RegexOptions.IgnoreCase);
                    t = Regex.Replace(t, @"^([A-Za-z]:[\\/]|\\\\)(.+[\\/])?lists[\\/]", "${LISTS}/", RegexOptions.IgnoreCase);
                    t = Regex.Replace(t, @"^([A-Za-z]:[\\/]|\\\\)(.+[\\/])?bin[\\/]", "${BIN}/", RegexOptions.IgnoreCase);
                    t = t.Replace('\\', '/');
                }
                else
                {
                    // опция с путём внутри: заменяем только сам путь, не трогая префикс
                    t = ReplacePathInside(t, binDir, "${BIN}/");
                    t = ReplacePathInside(t, listsDir, "${LISTS}/");
                    t = ReplacePathInside(t, luaDir, "${LUA}/");
                }

                result.Add(t);
            }
            return result;
        }

        private static bool IsAbsolutePath(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 3) return false;

            // "C:\..." или "\\server\..."
            if (char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/')) return true;
            if (value.Length >= 2 && value[0] == '\\' && value[1] == '\\') return true;

            return false;
        }

        /// <summary>Меняет известный путь на плейсхолдер, сохраняя префикс опции.</summary>
        private static string ReplacePathInside(string token, string dir, string placeholder)
        {
            if (string.IsNullOrEmpty(dir)) return token;

            string withSlash = dir.TrimEnd('\\', '/') + "\\";
            int idx = token.IndexOf(withSlash, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return token;

            return token.Substring(0, idx) + placeholder + token.Substring(idx + withSlash.Length);
        }

        private static string GuessProfileName(List<string> args)
        {
            string filterTcp = null, filterUdp = null, l7 = null, name = null, payload = null;

            foreach (string a in args)
            {
                if (a.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase)) filterTcp = a.Substring(13);
                else if (a.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase)) filterUdp = a.Substring(13);
                else if (a.StartsWith("--filter-l7=", StringComparison.OrdinalIgnoreCase)) l7 = a.Substring(12);
                else if (a.StartsWith("--name=", StringComparison.OrdinalIgnoreCase)) name = a.Substring(7).Trim('"');
                else if (a.StartsWith("--payload=", StringComparison.OrdinalIgnoreCase)) payload = a.Substring(10);
            }

            // v2: имя профиля задано явно в конфиге — используем его
            if (!string.IsNullOrEmpty(name)) return name;

            if (l7 != null) return "L7 " + l7;
            if (filterUdp != null && filterUdp.Contains("443")) return "QUIC (UDP 443)";
            if (filterUdp != null) return "UDP " + Shorten(filterUdp);
            if (filterTcp != null) return "TCP " + Shorten(filterTcp);
            if (payload != null) return "payload " + payload;
            return "профиль";
        }

        /// <summary>Короткая подпись профиля для списка в UI.</summary>
        private static string GuessProfileLabel(List<string> args)
        {
            string filterTcp = null, filterUdp = null, l7 = null;

            foreach (string a in args)
            {
                if (a.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase)) filterTcp = a.Substring(13);
                else if (a.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase)) filterUdp = a.Substring(13);
                else if (a.StartsWith("--filter-l7=", StringComparison.OrdinalIgnoreCase)) l7 = a.Substring(12);
            }

            if (l7 != null) return "l7:" + l7;
            if (filterUdp != null) return "udp:" + Shorten(filterUdp);
            if (filterTcp != null) return "tcp:" + Shorten(filterTcp);
            return "";
        }

        private static string Shorten(string ports)
        {
            if (ports.Length <= 24) return ports;
            return ports.Substring(0, 21) + "…";
        }

        private static List<string> GuessTargets(Strategy s)
        {
            List<string> targets = new List<string>();
            string all = string.Join(" ", s.Profiles.ConvertAll(p => string.Join(" ", p.Args)).ToArray());

            if (all.IndexOf("list-google", StringComparison.OrdinalIgnoreCase) >= 0) targets.Add("youtube");
            if (all.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0) targets.Add("discord");
            if (all.IndexOf("GameFilter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                all.IndexOf("${GAME_TCP}", StringComparison.OrdinalIgnoreCase) >= 0) targets.Add("games");
            if (targets.Count == 0) targets.Add("general");
            return targets;
        }

        private static string Describe(Strategy s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(s.Profiles.Count).Append(" профилей");

            string all = string.Join(" ", s.Profiles.ConvertAll(p => string.Join(" ", p.Args)).ToArray());

            if (s.Engine == "v2")
            {
                // v2: техника спрятана в --lua-desync=func:arg
                List<string> techniques = new List<string>();
                foreach (string func in new[] { "multisplit", "multidisorder", "fakedsplit", "hostfakesplit", "syndata", "fake" })
                {
                    if (all.IndexOf("lua-desync=" + func, StringComparison.OrdinalIgnoreCase) >= 0)
                        techniques.Add(func);
                }
                if (techniques.Count > 0) sb.Append(" · ").Append(string.Join("+", techniques.ToArray()));

                if (all.IndexOf("tcp_ts=", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · ts");
                if (all.IndexOf("tcp_seq=", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · badseq");
                if (all.IndexOf("tls_mod=", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · tls_mod");
                return sb.ToString();
            }

            if (all.IndexOf("multisplit", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · multisplit");
            else if (all.IndexOf("fakedsplit", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · fakedsplit");
            else if (all.IndexOf("hostfakesplit", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · hostfakesplit");
            else if (all.IndexOf("multidisorder", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · multidisorder");
            else if (all.IndexOf("syndata", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · syndata");

            if (all.IndexOf("--dpi-desync=fake", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" + fake");
            if (all.IndexOf("fooling=ts", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · ts");
            if (all.IndexOf("fooling=badseq", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append(" · badseq");

            return sb.ToString();
        }

        /// <summary>
        /// Имя файла -> идентификатор стратегии.
        /// "general (ALT12)"            -> "general-alt12"
        /// "general (FAKE TLS AUTO ALT)"-> "general-fake-tls-auto-alt"
        /// "general"                    -> "general"
        /// Читаемость важна: id попадает в настройки и в логи.
        /// </summary>
        private static string NormalizeId(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            bool lastDash = false;

            foreach (char raw in name.ToLowerInvariant())
            {
                char c = raw;
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    lastDash = false;
                    continue;
                }

                // всё остальное (пробел, скобки, дефис, подчёркивание) - разделитель
                if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }

            string id = sb.ToString().Trim('-');
            return id.Length == 0 ? "strategy" : id;
        }

        /// <summary>
        /// .bat-файлы Flowseal в UTF-8 (chcp 65001), но бывают и в OEM-866.
        /// Проверяем BOM, потом пробуем UTF-8 и откатываемся на 866.
        /// </summary>
        private static Encoding DetectEncoding(string path)
        {
            byte[] head = new byte[3];
            using (FileStream fs = File.OpenRead(path))
            {
                int read = fs.Read(head, 0, 3);
                if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                    return new UTF8Encoding(true);
            }
            try
            {
                // CodePagesEncodingProvider есть только в .NET Core.
                // В net48 Encoding.GetEncoding(866) работает из коробки.
                return Encoding.GetEncoding(866);
            }
            catch
            {
                return new UTF8Encoding(false);
            }
        }
    }
}
