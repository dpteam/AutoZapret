using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Ручной WebSocket-клиент (RFC 6455).
    ///
    /// Почему свой: System.Net.WebSockets в BCL — только ClientWebSocket,
    /// и он не даёт контроля над заголовками запроса (нужен ровно
    /// «Upgrade: websocket» без Origin/User-Agent, как у настоящего Telegram Web).
    ///
    /// Особенности, которые важны для Telegram:
    ///   * исходящие кадры ВСЕГДА маскируются (мы в роли клиента);
    ///   * Sec-WebSocket-Accept не проверяем — Telegram его не всегда шлёт корректно,
    ///     а нам важен только код 101;
    ///   * PING -> сразу PONG;
    ///   * кадр больше 16 МБ — ошибка (защита от мусора в потоке).
    /// </summary>
    public sealed class WsClient
    {
        public const int MaxMessageLen = 16 * 1024 * 1024;

        private const int OpContinuation = 0x0;
        private const int OpText = 0x1;
        private const int OpBinary = 0x2;
        private const int OpClose = 0x8;
        private const int OpPing = 0x9;
        private const int OpPong = 0xA;

        private readonly Stream _stream;
        private bool _closed;

        private WsClient(Stream stream)
        {
            _stream = stream;
        }

        public bool IsClosed { get { return _closed; } }

        /// <summary>
        /// Причина закрытия, если сервер прислал её в close-кадре.
        ///
        /// Зачем хранить: релей (Cloudflare Worker) закрывает соединение с
        /// текстом ошибки — «connect ... -> cannot connect to the specified
        /// address». Без этого человек видит только «сервер закрыл соединение»
        /// и не понимает, что именно запретил релей.
        /// </summary>
        public string CloseReason { get; private set; }

        /// <summary>
        /// Выполняет апгрейд. Возвращает клиент или null (с текстом ошибки).
        /// </summary>
        public static WsClient Connect(Stream stream, string host, string path, int timeoutMs, out string error)
        {
            error = null;

            byte[] keyBytes = new byte[16];
            using (System.Security.Cryptography.RandomNumberGenerator rng =
                   System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(keyBytes);
            }
            string key = Convert.ToBase64String(keyBytes);

            // Ровно те заголовки, что шлёт настоящий Telegram Web:
            // без Origin, User-Agent и Accept-Encoding — иначе запрос «пахнет» скриптом.
            StringBuilder request = new StringBuilder();
            request.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            request.Append("Host: ").Append(host).Append("\r\n");
            request.Append("Upgrade: websocket\r\n");
            request.Append("Connection: Upgrade\r\n");
            request.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
            request.Append("Sec-WebSocket-Version: 13\r\n");
            request.Append("Sec-WebSocket-Protocol: binary\r\n");
            request.Append("\r\n");

            byte[] payload = Encoding.ASCII.GetBytes(request.ToString());

            try
            {
                stream.WriteTimeout = timeoutMs;
                stream.Write(payload, 0, payload.Length);
                stream.Flush();

                string statusLine = ReadLine(stream, timeoutMs);
                if (statusLine == null)
                {
                    error = "сервер закрыл соединение на апгрейде";
                    return null;
                }

                int status = ParseStatus(statusLine);

                // дочитываем заголовки ответа
                int headerGuard = 0;
                while (headerGuard++ < 100)
                {
                    string line = ReadLine(stream, timeoutMs);
                    if (line == null || line.Length == 0) break;
                }

                if (status != 101)
                {
                    error = "апгрейд не удался: " + statusLine.Trim();
                    return null;
                }

                return new WsClient(stream);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // ------------------------------------------------------------ receive

        /// <summary>
        /// Читает одно сообщение (склеивая фрагменты). null = соединение закрыто.
        /// </summary>
        public byte[] ReceiveMessage(int timeoutMs)
        {
            if (_closed) return null;

            List<byte> assembled = null;
            int guard = 0;

            while (guard++ < 1000)
            {
                byte[] frame = ReadFrame(timeoutMs, out int opcode);
                if (frame == null)
                {
                    _closed = true;
                    return null;
                }

                if (opcode == OpPing)
                {
                    try { SendFrame(OpPong, frame, timeoutMs); } catch { }
                    continue;
                }

                if (opcode == OpPong) continue;

                if (opcode == OpClose)
                {
                    _closed = true;
                    CloseReason = ParseCloseReason(frame);
                    try { SendFrame(OpClose, new byte[0], timeoutMs); } catch { }
                    return null;
                }

                if (opcode == OpContinuation)
                {
                    if (assembled == null) assembled = new List<byte>();
                    assembled.AddRange(frame);
                    continue;
                }

                if (opcode == OpBinary || opcode == OpText)
                {
                    if (assembled != null)
                    {
                        // предыдущая последовательность не завершилась — считаем мусором
                        assembled = null;
                    }
                    return frame;
                }
            }

            _closed = true;
            return null;
        }

        private byte[] ReadFrame(int timeoutMs, out int opcode)
        {
            opcode = 0;

            byte[] header = ReadExact(2, timeoutMs);
            if (header == null) return null;

            bool fin = (header[0] & 0x80) != 0;
            opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if (length == 126)
            {
                byte[] ext = ReadExact(2, timeoutMs);
                if (ext == null) return null;
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = ReadExact(8, timeoutMs);
                if (ext == null) return null;
                length = 0;
                for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
            }

            if (length > MaxMessageLen)
            {
                _closed = true;
                return null;
            }

            byte[] mask = null;
            if (masked)
            {
                mask = ReadExact(4, timeoutMs);
                if (mask == null) return null;
            }

            byte[] payload = length == 0 ? new byte[0] : ReadExact((int)length, timeoutMs);
            if (payload == null) return null;

            if (mask != null)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i & 3];

            if (!fin && opcode != OpContinuation && opcode != OpBinary && opcode != OpText)
                return payload;

            return payload;
        }

        // --------------------------------------------------------------- send

        public bool SendBinary(byte[] payload, int timeoutMs)
        {
            return SendFrame(OpBinary, payload, timeoutMs);
        }

        private bool SendFrame(int opcode, byte[] payload, int timeoutMs)
        {
            if (_closed) return false;

            try
            {
                payload = payload ?? new byte[0];
                int length = payload.Length;

                byte[] mask = new byte[4];
                using (System.Security.Cryptography.RandomNumberGenerator rng =
                       System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(mask);
                }

                List<byte> frame = new List<byte>(length + 14);
                frame.Add((byte)(0x80 | opcode));

                if (length < 126)
                {
                    frame.Add((byte)(0x80 | length));
                }
                else if (length < 65536)
                {
                    frame.Add(0x80 | 126);
                    frame.Add((byte)((length >> 8) & 0xFF));
                    frame.Add((byte)(length & 0xFF));
                }
                else
                {
                    frame.Add(0x80 | 127);
                    for (int i = 7; i >= 0; i--)
                        frame.Add((byte)(((long)length >> (i * 8)) & 0xFF));
                }

                frame.AddRange(mask);

                byte[] masked = new byte[length];
                for (int i = 0; i < length; i++)
                    masked[i] = (byte)(payload[i] ^ mask[i & 3]);

                frame.AddRange(masked);

                byte[] bytes = frame.ToArray();
                _stream.WriteTimeout = timeoutMs;
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                return true;
            }
            catch
            {
                _closed = true;
                return false;
            }
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { SendFrame(OpClose, new byte[0], 1000); } catch { }
            try { _stream.Close(); } catch { }
        }

        // ------------------------------------------------------------- helpers

        private byte[] ReadExact(int count, int timeoutMs)
        {
            if (count <= 0) return new byte[0];

            byte[] buffer = new byte[count];
            int read = 0;

            try
            {
                _stream.ReadTimeout = timeoutMs;
                while (read < count)
                {
                    int n = _stream.Read(buffer, read, count - read);
                    if (n <= 0) return null;
                    read += n;
                }
                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Читает строку до CRLF побайтово (заголовки короткие).</summary>
        private static string ReadLine(Stream stream, int timeoutMs)
        {
            StringBuilder sb = new StringBuilder(64);

            try
            {
                stream.ReadTimeout = timeoutMs;
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return null;
                    if (b == '\n') return sb.ToString().TrimEnd('\r');
                    sb.Append((char)b);
                    if (sb.Length > 8192) return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ParseCloseReason(byte[] payload)
        {
            if (payload == null || payload.Length < 2) return "";

            try
            {
                int length = payload.Length - 2;
                if (length > 512) length = 512;

                return Encoding.UTF8.GetString(payload, 2, length).Trim();
            }
            catch
            {
                return "";
            }
        }

        private static int ParseStatus(string statusLine)
        {
            try
            {
                // "HTTP/1.1 101 Switching Protocols"
                int firstSpace = statusLine.IndexOf(' ');
                if (firstSpace < 0) return 0;
                int secondSpace = statusLine.IndexOf(' ', firstSpace + 1);
                string code = secondSpace < 0
                    ? statusLine.Substring(firstSpace + 1)
                    : statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1);

                int status;
                return int.TryParse(code.Trim(), out status) ? status : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
