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
            try
            {
                Resolver.Result resolved = Resolver.Resolve("www.youtube.com");

                if (!resolved.Ok) return "";
                if (!resolved.SystemLooksHijacked) return "";

                Log.Warn("DIAG", "системный DNS подменяется: " + resolved.SystemAnswer +
                                 ", DoH даёт " + resolved.Text());

                // Раньше здесь было «это точно VPN-туннель». Но точно так же
                // выглядит и подмена DNS провайдером (РКН перехватывает порт 53):
                // ответ 198.18.x приходит с любого «своего» DNS-сервера. Разница
                // важна: в первом случае виноват клиент на машине, во втором —
                // провайдер, и лечится это по-разному.
                return "системный DNS отдаёт " + resolved.SystemAnswer +
                       " (заглушка), а DoH — " + resolved.Text() + ". Так выглядит либо " +
                       "fake-IP режим VPN-клиента, либо подмена DNS провайдером. " +
                       "Проверки AutoZapret идут через DoH и видят настоящие адреса, " +
                       "поэтому обход измеряется по-настоящему. Браузеру включите " +
                       "Secure DNS (DoH) на 1.1.1.1 — иначе он ходит на заглушку.";
            }
            catch (Exception ex)
            {
                Log.Debug("DIAG", "проверка DNS: " + ex.Message);
                return "";
            }
        }
    }
}