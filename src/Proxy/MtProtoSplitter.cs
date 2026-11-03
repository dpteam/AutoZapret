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
    /// Форматы длины (из refs/tg-ws-proxy, bridge.py:98-135):
    ///   abridged:      первый байт; 0x7F/0xFF => длина в 3 байтах * 4, иначе (b & 0x7F) * 4
    ///   intermediate:  int32 LE & 0x7FFFFFFF
    ///   padded:        int32 LE & 0x7FFFFFFF
    ///
    /// Особый случай: quick-ack бит 0x80 в первом байте НЕ должен ломать разбор.
    /// </summary>
    public sealed class MtProtoSplitter
    {
        private readonly MtProtoTag _tag;
        private readonly List<byte> _buffer = new List<byte>(8192);
        private bool _disabled;

        public MtProtoSplitter(MtProtoTag tag)
        {
            _tag = tag;
        }

        /// <summary>
        /// Добавляет данные и возвращает готовые пакеты (может быть пусто).
        /// </summary>
        public List<byte[]> Feed(byte[] data, int offset, int count)
        {
            List<byte[]> packets = new List<byte[]>();
            if (data == null || count <= 0) return packets;

            for (int i = 0; i < count; i++)
                _buffer.Add(data[offset + i]);

            if (_disabled)
            {
                if (_buffer.Count > 0)
                {
                    packets.Add(_buffer.ToArray());
                    _buffer.Clear();
                }
                return packets;
            }

            int pos = 0;

            while (true)
            {
                int remaining = _buffer.Count - pos;
                if (remaining <= 0) break;

                int headerLen;
                int payloadLen;

                if (!TryReadLength(pos, remaining, out headerLen, out payloadLen))
                    break;   // нужно больше данных

                if (payloadLen < 0)
                {
                    // длина 0 или мусор: прекращаем нарезку, дальше пропускаем как есть
                    _disabled = true;
                    break;
                }

                if (payloadLen == 0)
                {
                    _disabled = true;
                    break;
                }

                if (remaining < headerLen + payloadLen) break;   // пакет ещё не целиком

                byte[] packet = new byte[headerLen + payloadLen];
                _buffer.CopyTo(pos, packet, 0, packet.Length);
                packets.Add(packet);

                pos += packet.Length;
            }

            if (pos > 0) _buffer.RemoveRange(0, pos);
            return packets;
        }

        /// <summary>Отдаёт остаток буфера (при закрытии соединения).</summary>
        public byte[] Flush()
        {
            if (_buffer.Count == 0) return new byte[0];
            byte[] rest = _buffer.ToArray();
            _buffer.Clear();
            return rest;
        }

        private bool TryReadLength(int pos, int available, out int headerLen, out int payloadLen)
        {
            headerLen = 0;
            payloadLen = -1;

            if (_tag == MtProtoTag.Abridged)
            {
                if (available < 1) return false;

                int first = _buffer[pos];

                // quick-ack: бит 0x80 не влияет на длину
                int lenByte = first & 0x7F;

                if (lenByte == 0x7F)
                {
                    if (available < 4) return false;
                    int words = _buffer[pos + 1] | (_buffer[pos + 2] << 8) | (_buffer[pos + 3] << 16);
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

            int raw = _buffer[pos] | (_buffer[pos + 1] << 8) |
                      (_buffer[pos + 2] << 16) | (_buffer[pos + 3] << 24);
            raw &= 0x7FFFFFFF;

            headerLen = 4;
            payloadLen = raw;
            return true;
        }
    }
}
