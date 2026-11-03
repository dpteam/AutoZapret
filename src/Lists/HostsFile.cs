using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Одна запись hosts: IP + одно или несколько имён.</summary>
    public sealed class HostsEntry
    {
        public string Ip = "";
        public List<string> Hosts = new List<string>();
        public string Source = "";

        public string ToLine()
        {
            return Ip + " " + string.Join(" ", Hosts.ToArray());
        }
    }

    /// <summary>Конфликт: одно имя указывает на разные IP в разных источниках.</summary>
    public sealed class HostsConflict
    {
        public string Hostname = "";
        public List<string> Ips = new List<string>();
        public List<string> Sources = new List<string>();
        public string Chosen = "";
        public string ChosenSource = "";

        /// <summary>Проверен ли выбранный IP по TCP (и чем это закончилось).</summary>
        public bool Verified;
        public int VerifiedMs;
        public string VerifiedNote = "";

        public string Describe()
        {
            string basis = Verified
                ? "проверен: " + (VerifiedMs >= 0 ? VerifiedMs + " мс" : VerifiedNote)
                : "выбран по приоритету источника";

            return Hostname + ": " + string.Join(" / ", Ips.ToArray()) +
                   " → " + Chosen + " (" + basis + ")";
        }
    }

    /// <summary>Итог разбора: что добавится, что конфликтует, что мусор.</summary>
    public sealed class HostsAnalysis
    {
        public List<HostsEntry> Entries = new List<HostsEntry>();
        public List<HostsConflict> Conflicts = new List<HostsConflict>();
        public List<string> Invalid = new List<string>();
        public int ExactDuplicates;
        public int UserCollisions;      // имя уже есть в пользовательской части hosts
        public List<string> UserCollisionNames = new List<string>();

        public int UniqueHosts
        {
            get
            {
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (HostsEntry e in Entries)
                    foreach (string h in e.Hosts) set.Add(h);
                return set.Count;
            }
        }

        public string Summary()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(UniqueHosts).Append(" имён");

            if (ExactDuplicates > 0) sb.Append(", дубликатов ").Append(ExactDuplicates);
            if (Conflicts.Count > 0) sb.Append(", конфликтов ").Append(Conflicts.Count);
            if (Invalid.Count > 0) sb.Append(", мусора ").Append(Invalid.Count);
            if (UserCollisions > 0) sb.Append(", пересечений с вашими записями ").Append(UserCollisions);

            return sb.ToString();
        }
    }

    /// <summary>
    /// Управление файлом hosts.
    ///
    /// Зачем: hosts — самый «глупый» но иногда единственный способ обойти блокировку,
    /// когда DPI режет по DNS или когда домен вообще не резолвится.
    /// Правила:
    ///   * своя часть пишется между маркерами, всё остальное в файле не трогаем;
    ///   * перед первой записью делаем резервную копию;
    ///   * конфликты (одно имя -> разные IP) НЕ решаем молча: показываем и выбираем
    ///     по приоритету источника, потому что молчаливый выбор ломает сайт;
    ///   * перед записью проверяем, что имя не занято в пользовательской части файла —
    ///     иначе наши записи «перекроют» то, что человек настроил руками.
    /// </summary>
    public static class HostsFile
    {
        public const string BeginMarker = "# >>> AutoZapret >>>";
        public const string EndMarker = "# <<< AutoZapret <<<";

        /// <summary>Максимальный размер файла hosts, который мы готовы принять.</summary>
        private const int MaxHostsBytes = 8 * 1024 * 1024;

        private static readonly Regex HostsLineRegex = new Regex(
            @"^\s*(?<ip>\S+)\s+(?<names>[^#]+?)\s*(?:#.*)?$",
            RegexOptions.Compiled);

        private static readonly Regex HostnameRegex = new Regex(
            @"^(?=.{1,253}$)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Системный путь к hosts.</summary>
        public static string SystemPath
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(root)) root = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
                return Path.Combine(root, "System32", "drivers", "etc", "hosts");
            }
        }

        public static string BackupPath
        {
            get { return Path.Combine(Paths.StateDir, "hosts.backup"); }
        }

        // ------------------------------------------------------------------ read

        /// <summary>Читает hosts целиком. При ошибке возвращает null.</summary>
        public static string ReadSystem()
        {
            try
            {
                if (!File.Exists(SystemPath)) return "";
                return File.ReadAllText(SystemPath, DetectEncoding(SystemPath));
            }
            catch (Exception ex)
            {
                Log.Warn("HOSTS", "не прочитал hosts: " + ex.Message);
                return null;
            }
        }

        /// <summary>Есть ли уже наш блок и сколько в нём строк.</summary>
        public static bool HasBlock(string text, out int lineCount)
        {
            lineCount = 0;
            if (string.IsNullOrEmpty(text)) return false;

            int start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
            if (start < 0 || end < start) return false;

            string block = text.Substring(start, end - start);
            foreach (string line in block.Split('\n'))
                if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    lineCount++;

            return true;
        }

        /// <summary>Текст файла без нашего блока (пользовательская часть).</summary>
        public static string StripBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            int start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (start < 0) return text;

            int end = text.IndexOf(EndMarker, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            else end += EndMarker.Length;

            return text.Substring(0, start) + text.Substring(Math.Min(end, text.Length));
        }

        // ----------------------------------------------------------------- parse

        /// <summary>Разбирает произвольный текст hosts в записи.</summary>
        public static List<HostsEntry> Parse(string text, string source, HostsAnalysis analysis)
        {
            List<HostsEntry> entries = new List<HostsEntry>();
            if (string.IsNullOrEmpty(text)) return entries;

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                Match m = HostsLineRegex.Match(line);
                if (!m.Success)
                {
                    if (analysis != null) analysis.Invalid.Add(line);
                    continue;
                }

                string ip = m.Groups["ip"].Value.Trim();
                string namesPart = m.Groups["names"].Value.Trim();

                if (!IsValidIp(ip))
                {
                    if (analysis != null) analysis.Invalid.Add(line);
                    continue;
                }

                HostsEntry entry = new HostsEntry { Ip = ip, Source = source };

                foreach (string name in namesPart.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string host = name.Trim().ToLowerInvariant();
                    if (host.Length == 0) continue;
                    if (host == "localhost") continue;   // не трогаем
                    if (!HostnameRegex.IsMatch(host))
                    {
                        if (analysis != null) analysis.Invalid.Add(line);
                        continue;
                    }
                    entry.Hosts.Add(host);
                }

                if (entry.Hosts.Count > 0) entries.Add(entry);
            }

            return entries;
        }

        private static bool IsValidIp(string value)
        {
            IPAddress address;
            return IPAddress.TryParse(value, out address);
        }

        private static bool IsIpv6(string value)
        {
            IPAddress address;
            if (!IPAddress.TryParse(value, out address)) return false;
            return address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        // --------------------------------------------------------------- analyze

        /// <summary>
        /// Сливает несколько источников и находит конфликты.
        ///
        /// Приоритет источников: чем раньше в списке, тем выше доверие.
        /// Flowseal идёт первым: его hosts выверены под их же стратегии.
        ///
        /// preferredIps — результат проверки IP (имя -> адрес). Если задан,
        /// он важнее приоритета источника: живой адрес лучше «правильного».
        /// </summary>
        public static HostsAnalysis Analyze(List<KeyValuePair<string, string>> sources, string existingUserText)
        {
            return Analyze(sources, existingUserText, null);
        }

        public static HostsAnalysis Analyze(List<KeyValuePair<string, string>> sources, string existingUserText,
                                            Dictionary<string, string> preferredIps)
        {
            HostsAnalysis analysis = new HostsAnalysis();

            // hostname -> список (ip, источник) в порядке приоритета
            Dictionary<string, List<KeyValuePair<string, string>>> byHost =
                new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> exactPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> source in sources)
            {
                List<HostsEntry> parsed = Parse(source.Value, source.Key, analysis);

                foreach (HostsEntry entry in parsed)
                {
                    foreach (string host in entry.Hosts)
                    {
                        string key = entry.Ip + "|" + host;

                        if (!exactPairs.Add(key))
                        {
                            analysis.ExactDuplicates++;
                            continue;
                        }

                        List<KeyValuePair<string, string>> list;
                        if (!byHost.TryGetValue(host, out list))
                        {
                            list = new List<KeyValuePair<string, string>>();
                            byHost[host] = list;
                        }
                        list.Add(new KeyValuePair<string, string>(entry.Ip, source.Key));
                    }
                }
            }

            // имена, которые пользователь уже прописал вне нашего блока
            HashSet<string> userNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(existingUserText))
            {
                foreach (HostsEntry e in Parse(StripBlock(existingUserText), "user", null))
                    foreach (string h in e.Hosts) userNames.Add(h);
            }

            // собираем результат: на каждое имя — по одному адресу в каждом семействе.
            //
            // IPv4 и IPv6 НЕ конфликтуют: hosts поддерживает обе записи, и держать
            // обе правильнее (двухстековое подключение). Конфликт — это только
            // два разных адреса ВНУТРИ одного семейства.
            Dictionary<string, HostsEntry> byIp = new Dictionary<string, HostsEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> pair in byHost)
            {
                string host = pair.Key;
                List<KeyValuePair<string, string>> candidates = pair.Value;

                // разделяем на v4 и v6, сохраняя порядок приоритета
                List<string> v4 = new List<string>();
                List<string> v6 = new List<string>();
                foreach (KeyValuePair<string, string> c in candidates)
                {
                    bool isV6 = IsIpv6(c.Key);
                    List<string> target = isV6 ? v6 : v4;
                    if (!target.Contains(c.Key)) target.Add(c.Key);
                }

                List<string> chosen = new List<string>(2);

                // проверенный живой адрес важнее приоритета, но только внутри семейства
                string preferred = null;
                bool hasPreferred = preferredIps != null &&
                                    preferredIps.TryGetValue(host, out preferred) &&
                                    !string.IsNullOrEmpty(preferred);

                if (hasPreferred)
                {
                    bool prefV6 = IsIpv6(preferred);
                    List<string> family = prefV6 ? v6 : v4;
                    if (family.Contains(preferred)) chosen.Add(preferred);
                    else if (family.Count > 0) chosen.Add(family[0]);
                }
                else
                {
                    if (v4.Count > 0) chosen.Add(v4[0]);
                    if (v6.Count > 0) chosen.Add(v6[0]);
                }

                // если по семействам что-то потерялось — добираем первым из оставшихся
                if (chosen.Count == 0 && candidates.Count > 0) chosen.Add(candidates[0].Key);

                bool realConflict = (v4.Count > 1 || v6.Count > 1);

                if (realConflict)
                {
                    HostsConflict conflict = new HostsConflict
                    {
                        Hostname = host,
                        Chosen = chosen.Count > 0 ? chosen[0] : "",
                        ChosenSource = candidates[0].Value
                    };
                    foreach (KeyValuePair<string, string> c in candidates)
                    {
                        if (!conflict.Ips.Contains(c.Key)) conflict.Ips.Add(c.Key);
                        if (!conflict.Sources.Contains(c.Value)) conflict.Sources.Add(c.Value);
                    }
                    analysis.Conflicts.Add(conflict);
                }

                if (userNames.Contains(host))
                {
                    analysis.UserCollisions++;
                    if (!analysis.UserCollisionNames.Contains(host)) analysis.UserCollisionNames.Add(host);
                    continue;   // не перекрываем пользовательскую запись
                }

                foreach (string ip in chosen)
                {
                    HostsEntry target;
                    if (!byIp.TryGetValue(ip, out target))
                    {
                        target = new HostsEntry { Ip = ip, Source = "merge" };
                        byIp[ip] = target;
                    }
                    if (!target.Hosts.Contains(host)) target.Hosts.Add(host);
                }
            }

            analysis.Entries.AddRange(byIp.Values);

            // стабильная сортировка: по IP, чтобы diff был читаемым
            analysis.Entries.Sort((a, b) =>
            {
                int cmp = CompareIp(a.Ip, b.Ip);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Ip, b.Ip);
            });

            foreach (HostsEntry e in analysis.Entries)
                e.Hosts.Sort(StringComparer.OrdinalIgnoreCase);

            return analysis;
        }

        private static int CompareIp(string a, string b)
        {
            IPAddress ia, ib;
            if (!IPAddress.TryParse(a, out ia) || !IPAddress.TryParse(b, out ib)) return 0;

            byte[] ba = ia.GetAddressBytes();
            byte[] bb = ib.GetAddressBytes();
            if (ba.Length != bb.Length) return ba.Length.CompareTo(bb.Length);   // IPv4 раньше IPv6

            for (int i = 0; i < ba.Length; i++)
                if (ba[i] != bb[i]) return ba[i].CompareTo(bb[i]);

            return 0;
        }

        // ----------------------------------------------------------------- build

        /// <summary>Собирает итоговый файл: пользовательская часть + наш блок.</summary>
        public static string Build(string existingText, HostsAnalysis analysis, string note)
        {
            string baseText = StripBlock(existingText ?? "").TrimEnd();

            StringBuilder sb = new StringBuilder(baseText.Length + 64 * 1024);

            if (baseText.Length > 0)
            {
                sb.Append(baseText).Append("\r\n\r\n");
            }

            sb.Append(BeginMarker).Append("\r\n");
            sb.Append("# ").Append(note).Append("\r\n");
            sb.Append("# Обновлено: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("# Имён: ").Append(analysis.UniqueHosts.ToString(CultureInfo.InvariantCulture));
            if (analysis.Conflicts.Count > 0)
                sb.Append(", конфликтов решено: ").Append(analysis.Conflicts.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append("\r\n");
            sb.Append("# Удалить этот блок: вкладка «Списки» -> «Восстановить hosts»\r\n");

            foreach (HostsEntry entry in analysis.Entries)
            {
                sb.Append(entry.Ip).Append(' ').Append(string.Join(" ", entry.Hosts.ToArray())).Append("\r\n");
            }

            sb.Append(EndMarker).Append("\r\n");

            return sb.ToString();
        }

        // ------------------------------------------------------------------ write

        /// <summary>
        /// Записывает hosts. Делает резервную копию, если её ещё нет.
        /// Возвращает null при успехе или текст ошибки.
        /// </summary>
        public static string Apply(string content)
        {
            try
            {
                string path = SystemPath;

                // резервная копия — только первая, чтобы не затереть «до» более поздним «после»
                if (!File.Exists(BackupPath))
                {
                    try
                    {
                        Directory.CreateDirectory(Paths.StateDir);
                        if (File.Exists(path)) File.Copy(path, BackupPath, false);
                        else File.WriteAllText(BackupPath, "", new UTF8Encoding(false));
                        Log.Info("HOSTS", "резервная копия: " + BackupPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("HOSTS", "не удалось сделать резервную копию: " + ex.Message);
                    }
                }

                // hosts читают системные компоненты; пишем без BOM и с CRLF
                AtomicFile.WriteAllText(path, content, new UTF8Encoding(false));

                Log.Info("HOSTS", "записан: " + path);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Нет прав на запись hosts. Запустите AutoZapret от имени администратора.";
            }
            catch (Exception ex)
            {
                Log.Exception("HOSTS", "запись", ex);
                return ex.Message;
            }
        }

        /// <summary>Восстанавливает hosts из резервной копии. null = успех.</summary>
        public static string Restore()
        {
            try
            {
                if (!File.Exists(BackupPath))
                    return "Резервной копии нет — возможно, hosts ещё не менялся.";

                string content = File.ReadAllText(BackupPath, DetectEncoding(BackupPath));
                AtomicFile.WriteAllText(SystemPath, content, new UTF8Encoding(false));

                Log.Info("HOSTS", "восстановлен из " + BackupPath);
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Нет прав на запись hosts. Запустите AutoZapret от имени администратора.";
            }
            catch (Exception ex)
            {
                Log.Exception("HOSTS", "восстановление", ex);
                return ex.Message;
            }
        }

        /// <summary>Убирает только наш блок, остальное оставляет.</summary>
        public static string RemoveBlock()
        {
            try
            {
                string current = ReadSystem();
                if (current == null) return "Не удалось прочитать hosts.";

                if (current.IndexOf(BeginMarker, StringComparison.Ordinal) < 0)
                    return null;   // блока нет — уже хорошо

                string cleaned = StripBlock(current).TrimEnd() + "\r\n";
                AtomicFile.WriteAllText(SystemPath, cleaned, new UTF8Encoding(false));

                Log.Info("HOSTS", "блок AutoZapret удалён");
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return "Нет прав на запись hosts. Запустите AutoZapret от имени администратора.";
            }
            catch (Exception ex)
            {
                Log.Exception("HOSTS", "удаление блока", ex);
                return ex.Message;
            }
        }

        // -------------------------------------------------------------- download

        public const string MalwLinkUrl = "https://info.dns.malw.link/hosts";

        /// <summary>Скачивает hosts от dns.malw.link.</summary>
        public static string DownloadMalwLink()
        {
            return Download(MalwLinkUrl, "malw.link");
        }

        private static string Download(string url, string name)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Log.Warn("HOSTS", name + ": HTTP " + (int)response.StatusCode);
                        return null;
                    }

                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream == null) return null;
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            string text = reader.ReadToEnd();

                            if (text.Length == 0 || text.Length > MaxHostsBytes)
                            {
                                Log.Warn("HOSTS", name + ": подозрительный размер " + text.Length);
                                return null;
                            }

                            string head = text.Length > 200 ? text.Substring(0, 200) : text;
                            if (head.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Log.Warn("HOSTS", name + ": сервер вернул HTML");
                                return null;
                            }

                            return text;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("HOSTS", name + ": " + ex.Message);
                return null;
            }
        }

        private static Encoding DetectEncoding(string path)
        {
            try
            {
                byte[] head = new byte[3];
                using (FileStream fs = File.OpenRead(path))
                {
                    int read = fs.Read(head, 0, 3);
                    if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                        return new UTF8Encoding(true);
                }
            }
            catch { }

            return new UTF8Encoding(false);
        }
    }
}