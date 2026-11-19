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
        /// MTU туннеля.
        ///
        /// 8500 — не опечатка и не «оптимизация». Это ровно то значение,
        /// с которым работает ByeByeDPI (см. refs/byebyedpi
        /// NativeSession.kt: `appendLine("  mtu: 8500")`).
        ///
        /// Почему это принципиально. ClientHello современного Chrome —
        /// 1991 байт (снято с этого телефона: `split cancel: ... n=1991`).
        /// При MTU 1500 он не влезает в один сегмент (1991 + 40 = 2031) и
        /// уходит двумя: 1460 + 531. DPI собирает их обратно и видит SNI,
        /// а все техники byedpi рассчитаны на целый hello в одном куске —
        /// в логе это `split cancel`, то есть разрез ОТМЕНЁН. Снаружи
        /// выглядит так: мелкие запросы (curl, hello 296 Б) проходят
        /// мегабайтами, а браузер висит до ERR_TIMED_OUT.
        ///
        /// Значение обязано совпадать с builder.SetMtu и с конфигом hev.
        /// </summary>
        public const int Mtu = 8500;

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

        /// <summary>Собрать YAML ровно в том виде, который ждёт hev (логи в /dev/null).</summary>
        public static string BuildConfig(string tunIpv4, string tunIpv6, int socksPort)
        {
            return BuildConfig(tunIpv4, tunIpv6, socksPort, "/dev/null");
        }

        /// <summary>Собрать YAML ровно в том виде, который ждёт hev.</summary>
        public static string BuildConfig(string tunIpv4, string tunIpv6, int socksPort, string logPath)
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
                // offload НЕ задаём намеренно: у ByeByeDPI его тоже нет, и
                // jumbo остаётся включённым по умолчанию. Вместе с mtu 8500
                // это и есть рабочая комбинация: супер-сегмент уходит в tun
                // целиком, а не дробится на 1460+531.
                "socks5:\n" +
                "  port: " + socksPort + "\n" +
                "  address: 127.0.0.1\n" +
                "  udp: 'udp'\n" +
                "misc:\n" +
                // Логи hev в файл, а не в stdout: в Android-приложении stdout
                // уходит в /dev/null, и «log-file: stdout» означает «логов нет».
                // Файл потом читает фоновый поток и отдаёт в журнал приложения.
                "  log-file: " + logPath + "\n" +
                // Как у ByeByeDPI: стек задачи под 64-КБ супер-сегмент.
                // Меньше — hev не сможет отдать целый hello одним куском.
                "  task-stack-size: 81920\n" +
                "  log-level: " + LogLevel + "\n";
        }

        /// <summary>
        /// Уровень логов hev: debug / info / warn / error.
        ///
        /// debug включается флагом приложения и нужен для разбора поломок:
        /// видно каждый прочитанный из tun пакет и каждое открытие сессии.
        /// </summary>
        public static string LogLevel = "warn";

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