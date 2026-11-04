using System;
using System.Collections.Generic;

namespace AutoZapret.Core
{
    /// <summary>DoH-сервер, который мы умеем спрашивать.</summary>
    public sealed class DohServer
    {
        public string Uri;
        public string Title;

        /// <summary>
        /// true — адрес в URI уже IP, резолвить нечего (работает всегда).
        /// false — в URI имя: чтобы к нему подключиться, нужен bootstrap через
        /// уже работающий IP-сервер, иначе получается «нужен DNS, чтобы узнать
        /// адрес DNS».
        /// </summary>
        public bool ByIp;
    }

    /// <summary>
    /// Список DoH-серверов.
    ///
    /// Порядок не случайный: сначала те, чей адрес известен как IP. Они работают
    /// даже при полностью подменённом DNS, и именно через них мы узнаём адреса
    /// остальных (bootstrap). Провайдеры блокируют по-разному, поэтому список
    /// длинный — российские, международные, AdGuard, Яндекс, Quad9, OpenDNS.
    /// </summary>
    public static class DohServers
    {
        public static List<DohServer> All()
        {
            List<DohServer> list = new List<DohServer>();

            // ---- по IP: не требуют DNS вообще
            Add(list, "https://1.1.1.1/dns-query", "Cloudflare 1.1.1.1");
            Add(list, "https://1.0.0.1/dns-query", "Cloudflare 1.0.0.1");
            Add(list, "https://8.8.8.8/dns-query", "Google 8.8.8.8");
            Add(list, "https://8.8.4.4/dns-query", "Google 8.8.4.4");
            Add(list, "https://9.9.9.9/dns-query", "Quad9 9.9.9.9");
            Add(list, "https://149.112.112.112/dns-query", "Quad9 149.112.112.112");
            Add(list, "https://94.140.14.14/dns-query", "AdGuard 94.140.14.14");
            Add(list, "https://94.140.15.15/dns-query", "AdGuard 94.140.15.15");
            Add(list, "https://208.67.222.222/dns-query", "OpenDNS 208.67.222.222");
            Add(list, "https://77.88.8.8/dns-query", "Яндекс 77.88.8.8");

            // ---- по имени: подключаемся к IP, полученному через bootstrap,
            //      но SNI и сертификат проверяются по имени
            Add(list, "https://cloudflare-dns.com/dns-query", "Cloudflare (имя)");
            Add(list, "https://dns.google/dns-query", "Google (имя)");
            Add(list, "https://dns.quad9.net/dns-query", "Quad9 (имя)");
            Add(list, "https://dns.adguard.com/dns-query", "AdGuard (имя)");
            Add(list, "https://dns.alidns.com/dns-query", "AliDNS");
            Add(list, "https://dns.comss.one/dns-query", "Comss");
            Add(list, "https://dns.astracat.network/dns-query", "AstraCat");
            Add(list, "https://dns.mafioznik.xyz/dns-query", "mafioznik.xyz");
            Add(list, "https://dns.malw.link/dns-query", "malw.link");
            Add(list, "https://free.shecan.ir/dns-query", "Shecan (IR)");
            Add(list, "https://dns.zapret.moe/dns-query", "zapret.moe");
            Add(list, "https://dns.wlrus.cfd/dns-query", "wlrus.cfd");
            Add(list, "https://dns.dagami.ru/dns-query", "dagami.ru");
            Add(list, "https://dns.magicdns.ru/dns-query", "MagicDNS");
            Add(list, "https://common.dot.dns.yandex.net/dns-query", "Яндекс DoT-имя");
            Add(list, "https://dns.nextdns.io", "NextDNS");
            Add(list, "https://doh.cleanbrowsing.org/doh/security-filter/", "CleanBrowsing");
            Add(list, "https://doh.opendns.com/dns-query", "OpenDNS (имя)");
            Add(list, "https://wikimedia-dns.org/dns-query", "Wikimedia");
            Add(list, "https://xbox-dns.ru/dns-query", "xbox-dns.ru");
            Add(list, "https://dns.bezmezhau.com/dns-query", "bezmezhau (BY)");
            Add(list, "https://dns.dns-ai.ru/dns-query", "dns-ai.ru");
            Add(list, "https://frd4wvnobp.cloudflare-gateway.com/dns-query", "Cloudflare Gateway");

            return list;
        }

        /// <summary>Первый URL, который мы порекомендуем браузеру.</summary>
        public static string Recommended()
        {
            return "https://1.1.1.1/dns-query";
        }

        private static void Add(List<DohServer> list, string uri, string title)
        {
            bool byIp;

            try
            {
                System.Net.IPAddress parsed;
                string host = new Uri(uri).Host;
                byIp = System.Net.IPAddress.TryParse(host, out parsed);
            }
            catch
            {
                byIp = false;
            }

            list.Add(new DohServer { Uri = uri, Title = title, ByIp = byIp });
        }
    }
}