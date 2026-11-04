using System;
using System.Runtime.InteropServices;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// P/Invoke над WinDivert. Первый слой своего движка (ROADMAP P10.1).
    ///
    /// Почему нативный вызов, а не winws.exe: нам нужен доступ к самим пакетам,
    /// а не к готовому бинарю, который решает всё внутри себя. Без этого нельзя
    /// ни показать, что реально происходит, ни сделать свои desync-техники.
    ///
    /// Структуры объявлены как «сырые» байты, а не как C#-структуры: у WINDIVERT_ADDRESS
    /// битовые поля, и любая ошибка в layout даёт мусор вместо адреса, причём
    /// молча. Поэтому размер фиксирован константой, а поля читаются через смещения.
    /// </summary>
    public static class WinDivertInterop
    {
        private const string Dll = "WinDivert.dll";

        /// <summary>
        /// WinDivert.dll лежит в engine\bin, а не рядом с exe, поэтому обычный
        /// поиск DLL её не находит (DllNotFoundException). Резолвер ищет по
        /// полному пути — без SetDllDirectory и без правки PATH.
        ///
        /// Заодно рядом с DLL лежит WinDivert64.sys: драйвер ищется относительно
        /// неё, а не рабочего каталога, поэтому загрузка по полному пути важна
        /// ещё и для него.
        /// </summary>
        static WinDivertInterop()
        {
            try
            {
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                    typeof(WinDivertInterop).Assembly, Resolve);
            }
            catch
            {
                // резолвер уже назначен кем-то другим — не падаем, попробуем обычный путь
            }
        }

        private static IntPtr Resolve(string name, System.Reflection.Assembly assembly,
                                      System.Runtime.InteropServices.DllImportSearchPath? searchPath)
        {
            if (!string.Equals(name, Dll, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

            try
            {
                string full = System.IO.Path.Combine(Core.Paths.EngineBinDir, Dll);

                if (System.IO.File.Exists(full) &&
                    System.Runtime.InteropServices.NativeLibrary.TryLoad(full, out IntPtr handle))
                    return handle;
            }
            catch { }

            return IntPtr.Zero;
        }

        /// <summary>WINDIVERT_LAYER_NETWORK — то, что нам нужно для TCP.</summary>
        public const int LayerNetwork = 0;

        // Флаги WinDivertOpen
        public const ulong FlagSniff = 0x0001;       // копировать, НЕ перехватывать
        public const ulong FlagDrop = 0x0002;        // сразу отбрасывать
        public const ulong FlagRecvOnly = 0x0004;
        public const ulong FlagSendOnly = 0x0008;
        public const ulong FlagNoInstall = 0x0010;   // не ставить службу драйвера

        /// <summary>ERROR_TIMEOUT: recv истёк по таймауту, это не ошибка.</summary>
        public const int ErrorTimeout = 1460;
        public const int ErrorNoData = 232;
        public const int ErrorInsufficientBuffer = 122;

        /// <summary>Размер WINDIVERT_ADDRESS в байтах (2.2.x).</summary>
        public const int AddressSize = 88;

        /// <summary>Максимальный размер пакета, который отдаёт WinDivert.</summary>
        public const int MaxPacketSize = 65535;

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen,
                                                out uint recvLen, IntPtr address);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen,
                                                out uint sendLen, IntPtr address);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertClose(IntPtr handle);

        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertSetParam(IntPtr handle, int param, ulong value);

        /// <summary>
        /// Пересчёт контрольных сумм IP/TCP/UDP прямо в буфере пакета.
        /// Без него любой наш изменённый пакет уйдёт с битой суммой, и получатель
        /// молча его выбросит — самый неприятный вид «ничего не работает».
        /// </summary>
        [DllImport(Dll, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen,
                                                               IntPtr address, ulong flags);

        // Параметры WinDivertSetParam
        public const int ParamQueueLength = 0;
        public const int ParamQueueTime = 1;
        public const int ParamQueueSize = 2;

        /// <summary>
        /// Есть ли WinDivert.dll рядом с движком. Без неё любые вызовы — исключение
        /// при первом обращении, а не внятная ошибка.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                string path = System.IO.Path.Combine(Core.Paths.EngineBinDir, Dll);
                return System.IO.File.Exists(path);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Разбор WINDIVERT_ADDRESS по смещениям. Битовые поля расположены так
    /// (см. windivert.h, 2.2):
    ///   flags: Layer 0-7, Event 8-15, Sniffed 16, Outbound 17, Loopback 18,
    ///          Impostor 19, IPv6 20, IPChecksum 21, TCPChecksum 22, UDPChecksum 23
    /// </summary>
    public struct WinDivertAddress
    {
        public long Timestamp;
        public uint Flags;
        public uint IfIdx;
        public uint SubIfIdx;

        public int Layer { get { return (int)(Flags & 0xFF); } }
        public bool Outbound { get { return ((Flags >> 17) & 1) != 0; } }
        public bool IsIPv6 { get { return ((Flags >> 20) & 1) != 0; } }

        public static WinDivertAddress FromBuffer(byte[] buffer)
        {
            WinDivertAddress address = new WinDivertAddress();
            if (buffer == null || buffer.Length < 20) return address;

            address.Timestamp = BitConverter.ToInt64(buffer, 0);
            address.Flags = BitConverter.ToUInt32(buffer, 8);
            address.IfIdx = BitConverter.ToUInt32(buffer, 12);
            address.SubIfIdx = BitConverter.ToUInt32(buffer, 16);

            return address;
        }
    }

    /// <summary>
    /// Разбор сетевого слоя пакета: IPv4/IPv6 → TCP/UDP → порты и полезная нагрузка.
    ///
    /// Только чтение и только то, что нужно для счётчиков и поиска SNI.
    /// Никаких изменений пакета здесь нет — это следующий этап.
    /// </summary>
    public struct PacketInfo
    {
        public bool IsIPv6;
        public bool IsTcp;
        public bool IsUdp;
        public string SrcIp;
        public string DstIp;
        public int SrcPort;
        public int DstPort;

        /// <summary>Смещение полезной нагрузки TCP/UDP внутри пакета.</summary>
        public int PayloadOffset;

        public int PayloadLength;

        public bool HasPayload { get { return PayloadLength > 0 && PayloadOffset > 0; } }
    }

    public static class PacketParser
    {
        /// <summary>Возвращает false, если это не IPv4/IPv6 с TCP/UDP.</summary>
        public static bool TryParse(byte[] packet, int length, bool isIPv6, out PacketInfo info)
        {
            info = new PacketInfo();
            info.IsIPv6 = isIPv6;

            if (packet == null || length < 20) return false;

            int offset;
            int protocol;

            if (!isIPv6)
            {
                int version = (packet[0] >> 4) & 0x0F;
                if (version != 4) return false;

                int headerLength = (packet[0] & 0x0F) * 4;
                if (headerLength < 20 || length < headerLength) return false;

                protocol = packet[9];
                info.SrcIp = packet[12] + "." + packet[13] + "." + packet[14] + "." + packet[15];
                info.DstIp = packet[16] + "." + packet[17] + "." + packet[18] + "." + packet[19];
                offset = headerLength;
            }
            else
            {
                int version = (packet[0] >> 4) & 0x0F;
                if (version != 6) return false;
                if (length < 40) return false;

                protocol = packet[6];
                info.SrcIp = FormatIPv6(packet, 8);
                info.DstIp = FormatIPv6(packet, 24);
                offset = 40;
            }

            if (protocol == 6)
            {
                info.IsTcp = true;
                if (length < offset + 20) return false;

                int dataOffset = (packet[offset + 12] >> 4) * 4;
                info.SrcPort = (packet[offset] << 8) | packet[offset + 1];
                info.DstPort = (packet[offset + 2] << 8) | packet[offset + 3];

                info.PayloadOffset = offset + dataOffset;
                info.PayloadLength = Math.Max(0, length - info.PayloadOffset);
                return true;
            }

            if (protocol == 17)
            {
                info.IsUdp = true;
                if (length < offset + 8) return false;

                info.SrcPort = (packet[offset] << 8) | packet[offset + 1];
                info.DstPort = (packet[offset + 2] << 8) | packet[offset + 3];

                info.PayloadOffset = offset + 8;
                info.PayloadLength = Math.Max(0, length - info.PayloadOffset);
                return true;
            }

            return false;
        }

        private static string FormatIPv6(byte[] packet, int offset)
        {
            try
            {
                byte[] address = new byte[16];
                Array.Copy(packet, offset, address, 0, 16);
                return new System.Net.IPAddress(address).ToString();
            }
            catch { return "?"; }
        }
    }
}