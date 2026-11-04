using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace AutoZapret.Core
{
    /// <summary>
    /// Единая точка разрешения имён.
    ///
    /// Порядок: DoH по IP (не зависит от системного DNS) → системный DNS.
    /// Причина: системный DNS провайдер может подменять, и тогда программа
    /// честно меряет не DPI, а заглушку. Проверять и тестировать надо по
    /// настоящим адресам — иначе «0 из 4» у всех стратегий.
    /// </summary>
    public static class Resolver
    {
        public sealed class Result
        {
            public List<IPAddress> Addresses = new List<IPAddress>();

            /// <summary>«DoH 1.1.1.1» или «системный DNS».</summary>
            public string Source = "";

            /// <summary>Была ли подмена: системный ответ — fake-IP, а DoH дал обычные адреса.</summary>
            public bool SystemLooksHijacked;

            /// <summary>Что вернул системный DNS (для отчёта), даже если выбрали DoH.</summary>
            public string SystemAnswer = "";

            public bool Ok { get { return Addresses.Count > 0; } }

            public IPAddress Primary { get { return Addresses.Count > 0 ? Addresses[0] : null; } }

            public string Text()
            {
                List<string> parts = new List<string>();
                foreach (IPAddress ip in Addresses) parts.Add(ip.ToString());
                return string.Join(", ", parts.ToArray());
            }
        }

        public static Result Resolve(string host)
        {
            Result result = new Result();

            // ---- 1. DoH
            List<IPAddress> doh = Doh.Resolve(host);

            // ---- 2. системный DNS (всегда, чтобы показать подмену)
            List<IPAddress> system = new List<IPAddress>();

            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(host))
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        system.Add(ip);
            }
            catch (Exception ex)
            {
                Log.Debug("DNS", "системный DNS " + host + ": " + ex.Message);
            }

            result.SystemAnswer = Join(system);

            if (doh.Count > 0)
            {
                result.Addresses = doh;
                result.Source = "DoH " + ShortServer(Doh.LastServer);
                result.SystemLooksHijacked = system.Count > 0 && IsFakeList(system);
                return result;
            }

            result.Addresses = system;
            result.Source = "системный DNS";
            return result;
        }

        public static string Join(List<IPAddress> addresses)
        {
            List<string> parts = new List<string>();
            foreach (IPAddress ip in addresses) parts.Add(ip.ToString());
            return string.Join(", ", parts.ToArray());
        }

        private static bool IsFakeList(List<IPAddress> addresses)
        {
            foreach (IPAddress ip in addresses)
            {
                byte[] b = ip.GetAddressBytes();

                // 198.18.0.0/15 — типичная заглушка подмены (benchmark-диапазон)
                if (b.Length == 4 && b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;

                // 0.0.0.0 / 127.x — тоже заглушки
                if (b.Length == 4 && (b[0] == 0 || b[0] == 127)) return true;
            }

            return false;
        }

        private static string ShortServer(string url)
        {
            if (string.IsNullOrEmpty(url)) return "?";

            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return url;
            }
        }

        /// <summary>DNS-серверы, прописанные в адаптерах: их полезно видеть в отчёте.</summary>
        public static string DnsServers()
        {
            List<string> servers = new List<string>();

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

                        string text = dns + " (" + ni.Name + ")";
                        if (!servers.Contains(text)) servers.Add(text);
                    }
                }
            }
            catch { }

            return servers.Count == 0 ? "не заданы" : string.Join(", ", servers.ToArray());
        }
    }
}