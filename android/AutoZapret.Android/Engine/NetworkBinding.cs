using System;
using System.Collections.Generic;
using Android.Content;
using Android.Net;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Выбор сети для исходящих соединений.
    ///
    /// Проблема, которую это решает (проверена на живом телефоне). Когда
    /// включены одновременно Wi-Fi и мобильная сеть, Android держит обе
    /// активными и сам решает, через какую отправить пакет. Решение не всегда
    /// то, что нужно: у оператора адрес может быть заблокирован по IP, а через
    /// домашний Wi-Fi тот же адрес открывается.
    ///
    /// Симптом ровно такой: DoH отвечает (1.1.1.1 доступен везде), а connect
    /// к адресу YouTube висит до таймаута. Снаружи это выглядит как «обход не
    /// работает», хотя дело в маршруте.
    ///
    /// Почему привязка процесса, а не сокета к локальному IP. Пробовали второе:
    /// сокет с source-адресом одной сети уходит в таблицу маршрутов другой и
    /// отбрасывается — с привязкой к Wi-Fi не открывался даже 1.1.1.1:443.
    /// `BindProcessToNetwork` — штатный механизм Android, он переключает
    /// маршрутизацию для всех сокетов процесса.
    /// </summary>
    public static class NetworkBinding
    {
        /// <summary>
        /// Какую сеть использовать для исходящих соединений.
        ///
        /// Зачем выбор руками. Автоматика берёт сеть по умолчанию, а она не
        /// всегда рабочая: домашний Wi-Fi без интернета или с captive portal,
        /// мобильная с блокировкой по IP. Человек должен иметь возможность
        /// переключить, не выключая Wi-Fi в системе.
        /// </summary>
        public const string ModeAuto = "auto";
        public const string ModeWifi = "wifi";
        public const string ModeMobile = "mobile";

        public static string ModeName(string mode)
        {
            if (mode == ModeWifi) return "только Wi-Fi";
            if (mode == ModeMobile) return "только мобильная";

            return "автоматически";
        }

        /// <summary>
        /// IPv4-адрес сети нужного типа. Пусто — сети нет или режим auto.
        ///
        /// Нужен движку: его дочерний процесс не наследует
        /// BindProcessToNetwork, поэтому при двух сетях он выберет маршрут сам.
        /// Флаг `-I` в byedpi привязывает исходящие соединения к этому адресу.
        /// </summary>
        public static string AddressOf(Context context, string mode)
        {
            if (string.IsNullOrEmpty(mode) || mode == ModeAuto) return "";

            try
            {
                ConnectivityManager manager =
                    context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

                if (manager == null) return "";

                foreach (Network network in manager.GetAllNetworks())
                {
                    NetworkCapabilities caps = manager.GetNetworkCapabilities(network);

                    if (caps == null) continue;
                    if (!caps.HasCapability(NetCapability.Internet)) continue;

                    bool match = (mode == ModeWifi && caps.HasTransport(TransportType.Wifi)) ||
                                 (mode == ModeMobile && caps.HasTransport(TransportType.Cellular));

                    if (!match) continue;

                    LinkProperties props = manager.GetLinkProperties(network);

                    if (props == null) continue;

                    string iface = props.InterfaceName ?? "";

                    if (iface.StartsWith("tun", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (LinkAddress link in props.LinkAddresses)
                    {
                        Java.Net.InetAddress address = link == null ? null : link.Address;

                        if (address == null) continue;

                        byte[] raw = address.GetAddress();

                        if (raw != null && raw.Length == 4) return address.HostAddress;
                    }
                }
            }
            catch
            {
            }

            return "";
        }

        /// <summary>
        /// Привязать ПРОЦЕСС к выбранной сети. Возвращает описание результата.
        ///
        /// mode: auto — снять привязку (пусть решает система), wifi/mobile —
        /// привязать к сети этого типа. Если запрошенной сети нет, привязка
        /// снимается: иначе приложение осталось бы вообще без связи.
        /// </summary>
        public static string BindProcess(Context context, string mode)
        {
            try
            {
                ConnectivityManager manager =
                    context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

                if (manager == null) return "нет доступа к сведениям о сети";

                if (string.IsNullOrEmpty(mode) || mode == ModeAuto)
                {
                    manager.BindProcessToNetwork(null);
                    return "автоматически (решает система)";
                }

                Network wanted = FindNetwork(manager, mode);

                if (wanted == null)
                {
                    manager.BindProcessToNetwork(null);
                    return "сети «" + ModeName(mode) + "» нет — оставил автоматически";
                }

                manager.BindProcessToNetwork(wanted);

                LinkProperties props = manager.GetLinkProperties(wanted);

                string iface = props == null ? "?" : (props.InterfaceName ?? "?");

                return ModeName(mode) + " (" + iface + ")";
            }
            catch (Exception ex)
            {
                return "ошибка привязки: " + ex.GetType().Name;
            }
        }

        /// <summary>Снять привязку (при остановке прокси).</summary>
        public static void Unbind(Context context)
        {
            try
            {
                ConnectivityManager manager =
                    context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

                if (manager != null) manager.BindProcessToNetwork(null);
            }
            catch
            {
            }
        }

        private static Network FindNetwork(ConnectivityManager manager, string mode)
        {
            foreach (Network network in manager.GetAllNetworks())
            {
                NetworkCapabilities caps = manager.GetNetworkCapabilities(network);

                if (caps == null) continue;
                if (!caps.HasCapability(NetCapability.Internet)) continue;

                // VPN-сеть пропускаем: привязка к ней даст петлю.
                LinkProperties props = manager.GetLinkProperties(network);
                string iface = props == null ? "" : (props.InterfaceName ?? "");

                if (iface.StartsWith("tun", StringComparison.OrdinalIgnoreCase)) continue;

                if (mode == ModeWifi && caps.HasTransport(TransportType.Wifi)) return network;
                if (mode == ModeMobile && caps.HasTransport(TransportType.Cellular)) return network;
            }

            return null;
        }

        /// <summary>
        /// Какие сети реально могут выйти в интернет — для строки в журнале.
        /// Без этого поведение при двух сетях выглядит случайным.
        /// </summary>
        public static List<string> Describe(Context context)
        {
            List<string> result = new List<string>();

            try
            {
                ConnectivityManager manager =
                    context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

                if (manager == null) return result;

                foreach (Network network in manager.GetAllNetworks())
                {
                    NetworkCapabilities caps = manager.GetNetworkCapabilities(network);

                    if (caps == null || !caps.HasCapability(NetCapability.Internet)) continue;

                    LinkProperties props = manager.GetLinkProperties(network);

                    if (props == null) continue;

                    string kind = "другая";

                    if (caps.HasTransport(TransportType.Wifi)) kind = "Wi-Fi";
                    else if (caps.HasTransport(TransportType.Cellular)) kind = "мобильная";
                    else if (caps.HasTransport(TransportType.Ethernet)) kind = "Ethernet";

                    result.Add(kind + " (" + (props.InterfaceName ?? "?") + ")");
                }
            }
            catch
            {
            }

            return result;
        }
    }
}
