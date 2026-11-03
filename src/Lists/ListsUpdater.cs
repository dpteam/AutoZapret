using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    public sealed class UpdateResult
    {
        public bool Ok;
        public string Message = "";
        public int Entries;
    }

    /// <summary>
    /// Обновление списков из интернета.
    ///
    /// Правила безопасности (AGENTS.md):
    ///   * скачанное проверяем: не HTML, размер в разумных пределах, каждая строка валидна;
    ///   * атомарная подмена: при ошибке остаётся предыдущая версия, а не пустой файл;
    ///   * ipset нормализуется (CRLF -> LF), иначе winws его не разберёт;
    ///   * никаких «докачаю поверх» — только полная замена после проверки.
    /// </summary>
    public static class ListsUpdater
    {
        /// <summary>ipset от V3nilla: CIDR-подсети, которые чаще всего недоступны.</summary>
        public const string IpsetUrl =
            "https://raw.githubusercontent.com/V3nilla/IPSets-For-Bypass-in-Russia/main/ipset-all.txt";

        /// <summary>Исключения по IP оттуда же.</summary>
        public const string IpsetExcludeUrl =
            "https://raw.githubusercontent.com/V3nilla/IPSets-For-Bypass-in-Russia/main/exclude.txt";

        /// <summary>Домены-исключения: банки, госуслуги, маркетплейсы.</summary>
        public const string ExcludeDomainsUrl =
            "https://raw.githubusercontent.com/V3nilla/IPSets-For-Bypass-in-Russia/main/exclude-domains.txt";

        private const int MinIpsetBytes = 10 * 1024;      // меньше — почти наверняка ошибка
        private const int MaxIpsetBytes = 20 * 1024 * 1024;

        public static string IpsetPath { get { return Path.Combine(Paths.EngineListsDir, "ipset-all.txt"); } }

        // -------------------------------------------------------------- ipset

        public static UpdateResult UpdateIpset()
        {
            UpdateResult result = new UpdateResult();

            try
            {
                Log.Info("LIST", "скачиваю ipset: " + IpsetUrl);

                string text = Download(IpsetUrl);
                if (text == null)
                {
                    result.Message = "не удалось скачать список";
                    return result;
                }

                // защита от подсунутой HTML-страницы
                string head = text.Length > 200 ? text.Substring(0, 200) : text;
                if (head.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Message = "сервер вернул HTML вместо списка";
                    Log.Error("LIST", result.Message);
                    return result;
                }

                int bytes = Encoding.UTF8.GetByteCount(text);
                if (bytes < MinIpsetBytes)
                {
                    result.Message = "список подозрительно мал (" + bytes + " байт)";
                    Log.Error("LIST", result.Message);
                    return result;
                }
                if (bytes > MaxIpsetBytes)
                {
                    result.Message = "список подозрительно велик (" + bytes + " байт)";
                    Log.Error("LIST", result.Message);
                    return result;
                }

                // пишем во временный файл рядом с целевым, нормализуем, потом подменяем
                string temp = IpsetPath + ".download";
                AtomicFile.WriteAllText(temp, text);

                int entries = HostlistManager.NormalizeIpset(temp);
                if (entries < 100)
                {
                    result.Message = "после проверки осталось мало записей (" + entries + ")";
                    Log.Error("LIST", result.Message);
                    try { File.Delete(temp); } catch { }
                    return result;
                }

                File.Copy(temp, IpsetPath, true);
                try { File.Delete(temp); } catch { }

                result.Ok = true;
                result.Entries = entries;
                result.Message = "обновлено записей: " + entries;

                Log.Info("LIST", "ipset обновлён: " + entries + " записей, " + (bytes / 1024) + " КБ");
                return result;
            }
            catch (Exception ex)
            {
                Log.Exception("LIST", "обновление ipset", ex);
                result.Message = ex.Message;
                return result;
            }
        }

        // ------------------------------------------------------------ exclusions

        /// <summary>Дополняет пользовательские исключения доменами от V3nilla.</summary>
        public static UpdateResult UpdateExcludeDomains()
        {
            UpdateResult result = new UpdateResult();

            try
            {
                string text = Download(ExcludeDomainsUrl);
                if (text == null)
                {
                    result.Message = "не удалось скачать список";
                    return result;
                }

                List<string> domains = HostlistManager.ParseInput(text);
                List<ListEntry> entries = HostlistManager.Read(HostlistManager.ExcludePath);

                HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ListEntry e in entries) have.Add(e.Value);

                int added = 0;
                foreach (string domain in domains)
                {
                    if (!HostlistManager.IsValidDomain(domain)) continue;
                    if (have.Contains(domain)) continue;

                    entries.Add(new ListEntry { Value = domain, Enabled = true, Comment = "v3nilla" });
                    have.Add(domain);
                    added++;
                }

                bool changed = HostlistManager.Write(HostlistManager.ExcludePath, entries);

                result.Ok = true;
                result.Entries = added;
                result.Message = added == 0
                    ? "все домены уже в списке (" + entries.Count + ")"
                    : "добавлено доменов: " + added + ", всего " + entries.Count;

                if (!changed && added == 0)
                    Log.Info("LIST", "исключения без изменений");

                return result;
            }
            catch (Exception ex)
            {
                Log.Exception("LIST", "обновление исключений", ex);
                result.Message = ex.Message;
                return result;
            }
        }

        /// <summary>Исключения по IP: приватные диапазоны + пара адресов инфраструктуры.</summary>
        public static UpdateResult UpdateIpsetExclude()
        {
            UpdateResult result = new UpdateResult();

            try
            {
                string text = Download(IpsetExcludeUrl);
                if (text == null)
                {
                    result.Message = "не удалось скачать список";
                    return result;
                }

                List<string> values = HostlistManager.ParseInput(text);
                List<string> valid = new List<string>();
                foreach (string v in values)
                    if (HostlistManager.IsValidIpOrCidr(v)) valid.Add(v);

                if (valid.Count == 0)
                {
                    result.Message = "в списке нет валидных подсетей";
                    return result;
                }

                // ВАЖНО: локальные диапазоны должны быть всегда, независимо от источника.
                // Без них обход лезет в локальную сеть и ломает её.
                foreach (string required in RequiredLocalRanges)
                    if (!valid.Contains(required)) valid.Insert(0, required);

                StringBuilder sb = new StringBuilder();
                foreach (string v in valid) sb.Append(v).Append('\n');
                AtomicFile.WriteAllText(HostlistManager.IpsetExcludePath, sb.ToString());

                result.Ok = true;
                result.Entries = valid.Count;
                result.Message = "подсетей-исключений: " + valid.Count;
                return result;
            }
            catch (Exception ex)
            {
                Log.Exception("LIST", "обновление ipset-исключений", ex);
                result.Message = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Локальные диапазоны, которые обязаны быть в исключениях всегда.
        /// Без них обход пытается «лечить» домашнюю сеть и принтеры.
        /// </summary>
        public static readonly string[] RequiredLocalRanges =
        {
            "0.0.0.0/8",
            "10.0.0.0/8",
            "127.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "169.254.0.0/16",
            "224.0.0.0/4",
            "100.64.0.0/10"
        };

        // -------------------------------------------------------------- helpers

        private static string Download(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 5;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Log.Warn("LIST", "HTTP " + (int)response.StatusCode + " от " + url);
                        return null;
                    }

                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream == null) return null;
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            return reader.ReadToEnd();
                    }
                }
            }
            catch (WebException ex)
            {
                Log.Warn("LIST", "сеть: " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("LIST", "скачивание: " + ex.Message);
                return null;
            }
        }
    }
}
