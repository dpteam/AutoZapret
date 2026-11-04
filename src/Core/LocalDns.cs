using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutoZapret.Core
{
    /// <summary>Что делает локальный DNS-прокси: счётчики для интерфейса и лога.</summary>
    public sealed class LocalDnsStats
    {
        public long Queries;
        public long CacheHits;
        public long Failures;

        public string Summary()
        {
            return "запросов " + Queries + ", из кэша " + CacheHits + ", ошибок " + Failures;
        }
    }

    /// <summary>
    /// Локальный DNS-сервер на 127.0.0.1:53, который ходит в DoH.
    ///
    /// Зачем: в Windows 10 нет системного DoH (это появилось в Windows 11),
    /// поэтому приложение (Discord, игры, Telegram) резолвит через DNS провайдера,
    /// попадает на заглушку 198.18.x и «не работает», хотя браузер с своим
    /// Secure DNS открывает то же самое. Единственный способ дать DoH всей
    /// системе на десятке — подсунуть ей свой резолвер.
    ///
    /// Как работает: принимаем DNS-запрос по UDP, отправляем те же байты в DoH
    /// по HTTPS (тело application/dns-message), ответ отдаём обратно как есть,
    /// поправив только ID транзакции. Разбирать ответы не нужно вообще — DoH
    /// возвращает ровно тот же формат, что ждёт клиент. Поэтому прокси работает
    /// для всех типов запросов (A, AAAA, PTR, MX, TXT), а не только для A.
    ///
    /// Приватность: наружу уходит только сам вопрос, без EDNS Client Subnet —
    /// как и любой DoH-клиент.
    /// </summary>
    public sealed class LocalDns : IDisposable
    {
        /// <summary>Куда пересылать. Ставится тем сервером, который ответил первым.</summary>
        public string Upstream = "https://1.1.1.1/dns-query";

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly LocalDnsStats _stats = new LocalDnsStats();

        private sealed class Entry
        {
            public byte[] Response;
            public DateTime At;
        }

        private const int CacheSeconds = 120;
        private const int CacheLimit = 2000;

        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _running;

        private static readonly HttpClient Http = CreateClient();

        public bool IsRunning { get { return _running; } }
        public LocalDnsStats Stats { get { return _stats; } }

        /// <summary>Порт для тестов; в бою 53.</summary>
        public int Port = 53;

        private static HttpClient CreateClient()
        {
            try
            {
                SocketsHttpHandler handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(4),
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };

                HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoZapret/1.0");
                return client;
            }
            catch
            {
                return new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            }
        }

        /// <summary>Запустить. null — успех, иначе причина отказа.</summary>
        public string Start()
        {
            lock (_gate)
            {
                if (_running) return null;

                try
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));

                    // 53-й порт мог остаться занятым прошлым экземпляром
                    _udp.Client.ReceiveTimeout = 0;
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.AddressAlreadyInUse)
                        return "не удалось открыть порт " + Port + ": " + ex.Message;

                    // Порт 53 почти всегда занят чьим-то DNS: ICS, Docker, VPN-клиент.
                    // Без имени процесса человек не поймёт, что выключать.
                    string owner = PortOwner(Port);

                    return "порт " + Port + " занят" +
                           (owner.Length > 0 ? " (" + owner + ")" : "") +
                           ": системный DNS переключить не получится, пока он не освободится";
                }

                _running = true;

                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "local-dns"
                };

                _thread.Start();

                Log.Info("DNS", "локальный DNS запущен на 127.0.0.1:" + Port + " → " + Upstream);
                return null;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _running = false;

                try { if (_udp != null) _udp.Close(); } catch { }
                _udp = null;
            }

            try { if (_thread != null) _thread.Join(1000); } catch { }
            _thread = null;

            Log.Info("DNS", "локальный DNS остановлен: " + _stats.Summary());
        }

        public string StatusLine()
        {
            return _running
                ? "работает на 127.0.0.1:" + Port + ", " + _stats.Summary()
                : "выключен";
        }

        private void Loop()
        {
            UdpClient socket;

            lock (_gate) socket = _udp;
            if (socket == null) return;

            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                byte[] query;

                try
                {
                    query = socket.Receive(ref remote);
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (query == null || query.Length < 12) continue;

                Interlocked.Increment(ref _stats.Queries);

                try
                {
                    byte[] answer = Answer(query);

                    if (answer == null)
                    {
                        Interlocked.Increment(ref _stats.Failures);
                        answer = ServFail(query);
                    }

                    socket.Send(answer, answer.Length, remote);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _stats.Failures);
                    Log.Debug("DNS", "запрос не обработан: " + ex.Message);

                    byte[] fail = ServFail(query);
                    try { socket.Send(fail, fail.Length, remote); } catch { }
                }
            }
        }

        private byte[] Answer(byte[] query)
        {
            string key = CacheKey(query);

            lock (_gate)
            {
                Entry cached;

                if (_cache.TryGetValue(key, out cached) &&
                    (DateTime.UtcNow - cached.At).TotalSeconds < CacheSeconds)
                {
                    Interlocked.Increment(ref _stats.CacheHits);
                    return Rewrite(cached.Response, query);
                }
            }

            byte[] response = Query(query);

            if (response != null)
            {
                lock (_gate)
                {
                    if (_cache.Count > CacheLimit) _cache.Clear();

                    _cache[key] = new Entry { Response = response, At = DateTime.UtcNow };
                }
            }

            return response == null ? null : Rewrite(response, query);
        }

        /// <summary>Собственно DoH-запрос: те же байты, что пришли от клиента.</summary>
        private byte[] Query(byte[] request)
        {
            List<string> servers = new List<string> { Upstream };

            foreach (DohServer server in DohServers.All())
                if (!servers.Contains(server.Uri)) servers.Add(server.Uri);

            foreach (string server in servers)
            {
                try
                {
                    using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, server))
                    {
                        ByteArrayContent content = new ByteArrayContent(request);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                        message.Content = content;

                        message.Headers.Accept.Clear();
                        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

                        using (HttpResponseMessage response = Http.SendAsync(message).GetAwaiter().GetResult())
                        {
                            if (!response.IsSuccessStatusCode) continue;

                            byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

                            if (body == null || body.Length < 12) continue;

                            // запомнили рабочего провайдера, чтобы не перебирать каждый раз
                            if (!string.Equals(Upstream, server, StringComparison.OrdinalIgnoreCase))
                            {
                                Upstream = server;
                                Log.Info("DNS", "рабочий DoH: " + server);
                            }

                            return body;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("DNS", server + ": " + ex.GetType().Name);
                }
            }

            return null;
        }

        /// <summary>
        /// Кэш без ID: у каждого запроса свой ID, а вопрос (имя и тип) одинаковый.
        /// Поэтому ключ — весь пакет без первых двух байт.
        /// </summary>
        public static string CacheKey(byte[] query)
        {
            if (query == null || query.Length <= 2) return "";

            StringBuilder sb = new StringBuilder(query.Length * 2);

            for (int i = 2; i < query.Length; i++) sb.Append(query[i].ToString("x2"));

            return sb.ToString();
        }

        /// <summary>Ответ должен нести ID исходного запроса, иначе клиент его отбросит.</summary>
        public static byte[] Rewrite(byte[] response, byte[] query)
        {
            if (response == null || query == null || response.Length < 2 || query.Length < 2) return response;

            byte[] copy = (byte[])response.Clone();
            copy[0] = query[0];
            copy[1] = query[1];

            return copy;
        }

        /// <summary>SERVFAIL: честный отказ вместо тишины, иначе приложение висит 5 с.</summary>
        public static byte[] ServFail(byte[] query)
        {
            byte[] answer = new byte[12];

            if (query != null && query.Length >= 2)
            {
                answer[0] = query[0];
                answer[1] = query[1];
            }

            answer[2] = 0x81;   // ответ, рекурсия доступна
            answer[3] = 0x82;   // RCODE = 2 (SERVFAIL)

            return answer;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Кто держит UDP-порт. `netstat -ano` + имя процесса: иначе сообщение
        /// «порт занят» бесполезно, человек не знает, что закрывать.
        /// </summary>
        public static string PortOwner(int port)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c chcp 437 >nul & netstat -ano -p UDP",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);

                    foreach (string line in output.Split('\n'))
                    {
                        string t = line.Trim();

                        // "UDP    0.0.0.0:53    *:*    4060"
                        if (!t.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)) continue;
                        if (t.IndexOf(":" + port + " ", StringComparison.Ordinal) < 0) continue;

                        string[] parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 4) continue;

                        string pid = parts[parts.Length - 1];
                        int id;
                        if (!int.TryParse(pid, out id)) continue;

                        try
                        {
                            using (System.Diagnostics.Process owner = System.Diagnostics.Process.GetProcessById(id))
                                return owner.ProcessName + ", pid " + id;
                        }
                        catch
                        {
                            return "pid " + id;
                        }
                    }
                }
            }
            catch { }

            return "";
        }
    }
}