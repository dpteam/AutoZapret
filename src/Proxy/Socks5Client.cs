using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Минимальный SOCKS5-клиент: подключиться к цели через локальный прокси.
    ///
    /// ЗАЧЕМ
    ///   На Windows обход (`winws`) перехватывает трафик всей системы, и наш
    ///   прокси ходит наружу напрямую — его уже обходит движок.
    ///
    ///   На Android обход — это ЛОКАЛЬНЫЙ SOCKS5 (ciadpi) внутри VPN-сервиса,
    ///   а не перехват всей системы. Значит наш Telegram-прокси обязан идти
    ///   наружу ЧЕРЕЗ него, иначе соединение уходит мимо обхода.
    ///
    ///   Проверено на телефоне: `kws2.pclead.co.uk` (Cloudflare) не пускает
    ///   TCP:443 вообще, а с ПК, где работает winws2, — пускает. Разница
    ///   именно в том, что на ПК обход системный.
    ///
    /// ЧТО УМЕЕТ
    ///   Только то, что нужно: без аутентификации, CONNECT, доменное имя
    ///   (чтобы резолвил прокси, а не мы), IPv4-ответ. Никаких BIND/UDP.
    /// </summary>
    public static class Socks5Client
    {
        /// <summary>
        /// Соединиться с host:port через SOCKS5-прокси.
        ///
        /// Возвращает подключённый TcpClient (уже после CONNECT) или null
        /// с текстом ошибки.
        /// </summary>
        public static TcpClient Connect(string proxyHost, int proxyPort,
                                        string targetHost, int targetPort,
                                        int timeoutMs, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(proxyHost) || proxyPort <= 0)
            {
                error = "прокси не задан";
                return null;
            }

            TcpClient client = null;

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(proxyHost, proxyPort, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    client.Close();
                    error = proxyHost + ":" + proxyPort + " — таймаут TCP до обхода";
                    return null;
                }

                client.EndConnect(ar);

                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                // ---- 1. приветствие: только «без аутентификации» (0x00)
                stream.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);

                byte[] reply = ReadExact(stream, 2);

                if (reply == null)
                {
                    error = "обход закрыл соединение на приветствии";
                    client.Close();
                    return null;
                }

                if (reply[0] != 0x05)
                {
                    error = "это не SOCKS5 (версия " + reply[0] + ")";
                    client.Close();
                    return null;
                }

                if (reply[1] != 0x00)
                {
                    // 0x02 — прокси требует пароль, 0xFF — не принимает наш способ.
                    // Наш ciadpi работает без пароля, поэтому это чужая служба.
                    error = "обход требует аутентификацию (метод " + reply[1] +
                            ") — наш работает без пароля, проверьте порт";
                    client.Close();
                    return null;
                }

                // ---- 2. CONNECT с доменным именем
                //
                // Именно имя, а не IP: пусть резолвит прокси. На телефоне
                // системный DNS может отдавать заглушки, а обход знает
                // правильный адрес.
                byte[] host = Encoding.ASCII.GetBytes(targetHost ?? "");

                if (host.Length == 0 || host.Length > 255)
                {
                    error = "некорректное имя цели";
                    client.Close();
                    return null;
                }

                byte[] request = new byte[4 + 1 + host.Length + 2];

                request[0] = 0x05;   // версия
                request[1] = 0x01;   // CONNECT
                request[2] = 0x00;   // зарезервировано
                request[3] = 0x03;   // тип адреса: доменное имя
                request[4] = (byte)host.Length;

                Buffer.BlockCopy(host, 0, request, 5, host.Length);

                int at = 5 + host.Length;
                request[at] = (byte)((targetPort >> 8) & 0xFF);
                request[at + 1] = (byte)(targetPort & 0xFF);

                stream.Write(request, 0, request.Length);

                // ---- 3. ответ: версия, код, резерв, тип, адрес, порт
                byte[] head = ReadExact(stream, 4);

                if (head == null)
                {
                    error = "обход закрыл соединение на CONNECT";
                    client.Close();
                    return null;
                }

                if (head[1] != 0x00)
                {
                    error = "обход отказал в CONNECT: " + Describe(head[1]);
                    client.Close();
                    return null;
                }

                // Дочитываем адрес из ответа: без этого остаток попадёт
                // в TLS-поток и сломает рукопожатие.
                int skip;

                if (head[3] == 0x01) skip = 4;             // IPv4
                else if (head[3] == 0x04) skip = 16;       // IPv6
                else if (head[3] == 0x03)                  // доменное имя
                {
                    byte[] len = ReadExact(stream, 1);
                    if (len == null) { client.Close(); error = "обход оборвал ответ"; return null; }
                    skip = len[0];
                }
                else
                {
                    client.Close();
                    error = "непонятный тип адреса в ответе: " + head[3];
                    return null;
                }

                if (ReadExact(stream, skip + 2) == null)
                {
                    client.Close();
                    error = "обход оборвал ответ";
                    return null;
                }

                return client;
            }
            catch (Exception ex)
            {
                if (client != null) { try { client.Close(); } catch { } }

                error = "через обход: " + ex.Message;
                return null;
            }
        }

        /// <summary>Код отказа SOCKS5 человеческими словами.</summary>
        private static string Describe(byte code)
        {
            switch (code)
            {
                case 0x01: return "общий сбой";
                case 0x02: return "правило запрещает";
                case 0x03: return "сеть недоступна";
                case 0x04: return "хост недоступен";
                case 0x05: return "отказ в соединении";
                case 0x06: return "TTL истёк";
                case 0x07: return "команда не поддерживается";
                case 0x08: return "тип адреса не поддерживается";
                default: return "код " + code;
            }
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;

            try
            {
                while (read < count)
                {
                    int n = stream.Read(buffer, read, count - read);
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

        /// <summary>Разобрать «127.0.0.1:1080». false — строка не адрес.</summary>
        public static bool TryParse(string value, out string host, out int port)
        {
            host = "";
            port = 0;

            string text = (value ?? "").Trim();

            if (text.Length == 0) return false;

            int colon = text.LastIndexOf(':');

            if (colon <= 0 || colon == text.Length - 1) return false;

            host = text.Substring(0, colon).Trim();

            return int.TryParse(text.Substring(colon + 1), out port) && port > 0 && port <= 65535;
        }
    }
}
