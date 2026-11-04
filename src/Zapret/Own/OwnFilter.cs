using System;
using System.Collections.Generic;
using System.Net;
using AutoZapret.Core;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Какие соединения трогать, а какие нет.
    ///
    /// Зачем это движку: без фильтра он десинкает ВЕСЬ TCP/443 — банк, госуслуги,
    /// рабочий VPN. winws этого не делает, у него списки; свой движок обязан
    /// вести себя так же, иначе им нельзя пользоваться вне теста.
    ///
    /// Правила повторяют winws, чтобы стратегии переносились без сюрпризов:
    ///   * `--hostlist` — домены, которые надо обходить. Если задан, остальное
    ///     не трогаем;
    ///   * `--ipset` — подсети. Проверяется по адресу назначения;
    ///   * `--hostlist-exclude` / `--ipset-exclude` — что не трогать никогда,
    ///     даже если оно попало в основной список;
    ///   * `!domain` внутри hostlist — тоже исключение (в zapret это не цель).
    ///
    /// Если не задано НИ ОДНОГО списка, фильтр пропускает всё: это режим
    /// «проверить технику», и он должен работать без настройки списков.
    /// </summary>
    public sealed class OwnFilter
    {
        private readonly List<string> _hosts = new List<string>();
        private readonly List<string> _hostsExclude = new List<string>();

        private IpRangeSet _ipset;
        private IpRangeSet _ipsetExclude;

        public int HostCount { get { return _hosts.Count; } }
        public int HostExcludeCount { get { return _hostsExclude.Count; } }
        public int IpCount { get { return _ipset == null ? 0 : _ipset.Count; } }
        public int IpExcludeCount { get { return _ipsetExclude == null ? 0 : _ipsetExclude.Count; } }

        /// <summary>Есть ли вообще ограничения. Если нет — пропускаем всё.</summary>
        public bool HasLimits
        {
            get { return _hosts.Count > 0 || ( _ipset != null && _ipset.Count > 0); }
        }

        public string Describe()
        {
            if (!HasLimits) return "без списков (весь трафик)";

            List<string> parts = new List<string>();

            if (_hosts.Count > 0) parts.Add("доменов " + _hosts.Count);
            if (_ipset != null && _ipset.Count > 0) parts.Add("подсетей " + _ipset.Count);

            if (_hostsExclude.Count > 0) parts.Add("исключений " + _hostsExclude.Count);
            if (_ipsetExclude != null && _ipsetExclude.Count > 0)
                parts.Add("подсетей-исключений " + _ipsetExclude.Count);

            return string.Join(", ", parts.ToArray());
        }

        // ------------------------------------------------------------------ load

        /// <summary>Загрузить списки из engine\lists. Отсутствующие файлы — не ошибка.</summary>
        public static OwnFilter FromEngineLists()
        {
            OwnFilter filter = new OwnFilter();

            string dir = Paths.EngineListsDir;

            filter.LoadHosts(System.IO.Path.Combine(dir, "list-general.txt"), false);
            filter.LoadHosts(System.IO.Path.Combine(dir, "list-general-user.txt"), false);
            filter.LoadHosts(System.IO.Path.Combine(dir, "list-google.txt"), false);
            filter.LoadHosts(System.IO.Path.Combine(dir, "list-exclude.txt"), true);
            filter.LoadHosts(System.IO.Path.Combine(dir, "list-exclude-user.txt"), true);

            filter._ipset = IpRangeSet.Load(System.IO.Path.Combine(dir, "ipset-all.txt"), out _);
            filter._ipsetExclude = IpRangeSet.Load(System.IO.Path.Combine(dir, "ipset-exclude.txt"), out _);

            Log.Info("OWN", "фильтр: " + filter.Describe());

            return filter;
        }

        /// <summary>
        /// Прочитать hostlist. `!domain` считается исключением независимо от файла:
        /// в zapret это «не трогать», и трактовать его как цель — самая дорогая
        /// ошибка в этих списках.
        /// </summary>
        public void LoadHosts(string path, bool excludeFile)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;

                foreach (string raw in System.IO.File.ReadLines(path))
                {
                    string line = raw.Trim();

                    if (line.Length == 0) continue;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                    bool negated = line.StartsWith("!", StringComparison.Ordinal);

                    if (negated) line = line.Substring(1).Trim();

                    // `^domain` в списках Flowseal — обычный домен с начала строки
                    if (line.StartsWith("^", StringComparison.Ordinal)) line = line.Substring(1).Trim();

                    if (line.Length == 0) continue;

                    // адреса и подсети в hostlist не место — они идут в ipset
                    if (line.IndexOf('/') > 0) continue;

                    string domain = NormalizeDomain(line);
                    if (domain.Length == 0) continue;

                    if (negated || excludeFile) _hostsExclude.Add(domain);
                    else _hosts.Add(domain);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("OWN", "hostlist " + System.IO.Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        private static string NormalizeDomain(string value)
        {
            string d = value.Trim().TrimEnd('.').ToLowerInvariant();

            if (d.StartsWith("*.", StringComparison.Ordinal)) d = d.Substring(2);
            if (d.StartsWith(".", StringComparison.Ordinal)) d = d.Substring(1);

            return d;
        }

        // ----------------------------------------------------------------- match

        /// <summary>
        /// Трогать ли это соединение.
        ///
        /// sni может быть пустым: тогда решение принимается только по адресу.
        /// Если задан hostlist, а SNI нет — соединение пропускаем: без имени
        /// нельзя сказать, наша это цель или чужой банк.
        /// </summary>
        public bool ShouldDesync(string sni, IPAddress destination)
        {
            // 1) исключения по адресу — раньше всех остальных правил
            if (destination != null && _ipsetExclude != null && _ipsetExclude.Contains(destination))
                return false;

            // 2) исключения по имени
            if (!string.IsNullOrEmpty(sni) && MatchesAny(_hostsExclude, sni))
                return false;

            // 3) без ограничений — трогаем всё
            if (!HasLimits) return true;

            // 4) списки по имени
            if (_hosts.Count > 0 && !string.IsNullOrEmpty(sni) && MatchesAny(_hosts, sni))
                return true;

            // 5) списки по адресу
            if (_ipset != null && _ipset.Count > 0 && destination != null && _ipset.Contains(destination))
                return true;

            return false;
        }

        /// <summary>
        /// Совпадение домена: сам домен или любой поддомен.
        ///
        /// Проверяем по меткам с конца, а не `EndsWith`: иначе `notexample.com`
        /// совпадёт с `example.com`, и мы полезем в чужой трафик.
        /// </summary>
        public static bool MatchesAny(List<string> list, string host)
        {
            if (list == null || list.Count == 0) return false;

            string h = NormalizeDomain(host);
            if (h.Length == 0) return false;

            foreach (string domain in list)
            {
                if (h.Length == domain.Length)
                {
                    if (string.Equals(h, domain, StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }

                if (h.Length < domain.Length + 1) continue;

                // h должен заканчиваться на "." + domain
                if (h[h.Length - domain.Length - 1] != '.') continue;

                if (string.Compare(h, h.Length - domain.Length, domain, 0, domain.Length,
                                   StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }

            return false;
        }
    }
}