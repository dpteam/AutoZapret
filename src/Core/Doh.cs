using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Резолвер через DoH (DNS over HTTPS) по IP-адресу сервера.
    ///
    /// Зачем свой, если есть Dns.GetHostAddresses: системный DNS можно подменить
    /// на уровне провайдера — прозрачный перехват порта 53 отвечает чем угодно,
    /// и запрос к 8.8.8.8, и к 1.1.1.1, и к «своему» серверу возвращает один и
    /// тот же чужой ответ. Именно так выглядели все замеры: youtube отдавал
    /// 198.18.0.4, до настоящего Google пакет не доходил, и любая стратегия
    /// давала «0 из 4».
    ///
    /// DoH идёт обычным HTTPS на IP, без DNS: соединение устанавливается к
    /// 1.1.1.1 и 8.8.8.8 напрямую, сертификат проверяется по имени, а запрос
    /// уходит телом POST. Подменить это можно только заблокировав сам HTTPS,
    /// что уже видно как ошибка, а не как «неправильный адрес».
    ///
    /// Своя реализация формата DNS, потому что NuGet запрещён (AGENTS.md):
    /// сборка запроса и разбор ответа — это ~120 строк.
    /// </summary>
    public static class Doh
    {
        /// <summary>Куда удалось достучаться в прошлый раз — для логов.</summary>
        public static string LastServer = "";

        private const int CacheSeconds = 300;

        private sealed class Entry
        {
            public List<IPAddress> Addresses;
            public DateTime At;
            public string Source;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Servers =
        {
            "https://1.1.1.1/dns-query",
            "https://8.8.8.8/dns-query",
            "https://1.0.0.1/dns-query",
            "https://9.9.9.9/dns-query"
        };

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try
            {
                HttpClientHandler handler = new HttpClientHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                             System.Net.DecompressionMethods.Deflate
                };

                HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoZapret/1.0");
                return client;
            }
            catch
            {
                return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            }
        }

        /// <summary>
        /// Разрешить имя через DoH. Пустой список — не получилось (тогда вызывающий
        /// падает обратно на системный DNS).
        /// </summary>
        public static List<IPAddress> Resolve(string host, int timeoutMs = 4000)
        {
            if (string.IsNullOrEmpty(host)) return new List<IPAddress>();

            lock (Gate)
            {
                Entry cached;

                if (Cache.TryGetValue(host, out cached) &&
                    (DateTime.UtcNow - cached.At).TotalSeconds < CacheSeconds)
                {
                    return new List<IPAddress>(cached.Addresses);
                }
            }

            List<IPAddress> result = ResolveCore(host, timeoutMs);

            if (result.Count > 0)
            {
                lock (Gate)
                {
                    Cache[host] = new Entry
                    {
                        Addresses = new List<IPAddress>(result),
                        At = DateTime.UtcNow,
                        Source = LastServer
                    };
                }
            }

            return result;
        }

        /// <summary>Источник адресов для отчёта: «DoH 1.1.1.1» или «системный DNS».</summary>
        public static string SourceOf(string host)
        {
            lock (Gate)
            {
                Entry cached;
                if (Cache.TryGetValue(host, out cached)) return cached.Source;
            }

            return "";
        }

        public static void Reset()
        {
            lock (Gate) Cache.Clear();
        }

        private static List<IPAddress> ResolveCore(string host, int timeoutMs)
        {
            byte[] query = BuildQuery(host, 1);   // 1 = A

            foreach (string server in Servers)
            {
                try
                {
                    long start = Environment.TickCount;

                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, server))
                    {
                        ByteArrayContent content = new ByteArrayContent(query);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                        request.Content = content;

                        request.Headers.Accept.Clear();
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

                        using (HttpResponseMessage response = Http.SendAsync(request).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                Log.Debug("DNS", server + ": HTTP " + (int)response.StatusCode);
                                continue;
                            }

                            byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                            List<IPAddress> addresses = ParseAnswer(body, 1);

                            if (addresses.Count == 0) continue;

                            LastServer = server;
                            Log.Info("DNS", host + " → " + Join(addresses) + " через " + server +
                                            " (" + (Environment.TickCount - start) + " мс)");

                            return addresses;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("DNS", server + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return new List<IPAddress>();
        }

        // ------------------------------------------------------------ DNS-формат

        private const ushort TypeA = 1;
        private const ushort TypeAaaa = 28;
        private const ushort ClassIn = 1;

        /// <summary>Собрать запрос: заголовок + один вопрос, рекурсия разрешена.</summary>
        public static byte[] BuildQuery(string host, ushort type)
        {
            List<byte> data = new List<byte>(64);

            ushort id = (ushort)(Environment.TickCount & 0xFFFF);
            data.Add((byte)(id >> 8));
            data.Add((byte)id);

            data.Add(0x01);   // RD
            data.Add(0x00);
            data.Add(0x00); data.Add(0x01);   // QDCOUNT = 1
            data.Add(0x00); data.Add(0x00);   // ANCOUNT
            data.Add(0x00); data.Add(0x00);   // NSCOUNT
            data.Add(0x00); data.Add(0x00);   // ARCOUNT

            foreach (string label in host.TrimEnd('.').Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }

            data.Add(0x00);
            data.Add((byte)(type >> 8));
            data.Add((byte)type);
            data.Add((byte)(ClassIn >> 8));
            data.Add((byte)ClassIn);

            return data.ToArray();
        }

        /// <summary>
        /// Разобрать ответ и достать адреса нужного типа. Имена в ответе могут быть
        /// указателями (0xC0xx) на другие имена — поэтому после вопроса идём по
        /// записям, а не «ищем строку».
        /// </summary>
        public static List<IPAddress> ParseAnswer(byte[] message, ushort wantType)
        {
            List<IPAddress> result = new List<IPAddress>();

            if (message == null || message.Length < 12) return result;

            int questions = (message[4] << 8) | message[5];
            int answers = (message[6] << 8) | message[7];

            int offset = 12;

            for (int i = 0; i < questions; i++)
            {
                offset = SkipName(message, offset);
                if (offset < 0 || offset + 4 > message.Length) return result;
                offset += 4;
            }

            for (int i = 0; i < answers; i++)
            {
                offset = SkipName(message, offset);
                if (offset < 0 || offset + 10 > message.Length) break;

                ushort type = (ushort)((message[offset] << 8) | message[offset + 1]);
                int length = (message[offset + 8] << 8) | message[offset + 9];
                offset += 10;

                if (offset + length > message.Length) break;

                if (type == wantType)
                {
                    if (type == TypeA && length == 4)
                    {
                        result.Add(new IPAddress(new[]
                        {
                            message[offset], message[offset + 1],
                            message[offset + 2], message[offset + 3]
                        }));
                    }
                    else if (type == TypeAaaa && length == 16)
                    {
                        byte[] bytes = new byte[16];
                        Array.Copy(message, offset, bytes, 0, 16);
                        result.Add(new IPAddress(bytes));
                    }
                }

                offset += length;
            }

            return result;
        }

        private static int SkipName(byte[] message, int offset)
        {
            int guard = 0;

            while (offset < message.Length && guard++ < 128)
            {
                byte length = message[offset];

                if (length == 0) return offset + 1;

                // указатель: два байта и конец имени
                if ((length & 0xC0) == 0xC0) return offset + 2;

                offset += length + 1;
            }

            return -1;
        }

        private static string Join(List<IPAddress> addresses)
        {
            List<string> parts = new List<string>();
            foreach (IPAddress ip in addresses) parts.Add(ip.ToString());
            return string.Join(", ", parts.ToArray());
        }
    }
}