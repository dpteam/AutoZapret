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

        /// <summary>
        /// Собрать пакет с ЧУЖИМ payload: заголовки наши, данные — подставные.
        ///
        /// Это и есть основа техники `fake`: DPI видит правдоподобный ClientHello,
        /// отправляет его «на проверку» и теряет интерес к настоящему. Получатель
        /// же такой пакет отбрасывает, потому что seq не попадает в окно.
        ///
        /// seqAdjust — сдвиг sequence number. Отрицательный уводит пакет «в прошлое»
        /// (уже принято, отбросить), большой положительный — «в будущее»
        /// (вне окна, тоже отбросить). Ноль означал бы настоящие данные, поэтому
        /// для фейка ноль не используется никогда.
        /// </summary>
        public byte[] BuildWithPayload(byte[] payload, int seqAdjust, out int outLength)
        {
            int payloadSize = payload == null ? 0 : payload.Length;
            int size = PayloadOffset + payloadSize;

            byte[] result = new byte[size];

            Array.Copy(_packet, 0, result, 0, PayloadOffset);

            if (payloadSize > 0)
                Array.Copy(payload, 0, result, PayloadOffset, payloadSize);

            result[2] = (byte)(size >> 8);
            result[3] = (byte)(size & 0xFF);

            WriteU32(result, TcpOffset + 4, unchecked(Seq + (uint)seqAdjust));

            RecomputeChecksums(result, size, IpHeaderLength, TcpOffset, size - TcpOffset);

            outLength = size;
            return result;
        }

        /// <summary>
        /// Пакет с испорченной контрольной суммой TCP (техника fooling `badsum`).
        ///
        /// Зачем: часть DPI проверяет сумму и такой пакет пропускает мимо
        /// обработки, а настоящий получатель его всё равно отбросит. То есть
        /// фейк не доходит ни до кого, кроме того, кого мы обманываем.
        /// </summary>
        public static void CorruptTcpChecksum(byte[] packet, int tcpOffset)
        {
            if (packet == null || tcpOffset + 18 > packet.Length) return;

            packet[tcpOffset + 16] = (byte)(packet[tcpOffset + 16] ^ 0xFF);
            packet[tcpOffset + 17] = (byte)(packet[tcpOffset + 17] ^ 0xFF);
        }

        /// <summary>
        /// Фейк с приёмами fooling: `md5sig` и `datanoack`.
        ///
        /// Зачем отдельный метод, а не флаги в BuildWithPayload: md5sig УВЕЛИЧИВАЕТ
        /// заголовок TCP на 18 байт, то есть сдвигает payload и требует нового
        /// data offset. Это меняет раскладку пакета целиком, и смешивать такой
        /// случай с обычной сборкой в одном методе — верный способ однажды
        /// посчитать смещение не там.
        ///
        /// md5sig: опция TCP kind=19 len=18 с 16 байтами «подписи». Часть DPI
        /// не умеет разбирать нестандартную длину заголовка и теряет поток.
        /// Получатель пакет всё равно отбросит — seq не тот.
        ///
        /// datanoack: снять флаг ACK. Пакет с данными и без ACK невалиден, но
        /// DPI, который смотрит только payload, этого не замечает.
        /// </summary>
        public byte[] BuildFake(byte[] payload, int seqAdjust, bool md5sig, bool dataNoAck,
                                out int outLength)
        {
            int payloadSize = payload == null ? 0 : payload.Length;

            // 18 байт опции + 2 байта выравнивания: длина TCP-заголовка
            // обязана быть кратна 4, иначе data offset в неё не влезет и
            // пакет развалится у получателя.
            int extra = md5sig ? 20 : 0;
            int tcpHeaderSize = 20 + extra;

            int tcpStart = IpOffset + IpHeaderLength;
            int payloadStart = tcpStart + tcpHeaderSize;
            int size = payloadStart + payloadSize;

            byte[] result = new byte[size];

            // IP-заголовок копируется как есть: адреса, TOS, TTL не меняются
            Array.Copy(_packet, 0, result, 0, tcpStart);

            // первые 20 байт TCP: порты, seq, ack, флаги, окно
            Array.Copy(_packet, TcpOffset, result, tcpStart, 20);

            // data offset в старших четырёх битах 12-го байта TCP
            int dataOffsetWords = tcpHeaderSize / 4;
            result[tcpStart + 12] = (byte)((dataOffsetWords << 4) | (result[tcpStart + 12] & 0x0F));

            if (md5sig)
            {
                result[tcpStart + 20] = 19;      // kind: MD5 signature
                result[tcpStart + 21] = 18;      // length

                // «Подпись» детерминированная: псевдослучайная, но одинаковая
                // для одинакового входа. Случайная тоже годится, но тогда
                // тест на синтетическом пакете не сможет ничего сравнить.
                uint seed = unchecked(Seq + (uint)seqAdjust + (uint)payloadSize);

                for (int i = 0; i < 16; i++)
                {
                    seed = unchecked(seed * 1103515245u + 12345u);
                    result[tcpStart + 22 + i] = (byte)(seed >> 16);
                }

                // выравнивание до 4 байт: два NOP (kind 1)
                result[tcpStart + 38] = 1;
                result[tcpStart + 39] = 1;
            }

            if (payloadSize > 0)
                Array.Copy(payload, 0, result, payloadStart, payloadSize);

            result[2] = (byte)(size >> 8);
            result[3] = (byte)(size & 0xFF);

            WriteU32(result, tcpStart + 4, unchecked(Seq + (uint)seqAdjust));

            if (dataNoAck) result[tcpStart + 13] = (byte)(result[tcpStart + 13] & ~0x10);

            RecomputeChecksums(result, size, IpHeaderLength, tcpStart, size - tcpStart);

            outLength = size;
            return result;
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
        /// SYN с данными внутри (техника `syndata`).
        ///
        /// Идея: DPI ожидает, что SYN приходит пустым, и часто не разбирает
        /// payload у SYN-пакетов вовсе. Настоящий сервер такой SYN тоже
        /// отбросит (данные в SYN не по стандарту), а DPI уже увидел
        /// ClientHello и «запомнил» его как начало соединения.
        ///
        /// Заголовки берём от настоящего пакета, но флаги и seq подменяем:
        /// ACK снимаем, ставим SYN, seq = isn. Контрольные суммы пересчитываем —
        /// без этого пакет отбросят все, включая DPI.
        /// </summary>
        public byte[] BuildSynWithPayload(byte[] payload, uint isn, out int outLength)
        {
            int payloadSize = payload == null ? 0 : payload.Length;
            int size = PayloadOffset + payloadSize;

            byte[] result = new byte[size];
            Array.Copy(_packet, 0, result, 0, PayloadOffset);

            if (payloadSize > 0)
                Array.Copy(payload, 0, result, PayloadOffset, payloadSize);

            result[2] = (byte)(size >> 8);
            result[3] = (byte)(size & 0xFF);

            // seq = ISN, ack = 0: это начало соединения, а не продолжение
            WriteU32(result, TcpOffset + 4, isn);
            WriteU32(result, TcpOffset + 8, 0);

            // флаги: только SYN. ACK/PSH/FIN сняты — иначе это не SYN
            result[TcpOffset + 13] = 0x02;

            RecomputeChecksums(result, size, IpHeaderLength, TcpOffset, size - TcpOffset);

            outLength = size;
            return result;
        }

        /// <summary>
        /// Один байт как срочные данные (техника `oob`, out-of-band).
        ///
        /// DPI, который собирает поток по порядку, спотыкается о срочный байт:
        /// он вне обычного потока. Получатель его тоже отбросит — данных там нет.
        /// </summary>
        public byte[] BuildUrgentByte(byte b, int seqAdjust, out int outLength)
        {
            byte[] payload = new byte[] { b };
            byte[] result = BuildWithPayload(payload, seqAdjust, out outLength);

            // URG + ACK: срочные данные в обычном сегменте
            result[TcpOffset + 13] = 0x30;

            // urgent pointer = 1: срочный байт — первый в payload
            result[TcpOffset + 18] = 0x00;
            result[TcpOffset + 19] = 0x01;

            RecomputeChecksums(result, outLength, IpHeaderLength, TcpOffset, outLength - TcpOffset);

            return result;
        }

        /// <summary>
        /// Копия пакета с другим размером окна (техника `wsize`).
        ///
        /// Окно — это сколько данных получатель готов принять. Маленькое окно
        /// заставляет DPI считать, что поток почти закрыт, и он перестаёт
        /// собирать данные для проверки. Получатель настоящий пакет всё равно
        /// получит: окно мы меняем только в копии, оригинал не трогаем.
        /// </summary>
        public byte[] BuildWithWindow(ushort window, out int outLength)
        {
            byte[] result = (byte[])_packet.Clone();
            outLength = TotalLength;

            result[TcpOffset + 14] = (byte)(window >> 8);
            result[TcpOffset + 15] = (byte)(window & 0xFF);

            RecomputeChecksums(result, outLength, IpHeaderLength, TcpOffset, outLength - TcpOffset);

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

                                /// <summary>Адрес назначения как IPAddress — для фильтра по подсетям.</summary>
        public System.Net.IPAddress DestinationAddress()
        {
            byte[] bytes =
            {
                (byte)(DstIp >> 24), (byte)(DstIp >> 16), (byte)(DstIp >> 8), (byte)DstIp
            };

            return new System.Net.IPAddress(bytes);
        }

        public static string FlagsText(byte flags)
        {
            string text = "";
            if ((flags & 0x02) != 0) text += "S";            if ((flags & 0x10) != 0) text += "A";
            if ((flags & 0x08) != 0) text += "P";
            if ((flags & 0x01) != 0) text += "F";
            if ((flags & 0x04) != 0) text += "R";
            return text;
        }
    }
}