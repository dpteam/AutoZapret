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
        private volatile string _relay = "";
        private volatile bool _cfProxy = true;

        /// <summary>Один раз сообщили, что ключ выведен из 17-байтного секрета.</summary>
        private static bool _loggedExtendedSecret;

        /// <summary>
        /// Локальный SOCKS5 обхода («127.0.0.1:1080»). Пусто — ходим напрямую.
        ///
        /// Нужно на Android: там обход это локальный SOCKS5, а не перехват
        /// всей системы. Без этого наши соединения к Telegram и к CfProxy
        /// уходят МИМО обхода — и на телефоне не работают, хотя на ПК с
        /// winws работают. Проверено: с телефона TCP:443 до Cloudflare не
        /// проходит вообще, с ПК (где запущен winws2) — проходит.
        /// </summary>
        private volatile string _upstreamSocks = "";
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
        public string Start(string host, int port, string secretHex, string relay = null, bool cfProxy = true,
                            string upstreamSocks = null)
        {
            lock (_gate)
            {
                if (_state == ProxyState.Running || _state == ProxyState.Starting)
                    return "Прокси уже запущен";
            }

            _cfProxy = cfProxy;
            _upstreamSocks = (upstreamSocks ?? "").Trim();

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
                    _relay = (relay ?? "").Trim();
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

                // Один раз сообщаем, каким секретом расшифровался init: 16 байт
                // (обычная схема) или 17 (dd-префикс вместе с ключом). Это
                // объясняет, почему ссылка «работает у одного клиента и не
                // работает у другого», и без такой строки такое поведение
                // выглядит как случайное.
                if (hs.EffectiveSecret != null && hs.EffectiveSecret.Length != secret.Length &&
                    !_loggedExtendedSecret)
                {
                    _loggedExtendedSecret = true;

                    Log.Info("PROXY", "ключ выведен из секрета с префиксом dd (17 байт) — " +
                                      "так делает Telegram в режиме random padding");
                }

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
                //
                // Тег берём КЛИЕНТСКИЙ: Telegram будет разбирать наш поток тем
                // же форматом длины, каким клиент размечен. Если объявить здесь
                // intermediate, а клиент пришёл с abridged, дальше handshake
                // ничего не работает.
                byte[] serverInit = MtProtoHandshake.GenerateRelayInit(hs.Dc, hs.IsMedia, hs.Tag);

                // Ключи направлений
                byte[] clientToProxyKey = hs.ClientKey;
                byte[] clientToProxyIv = hs.ClientIv;

                byte[] proxyToClientKey, proxyToClientIv;
                MtProtoHandshake.DeriveReverse(init, hs.EffectiveSecret ?? secret,
                                               out proxyToClientKey, out proxyToClientIv);

                // ВАЖНО: ключ к Telegram выводится из НАШЕГО init без secret,
                // и первые 64 байта keystream уже «съедены» самим init.
                byte[] relayKey, relayIv;
                MtProtoHandshake.DeriveRelay(serverInit, out relayKey, out relayIv);

                byte[] relayDownKey, relayDownIv;
                MtProtoHandshake.DeriveRelayReverse(serverInit, out relayDownKey, out relayDownIv);

                stream.Write(serverInit, 0, serverInit.Length);
                stream.Flush();

                // Наш init обязан уйти в туннель ПЕРВЫМ кадром.
                //
                // Это не протокол клиента, а протокол kws-точки Telegram: она
                // ждёт obfuscated2-заголовок как начало потока. Эталон делает
                // ровно это (`bridge.py:229` и `tg_ws_proxy.py:346` —
                // `await ws.send(relay_init)`).
                //
                // Без этого кадра Telegram получает данные без заголовка, не
                // может их разобрать и закрывает соединение. Снаружи: handshake
                // прошёл, «через публичный пул CfProxy» в журнале есть, а
                // клиент навсегда остаётся на «Соединение…».
                if (!ws.SendBinary(serverInit, ReadTimeoutMs))
                {
                    Log.Warn("PROXY", "не удалось отправить init в туннель — соединение закрыто");
                    return;
                }

                // 5) мост
                //
                // Здесь и живёт вся криптография MTProxy, и её нельзя пропустить:
                //   клиент -> прокси : расшифровать, зашифровать к Telegram
                //   Telegram -> прокси : расшифровать, зашифровать к клиенту
                //
                // Раньше мост пересылал байты как есть. Handshake проходил,
                // «через публичный пул CfProxy» писалось в журнал, а Telegram
                // навсегда оставался на «Соединение…»: сервер получал шифротекст
                // от клиента, зашифрованный ДРУГИМ ключом, и молчал.
                //
                // Пропуски keystream — не косметика:
                //   * от клиента: 64 байта (его init уже израсходовал их);
                //   * к Telegram: 64 байта (наш init тоже израсходовал);
                //   * к клиенту и от Telegram: НЕ пропускаем — там поток
                //     начинается с нуля.
                using (AesCtr cltDec = new AesCtr(clientToProxyKey, clientToProxyIv))
                using (AesCtr cltEnc = new AesCtr(proxyToClientKey, proxyToClientIv))
                using (AesCtr tgEnc = new AesCtr(relayKey, relayIv))
                using (AesCtr tgDec = new AesCtr(relayDownKey, relayDownIv))
                {
                    cltDec.Skip(MtProtoHandshake.HandshakeLen);
                    tgEnc.Skip(MtProtoHandshake.HandshakeLen);

                    Bridge(client, stream, ws, hs.Tag, cltDec, cltEnc, tgEnc, tgDec, hs.Dc);
                }
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

            // Пауза после того, как не сработало НИЧЕГО.
            //
            // Без неё каждое соединение клиента заново перебирает все домены
            // с таймаутами: Telegram висит на «соединение…», а мы честно
            // тратим 25 секунд, зная заранее, что не выйдет. Пауза растёт
            // вдвое за каждую неудачу, но ручная проверка её сбрасывает.
            //
            // К релею пауза НЕ относится: это другой транспорт и другой адрес.
            // Если релей настроен, сначала пробуем его, и только потом решаем,
            // стоит ли вообще трогать прямой путь.
            string downReason;
            int downLeft;

            List<string> domains = UpstreamRoutes.Order(dc, isMedia, DcTable.WsDomains(dc, isMedia));
            string path = DcTable.PathFor(isTest);

            // ---- сначала внешний релей, если он настроен
            //
            // В сетях, где `kws*.web.telegram.org` заблокирован по адресу,
            // прямой путь не работает вообще: перебор доменов — это перебор
            // имён, указывающих на тот же недоступный адрес. Релей меняет
            // адрес назначения, поэтому он всегда первый, а прямой путь
            // остаётся резервом (и работает там, где блокировки нет).
            string relay = _relay;

            // Для релея берём имена напрямую, без памяти о маршрутах: карантин
            // и удары относятся к ПРЯМОМУ пути. Релей — другой адрес, и чужие
            // отказы к нему отношения не имеют.
            List<string> relayDomains = DcTable.WsDomains(dc, isMedia);

            if (!string.IsNullOrEmpty(relay) && relayDomains.Count > 0)
            {
                string relayDomain = relayDomains[0];
                DateTime relayStarted = DateTime.UtcNow;

                // Релеев может быть несколько (через запятую): один воркер
                // удалили или заблокировали — идём к следующему. Порядок — как
                // ввёл человек: он знает, какой у него основной.
                foreach (string relayItem in WsTunnel.SplitRelays(relay))
                {
                    if ((DateTime.UtcNow - relayStarted).TotalSeconds > RoutesBudgetSec) break;

                    TcpClient relayTcp = null;
                    string relayError;

                    SslStream telegram = WsTunnel.Connect(relayItem, relayDomain, 443,
                        ConnectTimeoutMs * 2, out relayTcp, out relayError, _upstreamSocks);

                    if (telegram != null)
                    {
                        string wsError;
                        WsClient relayWs = WsClient.Connect(telegram, relayDomain, path, ConnectTimeoutMs, out wsError);

                        if (relayWs != null)
                        {
                            tcp = relayTcp;
                            tls = null;      // закрывается вместе с туннелем

                            UpstreamRoutes.Remember(dc, isMedia, relayDomain);

                            Log.Debug("PROXY", "через релей " + WsTunnel.Describe(relayItem) +
                                               " -> " + relayDomain + path);
                            return relayWs;
                        }

                        try { telegram.Dispose(); } catch { }
                        relayError = "апгрейд " + path + " не удался: " + wsError;
                    }

                    if (relayTcp != null) { try { relayTcp.Close(); } catch { } }

                    // Не MarkDown: «релей не ответил» — это не «Telegram недоступен».
                    // Прямой путь может работать, и запрещать его из-за релея нельзя.
                    WarnThrottled("relay:" + WsTunnel.Describe(relayItem),
                        "релей " + WsTunnel.Describe(relayItem) + " не сработал: " + relayError);
                }
            }

            // ---- дальше обычный прямой путь (и его пауза)
            if (UpstreamRoutes.IsDown(out downReason, out downLeft))
            {
                error = downReason + " — повтор через " + downLeft + " с";
                return null;
            }

            DateTime started = DateTime.UtcNow;

            // Если ВСЕ домены отказали на TCP — это не «сеть моргнула»,
            // а блокировка на уровне адреса. Сообщение должно это называть:
            // иначе человек бесконечно жмёт «повторить», хотя повторять нечего.
            bool allConnectFailed = true;
            List<string> tried = new List<string>();

            // Адреса, до которых в ЭТОМ проходе не достучались.
            //
            // Зачем: `kws2.web.telegram.org` и `kws2-1.web.telegram.org` —
            // разные ИМЕНА, но один и тот же IP (проверено через DoH:
            // оба 149.154.167.99). Если адрес заблокирован, второй вариант
            // гарантированно упрётся в тот же таймаут, то есть мы платим
            // 8 секунд за заведомо бесполезную попытку на КАЖДОЕ соединение.
            //
            // Идея из refs/telegram_core.py (`dead_ips`): «если шлюз не
            // принимает даже TCP-соединение, другие имена на нём не пробуем».
            HashSet<string> deadIps = new HashSet<string>(StringComparer.Ordinal);

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

                // Разрешаем имя САМИ, чтобы знать адрес.
                //
                // Раньше соединение шло по имени (`BeginConnect(domain, 443)`),
                // и система разрешала его внутри — мы не видели, к какому
                // адресу идём, и не могли отличить «второй домен» от «тот же
                // адрес ещё раз». Отсюда и двойной таймаут.
                IPAddress address = null;

                try
                {
                    foreach (IPAddress ip in System.Net.Dns.GetHostAddresses(domain))
                    {
                        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        address = ip;
                        break;
                    }
                }
                catch (Exception dnsEx)
                {
                    error = domain + ": DNS не ответил (" + dnsEx.Message + ")";
                    UpstreamRoutes.Fail(domain, true);
                    continue;
                }

                if (address == null)
                {
                    error = domain + ": нет IPv4-адреса";
                    UpstreamRoutes.Fail(domain, true);
                    continue;
                }

                string addressKey = address.ToString();

                // Этот адрес уже отказал в этом проходе — не тратим ещё один
                // таймаут. Говорим честно, что пропустили и почему.
                if (deadIps.Contains(addressKey))
                {
                    Log.Debug("PROXY", domain + " (" + addressKey + ") пропущен: адрес уже отказал");

                    // Пометку на домене НЕ ставим: домен сам по себе рабочий,
                    // просто его адрес недоступен. Иначе после снятия
                    // блокировки он остался бы в карантине.
                    error = domain + ": тот же адрес " + addressKey + ", что уже не ответил";
                    continue;
                }

                try
                {
                    // Через локальный SOCKS5 обхода, если он задан. На Android
                    // это единственный способ дойти до Cloudflare: системного
                    // перехвата там нет.
                    string connectError;

                    candidate = OpenTcp(address.ToString(), 443, ConnectTimeoutMs, out connectError);

                    if (candidate == null)
                    {
                        error = domain + ": " + connectError;
                        deadIps.Add(addressKey);
                        UpstreamRoutes.Fail(domain, true);
                        continue;
                    }

                    connectStage = false;
                    allConnectFailed = false;      // до TLS дошли: адрес доступен

                    // Строгая проверка сертификата, как и в WsTunnel.
                    // Раньше здесь стояло `delegate { return true; }`: любой
                    // сертификат принимался, и подмена DNS или локальный
                    // перехватчик давали «успешный» TLS, а потом
                    // «неизвестный proto tag». Человек искал причину в
                    // стратегии, хотя соединение ушло не в Telegram.
                    ssl = new SslStream(candidate.GetStream(), false, GatewayCheck.ValidateTelegram);

                    try
                    {
                        ssl.AuthenticateAsClient(domain);
                    }
                    catch (Exception tlsEx)
                    {
                        string seen = "";

                        try
                        {
                            if (ssl.RemoteCertificate != null)
                                seen = GatewayCheck.Describe(ssl.RemoteCertificate);
                        }
                        catch { }

                        error = domain + ": " + (seen.Length > 0
                            ? "сертификат не от Telegram (есть: " + seen + ")"
                            : tlsEx.Message);

                        UpstreamRoutes.Fail(domain, false);

                        try { ssl.Dispose(); } catch { }
                        candidate.Close();
                        continue;
                    }

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

            // ---- публичный пул CfProxy, если прямой путь не выжил
            //
            // Порядок именно такой: свой релей → прямой → пул. Пул последний,
            // потому что трафик идёт через ЧУЖОЙ воркер: содержимое остаётся
            // зашифрованным, но владелец видит DC, объём и время.
            //
            // Пробуем только когда прямого пути нет вовсе. Если хоть один
            // шлюз Telegram ответил, а сломался TLS — это не наша блокировка,
            // и уводить трафик к чужим воркерам незачем.
            string poolError = "";

            if (_cfProxy && allConnectFailed)
            {
                WsClient pooled = TryCfProxy(dc, isMedia, path, out poolError);

                if (pooled != null)
                {
                    tcp = null;
                    tls = null;
                    return pooled;
                }

                Log.Warn("PROXY", "публичный пул CfProxy не сработал: " + poolError);
            }

            // Ни один адрес не пустил даже TCP.
            //
            // Это принципиально другой отказ, чем «сертификат не тот» или
            // «WS не поднялся»: тут блокировка на уровне IP, и повторять
            // бессмысленно. Говорим прямо, что нужно, — иначе человек
            // бесконечно жмёт «проверить» и делает вывод «прокси сломан».
            //
            // Если пул тоже пробовали и он отказал — называем ОБЕ причины.
            // Раньше общая строка затирала причину пула, и в журнале было
            // «нужен внешний релей», хотя релей уже пробовали и он не помог.
            if (allConnectFailed && tried.Count > 0)
            {
                error = "не отвечает ни один шлюз Telegram (" +
                        string.Join(", ", tried.ToArray()) + "): " +
                        "kws* блокируется на уровне адреса";

                if (poolError.Length > 0)
                    error += ". Публичный пул CfProxy тоже не помог: " + poolError;
                else
                    error += ". Нужен внешний релей: Cloudflare Worker или свой сервер";
            }

            // Не вышло ничего — уходим в паузу, чтобы следующие соединения
            // не платили полный перебор заведомо впустую.
            int pause = UpstreamRoutes.MarkDown(error ?? "нет доступных маршрутов");

            error = (error ?? "нет доступных маршрутов") +
                    " · следующая попытка через " + pause + " с";

            return null;
        }

        /// <summary>
        /// Попробовать публичный пул CfProxy: TLS + WS-апгрейд к
        /// `kws{dc}.«домен-прослойки»`.
        ///
        /// Отличия от своего релея, и оба важны:
        ///   * НЕТ query-параметров `?host=…&amp;port=…` — воркер понимает адрес
        ///     назначения по имени хоста (`kws2.` = DC 2);
        ///   * сертификат принадлежит домену прослойки, а не Telegram,
        ///     поэтому проверяется цепочка, а не имя. Требовать здесь
        ///     `telegram.org` — значит отвергнуть рабочий путь.
        /// </summary>
        private WsClient TryCfProxy(int dc, bool isMedia, string path, out string error)
        {
            error = "";

            // Мгновенно, без сети: этот метод в пути подключения, и
            // блокирующая загрузка означала бы «соединение…» из-за нас же.
            CfProxyPool.EnsureLoaded();
            CfProxyPool.RefreshInBackground(false);

            List<string> hosts = CfProxyPool.Routes(dc, CfProxyPool.MaxPerPass);

            if (hosts.Count == 0)
            {
                error = "публичный пул CfProxy: нет доступных доменов";
                return null;
            }

            DateTime started = DateTime.UtcNow;
            string last = "";

            foreach (string host in hosts)
            {
                if ((DateTime.UtcNow - started).TotalSeconds > RoutesBudgetSec) break;

                TcpClient client = null;
                System.Net.Security.SslStream ssl = null;

                // Домен прослойки — часть хоста после первой точки.
                int dot = host.IndexOf('.');
                string baseDomain = dot > 0 ? host.Substring(dot + 1) : host;

                try
                {
                    string connectError;

                    client = OpenTcp(host, 443, CfProxyPool.ProbeTimeoutMs, out connectError);

                    if (client == null)
                    {
                        last = host + ": " + connectError;
                        CfProxyPool.MarkBad(baseDomain);
                        continue;
                    }

                    ssl = new System.Net.Security.SslStream(
                        client.GetStream(), false, GatewayCheck.ValidateRelay);

                    ssl.AuthenticateAsClient(host);

                    string wsError;
                    WsClient ws = WsClient.Connect(ssl, host, path, ConnectTimeoutMs, out wsError);

                    if (ws == null)
                    {
                        last = host + ": " + wsError;
                        CfProxyPool.MarkBad(baseDomain);
                        try { ssl.Dispose(); } catch { }
                        client.Close();
                        continue;
                    }

                    CfProxyPool.MarkGood(baseDomain);

                    Log.Info("PROXY", "через публичный пул CfProxy: " + host + path);
                    return ws;
                }
                catch (Exception ex)
                {
                    last = host + ": " + ex.GetType().Name;
                    CfProxyPool.MarkBad(baseDomain);

                    if (ssl != null) { try { ssl.Dispose(); } catch { } }
                    if (client != null) { try { client.Close(); } catch { } }
                }
            }

            error = "публичный пул CfProxy не сработал (" +
                    (last.Length > 0 ? last : "все домены в карантине") + ")";

            return null;
        }

        /// <summary>
        /// Открыть TCP до host:port — напрямую или через локальный SOCKS5.
        ///
        /// Одна точка на все соединения: прямой путь, CfProxy и релей. Иначе
        /// легко забыть провести через обход один из них, и он «не работает»
        /// только на телефоне — ровно то, что и случилось.
        /// </summary>
        private TcpClient OpenTcp(string host, int port, int timeoutMs, out string error)
        {
            error = null;

            string socks = _upstreamSocks;

            if (socks.Length > 0)
            {
                string proxyHost;
                int proxyPort;

                if (Socks5Client.TryParse(socks, out proxyHost, out proxyPort))
                {
                    TcpClient viaProxy = Socks5Client.Connect(proxyHost, proxyPort, host, port,
                                                              timeoutMs, out error);

                    if (viaProxy == null)
                        error = host + " через обход: " + error;

                    return viaProxy;
                }

                Log.Warn("PROXY", "адрес обхода не разобран: " + socks + " — подключаюсь напрямую");
            }

            TcpClient client = null;

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(host, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    client.Close();
                    error = host + ": таймаут TCP";
                    return null;
                }

                client.EndConnect(ar);
                return client;
            }
            catch (Exception ex)
            {
                if (client != null) { try { client.Close(); } catch { } }

                error = host + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Сколько секунд тратим на перебор всех шлюзов одного соединения.</summary>
        private const int RoutesBudgetSec = 25;

        // ----------------------------------------------------------------- bridge

        /// <summary>
        /// Двунаправленный мост: TCP-клиент ↔ WS-апстрим.
        ///
        /// Шифрование: клиентская сторона уже расшифрована нами (мы знаем секрет),
        /// поэтому в WS уходят «сырые» MTProto-пакеты. Обратно — так же.
        /// </summary>
        private void Bridge(TcpClient client, NetworkStream clientStream, WsClient ws, MtProtoTag tag,
                            AesCtr cltDec, AesCtr cltEnc, AesCtr tgEnc, AesCtr tgDec, int dc)
        {
            MtProtoSplitter up = new MtProtoSplitter(tag);

            bool running = true;
            object gate = new object();

            long startUp, startDown;

            // Один раз сообщаем, что сплиттер встал: иначе журнал забьётся.
            bool loggedStuck = false;

            lock (gate)
            {
                startUp = Stats.BytesUp;
                startDown = Stats.BytesDown;
            }

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

                        // Расшифровка от клиента и шифрование к Telegram — две
                        // разные дорожки, поэтому копия: одна идёт в сплиттер
                        // для чтения длин, другая уходит в WS.
                        byte[] plain = new byte[n];
                        Buffer.BlockCopy(buffer, 0, plain, 0, n);
                        cltDec.Transform(plain, 0, n);

                        byte[] cipher = (byte[])plain.Clone();
                        tgEnc.Transform(cipher, 0, n);

                        List<byte[]> packets = up.Feed(plain, cipher, n);
                        foreach (byte[] packet in packets)
                        {
                            if (!ws.SendBinary(packet, ReadTimeoutMs)) { running = false; break; }
                        }

                        // Если сплиттер копит и ничего не отдаёт — это видно в
                        // журнале. Иначе «вверх 43 КБ, вниз 0» не отличить от
                        // «отправили, но ответа нет»: первое чинится разбором
                        // длин, второе — маршрутом.
                        if (packets.Count == 0 && up.Buffered >= 4096 && !loggedStuck)
                        {
                            loggedStuck = true;

                            Log.Warn("PROXY", "сплиттер копит без отправки: " + up.Buffered +
                                              " Б, первые байты " + MtProtoHandshake.Hex(plain, 0, Math.Min(8, n)));
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

                        if (message == null)
                        {
                            // Причина — в журнал. «Получили null» без причины
                            // уже стоило нескольких часов разбора: таймаут,
                            // обрыв и битый кадр выглядят одинаково.
                            Log.Warn("PROXY", "туннель: " +
                                      (ws.LastError ?? "нет данных") +
                                      (string.IsNullOrEmpty(ws.CloseReason) ? "" : ", " + ws.CloseReason));
                            break;
                        }
                        lastUpstreamData = DateTime.UtcNow;

                        lock (gate) Stats.BytesDown += message.Length;

                        // Ответ Telegram приходит зашифрованным ключом, выведенным
                        // из перевёрнутого init, а клиент ждёт поток со своим
                        // обратным ключом. Без этой пары преобразований клиент
                        // видит мусор и висит на «Соединение…».
                        tgDec.Transform(message, 0, message.Length);
                        cltEnc.Transform(message, 0, message.Length);

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

            // Итог сессии — всегда. Молчание в журнале («handshake прошёл,
            // дальше ничего») не даёт понять, где оборвалось: на клиенте, на
            // туннеле или в крипте. Одна строка с объёмом в обе стороны
            // отвечает на этот вопрос сразу.
            long upBytes, downBytes;

            lock (gate)
            {
                upBytes = Stats.BytesUp - startUp;
                downBytes = Stats.BytesDown - startDown;
            }

            Log.Info("PROXY", "сессия DC" + dc + " закрыта: вверх " + upBytes +
                              " Б, вниз " + downBytes + " Б");
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
        /// Сменить внешний релей на ходу.
        ///
        /// Читается на каждом новом соединении (как и секрет), поэтому уже
        /// открытые сессии не рвутся — они работают через прежний транспорт.
        /// </summary>
        /// <summary>
        /// Сменить локальный SOCKS5 обхода на ходу.
        ///
        /// Нужно, потому что обход на Android поднимается и опускается
        /// отдельно от прокси: человек включил прокси, потом обход — и
        /// прокси должен начать ходить через него без перезапуска.
        /// </summary>
        public void UpdateUpstreamSocks(string socks)
        {
            string value = (socks ?? "").Trim();

            if (_upstreamSocks == value) return;

            _upstreamSocks = value;

            Log.Info("PROXY", "обход для апстрима: " +
                              (value.Length > 0 ? value : "напрямую"));
        }

        public void UpdateRelay(string relay)
        {
            string value = (relay ?? "").Trim();

            lock (_gate)
            {
                if (_relay == value) return;

                _relay = value;

                if (_state != ProxyState.Running) return;
            }

            // Сменили транспорт — прежний «всё лежит» больше не актуален:
            // следующее соединение обязано попробовать, а не ждать паузу,
            // накопленную прямым путём.
            UpstreamRoutes.Wake();

            Log.Info("PROXY", value.Length == 0
                ? "внешний релей отключён: подключаюсь к Telegram напрямую"
                : "внешний релей: " + WsTunnel.Describe(value));
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
