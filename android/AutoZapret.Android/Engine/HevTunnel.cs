using System;
using System.Runtime.InteropServices;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// tun2socks: hev-socks5-tunnel (MIT, github.com/heiher/hev-socks5-tunnel).
    ///
    /// Зачем он: VpnService отдаёт нам поток IP-пакетов (tun), а не сокеты.
    /// Чтобы этот поток дошёл до нашего SOCKS5 (где и делается desync), нужен
    /// user-space TCP/IP. Писать его самому — недели, а hev это уже умеет и
    /// работает в ByeByeDPI.
    ///
    /// Почему P/Invoke, а не JNI-обёртка: библиотека экспортирует обычные
    /// C-функции, а .NET умеет звать их напрямую. Ноль Java-кода, ноль JNI.
    ///
    /// ВАЖНО про порядок вызовов. Сначала я звал `hev_socks5_tunnel_init` и
    /// `hev_socks5_tunnel_run` напрямую — и получил SIGSEGV внутри
    /// `hev_task_new`: этим функциям нужен уже инициализированный
    /// hev-task-system и lwip. Правильная точка входа — `..._main_from_str`:
    /// она сама делает логгер, `hev_task_system_init`, `lwip_init`, затем
    /// init, блокирующий run и уборку. Это и есть контракт библиотеки.
    /// </summary>
    internal static class HevTunnel
    {
        private const string Lib = "hev-socks5-tunnel";

        /// <summary>
        /// MTU туннеля. Одно значение на всё приложение: его же передаёт
        /// VpnService.Builder.SetMtu. Если значения разойдутся, крупные
        /// пакеты (картинки, тела ответов) приходят битыми, и страница
        /// остаётся со скелетом при живых соединениях.
        /// </summary>
        public const int Mtu = 1500;

        /// <summary>
        /// Запустить туннель. Блокирует поток до Stop(). 0 — успех, иначе -1.
        /// </summary>
        [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_main_from_str", CallingConvention = CallingConvention.Cdecl)]
        private static extern int MainFromStr(byte[] config, uint length, int tunFd);

        /// <summary>Остановить из другого потока (run вернётся сам).</summary>
        [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_quit", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Quit();

        [DllImport(Lib, EntryPoint = "hev_socks5_tunnel_stats", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Stats(out IntPtr txPackets, out IntPtr txBytes,
                                         out IntPtr rxPackets, out IntPtr rxBytes);

        /// <summary>Собрать YAML ровно в том виде, который ждёт hev.</summary>
        public static string BuildConfig(string tunIpv4, string tunIpv6, int socksPort)
        {
            // tunnel.ipv4/ipv6 обязаны совпадать с адресами, которые выдан
            // VpnService. mtu — ровно тот же, что в builder.SetMtu.
            //
            // Почему это критично: hev выставляет MTU интерфейсу сам, через
            // ioctl(SIOCSIFMTU), и его значение перекрывает builder.SetMtu.
            // Если здесь 8500, а канал 1500, приложения шлют сегменты по
            // 8500 байт, и крупные пакеты рвутся: страница рисует скелет и
            // зависает при живых соединениях.
            //
            // ipv6 передаётся только когда у сети действительно есть IPv6:
            // если его нет, а маршрут `::/0` завернут в туннель, ответ «не
            // подключиться» приходит сбросом, и браузер не откатывается на
            // IPv4 — сайт просто не открывается. Проверено на телефоне.
            string ipv6Line = string.IsNullOrEmpty(tunIpv6) ? "" : "  ipv6: '" + tunIpv6 + "'\n";

            return
                "tunnel:\n" +
                "  name: tun0\n" +
                "  mtu: " + Mtu + "\n" +
                "  ipv4: " + tunIpv4 + "\n" +
                ipv6Line +
                "  icmp: 'reply'\n" +
                "socks5:\n" +
                "  port: " + socksPort + "\n" +
                "  address: 127.0.0.1\n" +
                "  udp: 'udp'\n";
        }

        /// <summary>
        /// Блокирующий запуск. Возвращает текст ошибки или null.
        /// Вызывать только из фонового потока.
        /// </summary>
        public static string Run(int tunFd, string config)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(config);

            int result;

            try
            {
                result = MainFromStr(bytes, (uint)bytes.Length, tunFd);
            }
            catch (Exception ex)
            {
                return "hev: " + ex.GetType().Name + ": " + ex.Message;
            }

            return result == 0 ? null : "hev: туннель завершился с кодом " + result;
        }

        public static void Stop()
        {
            try { Quit(); } catch { }
        }

        public static string DescribeStats()
        {
            try
            {
                IntPtr txPackets, txBytes, rxPackets, rxBytes;

                Stats(out txPackets, out txBytes, out rxPackets, out rxBytes);

                return "tx " + txBytes.ToInt64() + " Б, rx " + rxBytes.ToInt64() + " Б";
            }
            catch
            {
                return "";
            }
        }
    }
}