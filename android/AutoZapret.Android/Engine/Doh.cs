using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Свой резолвер через DNS-over-HTTPS.
    ///
    /// Зачем он нужен — это не «улучшение», а лечение конкретной поломки.
    /// Проверено на живой сети МТС (Android 16):
    ///
    ///   www.youtube.com -> 198.18.0.4     (подлог)
    ///   discord.com     -> 198.18.0.74    (подлог)
    ///   twitch.tv       -> 151.101.194.167 (честно)
    ///   ya.ru           -> 5.255.255.242   (честно)
    ///
    /// 198.18.0.0/15 — это диапазон для тестовых сетей (RFC 2544). Настоящих
    /// серверов там нет: пакет уходит в чёрную дыру и соединение тихо висит
    /// или рвётся. Именно поэтому «YouTube не открывается, а ya.ru открывается»,
    /// и никакой разрез ClientHello тут не помогает — мы стучимся не туда.
    ///
    /// Свой резолвер решает это полностью: имя разбирается по HTTPS, где
    /// подменить ответ по пути нельзя (сертификат).
    /// </summary>
    public static class Doh
    {
        /// <summary>198.18.0.0/15 — диапазон подлога. Всё, что сюда попало, недостоверно.</summary>
        public static bool IsPoisoned(IPAddress ip)
        {
            if (ip == null) return false;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

            byte[] b = ip.GetAddressBytes();

            return b[0] == 198 && (b[1] == 18 || b[1] == 19);
        }

        public static bool HasPoison(IEnumerable<IPAddress> addresses)
        {
            if (addresses == null) return false;

            foreach (IPAddress ip in addresses)
                if (IsPoisoned(ip)) return true;

            return false;
        }

        /// <summary>Работал ли последний запрос через DoH (для лога).</summary>
        public static string LastServer = "";

        private const int TimeoutMs = 4000;

        /// <summary>Кэш: домен -> адреса. Без ограничения — домены тут реальные, их десятки.</summary>
        private static readonly Dictionary<string, List<IPAddress>> Cache =
            new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();

        /// <summary>
        /// Серверы в порядке предпочтения.
        ///
        /// По IP, а не по имени: чтобы достучаться до `cloudflare-dns.com`,
        /// нужно сначала его разрешить — а разрешать нечем, мы для этого и
        /// здесь. Поэтому только адреса.
        /// </summary>
        private static readonly string[] Servers =
        {
            "https://1.1.1.1/dns-query",
            "https://8.8.8.8/dns-query",
            "https://9.9.9.9/dns-query",
            "https://94.140.14.14/dns-query"
        };

        /// <summary>
        /// Разобрать имя. Возвращает пустой список, если не вышло ни одним
        /// способом — вызывающий тогда решает сам, пробовать ли системный DNS.
        /// </summary>
        public static List<IPAddress> Resolve(string host)
        {
            if (string.IsNullOrEmpty(host)) return new List<IPAddress>();

            IPAddress direct;

            if (IPAddress.TryParse(host, out direct))
            {
                List<IPAddress> single = new List<IPAddress>();
                single.Add(direct);

                return single;
            }

            lock (Gate)
            {
                List<IPAddress> cached;

                if (Cache.TryGetValue(host, out cached)) return cached;
            }

            List<IPAddress> result = null;

            foreach (string server in Servers)
            {
                result = Query(server, host);

                if (result != null && result.Count > 0)
                {
                    LastServer = server;
                    break;
                }
            }

            if (result == null || result.Count == 0) return new List<IPAddress>();

            lock (Gate)
            {
                Cache[host] = result;
            }

            return result;
        }

        public static int CachedCount
        {
            get { lock (Gate) { return Cache.Count; } }
        }

        public static void Clear()
        {
            lock (Gate) { Cache.Clear(); }
        }

        /// <summary>Один запрос к одному серверу через JSON-API (его поддерживают все четыре).</summary>
        private static List<IPAddress> Query(string server, string host)
        {
            TcpClient client = null;

            try
            {
                Uri uri = new Uri(server);

                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(uri.Host, uri.Port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(TimeoutMs))
                {
                    return null;
                }

                client.EndConnect(ar);

                client.ReceiveTimeout = TimeoutMs;
                client.SendTimeout = TimeoutMs;

                // Соединяемся по IP, но SNI и Host обязаны быть именем —
                // иначе Cloudflare отдаст чужой сертификат и TLS не сойдётся.
                string ip = uri.Host;

                using (System.Net.Security.SslStream ssl =
                       new System.Net.Security.SslStream(client.GetStream(), false))
                {
                    ssl.AuthenticateAsClient(ip);

                    string path = uri.AbsolutePath + "?name=" + Uri.EscapeDataString(host) + "&type=A";

                    string request =
                        "GET " + path + " HTTP/1.1\r\n" +
                        "Host: " + ip + "\r\n" +
                        "Accept: application/dns-json\r\n" +
                        "User-Agent: AutoZapret\r\n" +
                        "Connection: close\r\n\r\n";

                    byte[] bytes = Encoding.ASCII.GetBytes(request);

                    ssl.Write(bytes, 0, bytes.Length);
                    ssl.Flush();

                    string body = ReadBody(ssl);

                    return ParseJson(body);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
        }

        /// <summary>Прочитать тело HTTP-ответа (заголовки пропускаем).</summary>
        private static string ReadBody(Stream stream)
        {
            MemoryStream all = new MemoryStream();
            byte[] buffer = new byte[8192];

            try
            {
                while (true)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;

                    all.Write(buffer, 0, n);

                    // Тело небольшое: как только нашли конец заголовков и
                    // набрали хоть что-то — выходим, не дожидаясь закрытия.
                    if (all.Length > 512) break;
                }
            }
            catch
            {
                // обрыв после получения тела — нормален для Connection: close
            }

            string text = Encoding.UTF8.GetString(all.ToArray());

            int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            return split >= 0 ? text.Substring(split + 4) : text;
        }

        /// <summary>
        /// Разобрать JSON-ответ DoH. Свой разборщик, а не библиотека: нужен
        /// ровно один массив `Answer` с полями `type` и `data`.
        /// </summary>
        private static List<IPAddress> ParseJson(string json)
        {
            List<IPAddress> result = new List<IPAddress>();

            if (string.IsNullOrEmpty(json)) return result;

            int answer = json.IndexOf("\"Answer\"", StringComparison.Ordinal);
            if (answer < 0) return result;

            // Каждый ответ — объект {"name":..,"type":1,"TTL":..,"data":"1.2.3.4"}
            int cursor = answer;

            while (true)
            {
                int type = json.IndexOf("\"type\"", cursor, StringComparison.Ordinal);
                if (type < 0) break;

                int data = json.IndexOf("\"data\"", type, StringComparison.Ordinal);
                if (data < 0) break;

                // Между ними не должно быть закрытия массива — иначе мы уже
                // ушли в следующий раздел ответа.
                int arrayEnd = json.IndexOf(']', cursor);
                if (arrayEnd >= 0 && arrayEnd < type) break;

                int typeValue = ReadInt(json, type + 6);
                string value = ReadString(json, data + 6);

                cursor = data + 6;

                // 1 = A (IPv4), 28 = AAAA (IPv6). Остальные записи (CNAME и
                // прочее) нам не нужны.
                if (typeValue != 1 && typeValue != 28) continue;

                IPAddress parsed;

                if (IPAddress.TryParse(value, out parsed)) result.Add(parsed);
            }

            return result;
        }

        private static int ReadInt(string json, int from)
        {
            int colon = json.IndexOf(':', from);
            if (colon < 0) return 0;

            int start = colon + 1;

            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;

            int end = start;

            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;

            int value;

            if (int.TryParse(json.Substring(start, end - start), out value)) return value;

            return 0;
        }

        private static string ReadString(string json, int from)
        {
            int colon = json.IndexOf(':', from);
            if (colon < 0) return "";

            int quote = json.IndexOf('"', colon + 1);
            if (quote < 0) return "";

            int end = json.IndexOf('"', quote + 1);
            if (end < 0) return "";

            return json.Substring(quote + 1, end - quote - 1);
        }
    }
}
