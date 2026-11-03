using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Одна запись пользовательского списка.</summary>
    public sealed class ListEntry
    {
        public string Value = "";
        public bool Enabled = true;
        public string Comment = "";

        public string ToLine()
        {
            string body = Enabled ? Value : "# " + Value;
            if (!string.IsNullOrEmpty(Comment)) body += "  # " + Comment;
            return body;
        }
    }

    /// <summary>
    /// Пользовательские списки доменов и IP.
    ///
    /// Правила (выстраданы в FluxRoute, issue #77):
    ///   * синхронизация ИДЕМПОТЕНТНА: если набор доменов не изменился, файл не
    ///     перезаписывается — иначе каждый старт затирает комментарии пользователя;
    ///   * `!domain` в списке целей означает ИСКЛЮЧЕНИЕ, а не цель. Отбрасывание «!»
    ///     превращает исключение во включение — самая дорогая ошибка в этом месте;
    ///   * запись только через AtomicFile;
    ///   * ipset приходит с CRLF — нормализуем в LF, иначе winws не разберёт.
    /// </summary>
    public static class HostlistManager
    {
        public const string FileGeneral = "list-general-user.txt";
        public const string FileExclude = "list-exclude-user.txt";
        public const string FileIpsetExclude = "ipset-exclude-user.txt";

        private static readonly Regex CommentRegex = new Regex(@"\s*#.*$", RegexOptions.Compiled);
        private static readonly Regex DomainRegex = new Regex(
            @"^[!]?(\*\.)?([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string GeneralPath { get { return Path.Combine(Paths.EngineListsDir, FileGeneral); } }
        public static string ExcludePath { get { return Path.Combine(Paths.EngineListsDir, FileExclude); } }
        public static string IpsetExcludePath { get { return Path.Combine(Paths.EngineListsDir, FileIpsetExclude); } }

        // ------------------------------------------------------------------ read

        /// <summary>Читает список. Понимает комментарии и «закомментированные» записи.</summary>
        public static List<ListEntry> Read(string path)
        {
            List<ListEntry> entries = new List<ListEntry>();

            string text = AtomicFile.ReadAllTextOrNull(path);
            if (string.IsNullOrEmpty(text)) return entries;

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                ListEntry entry = new ListEntry();

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    // «# domain  # comment» — отключённая запись.
                    // Но «# AutoZapret — пользовательский список» — это просто комментарий.
                    // Различаем по содержимому: запись должна быть похожа на домен или IP.
                    string body = line.TrimStart('#').Trim();
                    if (body.Length == 0) continue;

                    string withoutComment = CommentRegex.Replace(body, "").Trim();
                    if (withoutComment.Length == 0) continue;

                    if (!LooksLikeEntry(withoutComment)) continue;   // обычный комментарий

                    entry.Value = withoutComment;
                    entry.Enabled = false;
                    entry.Comment = ExtractComment(body);
                }
                else
                {
                    entry.Value = CommentRegex.Replace(line, "").Trim();
                    entry.Comment = ExtractComment(line);
                    entry.Enabled = entry.Value.Length > 0;
                }

                if (entry.Value.Length == 0) continue;
                entries.Add(entry);
            }

            return entries;
        }

        private static string ExtractComment(string line)
        {
            int idx = line.IndexOf('#');
            if (idx < 0) return "";
            return line.Substring(idx + 1).Trim();
        }

        /// <summary>
        /// Похоже ли значение на запись списка (домен или IP), а не на текст комментария.
        /// Нужно, чтобы «# AutoZapret — список» не превращался в запись.
        /// </summary>
        private static bool LooksLikeEntry(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.IndexOf(' ') >= 0) return false;      // в комментариях есть пробелы
            if (value.Length > 253) return false;

            return IsValidDomain(value) || IsValidIpOrCidr(value);
        }

        // ----------------------------------------------------------------- write

        /// <summary>
        /// Записывает список. Возвращает true, если файл действительно изменился.
        /// Идемпотентность: сравниваем эффективный набор, а не байты.
        /// </summary>
        public static bool Write(string path, List<ListEntry> entries)
        {
            List<ListEntry> current = Read(path);

            if (SameSet(current, entries))
            {
                Log.Debug("LIST", "список без изменений, не перезаписываю: " + Path.GetFileName(path));
                return false;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("# AutoZapret — пользовательский список\r\n");
            sb.Append("# Строка, начинающаяся с ! в списке целей, означает ИСКЛЮЧЕНИЕ.\r\n");
            sb.Append("# Изменения применяются при следующем запуске обхода.\r\n\r\n");

            foreach (ListEntry entry in entries)
                sb.Append(entry.ToLine()).Append("\r\n");

            AtomicFile.WriteAllText(path, sb.ToString());
            Log.Info("LIST", Path.GetFileName(path) + ": записей " + entries.Count);
            return true;
        }

        /// <summary>Сравнение по значению и включённости. Комментарии и порядок не важны.</summary>
        private static bool SameSet(List<ListEntry> a, List<ListEntry> b)
        {
            if (a.Count != b.Count) return false;

            HashSet<string> setA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListEntry e in a) setA.Add((e.Enabled ? "+" : "-") + e.Value.ToLowerInvariant());

            HashSet<string> setB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListEntry e in b) setB.Add((e.Enabled ? "+" : "-") + e.Value.ToLowerInvariant());

            return setA.SetEquals(setB);
        }

        // -------------------------------------------------------------- validate

        /// <summary>Похоже ли на домен (или маску *.domain).</summary>
        public static bool IsValidDomain(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();

            // «!domain» — допустимо только в списке целей, проверяем после снятия «!»
            if (v.StartsWith("!", StringComparison.Ordinal)) v = v.Substring(1);

            if (v.Length == 0 || v.Length > 253) return false;
            if (v.StartsWith(".", StringComparison.Ordinal) || v.EndsWith(".", StringComparison.Ordinal)) return false;
            if (v.Contains("..")) return false;

            return DomainRegex.IsMatch(v);
        }

        public static bool IsValidIpOrCidr(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();

            int slash = v.IndexOf('/');
            string addressPart = slash < 0 ? v : v.Substring(0, slash);

            System.Net.IPAddress address;
            if (!System.Net.IPAddress.TryParse(addressPart, out address)) return false;

            if (slash < 0) return true;

            int prefix;
            if (!int.TryParse(v.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix))
                return false;

            int max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return prefix >= 0 && prefix <= max;
        }

        /// <summary>Разбирает многострочный ввод: по одному значению на строку, запятые тоже делители.</summary>
        public static List<string> ParseInput(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                foreach (string part in line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string v = part.Trim();
                    if (v.Length > 0 && !result.Contains(v)) result.Add(v);
                }
            }
            return result;
        }

        // ----------------------------------------------------------------- ipset

        /// <summary>
        /// Нормализует ipset: убирает CRLF, пустые строки, комментарии и мусор.
        /// Возвращает количество валидных записей.
        /// </summary>
        public static int NormalizeIpset(string path)
        {
            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return 0;

            List<string> valid = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;

                if (!IsValidIpOrCidr(line))
                {
                    Log.Debug("LIST", "ipset: пропущена строка '" + line + "'");
                    continue;
                }

                if (seen.Add(line)) valid.Add(line);
            }

            StringBuilder sb = new StringBuilder();
            foreach (string v in valid) sb.Append(v).Append('\n');

            // именно LF: ipset-парсер winws чувствителен к \r
            AtomicFile.WriteAllText(path, sb.ToString());
            return valid.Count;
        }
    }
}
