using System;
using System.Collections.Generic;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Таблица дата-центров Telegram.
    ///
    /// Значения статические (как в refs/tg-ws-proxy): они не меняются годами,
    /// а лишний сетевой запрос на старте только замедляет подключение.
    /// </summary>
    public static class DcTable
    {
        public const string WsPath = "/apiws";
        public const string WsPathTest = "/apiws_test";

        private static readonly Dictionary<int, string> DefaultIps = new Dictionary<int, string>
        {
            { 1,   "149.154.175.50" },
            { 2,   "149.154.167.51" },
            { 3,   "149.154.175.100" },
            { 4,   "149.154.167.91" },
            { 5,   "149.154.171.5" },
            { 203, "91.105.192.100" }
        };

        private static readonly Dictionary<int, string> TestIps = new Dictionary<int, string>
        {
            { 1, "149.154.175.10" },
            { 2, "149.154.167.40" },
            { 3, "149.154.175.117" }
        };

        public static string IpFor(int dc, bool test)
        {
            string ip;
            if (test && TestIps.TryGetValue(dc, out ip)) return ip;
            if (DefaultIps.TryGetValue(dc, out ip)) return ip;
            return DefaultIps[2];   // безопасный дефолт
        }

        public static bool IsKnown(int dc)
        {
            return DefaultIps.ContainsKey(dc) || TestIps.ContainsKey(dc);
        }

        /// <summary>
        /// Домены для WS-подключения. DC 203 ходит через WS-DC 2
        /// (это особенность Telegram, не наша ошибка).
        /// </summary>
        public static List<string> WsDomains(int dc, bool isMedia)
        {
            if (dc == 203) dc = 2;

            List<string> result = new List<string>(2);

            if (!isMedia)
            {
                result.Add("kws" + dc + ".web.telegram.org");
                return result;
            }

            // медиа предпочитает «-1»-домен
            result.Add("kws" + dc + "-1.web.telegram.org");
            result.Add("kws" + dc + ".web.telegram.org");
            return result;
        }

        public static string PathFor(bool test)
        {
            return test ? WsPathTest : WsPath;
        }
    }
}
