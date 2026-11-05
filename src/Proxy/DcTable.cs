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
        ///
        /// ВАЖНО: возвращаем ОБА варианта имени, а не один.
        ///
        /// Раньше для обычного (не медиа) подключения отдавался только
        /// `kwsN.web.telegram.org` — то есть резервного варианта не было
        /// ВООБЩЕ. В сети, где провайдер режет это имя, прокси умирал на
        /// первом же домене: «таймаут TCP» и всё, пробовать больше нечего.
        /// Именно это и было в логе с живой машины — семь одинаковых строк
        /// подряд, потому что пробовать было нечего.
        ///
        /// Порядок задаёт только предпочтение: медиа сначала пробует `-1`
        /// (у него другой узел), обычное — основной. Остальные идут следом
        /// как резерв.
        /// </summary>
        public static List<string> WsDomains(int dc, bool isMedia)
        {
            if (dc == 203) dc = 2;

            List<string> result = new List<string>(2);

            string main = "kws" + dc + ".web.telegram.org";
            string alt = "kws" + dc + "-1.web.telegram.org";

            if (isMedia)
            {
                result.Add(alt);
                result.Add(main);
            }
            else
            {
                result.Add(main);
                result.Add(alt);
            }

            return result;
        }

        public static string PathFor(bool test)
        {
            return test ? WsPathTest : WsPath;
        }
    }
}
