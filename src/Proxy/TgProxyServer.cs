using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Proxy
{
    public enum ProxyState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    public sealed class ProxyStats
    {
        public int Active;
        public long TotalConnections;
        public long BytesUp;
        public long BytesDown;
        public long Failed;
        public DateTime StartedAt;

        /// <summary>
        /// Почему отклоняли handshake, по причинам.
        ///
        /// Считать причины, а не только «отказов N», — единственный способ
        /// отличить «клиент пришёл со старым секретом» от «на порт ломится
        /// что-то постороннее». Обе ситуации выглядят одинаково: соединение
        /// есть, дальше тишина.
        /// </summary>
        public int RejectSecret;
        public int RejectTls;
        public int RejectHttp;
        public int RejectGarbage;

        /// <summary>Последняя причина отказа — для подсказки в интерфейсе.</summary>
        public string LastReject = "";

        public int Rejected { get { return RejectSecret + RejectTls + RejectHttp + RejectGarbage; } }

        /// <summary>Короткая подсказка человеку: что именно не так с клиентом.</summary>
        public string RejectHint()
        {
            if (RejectTls > 0 && RejectTls >= RejectSecret)
                return "Telegram стучится с Fake-TLS секретом (ee). Наш прокси работает с dd — " +
                       "скопируйте ссылку из этой карточки и добавьте прокси заново";

            if (RejectHttp > 0 && RejectHttp >= RejectSecret)
                return "на порт прокси пришёл HTTP-запрос, а не Telegram. Проверьте, " +
                       "не указан ли этот порт как HTTP-прокси в браузере или системе";

            if (RejectSecret > 0)
                return "Telegram стучится, но секрет не совпадает — ссылка в Telegram устарела. " +
                       "Скопируйте её из этой карточки и добавьте прокси заново";

            return "";
        }
    }

    /// <summary>
    /// MTProto-over-WebSocket прокси.
    ///
    /// Схема:
    ///   клиент (Telegram) --> [наш TCP] --> obfuscated2 --> WS --> kws*.web.telegram.org
    ///
    /// Почему WS, а не прямой TCP: Telegram Desktop умеет ходить через WS-эндпоинты
    /// web.telegram.org, которые DPI обычно не трогает (это «обычный» HTTPS-трафик
    /// на порт 443 с валидным сертификатом).
    ///
    /// Реализация в один поток на соединение: при 20-50 соединениях это проще и
    /// безопаснее, чем общий event loop с блокировками.
    /// </summary>
    public sealed class TgProxyServer : IDisposable
    {
        private const int BufferSize = 256 * 1024;
        private const int ConnectTimeoutMs = 8000;
        private const int ReadTimeoutMs = 120000;
        private const int RelaySilenceTimeoutMs = 10000;

        private readonly object _gate = new object();
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly List<Thread> _threads = new List<Thread>();
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();

        private volatile bool _running;
        private byte[] _secret;
        private string _host;
        private int _port;
        private ProxyState _state = ProxyState.Stopped;
        private string _lastError;

        public ProxyStats Stats { get; } = new ProxyStats();

        public event Action<ProxyState, string> StateChanged;

        public ProxyState State { get { lock (_gate) return _state; } }
        public string LastError { get { lock (_gate) return _lastError; } }
        public int Port { get { return _port; } }
        public bool IsRunning { get { return _running; } }

        // ------------------------------------------------------------------ start

        /// <summary>Запустить. Возвращает null при успехе, иначе текст ошибки.</summary>
        public string Start(string host, int port, string secretHex)
        {
            lock (_gate)
            {
                if (_state == ProxyState.Running || _state == ProxyState.Starting)
                    return "Прокси уже запущен";
            }

            byte[] secret = ParseSecret(secretHex);
            if (secret == null) return "Секрет должен быть 32 hex-символа (16 байт)";

            IPAddress address;
            if (!IPAddress.TryParse(string.IsNullOrEmpty(host) ? "127.0.0.1" : host, out address))
                return "Неверный адрес прослушивания: " + host;

            SetState(ProxyState.Starting, null);

            try
            {
                TcpListener listener = new TcpListener(address, port);
                listener.Start();

                lock (_gate)
                {
                    _secret = secret;
                    _host = address.ToString();
                    _port = port;
                    _running = true;
                    Stats.StartedAt = DateTime.Now;
                    _listeners.Add(listener);
                }

                Thread accept = new Thread(() => AcceptLoop(listener))
                {
                    IsBackground = true,
                    Name = "proxy-accept-" + port
                };
                accept.Start();

                lock (_gate) _threads.Add(accept);

                SetState(ProxyState.Running, null);
                Log.Info("PROXY", "слушаю " + _host + ":" + _port);
                return null;
            }
            catch (SocketException ex)
            {
                string message = ExplainBindError(ex);
                Log.Error("PROXY", message);
                SetState(ProxyState.Error, message);
                return message;
            }
            catch (Exception ex)
            {
                Log.Exception("PROXY", "запуск", ex);
                SetState(ProxyState.Error, ex.Message);
                return ex.Message;
            }
        }

        /// <summary>Понятное объяснение ошибки bind — иначе пользователь видит «10048».</summary>
        private static string ExplainBindError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.AddressAlreadyInUse:
                    return "Порт уже занят. Закройте tg-ws-proxy/другой прокси или смените порт.";
                case SocketError.AccessDenied:
                    return "Доступ запрещён: порт занят системой, антивирусом или нужны права.";
                case SocketError.AddressNotAvailable:
                    return "Такого адреса нет на этом компьютере.";
                default:
                    return "Не удалось занять порт: " + ex.SocketErrorCode;
            }
        }

        // ------------------------------------------------------------------- stop

        public void Stop()
        {
            lock (_gate)
            {
                if (_state == ProxyState.Stopped) return;
                _state = ProxyState.Stopping;
                _running = false;
            }
            RaiseState(ProxyState.Stopping, null);

            Log.Info("PROXY", "остановка");

            // Память о маршрутах сбрасываем вместе с остановкой: сеть могла
            // смениться, а «запомненный рабочий домен» — свойство прошлой сети.
            UpstreamRoutes.Reset();

            lock (_gate)
            {
                foreach (TcpListener listener in _listeners)
                {
                    try { listener.Stop(); } catch { }
                }
                _listeners.Clear();

                foreach (TcpClient client in _clients)
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();
            }

            // даём потокам завершиться, но не ждём вечно
            List<Thread> threads;
            lock (_gate) threads = new List<Thread>(_threads);

            foreach (Thread thread in threads)
            {
                try { thread.Join(1500); } catch { }
            }
            lock (_gate) _threads.Clear();

            lock (_gate)
            {
                Stats.Active = 0;
            }

            SetState(ProxyState.Stopped, null);
            Log.Info("PROXY", "остановлен");
        }

        // ----------------------------------------------------------------- accept

        private void AcceptLoop(TcpListener listener)
        {
            while (_running)
            {
                TcpClient client = null;

                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!_running) return;
                    Log.Warn("PROXY", "accept: " + ex.Message);
                    Thread.Sleep(200);
                    continue;
                }

                if (client == null) continue;

                TcpClient captured = client;
                Thread worker = new Thread(() => HandleClient(captured))
                {
                    IsBackground = true,
                    Name = "proxy-session"
                };
                worker.Start();

                lock (_gate) _threads.Add(worker);
            }
        }

        // ---------------------------------------------------------------- session

        private void HandleClient(TcpClient client)
        {
            lock (_gate)
            {
                _clients.Add(client);
                Stats.Active++;
                Stats.TotalConnections++;
            }

            WsClient ws = null;
            TcpClient upstreamTcp = null;
            SslStream upstreamTls = null;

            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = ReadTimeoutMs;
                client.SendTimeout = ReadTimeoutMs;
                try { client.ReceiveBufferSize = BufferSize; client.SendBufferSize = BufferSize; } catch { }

                NetworkStream stream = client.GetStream();

                // 1) читаем 64-байтовый init
                byte[] init = ReadExact(stream, MtProtoHandshake.HandshakeLen);
                if (init == null)
                {
                    Log.Debug("PROXY", "клиент закрылся до handshake");
                    return;
                }

                // 2) расшифровываем
                byte[] secret;
                lock (_gate) secret = _secret;

                HandshakeResult hs = MtProtoHandshake.Decode(init, secret);
                if (!hs.Ok)
                {
                    // Считаем ПРИЧИНУ, а не только «отказ»: «не тот секрет»
                    // и «клиент на ee-секрете» лечатся совершенно по-разному,
                    // а в логе выглядят одинаково.
                    lock (_gate)
                    {
                        Stats.Failed++;
                        Stats.LastReject = hs.Error;

                        switch (hs.Reject)
                        {
                            case HandshakeReject.Tls: Stats.RejectTls++; break;
                            case HandshakeReject.Http: Stats.RejectHttp++; break;
                            case HandshakeReject.Secret: Stats.RejectSecret++; break;
                            default: Stats.RejectGarbage++; break;
                        }
                    }

                    WarnThrottled("handshake:" + hs.Reject, "handshake отклонён: " + hs.Error);

                    // Дренаж: не рвать сразу, иначе клиент получит RST и не покажет ошибку.
                    Drain(stream);
                    return;
                }

                Log.Debug("PROXY", "DC " + hs.Dc + (hs.IsMedia ? " (media)" : "") +
                                   ", " + MtProtoHandshake.TagName(hs.Tag) +
                                   (hs.IsTestDc ? ", test" : ""));

                // 3) подключаемся к Telegram по WS
                string error;
                ws = OpenUpstream(hs.Dc, hs.IsMedia, hs.IsTestDc, out upstreamTcp, out upstreamTls, out error);
                if (ws == null)
                {
                    WarnThrottled("upstream:" + hs.Dc, "не удалось подключиться к Telegram: " + error);
                    lock (_gate) Stats.Failed++;
                    return;
                }

                // 4) отвечаем клиенту своим init (тот же 64-байтовый формат, но с нашим ключом)
                byte[] serverInit = MtProtoHandshake.GenerateRelayInit(hs.Dc, hs.IsMedia);

                // Ключи направлений
                byte[] clientToProxyKey = hs.ClientKey;
                byte[] clientToProxyIv = hs.ClientIv;

                byte[] proxyToClientKey, proxyToClientIv;
                MtProtoHandshake.DeriveReverse(init, secret, out proxyToClientKey, out proxyToClientIv);

                // ВАЖНО: ключ к Telegram выводится из НАШЕГО init без secret,
                // и первые 64 байта keystream уже «съедены» самим init.
                byte[] relayKey, relayIv;
                MtProtoHandshake.DeriveRelay(serverInit, out relayKey, out relayIv);

                stream.Write(serverInit, 0, serverInit.Length);
                stream.Flush();

                // 5) мост
                Bridge(client, stream, ws, hs.Tag,
                       clientToProxyKey, clientToProxyIv,
                       proxyToClientKey, proxyToClientIv);
            }
            catch (Exception ex)
            {
                Log.Debug("PROXY", "сессия: " + ex.GetType().Name + ": " + ex.Message);
                lock (_gate) Stats.Failed++;
            }
            finally
            {
                if (ws != null) { try { ws.Close(); } catch { } }
                if (upstreamTls != null) { try { upstreamTls.Dispose(); } catch { } }
                if (upstreamTcp != null) { try { upstreamTcp.Close(); } catch { } }
                try { client.Close(); } catch { }

                lock (_gate)
                {
                    _clients.Remove(client);
                    if (Stats.Active > 0) Stats.Active--;
                }
            }
        }

        /// <summary>
        /// Подключение к Telegram: TCP -> TLS(SNI kwsN) -> WS-апгрейд.
        /// Пробуем домены по очереди: media сначала «-1», обычные — основной.
        /// </summary>
        private WsClient OpenUpstream(int dc, bool isMedia, bool isTest,
                                      out TcpClient tcp, out SslStream tls, out string error)
        {
            tcp = null;
            tls = null;
            error = null;

            List<string> domains = UpstreamRoutes.Order(dc, isMedia, DcTable.WsDomains(dc, isMedia));
            string path = DcTable.PathFor(isTest);

            DateTime started = DateTime.UtcNow;

            // Если ВСЕ домены отказали на TCP — это не «сеть моргнула»,
            // а блокировка на уровне адреса. Сообщение должно это называть:
            // иначе человек бесконечно жмёт «повторить», хотя повторять нечего.
            bool allConnectFailed = true;
            List<string> tried = new List<string>();

            foreach (string domain in domains)
            {
                // Общий бюджет на перебор: без него «соединение…» в Telegram
                // висит столько, сколько доменов × таймаут. 25 секунд — как в
                // эталоне, после этого честно говорим, что не вышло.
                if ((DateTime.UtcNow - started).TotalSeconds > RoutesBudgetSec)
                {
                    error = "перебор маршрутов занял больше " + RoutesBudgetSec + " с";
                    break;
                }

                TcpClient candidate = null;
                SslStream ssl = null;
                bool connectStage = true;

                tried.Add(domain);

                try
                {
                    candidate = new TcpClient();
                    candidate.NoDelay = true;

                    IAsyncResult ar = candidate.BeginConnect(domain, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                    {
                        error = domain + ": таймаут TCP";
                        UpstreamRoutes.Fail(domain, true);
                        candidate.Close();
                        continue;
                    }
                    candidate.EndConnect(ar);

                    connectStage = false;
                    allConnectFailed = false;      // до TLS дошли: адрес доступен

                    ssl = new SslStream(candidate.GetStream(), false, delegate { return true; });
                    ssl.AuthenticateAsClient(domain);

                    string wsError;
                    WsClient ws = WsClient.Connect(ssl, domain, path, ConnectTimeoutMs, out wsError);

                    if (ws == null)
                    {
                        error = domain + ": " + wsError;
                        UpstreamRoutes.Fail(domain, false);
                        try { ssl.Dispose(); } catch { }
                        candidate.Close();
                        continue;
                    }

                    tcp = candidate;
                    tls = ssl;

                    UpstreamRoutes.Remember(dc, isMedia, domain);

                    Log.Debug("PROXY", "подключён к " + domain + path);
                    return ws;
                }
                catch (Exception ex)
                {
                    error = domain + ": " + ex.GetType().Name;
                    UpstreamRoutes.Fail(domain, connectStage);

                    if (ssl != null) { try { ssl.Dispose(); } catch { } }
                    if (candidate != null) { try { candidate.Close(); } catch { } }
                }
            }

            // Ни один адрес не пустил даже TCP.
            //
            // Это принципиально другой отказ, чем «сертификат не тот» или
            // «WS не поднялся»: тут блокировка на уровне IP, и повторять
            // бессмысленно. Говорим прямо, что нужно, — иначе человек
            // бесконечно жмёт «проверить» и делает вывод «прокси сломан».
            if (allConnectFailed && tried.Count > 0)
            {
                error = "не отвечает ни один шлюз Telegram (" +
                        string.Join(", ", tried.ToArray()) + "): " +
                        "kws* блокируется на уровне адреса. " +
                        "Нужен внешний релей: Cloudflare Worker или свой сервер — " +
                        "прямое подключение из этой сети не пройдёт";
            }

            return null;
        }

        /// <summary>Сколько секунд тратим на перебор всех шлюзов одного соединения.</summary>
        private const int RoutesBudgetSec = 25;

        // ----------------------------------------------------------------- bridge

        /// <summary>
        /// Двунаправленный мост: TCP-клиент <-> WS-апстрим.
        ///
        /// Шифрование: клиентская сторона уже расшифрована нами (мы знаем секрет),
        /// поэтому в WS уходят «сырые» MTProto-пакеты. Обратно — так же.
        /// </summary>
        private void Bridge(TcpClient client, NetworkStream clientStream, WsClient ws, MtProtoTag tag,
                            byte[] c2pKey, byte[] c2pIv, byte[] p2cKey, byte[] p2cIv)
        {
            MtProtoSplitter up = new MtProtoSplitter(tag);
            MtProtoSplitter down = new MtProtoSplitter(tag);

            bool running = true;
            object gate = new object();

            DateTime lastUpstreamData = DateTime.UtcNow;

            // --- клиент -> Telegram
            Thread toUpstream = new Thread(() =>
            {
                byte[] buffer = new byte[65536];

                try
                {
                    while (running && _running)
                    {
                        int n = clientStream.Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;

                        lock (gate) Stats.BytesUp += n;

                        List<byte[]> packets = up.Feed(buffer, 0, n);
                        foreach (byte[] packet in packets)
                        {
                            if (!ws.SendBinary(packet, ReadTimeoutMs)) { running = false; break; }
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (gate) running = false;
                    byte[] rest = up.Flush();
                    if (rest.Length > 0) { try { ws.SendBinary(rest, 2000); } catch { } }
                }
            })
            { IsBackground = true, Name = "proxy-up" };

            // --- Telegram -> клиент
            Thread toClient = new Thread(() =>
            {
                try
                {
                    while (running && _running)
                    {
                        byte[] message = ws.ReceiveMessage(ReadTimeoutMs);
                        if (message == null) break;

                        lastUpstreamData = DateTime.UtcNow;

                        lock (gate) Stats.BytesDown += message.Length;

                        clientStream.Write(message, 0, message.Length);
                        clientStream.Flush();
                    }
                }
                catch { }
                finally
                {
                    lock (gate) running = false;
                    try { client.Close(); } catch { }
                }
            })
            { IsBackground = true, Name = "proxy-down" };

            toUpstream.Start();
            toClient.Start();

            toUpstream.Join();
            toClient.Join();
        }

        // ---------------------------------------------------------------- helpers

        private static byte[] ReadExact(Stream stream, int count)
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

        /// <summary>Читает и выбрасывает данные, чтобы клиент не получил RST раньше времени.</summary>
        private static void Drain(Stream stream)
        {
            try
            {
                NetworkStream network = stream as NetworkStream;
                byte[] sink = new byte[8192];
                int total = 0;
                DateTime until = DateTime.UtcNow.AddMilliseconds(500);

                while (total < 65536 && DateTime.UtcNow < until)
                {
                    // DataAvailable есть только у NetworkStream
                    if (network != null && !network.DataAvailable)
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    int n = stream.Read(sink, 0, sink.Length);
                    if (n <= 0) break;
                    total += n;
                }
            }
            catch { }
        }

        /// <summary>
        /// Сменить секрет НА ХОДУ, не перезапуская прокси.
        ///
        /// Зачем: раньше кнопка «Новый» меняла только настройки, а работающий
        /// прокси продолжал слушать со СТАРЫМ секретом. Дальше человек копировал
        /// новую ссылку в Telegram, клиент подключался с новым секретом, прокси
        /// расшифровывал старым — и получал «неизвестный proto tag». Выглядит
        /// как «прокси сломался», хотя он честно не понимает клиента.
        ///
        /// Секрет читается под замком на каждом соединении, поэтому подмена
        /// безопасна: новое соединение либо целиком со старым секретом, либо
        /// целиком с новым, промежуточного состояния нет.
        /// </summary>
        public string UpdateSecret(string secretHex)
        {
            byte[] secret = ParseSecret(secretHex);
            if (secret == null) return "Секрет должен быть 32 hex-символа (16 байт)";

            lock (_gate)
            {
                if (_state != ProxyState.Running) return null;   // не запущен — менять нечего

                _secret = secret;
            }

            // Старые соединения уже используют свои ключи и не ломаются:
            // ключ выведен при handshake и живёт в потоке.
            Log.Info("PROXY", "секрет заменён на ходу: старые ссылки в Telegram больше не работают");

            return null;
        }

        /// <summary>
        /// Лог с подавлением повторов.
        ///
        /// В живом логе было семь одинаковых «таймаут TCP» подряд за восемь
        /// секунд: каждое соединение клиента повторяет ту же ошибку. В логе
        /// это мусор, а в трее — семь уведомлений.
        ///
        /// Ключ — сама суть ошибки без цифр: одинаковые причины схлопываются,
        /// разные (DC, домен, стадия) остаются отдельными строками.
        /// </summary>
        private void WarnThrottled(string key, string message, int seconds = 10)
        {
            int skipped;

            lock (_gate)
            {
                DateTime last;

                if (_warnAt.TryGetValue(key, out last) &&
                    (DateTime.UtcNow - last).TotalSeconds < seconds)
                {
                    _warnSkipped.TryGetValue(key, out skipped);
                    _warnSkipped[key] = skipped + 1;
                    return;
                }

                _warnAt[key] = DateTime.UtcNow;

                _warnSkipped.TryGetValue(key, out skipped);
                _warnSkipped[key] = 0;
            }

            Log.Warn("PROXY", message + (skipped > 0 ? " (повторов подавлено: " + skipped + ")" : ""));
        }

        private readonly Dictionary<string, DateTime> _warnAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _warnSkipped = new Dictionary<string, int>();

        public static byte[] ParseSecret(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            string value = hex.Trim();
            if (value.StartsWith("dd", StringComparison.OrdinalIgnoreCase) && value.Length > 32)
                value = value.Substring(2);

            if (value.Length != 32) return null;

            byte[] result = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                string pair = value.Substring(i * 2, 2);
                int b;
                if (!int.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    return null;
                result[i] = (byte)b;
            }
            return result;
        }

        private void SetState(ProxyState state, string error)
        {
            lock (_gate)
            {
                _state = state;
                _lastError = error;
            }
            RaiseState(state, error);
        }

        private void RaiseState(ProxyState state, string error)
        {
            Action<ProxyState, string> handler = StateChanged;
            if (handler != null) { try { handler(state, error); } catch { } }
        }

        public string StatusLine()
        {
            if (!IsRunning) return "выключен";
            return _host + ":" + _port + ", соединений: " + Stats.Active +
                   ", всего: " + Stats.TotalConnections;
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
        }
    }
}
