using System;
using System.Collections.Generic;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Нарезка MTProto-потока на отдельные пакеты.
    ///
    /// Зачем: Telegram-сервер ожидает один MTProto-пакет на один WS-фрейм.
    /// Если слать поток как есть, часть пакетов склеится и сервер их не разберёт.
    ///
    /// ПОЧЕМУ ДВА БУФЕРА (и почему это не оптимизация, а условие работы).
    ///   Длина пакета лежит ВНУТРИ шифрованного потока, то есть в открытом виде
    ///   её не видно: AES-CTR шифрует и префикс длины тоже. Значит разбирать
    ///   границы можно только по РАСШИФРОВАННЫМ данным, а отправлять дальше
    ///   нужно ШИФРОВАННЫЕ. Отсюда две параллельные дорожки одинаковой длины:
    ///   `plain` — для чтения длин, `cipher` — то, что реально уходит.
    ///
    ///   Раньше сплиттер получал только один поток (шифрованный) и «находил»
    ///   в нём длины — то есть резал случайные байты. Снаружи это выглядело
    ///   как «Telegram показывает Соединение… и никогда не подключается»,
    ///   при полностью успешном handshake.
    ///
    /// Форматы длины (из refs/tg-ws-proxy, bridge.py:98-135):
    ///   abridged:      первый байт; 0x7F/0xFF => длина в 3 байтах * 4, иначе (b &amp; 0x7F) * 4
    ///   intermediate:  int32 LE &amp; 0x7FFFFFFF
    ///   padded:        int32 LE &amp; 0x7FFFFFFF
    ///
    /// Особый случай: quick-ack бит 0x80 в первом байте НЕ должен ломать разбор.
    /// </summary>
    public sealed class MtProtoSplitter
    {
        private readonly MtProtoTag _tag;

        private readonly List<byte> _plain = new List<byte>(8192);
        private readonly List<byte> _cipher = new List<byte>(8192);

        private bool _disabled;

        public MtProtoSplitter(MtProtoTag tag)
        {
            _tag = tag;
        }

        /// <summary>
        /// Добавить данные и получить готовые пакеты (может быть пусто).
        ///
        /// plain  — расшифрованные данные (по ним читаются длины);
        /// cipher — те же данные, но зашифрованные (их и отправляем).
        /// Длины обязаны совпадать: это одна и та же порция потока.
        /// </summary>
        public List<byte[]> Feed(byte[] plain, byte[] cipher, int count)
        {
            List<byte[]> packets = new List<byte[]>();

            if (plain == null || cipher == null || count <= 0) return packets;

            for (int i = 0; i < count; i++)
            {
                _plain.Add(plain[i]);
                _cipher.Add(cipher[i]);
            }

            if (_disabled)
            {
                if (_cipher.Count > 0)
                {
                    packets.Add(_cipher.ToArray());
                    _cipher.Clear();
                    _plain.Clear();
                }

                return packets;
            }

            int pos = 0;

            while (true)
            {
                int remaining = _plain.Count - pos;
                if (remaining <= 0) break;

                int headerLen;
                int payloadLen;

                if (!TryReadLength(pos, remaining, out headerLen, out payloadLen))
                    break;   // нужно больше данных

                if (payloadLen <= 0)
                {
                    // длина 0 или мусор: нарезка больше не имеет смысла —
                    // отдаём остаток как есть, чтобы не потерять данные.
                    _disabled = true;
                    break;
                }

                if (remaining < headerLen + payloadLen) break;   // пакет ещё не целиком

                byte[] packet = new byte[headerLen + payloadLen];
                _cipher.CopyTo(pos, packet, 0, packet.Length);
                packets.Add(packet);

                pos += packet.Length;
            }

            if (pos > 0)
            {
                _plain.RemoveRange(0, pos);
                _cipher.RemoveRange(0, pos);
            }

            return packets;
        }

        /// <summary>
        /// Старый вариант «один поток»: годится только для уже открытых данных
        /// (так его использует selftest). В рабочем мосте нужен вариант с двумя
        /// буферами, иначе длины читаются из шифротекста.
        /// </summary>
        public List<byte[]> Feed(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return new List<byte[]>();

            byte[] slice = new byte[count];
            Buffer.BlockCopy(data, offset, slice, 0, count);

            return Feed(slice, slice, count);
        }

        /// <summary>Сколько байт накоплено и не отдано (для диагностики).</summary>
        public int Buffered { get { return _plain.Count; } }

        /// <summary>Отдаёт остаток буфера (при закрытии соединения).</summary>
        public byte[] Flush()
        {
            if (_cipher.Count == 0) return new byte[0];

            byte[] rest = _cipher.ToArray();

            _cipher.Clear();
            _plain.Clear();

            return rest;
        }

        private bool TryReadLength(int pos, int available, out int headerLen, out int payloadLen)
        {
            headerLen = 0;
            payloadLen = -1;

            if (_tag == MtProtoTag.Abridged)
            {
                if (available < 1) return false;

                int first = _plain[pos];

                // quick-ack: бит 0x80 не влияет на длину
                int lenByte = first & 0x7F;

                if (lenByte == 0x7F)
                {
                    if (available < 4) return false;
                    int words = _plain[pos + 1] | (_plain[pos + 2] << 8) | (_plain[pos + 3] << 16);
                    headerLen = 4;
                    payloadLen = words * 4;
                    return true;
                }

                headerLen = 1;
                payloadLen = lenByte * 4;
                return true;
            }

            // intermediate / padded
            if (available < 4) return false;

            int raw = _plain[pos] | (_plain[pos + 1] << 8) |
                      (_plain[pos + 2] << 16) | (_plain[pos + 3] << 24);
            raw &= 0x7FFFFFFF;

            headerLen = 4;
            payloadLen = raw;
            return true;
        }
    }
}
