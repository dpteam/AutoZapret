using System;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Работа с IPv4/TCP-пакетом на уровне байтов: где заголовки, как пересчитать
    /// суммы, как отрезать кусок payload и собрать новый пакет.
    ///
    /// Зачем свои суммы, а не WinDivertHelperCalcChecksums: хелпер есть не во всех
    /// сборках WinDivert и требует адрес в неуправляемой памяти. Считать сумму
    /// самому — тридцать строк, зато без зависимости и с проверяемой арифметикой
    /// (в selftest сумма проверяется инвариантом: с посчитанной суммой сумма
    /// всех слов равна 0xFFFF).
    ///
    /// Только IPv4: IPv6 в desync нужен редко, а ошибиться в нём легко. Для
    /// IPv6 пакет считается «не нашим» и проходит без изменений.
    /// </summary>
    public sealed class TcpPacket
    {
        private readonly byte[] _packet;
        private readonly int _length;

        public int IpOffset;
        public int IpHeaderLength;
        public int TcpOffset;
        public int TcpHeaderLength;
        public int PayloadOffset;
        public int PayloadLength;

        public uint SrcIp;
        public uint DstIp;
        public int SrcPort;
        public int DstPort;
        public uint Seq;
        public uint Ack;
        public byte Flags;

        public int TotalLength { get { return _length; } }
        public byte[] Buffer { get { return _packet; } }
        public bool HasPayload { get { return PayloadLength > 0; } }

        private TcpPacket(byte[] packet, int length)
        {
            _packet = packet;
            _length = length;
        }

        /// <summary>Разобрать пакет. null — если это не IPv4+TCP или он битый.</summary>
        public static TcpPacket Parse(byte[] packet, int length)
        {
            if (packet == null || length < 40) return null;

            TcpPacket p = new TcpPacket(packet, length);
            p.IpOffset = 0;

            int version = packet[0] >> 4;
            int ihl = (packet[0] & 0x0F) * 4;

            if (version != 4 || ihl < 20 || length < ihl + 20) return null;
            if (packet[9] != 6) return null;   // protocol != TCP

            p.IpHeaderLength = ihl;
            p.SrcIp = ReadU32(packet, 12);
            p.DstIp = ReadU32(packet, 16);

            int totalLength = (packet[2] << 8) | packet[3];
            if (totalLength < ihl + 20 || totalLength > length) totalLength = length;

            p.TcpOffset = ihl;
            int dataOffset = (packet[ihl + 12] >> 4) * 4;

            if (dataOffset < 20 || ihl + dataOffset > totalLength) return null;

            p.TcpHeaderLength = dataOffset;
            p.SrcPort = (packet[ihl] << 8) | packet[ihl + 1];
            p.DstPort = (packet[ihl + 2] << 8) | packet[ihl + 3];
            p.Seq = ReadU32(packet, ihl + 4);
            p.Ack = ReadU32(packet, ihl + 8);
            p.Flags = packet[ihl + 13];
            p.PayloadOffset = ihl + dataOffset;
            p.PayloadLength = totalLength - p.PayloadOffset;

            return p;
        }

        /// <summary>Собрать копию пакета с куском payload [from, to) и сдвигом seq.</summary>
        public byte[] BuildSegment(int from, int to, int seqAdjust, out int outLength)
        {
            int size = PayloadOffset + (to - from);
            byte[] result = new byte[size];

            Array.Copy(_packet, 0, result, 0, PayloadOffset);
            Array.Copy(_packet, PayloadOffset + from, result, PayloadOffset, to - from);

            // длины: IP total length и TCP data offset не меняются (заголовки те же)
            result[2] = (byte)(size >> 8);
            result[3] = (byte)(size & 0xFF);

            WriteU32(result, TcpOffset + 4, unchecked(Seq + (uint)seqAdjust));

            RecomputeChecksums(result, size, IpHeaderLength, TcpOffset, size - TcpOffset);

            outLength = size;
            return result;
        }

        // ---------------------------------------------------------- контрольные суммы

        /// <summary>
        /// Сумма слов в дополнительном коде (RFC 1071). initial — для псевдозаголовка.
        /// </summary>
        public static uint Sum(byte[] data, int offset, int length, uint initial)
        {
            uint sum = initial;

            int i = offset;
            int end = offset + length;

            while (i + 1 < end)
            {
                sum += (uint)((data[i] << 8) | data[i + 1]);
                i += 2;
            }

            if (i < end) sum += (uint)(data[i] << 8);

            while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);

            return sum;
        }

        /// <summary>Пересчитать IP-сумму и TCP-сумму (с псевдозаголовком).</summary>
        public static void RecomputeChecksums(byte[] packet, int length, int ipHeaderLength,
                                              int tcpOffset, int tcpLength)
        {
            // ---- IP
            packet[10] = 0;
            packet[11] = 0;

            uint ipSum = Sum(packet, 0, ipHeaderLength, 0);
            ushort ipChecksum = (ushort)~ipSum;

            packet[10] = (byte)(ipChecksum >> 8);
            packet[11] = (byte)(ipChecksum & 0xFF);

            // ---- TCP: псевдозаголовок (src, dst, 0, proto, длина)
            uint pseudo = 0;
            pseudo += ReadU32(packet, 12) & 0xFFFF;
            pseudo += (ReadU32(packet, 12) >> 16) & 0xFFFF;
            pseudo += ReadU32(packet, 16) & 0xFFFF;
            pseudo += (ReadU32(packet, 16) >> 16) & 0xFFFF;
            pseudo += 6;                       // protocol TCP
            pseudo += (uint)tcpLength;

            int checksumOffset = tcpOffset + 16;
            ushort saved = (ushort)((packet[checksumOffset] << 8) | packet[checksumOffset + 1]);
            packet[checksumOffset] = 0;
            packet[checksumOffset + 1] = 0;

            uint tcpSum = Sum(packet, tcpOffset, tcpLength, pseudo);
            ushort tcpChecksum = (ushort)~tcpSum;

            // Сумма 0x0000 в TCP означает 0xFFFF — иначе получатель решит, что суммы нет
            if (tcpChecksum == 0) tcpChecksum = 0xFFFF;

            packet[checksumOffset] = (byte)(tcpChecksum >> 8);
            packet[checksumOffset + 1] = (byte)(tcpChecksum & 0xFF);

            GC.KeepAlive(saved);
        }

        /// <summary>
        /// Проверка «сумма сходится»: сумма всех слов заголовка вместе с записанной
        /// суммой обязана давать 0xFFFF. Так selftest ловит ошибку в арифметике,
        /// не сверяясь с магическими числами.
        /// </summary>
        public static bool VerifyIpChecksum(byte[] packet, int length)
        {
            int ihl = (packet[0] & 0x0F) * 4;
            if (length < ihl) return false;

            return Sum(packet, 0, ihl, 0) == 0xFFFF;
        }

        public static bool VerifyTcpChecksum(byte[] packet, int offset, int tcpLength)
        {
            uint pseudo = 0;
            pseudo += ReadU32(packet, 12) & 0xFFFF;
            pseudo += (ReadU32(packet, 12) >> 16) & 0xFFFF;
            pseudo += ReadU32(packet, 16) & 0xFFFF;
            pseudo += (ReadU32(packet, 16) >> 16) & 0xFFFF;
            pseudo += 6;
            pseudo += (uint)tcpLength;

            return Sum(packet, offset, tcpLength, pseudo) == 0xFFFF;
        }

        // ---------------------------------------------------------------- байты

        private static uint ReadU32(byte[] b, int offset)
        {
            return ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) |
                   ((uint)b[offset + 2] << 8) | b[offset + 3];
        }

        private static void WriteU32(byte[] b, int offset, uint value)
        {
            b[offset] = (byte)(value >> 24);
            b[offset + 1] = (byte)(value >> 16);
            b[offset + 2] = (byte)(value >> 8);
            b[offset + 3] = (byte)value;
        }

        public static string FlagsText(byte flags)
        {
            string text = "";
            if ((flags & 0x02) != 0) text += "S";
            if ((flags & 0x10) != 0) text += "A";
            if ((flags & 0x08) != 0) text += "P";
            if ((flags & 0x01) != 0) text += "F";
            if ((flags & 0x04) != 0) text += "R";
            return text;
        }
    }
}