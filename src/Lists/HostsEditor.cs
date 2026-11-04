using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Одна строка hosts: адрес и имена.</summary>
    public sealed class HostsRow
    {
        public string Ip = "";
        public List<string> Hosts = new List<string>();
    }

    /// <summary>Что нашлось в тексте hosts.</summary>
    public sealed class HostsReport
    {
        public int Lines;
        public int Comments;
        public int Rows;
        public int UniqueHosts;
        public int DuplicateHosts;
        public int Conflicts;

        public List<string> ConflictDetails = new List<string>();
        public List<string> DuplicateDetails = new List<string>();
        public List<string> Invalid = new List<string>();

        public string Summary()
        {
            string text = "строк " + Lines + ", записей " + Rows + ", имён " + UniqueHosts;

            if (DuplicateHosts > 0) text += ", дублей " + DuplicateHosts;
            if (Conflicts > 0) text += ", конфликтов " + Conflicts;
            if (Invalid.Count > 0) text += ", битых строк " + Invalid.Count;

            return text;
        }
    }

    /// <summary>
    /// Редактор hosts как текста: разбор, поиск дублей и конфликтов, склейка,
    /// слияние, применение готовых фиксов.
    ///
    /// Почему текстом, а не «умной» моделью: человек должен видеть файл целиком
    /// и править его руками. Программа только подсказывает и убирает мусор.
    /// Модель полезна для проверки, но она не должна затирать комментарии и
    /// чужие записи — это то, что чаще всего и теряется в таких редакторах.
    /// </summary>
    public static class HostsEditor
    {
        /// <summary>Разобрать текст: только реальные записи, комментарии пропускаем.</summary>
        public static List<HostsRow> Parse(string text, HostsReport report)
        {
            List<HostsRow> rows = new List<HostsRow>();

            if (string.IsNullOrEmpty(text)) return rows;

            int lineNumber = 0;

            foreach (string raw in text.Split('\n'))
            {
                lineNumber++;

                string line = raw.TrimEnd('\r').Trim();

                if (line.Length == 0) continue;

                if (report != null) report.Lines++;

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (report != null) report.Comments++;
                    continue;
                }

                // комментарий в конце строки
                int hash = line.IndexOf('#');
                if (hash > 0) line = line.Substring(0, hash).Trim();

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string ip = parts[0];
                System.Net.IPAddress parsed;

                if (!System.Net.IPAddress.TryParse(ip, out parsed))
                {
                    if (report != null && report.Invalid.Count < 20)
                        report.Invalid.Add(lineNumber + ": " + Shorten(parts[0]));

                    continue;
                }

                HostsRow row = new HostsRow { Ip = ip };

                for (int i = 1; i < parts.Length; i++)
                {
                    string host = parts[i].ToLowerInvariant();
                    if (host.Length == 0 || host == "localhost") continue;
                    if (row.Hosts.Contains(host)) continue;

                    row.Hosts.Add(host);
                }

                if (row.Hosts.Count == 0) continue;

                rows.Add(row);
                if (report != null) report.Rows++;
            }

            return rows;
        }

        /// <summary>Полный отчёт: дубли имён и конфликты адресов.</summary>
        public static HostsReport Analyze(string text)
        {
            HostsReport report = new HostsReport();
            List<HostsRow> rows = Parse(text, report);

            Dictionary<string, List<string>> byHost = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (HostsRow row in rows)
            {
                foreach (string host in row.Hosts)
                {
                    List<string> ips;
                    if (!byHost.TryGetValue(host, out ips)) { ips = new List<string>(); byHost[host] = ips; }

                    if (!ips.Contains(row.Ip)) ips.Add(row.Ip);
                }
            }

            report.UniqueHosts = byHost.Count;

            foreach (KeyValuePair<string, List<string>> pair in byHost)
            {
                if (pair.Value.Count == 1) continue;

                // IPv4 и IPv6 одного имени — не конфликт (двухстек)
                bool v4 = false, v6 = false;

                foreach (string ip in pair.Value)
                {
                    System.Net.IPAddress parsed;
                    if (!System.Net.IPAddress.TryParse(ip, out parsed)) continue;

                    if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4 = true;
                    else v6 = true;
                }

                if (pair.Value.Count > 1) report.DuplicateHosts++;

                bool conflict = (v4 && v4 && pair.Value.Count > 1 && CountFamily(pair.Value, true) > 1) ||
                                (v6 && CountFamily(pair.Value, false) > 1);

                if (!conflict) continue;

                report.Conflicts++;

                if (report.ConflictDetails.Count < 20)
                {
                    report.ConflictDetails.Add(pair.Key + " → " + string.Join(", ", pair.Value.ToArray()));
                }
            }

            // повтор одного и того же имени в строках с одинаковым IP — тоже дубль
            return report;
        }

        private static int CountFamily(List<string> ips, bool v4)
        {
            int count = 0;

            foreach (string ip in ips)
            {
                System.Net.IPAddress parsed;
                if (!System.Net.IPAddress.TryParse(ip, out parsed)) continue;

                bool isV4 = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                if (isV4 == v4) count++;
            }

            return count;
        }

        /// <summary>
        /// Убрать дубли: одинаковые IP склеиваются в одну строку, повтор имён
        /// исчезает. Конфликтующие адреса остаются — выбирать за человека, какой
        /// из них правильный, мы не будем, но в отчёте они видны.
        /// </summary>
        public static string Dedupe(string text)
        {
            HostsReport report = new HostsReport();
            List<HostsRow> rows = Parse(text, report);

            List<string> head = new List<string>();
            List<HostsRow> merged = new List<HostsRow>();
            Dictionary<string, HostsRow> byIp = new Dictionary<string, HostsRow>(StringComparer.OrdinalIgnoreCase);

            foreach (HostsRow row in rows)
            {
                HostsRow existing;

                if (!byIp.TryGetValue(row.Ip, out existing))
                {
                    existing = new HostsRow { Ip = row.Ip };
                    byIp[row.Ip] = existing;
                    merged.Add(existing);
                }

                foreach (string host in row.Hosts)
                    if (!existing.Hosts.Contains(host)) existing.Hosts.Add(host);
            }

            StringBuilder sb = new StringBuilder(text.Length);

            // комментарии и пустые строки сохраняем: файл остаётся узнаваемым
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    head.Add(line);
            }

            if (head.Count > 0)
            {
                foreach (string line in head) sb.Append(line).Append("\r\n");
                sb.Append("\r\n");
            }

            foreach (HostsRow row in merged)
            {
                sb.Append(row.Ip).Append(' ').Append(string.Join(" ", row.Hosts.ToArray())).Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>Добавить записи (фикс) к тексту, не плодя дублей.</summary>
        public static string AddEntries(string text, string title, IEnumerable<KeyValuePair<string, string>> entries)
        {
            HostsReport report = new HostsReport();
            List<HostsRow> rows = Parse(text, report);

            Dictionary<string, string> hostToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (HostsRow row in rows)
                foreach (string host in row.Hosts)
                    if (!hostToIp.ContainsKey(host)) hostToIp[host] = row.Ip;

            List<string> added = new List<string>();
            int skipped = 0;

            foreach (KeyValuePair<string, string> entry in entries)
            {
                string host = entry.Value.ToLowerInvariant();
                string ip = entry.Key;

                string existing;

                if (hostToIp.TryGetValue(host, out existing))
                {
                    // тот же адрес — дубль не нужен; другой адрес — не спорим,
                    // но скажем об этом в логе
                    if (!string.Equals(existing, ip, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn("HOSTS", "фикс «" + title + "»: " + host + " уже указывает на " + existing +
                                          ", оставляю как есть");
                    }

                    skipped++;
                    continue;
                }

                hostToIp[host] = ip;
                added.Add(ip + " " + host);
            }

            if (added.Count == 0)
            {
                Log.Info("HOSTS", "фикс «" + title + "»: добавлять нечего, всё уже есть");
                return text;
            }

            StringBuilder sb = new StringBuilder(text.TrimEnd());

            sb.Append("\r\n\r\n");
            sb.Append("# >>> AutoZapret fix: ").Append(title).Append("\r\n");

            foreach (string line in added) sb.Append(line).Append("\r\n");

            sb.Append("# <<< AutoZapret fix: ").Append(title).Append("\r\n");

            Log.Info("HOSTS", "фикс «" + title + "»: добавлено " + added.Count + ", уже было " + skipped);

            return sb.ToString();
        }

        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length > 40 ? value.Substring(0, 40) + "…" : value;
        }
    }
}