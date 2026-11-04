using System;
using System.Collections.Generic;
using System.Net;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Ловушка «fake-IP»: VPN-клиенты на TUN (Hiddify, sing-box, Mihomo, Clash)
    /// подменяют DNS и отдают адреса из 198.18.0.0/15. Соединение «успешно»
    /// уходит в локальный туннель за 1-2 мс, дальше TLS рвётся, и ни один
    /// пакет не доходит до провайдера — то есть до DPI, который лечит zapret.
    ///
    /// По логам это выглядело как «TCP ok 1 мс, TLS сброс» и «движок не работает»:
    /// пакеты физически уходили в туннель, а не в интернет. WinDivert при этом
    /// видел только ответный RST от фальшивого адреса.
    ///
    /// Проверка дешёвая (один resolve) и отвечает на самый дорогой вопрос.
    /// </summary>
    public static class DnsGuard
    {
        /// <summary>
        /// 198.18.0.0/15 — диапазон бенчмарков, его используют TUN-клиенты
        /// для fake-IP. Реальный сайт туда попадать не может.
        /// </summary>
        public static bool IsFakeIp(IPAddress ip)
        {
            if (ip == null) return false;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

            byte[] b = ip.GetAddressBytes();
            return b[0] == 198 && (b[1] == 18 || b[1] == 19);
        }

        /// <summary>Адрес из fake-IP, если он есть среди ответов DNS.</summary>
        public static string FakeAddress(IEnumerable<IPAddress> addresses)
        {
            if (addresses == null) return "";

            foreach (IPAddress ip in addresses)
                if (IsFakeIp(ip)) return ip.ToString();

            return "";
        }

        /// <summary>
        /// Проверка на живом домене. Возвращает текст проблемы или "" если всё обычно.
        /// Результат кэшируется на сессию: проверка идёт перед каждым запуском
        /// стратегии, а DNS-запрос на ровном месте нам не нужен.
        /// </summary>
        public static string Detect()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                _cached = DetectCore();
                return _cached;
            }
        }

        /// <summary>Сбросить кэш: сеть могла смениться (VPN включили/выключили).</summary>
        public static void Reset()
        {
            lock (Gate) _cached = null;
        }

        private static readonly object Gate = new object();
        private static string _cached;

        private static string DetectCore()
        {
            string[] hosts = { "www.youtube.com", "discord.com", "www.google.com" };

            foreach (string host in hosts)
            {
                try
                {
                    IPAddress[] addresses = Dns.GetHostAddresses(host);
                    string fake = FakeAddress(addresses);

                    if (fake.Length == 0) continue;

                    Log.Warn("DIAG", "DNS отдаёт fake-IP " + fake + " для " + host +
                                     " — трафик уходит в туннель VPN");

                    return "DNS отдаёт " + fake + " для " + host +
                           ". Это fake-IP режим VPN-клиента (Hiddify/sing-box/Clash): " +
                           "трафик уходит в туннель и до провайдера не доходит, " +
                           "поэтому обходу нечего исправлять. Отключите VPN/туннель " +
                           "и проверьте снова.";
                }
                catch (Exception ex)
                {
                    Log.Debug("DIAG", "DNS " + host + ": " + ex.Message);
                }
            }

            return "";
        }
    }
}