using System;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Разбор и пересборка UDP-пакета (IPv4 и IPv6).
    ///
    /// Зачем отдельно от TcpPacket: у UDP другая арифметика. Заголовок 8 байт
    /// вместо 20, своя длина, и контрольная сумма необязательна в IPv4
    /// (ноль означает «не считать»), но обязательна в IPv6. Смешивать это в
    /// одном классе — гарантированные ошибки в суммах, а битая сумма значит,
    /// что пакет молча выбросят.
    ///
    /// Нужен для техники `udplen` и вообще для QUIC: без UDP движок видит
    /// только TCP/443, а половина современного трафика идёт по UDP/443.
    /// </summary>
    public sealed class UdpPacket
    {
        private readonly byte[] _packet;
        private readonly int _length;

        public bool IsIPv6;
        public int IpOffset;
        public int IpHeaderLength;
        public int UdpOffset;
        public int PayloadOffset;
        public int PayloadLength;

        public int SrcPort;
        public int DstPort;

        public int TotalLength { get { return _length; } }
        public byte[] Buffer { get { return _packet; } }
        public bool HasPayload { get { return PayloadLength > 0; } }

        private UdpPacket(byte[] packet, int length)
        {
            _packet = packet;
            _length = length;
        }

        /// <summary>Разобрать пакет. null — если это не UDP или он битый.</summary>
        public static UdpPacket Parse(byte[] packet, int length)
        {
            if (packet == null || length < 28) return null;

            UdpPacket p = new UdpPacket(packet, length);

            int version = (packet[0] >> 4) & 0x0F;

            if (version == 4)
            {
                int ihl = (packet[0] & 0x0F) * 4;

                if (ihl < 20 || length < ihl + 8) return null;
                if (packet[9] != 17) return null;   // protocol != UDP

                p.IsIPv6 = false;
                p.IpHeaderLength = ihl;
                p.UdpOffset = ihl;

                int totalLength = (packet[2] << 8) | packet[3];
                if (totalLength < ihl + 8 || totalLength > length) totalLength = length;

                p.PayloadOffset = ihl + 8;
                p.PayloadLength = Math.Max(0, totalLength - p.PayloadOffset);
            }
            else if (version == 6)
            {
                if (length < 48) return null;
                if (packet[6] != 17) return null;

                p.IsIPv6 = true;
                p.IpHeaderLength = 40;
                p.UdpOffset = 40;

                int payloadLength = (packet[4] << 8) | packet[5];
                int totalLength = 40 + payloadLength;

                if (totalLength > length) totalLength = length;

                p.PayloadOffset = 48;
                p.PayloadLength = Math.Max(0, totalLength - p.PayloadOffset);
            }
            else
            {
                return null;
            }

            p.SrcPort = (packet[p.UdpOffset] << 8) | packet[p.UdpOffset + 1];
            p.DstPort = (packet[p.UdpOffset + 2] << 8) | packet[p.UdpOffset + 3];

            return p;
        }

        /// <summary>Копия payload — для техник, которым нужны сами данные.</summary>
        public byte[] PayloadCopy()
        {
            if (!HasPayload) return new byte[0];

            byte[] result = new byte[PayloadLength];
            Array.Copy(_packet, PayloadOffset, result, 0, PayloadLength);

            return result;
        }

        /// <summary>
        /// Пакет с ПОДМЕНЁННЫМ payload — то, чем делается `fake` для UDP.
        ///
        /// Зачем: голосовой Discord и игры идут по UDP, и DPI их разбирает по
        /// содержимому (STUN имеет свою сигнатуру, QUIC — свою). Подсунуть
        /// поддельный STUN-запрос перед настоящим пакетом — единственный способ
        /// заставить DPI ошибиться, потому что seq в UDP нет и «сдвинуть»
        /// пакет некуда.
        ///
        /// Длины и суммы пересчитываются честно: заголовки IP и UDP обязаны
        /// совпасть с новым размером, иначе пакет отбросят на полпути.
        /// </summary>
        public byte[] BuildWithPayload(byte[] payload, out int outLength)
        {
            byte[] data = payload ?? new byte[0];
            int size = PayloadOffset + data.Length;

            byte[] result = new byte[size];
            Array.Copy(_packet, 0, result, 0, PayloadOffset);

            if (data.Length > 0) Array.Copy(data, 0, result, PayloadOffset, data.Length);

            int udpLength = 8 + data.Length;
            result[UdpOffset + 4] = (byte)(udpLength >> 8);
            result[UdpOffset + 5] = (byte)(udpLength & 0xFF);

            if (IsIPv6)
            {
                result[4] = (byte)(udpLength >> 8);
                result[5] = (byte)(udpLength & 0xFF);
            }
            else
            {
                result[2] = (byte)(size >> 8);
                result[3] = (byte)(size & 0xFF);
            }

            RecomputeChecksums(result, size);

            outLength = size;
            return result;
        }

        /// <summary>
        /// Пакет с тем же payload, но с другим полем длины UDP в заголовке.
        ///
        /// Это техника `udplen`: DPI, который определяет протокол по длине
        /// (например, отличает QUIC от мусора), видит неверную длину и
        /// ошибается с разбором. Настоящий получатель сверяет длину с
        /// фактической и такой пакет отбросит — поэтому оригинал отправляем
        /// следом.
        ///
        /// Новая длина ограничена размером payload: заявить больше, чем есть,
        /// значит получить пакет, который отбросят все, включая промежуточные
        /// узлы, и до получателя не дойдёт даже настоящий.
        /// </summary>
        public byte[] BuildWithLengthDelta(int delta, out int outLength)
        {
            byte[] result = (byte[])_packet.Clone();
            outLength = _length;

            int realLength = 8 + PayloadLength;
            int newLength = realLength + delta;

            // держим в пределах: минимум 8 (пустой UDP), максимум — реальный размер
            if (newLength < 8) newLength = 8;
            if (newLength > realLength) newLength = realLength;

            result[UdpOffset + 4] = (byte)(newLength >> 8);
            result[UdpOffset + 5] = (byte)(newLength & 0xFF);

            RecomputeChecksums(result, outLength);

            return result;
        }

        /// <summary>
        /// Пакет с добавленным «хвостом»: payload + случайные байты.
        ///
        /// Вторая половина `udplen`: DPI видит пакет больше, чем ожидает для
        /// QUIC Initial, и не пытается его разобрать. Получатель лишние байты
        /// просто проигнорирует, если протокол их допускает.
        /// </summary>
        public byte[] BuildPadded(int extraBytes, out int outLength)
        {
            int extra = Math.Max(0, Math.Min(256, extraBytes));
            int payloadSize = PayloadLength + extra;
            int size = PayloadOffset + payloadSize;

            byte[] result = new byte[size];
            Array.Copy(_packet, 0, result, 0, PayloadOffset);
            Array.Copy(_packet, PayloadOffset, result, PayloadOffset, PayloadLength);

            // добиваем нулями: QUIC-парсер пропустит их как padding
            for (int i = 0; i < extra; i++) result[PayloadOffset + PayloadLength + i] = 0;

            int udpLength = 8 + payloadSize;
            result[UdpOffset + 4] = (byte)(udpLength >> 8);
            result[UdpOffset + 5] = (byte)(udpLength & 0xFF);

            if (IsIPv6)
            {
                int ipPayload = udpLength;
                result[4] = (byte)(ipPayload >> 8);
                result[5] = (byte)(ipPayload & 0xFF);
            }
            else
            {
                result[2] = (byte)(size >> 8);
                result[3] = (byte)(size & 0xFF);
            }

            outLength = size;
            RecomputeChecksums(result, outLength);

            return result;
        }

        /// <summary>
        /// Пересчитать суммы. IPv4: IP-сумма всегда, UDP — только если она была
        /// ненулевой (ноль в IPv4 значит «не считать», и трогать его нельзя:
        /// получатель тогда вообще не проверяет). IPv6: UDP-сумма обязательна.
        /// </summary>
        public void RecomputeChecksums(byte[] packet, int length)
        {
            if (!IsIPv6)
            {
                packet[10] = 0;
                packet[11] = 0;

                uint ipSum = TcpPacket.Sum(packet, 0, IpHeaderLength, 0);
                ushort ipChecksum = (ushort)~ipSum;

                packet[10] = (byte)(ipChecksum >> 8);
                packet[11] = (byte)(ipChecksum & 0xFF);
            }

            int checksumOffset = UdpOffset + 6;
            ushort existing = (ushort)((packet[checksumOffset] << 8) | packet[checksumOffset + 1]);

            // в IPv4 нулевая сумма — законный «без проверки»
            if (!IsIPv6 && existing == 0) return;

            int udpLength = length - UdpOffset;

            packet[checksumOffset] = 0;
            packet[checksumOffset + 1] = 0;

            uint pseudo = 0;

            if (IsIPv6)
            {
                // src (16) + dst (16) + length (4) + next header (4)
                pseudo = TcpPacket.Sum(packet, 8, 32, 0);
                pseudo += (uint)udpLength;
                pseudo += 17;
            }
            else
            {
                pseudo += ReadU16(packet, 12);
                pseudo += ReadU16(packet, 14);
                pseudo += ReadU16(packet, 16);
                pseudo += ReadU16(packet, 18);
                pseudo += 17;
                pseudo += (uint)udpLength;
            }

            uint sum = TcpPacket.Sum(packet, UdpOffset, udpLength, pseudo);
            ushort checksum = (ushort)~sum;

            // ноль в UDP-сумме означает «нет суммы», поэтому 0x0000 -> 0xFFFF
            if (checksum == 0) checksum = 0xFFFF;

            packet[checksumOffset] = (byte)(checksum >> 8);
            packet[checksumOffset + 1] = (byte)(checksum & 0xFF);
        }

        private static uint ReadU16(byte[] b, int offset)
        {
            return (uint)((b[offset] << 8) | b[offset + 1]);
        }
    }
}