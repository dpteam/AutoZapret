using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Как именно резать первый пакет соединения.
    ///
    /// На Android без root нет ни WinDivert, ни raw-сокетов, поэтому «обход DPI»
    /// здесь делается на уровне сокета: мы сами решаем, какими сегментами уйдёт
    /// TLS ClientHello. DPI, который смотрит на первый сегмент, видит обрывок
    /// рукопожатия и не может сопоставить SNI.
    /// </summary>
    public sealed class DesyncConfig
    {
        public bool Enabled = true;

        /// <summary>
        /// Позиции разреза ClientHello. Пусто — не режем вовсе (пресет «без
        /// обхода»). Все позиции вне диапазона отбрасываются при отправке,
        /// поэтому за «1+5» на коротком пакете ничего не ломается.
        /// </summary>
        public int[] Positions = new[] { 1 };

        /// <summary>
        /// Позиция, которую нельзя задать числом заранее: `midsld` (середина
        /// домена второго уровня), `sniext+1` (сразу после расширения SNI).
        ///
        /// В конфигах winws так и написано, а число зависит от конкретного
        /// ClientHello. Считаем на месте, когда пакет уже есть.
        /// </summary>
        public string PositionHint = "";

        /// <summary>Пауза между сегментами. 0 — отправлять подряд.</summary>
        public int DelayMs;

        /// <summary>Резать только TLS ClientHello, остальное отправлять целиком.</summary>
        public bool OnlyTls = true;

        /// <summary>Порт SOCKS5 на localhost. 0 — выбрать свободный.</summary>
        public int Port = 1080;

        /// <summary>
        /// Отправить хвост ClientHello первым с TTL=1.
        ///
        /// До сервера такой пакет не доходит (умирает на первом же
        /// маршрутизаторе), но DPI стоит ближе и его видит — и получает поток
        /// «не по порядку». Дальше настоящие куски уходят обычным TTL.
        /// </summary>
        public bool Disorder;

        /// <summary>
        /// Один байт отправить как срочный (URG).
        ///
        /// Сервер, который не читает `MSG_OOB`, видит его обычным байтом и
        /// ничего не замечает, а простой парсер DPI сбивается на указателе
        /// срочности и теряет поток.
        /// </summary>
        public bool Oob;

        /// <summary>
        /// Отправить подставной ClientHello с низким TTL перед настоящим.
        ///
        /// Это единственная техника, которая прошла на живой сети МТС (см.
        /// `android/README.md`): DPI видит законченный ClientHello с
        /// безобидным именем, помечает соединение проверенным, и настоящий
        /// ClientHello следом идёт без разбора.
        ///
        /// TTL обязателен: без него фейк дойдёт до сервера, и тот закроет
        /// соединение (он не ждёт двух рукопожатий).
        /// </summary>
        public bool Fake;

        /// <summary>TTL для фейка. 8 проверено на живой сети: 3 и 5 не хватает до DPI, 12 уже доходит до сервера.</summary>
        public int FakeTtl = 8;

        /// <summary>
        /// SNI подставного ClientHello.
        ///
        /// Значение взято не с потолка: byedpi (проверенный движок) использует
        /// в своём fake_tls именно www.wikipedia.org, и на живой сети это
        /// работает. Google-домены брать нельзя: они сами могут быть под
        /// блокировкой, и тогда DPI видит в фейке запрещённое имя — фейк не
        /// только не помогает, но и подтверждает блокировку.
        /// </summary>
        public string FakeSni = "www.wikipedia.org";

        /// <summary>
        /// Снимать настоящий ClientHello с устройства вместо синтетики.
        ///
        /// Синтетический hello собран по RFC и правдоподобен, но его отпечаток
        /// (JA3) не совпадает ни с одним реальным клиентом. Снятый — это
        /// буквально отпечаток этого телефона, отличить его нечем.
        /// </summary>
        public bool UseRealHello = true;

        /// <summary>
        /// Не пропускать QUIC (UDP 443).
        ///
        /// Приложения Google, включая YouTube, первым делом пробуют QUIC. Наш
        /// desync работает только с TCP, а UDP мы отдаём как есть — значит
        /// через QUIC обход не применяется вообще. Отказ заставляет клиента
        /// откатиться на TCP, где разрез уже действует.
        /// </summary>
        public bool BlockQuic = true;

        public string Describe()
        {
            if (!Enabled) return "без desync";

            if (Fake)
            {
                string fake = "фейк TTL " + FakeTtl;

                if (Positions != null && Positions.Length > 0) fake += " + разрез";
                if (Disorder) fake += " + disorder";

                return fake + (DelayMs > 0 ? ", пауза " + DelayMs + " мс" : "");
            }

            if (Positions == null || Positions.Length == 0) return "без разреза";

            string list = "";

            foreach (int pos in Positions)
            {
                if (list.Length > 0) list += "+";
                list += pos;
            }

            string text = "разрез " + list;

            if (Disorder) text += " + disorder";
            if (Oob) text += " + oob";
            if (Fake) text += " + fake TTL " + FakeTtl;
            if (DelayMs > 0) text += ", пауза " + DelayMs + " мс";

            return text;
        }

        /// <summary>
        /// Локальный адрес, к которому привязывать исходящие сокеты.
        ///
        /// Не используется: привязка по IP на Android при двух сетях ломает
        /// соединение (пакеты уходят в таблицу маршрутов другой сети). Выбор
        /// сети делается через NetworkBinding.BindProcess. Поле оставлено,
        /// чтобы старые настройки читались без ошибок.
        /// </summary>
        public string BindAddress = "";

        /// <summary>
        /// Порт внешнего движка (ciadpi), если он используется.
        ///
        /// Когда задан, наш прокси не делает desync сам: он разбирает имя через
        /// DoH и передаёт соединение движку как SOCKS5 CONNECT на IP. Так
        /// честный DNS остаётся у нас, а отлаженный обход — у byedpi.
        /// 0 — работаем своим движком.
        /// </summary>
        public int ForwardPort = 0;
    }

    /// <summary>
    /// Локальный SOCKS5 с desync первого пакета.
    ///
    /// Почему SOCKS5, а не VPN сразу: это работает без root и без системного
    /// разрешения VPN. Telegram умеет SOCKS5 из коробки, браузер — через
    /// настройки прокси. VpnService (ступень B) появится поверх этого же
    /// движка, отдельно.
    ///
    /// Реализованы CONNECT и UDP ASSOCIATE без аутентификации: этого хватает,
    /// прокси слушает 127.0.0.1 и наружу не выставлен. UDP нужен не «на всякий
    /// случай», а чтобы в VPN-режиме работали DNS и голос от Discord.
    /// </summary>
    public sealed class Socks5Proxy : IDisposable
    {
        private readonly object _gate = new object();
        private TcpListener _listener;
        private Thread _accept;
        private volatile bool _running;

        private long _connections;
        private long _bytesUp;
        private long _bytesDown;
        private int _active;
        private volatile bool _quicBlockedLogged;
        private volatile bool _dnsAnswered;

        public DesyncConfig Config = new DesyncConfig();

        /// <summary>Куда писать события для интерфейса. Не должен бросать.</summary>
        public Action<string> Log;

        public bool IsRunning { get { return _running; } }
        public int ActualPort { get; private set; }
        public long Connections { get { return Interlocked.Read(ref _connections); } }
        public long BytesUp { get { return Interlocked.Read(ref _bytesUp); } }
        public long BytesDown { get { return Interlocked.Read(ref _bytesDown); } }
        public int Active { get { return Volatile.Read(ref _active); } }

        /// <summary>Запустить. null — успех, иначе текст ошибки.</summary>
        public string Start()
        {
            lock (_gate)
            {
                if (_running) return null;

                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, Config.Port);
                    _listener.Start();

                    ActualPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    _running = true;
                }
                catch (SocketException ex)
                {
                    return "не удалось занять порт " + Config.Port + ": " + ex.SocketErrorCode;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }

                _accept = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "socks-accept"
                };

                _accept.Start();

                Say("SOCKS5 слушает 127.0.0.1:" + ActualPort + " · " + Config.Describe() +
                    " [enabled=" + Config.Enabled + " fake=" + Config.Fake + " ttl=" + Config.FakeTtl +
                    " pos=" + (Config.Positions == null ? "null" : Config.Positions.Length.ToString()) +
                    " hint=" + (Config.PositionHint ?? "") + "]");                return null;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;

                _running = false;

                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
            }

            Say("SOCKS5 остановлен");
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------- accept

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    // Stop() закрывает listener — это штатный выход
                    return;
                }

                Thread session = new Thread(delegate () { Session(client); })
                {
                    IsBackground = true,
                    Name = "socks-session"
                };

                session.Start();
            }
        }

        private void Session(TcpClient client)
        {
            Interlocked.Increment(ref _connections);
            Interlocked.Increment(ref _active);

            TcpClient upstream = null;

            try
            {
                client.NoDelay = true;

                // 0 — без таймаута. Было 60000, и это убивало живые, но тихие
                // соединения: у Discord шлюз молчит по 41 секунде между
                // heartbeat, у Telegram — длинный poll. Через минуту тишины
                // поток чтения получал IOException, сессия закрывалась, и
                // «связь пропала» без единой ошибки в логе.
                client.ReceiveTimeout = 0;
                client.SendTimeout = 0;

                NetworkStream fromClient = client.GetStream();

                if (!Handshake(fromClient))
                {
                    Say("клиент не прошёл приветствие SOCKS5");
                    return;
                }

                byte command;
                string host;
                int port;

                if (!ReadRequest(fromClient, out command, out host, out port))
                {
                    Say("запрос SOCKS5 не разобран");
                    return;
                }

                // CMD 3 — UDP ASSOCIATE. Без него в VPN-режиме не работают ни
                // голос в Discord, ни DNS: и то и другое идёт датаграммами.
                if (command == 3)
                {
                    UdpRelay(client, fromClient);
                    return;
                }

                string connectError;

                upstream = ConnectUpstream(host, port, out connectError);

                if (upstream == null)
                {
                    Reply(fromClient, 4);   // host unreachable
                    Say("не удалось подключиться к " + host + ":" + port + " — " + connectError);
                    return;
                }

                Reply(fromClient, 0);

                NetworkStream toServer = upstream.GetStream();

                // Первый кусок в сторону сервера проходит через desync: именно
                // там лежит ClientHello, который и проверяет DPI.
                Thread up = new Thread(delegate () { PumpUp(fromClient, upstream, host); })
                {
                    IsBackground = true,
                    Name = "socks-up"
                };

                Thread down = new Thread(delegate () { PumpDown(toServer, fromClient); })
                {
                    IsBackground = true,
                    Name = "socks-down"
                };

                up.Start();
                down.Start();

                up.Join();
                down.Join();
            }
            catch (Exception ex)
            {
                Say("сессия: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (upstream != null) upstream.Close(); } catch { }
                try { client.Close(); } catch { }

                Interlocked.Decrement(ref _active);
            }
        }

        // ---------------------------------------------------------- протокол

        private static bool Handshake(NetworkStream stream)
        {
            byte[] head = ReadExact(stream, 2);
            if (head == null || head[0] != 5) return false;

            int methods = head[1];
            if (methods <= 0) return false;

            byte[] list = ReadExact(stream, methods);
            if (list == null) return false;

            // без аутентификации
            stream.Write(new byte[] { 5, 0 }, 0, 2);
            stream.Flush();
            return true;
        }

        private static bool ReadRequest(NetworkStream stream, out byte command, out string host, out int port)
        {
            command = 0;
            host = null;
            port = 0;

            byte[] head = ReadExact(stream, 4);
            if (head == null || head[0] != 5) return false;

            command = head[1];

            // CONNECT (1) и UDP ASSOCIATE (3). BIND не поддерживаем — он
            // нужен только входящим соединениям, а мы локальный прокси.
            if (command != 1 && command != 3) { Reply(stream, 7); return false; }

            switch (head[3])
            {
                case 1:   // IPv4
                    {
                        byte[] addr = ReadExact(stream, 4);
                        if (addr == null) return false;
                        host = new IPAddress(addr).ToString();
                        break;
                    }

                case 3:   // домен
                    {
                        byte[] len = ReadExact(stream, 1);
                        if (len == null) return false;

                        byte[] name = ReadExact(stream, len[0]);
                        if (name == null) return false;

                        host = Encoding.UTF8.GetString(name);
                        break;
                    }

                case 4:   // IPv6
                    {
                        byte[] addr = ReadExact(stream, 16);
                        if (addr == null) return false;
                        host = new IPAddress(addr).ToString();
                        break;
                    }

                default:
                    Reply(stream, 8);
                    return false;
            }

            byte[] portBytes = ReadExact(stream, 2);
            if (portBytes == null) return false;

            port = (portBytes[0] << 8) | portBytes[1];
            return true;
        }

        // ------------------------------------------------------------ UDP

        /// <summary>
        /// UDP ASSOCIATE — ретранслятор датаграмм.
        ///
        /// Зачем это критично. В VPN-режиме hev отдаёт сюда ВСЁ, что не TCP:
        /// DNS-запросы, голос Discord (RTP/DTLS), STUN, игровой трафик. Пока
        /// здесь стоял отказ (код 7), «текст грузится, а голос не подключается»,
        /// и это выглядело как поломка обхода, хотя обход тут вообще ни при чём.
        ///
        /// Схема стандартная, из RFC 1928:
        ///   * клиент просит ASSOCIATE по TCP и получает наш UDP-порт;
        ///   * дальше он шлёт датаграммы НА ЭТОТ порт с заголовком
        ///     RSV(2) FRAG(1) ATYP(1) ADDR PORT;
        ///   * мы вынимаем адрес, отправляем полезную нагрузку в сеть;
        ///   * ответы возвращаем тем же заголовком с адресом отправителя.
        ///
        /// Один исходящий сокет на сессию (не connected): так ответы от разных
        /// серверов приходят в один поток и не нужно держать сокет на каждого
        /// собеседника — у голосового звонка их десятки.
        /// </summary>
        private void UdpRelay(TcpClient client, NetworkStream control)
        {
            UdpClient relay = null;
            UdpClient out4 = null;
            UdpClient out6 = null;

            int alive = 1;
            IPEndPoint clientEnd = null;
            object endpointGate = new object();

            try
            {
                relay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

                // Два сокета, а не один: IPv6-адрес нельзя отправить с IPv4-сокета.
                // Раньше ответ из IPv6-сети просто не уходил.
                out4 = new UdpClient(AddressFamily.InterNetwork);

                // Если ядро без IPv6 — не повод терять всю UDP-сессию.
                try { out6 = new UdpClient(AddressFamily.InterNetworkV6); }
                catch { out6 = null; }

                int relayPort = ((IPEndPoint)relay.Client.LocalEndPoint).Port;

                // Ответ на ASSOCIATE: VER REP RSV ATYP=IPv4 127.0.0.1 PORT
                byte[] reply = new byte[10];
                reply[0] = 5;
                reply[1] = 0;
                reply[2] = 0;
                reply[3] = 1;
                reply[4] = 127;
                reply[7] = 1;
                reply[8] = (byte)(relayPort >> 8);
                reply[9] = (byte)(relayPort & 0xFF);

                control.Write(reply, 0, reply.Length);
                control.Flush();

                Say("UDP-сессия открыта, порт " + relayPort);

                // Пока жива управляющая TCP-связь — жива и UDP-сессия.
                // ReceiveTimeout у неё выставлен в 0, поэтому Read висит до
                // реального закрытия, а не до таймаута простоя.
                Thread watch = new Thread(delegate ()
                {
                    try
                    {
                        byte[] sink = new byte[64];

                        while (control.Read(sink, 0, sink.Length) > 0) { }
                    }
                    catch { }

                    Volatile.Write(ref alive, 0);

                    try { relay.Close(); } catch { }
                    try { out4.Close(); } catch { }
                    try { out6.Close(); } catch { }
                })
                {
                    IsBackground = true,
                    Name = "socks-udp-watch"
                };

                watch.Start();

                // --- из сети к клиенту (для каждого семейства свой сокет)
                Action<UdpClient, IPAddress, string> startDown = delegate (UdpClient sock, IPAddress any, string name)
                {
                    Thread down = new Thread(delegate ()
                    {
                        int failures = 0;

                        while (Volatile.Read(ref alive) == 1)
                        {
                            try
                            {
                                IPEndPoint from = new IPEndPoint(any, 0);
                                byte[] data = sock.Receive(ref from);

                                failures = 0;

                                if (data == null || data.Length == 0) continue;

                                IPEndPoint to;

                                lock (endpointGate) { to = clientEnd; }

                                if (to == null) continue;

                                byte[] packet = BuildUdpPacket(from, data);
                                relay.Send(packet, packet.Length, to);

                                Interlocked.Add(ref _bytesDown, data.Length);
                            }
                            catch (ObjectDisposedException)
                            {
                                break;
                            }
                            catch
                            {
                                // Сюда попадает ICMP «порт закрыт» от системы: это
                                // не повод закрывать всю сессию, но и крутиться в
                                // пустом цикле нельзя.
                                if (++failures > 50) break;
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = name
                    };

                    down.Start();
                };

                startDown(out4, IPAddress.Any, "socks-udp-down4");
                if (out6 != null) startDown(out6, IPAddress.IPv6Any, "socks-udp-down6");

                // --- от клиента в сеть
                while (Volatile.Read(ref alive) == 1)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] datagram;

                    try
                    {
                        datagram = relay.Receive(ref from);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch
                    {
                        continue;
                    }

                    if (datagram == null || datagram.Length < 10) continue;

                    lock (endpointGate) { clientEnd = from; }

                    // FRAG: фрагментацию SOCKS-датаграмм не делаем и не ждём
                    if (datagram[2] != 0) continue;

                    string targetHost;
                    int targetPort;
                    int offset;

                    if (!ParseUdpPacket(datagram, out targetHost, out targetPort, out offset)) continue;

                    int payload = datagram.Length - offset;

                    // ---- DNS перехватываем целиком
                    //
                    // Приложения в VPN резолвят имена сами и получают от
                    // оператора подлог (youtube -> 198.18.0.4). Отвечаем за
                    // оператора: разбираем запрос, спрашиваем DoH, собираем
                    // честный ответ. Тогда адреса правильные у ВСЕХ приложений,
                    // включая те, что ходят на зашитый 8.8.8.8.
                    if (targetPort == 53 && payload >= 12)
                    {
                        byte[] question = new byte[payload];
                        Buffer.BlockCopy(datagram, offset, question, 0, payload);

                        if (DnsServer.IsQuery(question))
                        {
                            byte[] answer = DnsServer.Answer(question);

                            if (answer != null)
                            {
                                byte[] packet = BuildUdpPacket(
                                    new IPEndPoint(IPAddress.Parse(targetHost), 53), answer);

                                relay.Send(packet, packet.Length, from);

                                Interlocked.Add(ref _bytesDown, answer.Length);

                                if (!_dnsAnswered)
                                {
                                    _dnsAnswered = true;
                                    Say("DNS отвечаем сами (DoH): системный отдаёт подлог");
                                }

                                continue;
                            }
                        }
                    }

                    // QUIC — единственный протокол, где наш desync не работает                    // вовсе: мы отдаём датаграммы как есть. Google-сервисы
                    // пробуют QUIC первыми, поэтому без этого отказа YouTube
                    // идёт мимо обхода и «не грузится», хотя TCP-путь исправен.
                    if (Config.BlockQuic && targetPort == 443)
                    {
                        if (!_quicBlockedLogged)
                        {
                            _quicBlockedLogged = true;
                            Say("QUIC (UDP 443) не пропускаем — приложения уйдут на TCP, где разрез работает");
                        }

                        continue;
                    }

                    IPAddress target = ResolveUdp(targetHost);
                    if (target == null) continue;

                    try
                    {
                        UdpClient sender = target.AddressFamily == AddressFamily.InterNetworkV6 && out6 != null ? out6 : out4;

                        sender.Client.SendTo(datagram, offset, payload, SocketFlags.None,
                                             new IPEndPoint(target, targetPort));

                        Interlocked.Add(ref _bytesUp, payload);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Say("UDP-сессия: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref alive, 0);

                try { if (relay != null) relay.Close(); } catch { }
                try { if (out4 != null) out4.Close(); } catch { }
                try { if (out6 != null) out6.Close(); } catch { }
                try { client.Close(); } catch { }
            }
        }

        /// <summary>Разобрать заголовок UDP-датаграммы SOCKS5.</summary>
        private static bool ParseUdpPacket(byte[] data, out string host, out int port, out int offset)
        {
            host = null;
            port = 0;
            offset = 0;

            if (data.Length < 5) return false;
            if (data[0] != 0 || data[1] != 0) return false;   // RSV

            int atype = data[3];
            int cursor;

            switch (atype)
            {
                case 1:
                    {
                        if (data.Length < 10) return false;

                        byte[] addr = new byte[4];
                        Buffer.BlockCopy(data, 4, addr, 0, 4);
                        host = new IPAddress(addr).ToString();
                        cursor = 8;
                        break;
                    }

                case 3:
                    {
                        int length = data[4];
                        if (data.Length < 5 + length + 2) return false;

                        host = Encoding.UTF8.GetString(data, 5, length);
                        cursor = 5 + length;
                        break;
                    }

                case 4:
                    {
                        if (data.Length < 22) return false;

                        byte[] addr = new byte[16];
                        Buffer.BlockCopy(data, 4, addr, 0, 16);
                        host = new IPAddress(addr).ToString();
                        cursor = 20;
                        break;
                    }

                default:
                    return false;
            }

            port = (data[cursor] << 8) | data[cursor + 1];
            offset = cursor + 2;

            return true;
        }

        /// <summary>Собрать датаграмму SOCKS5 с адресом отправителя.</summary>
        private static byte[] BuildUdpPacket(IPEndPoint from, byte[] data)
        {
            bool ipv6 = from.AddressFamily == AddressFamily.InterNetworkV6;
            int head = ipv6 ? 22 : 10;

            byte[] packet = new byte[head + data.Length];
            packet[3] = (byte)(ipv6 ? 4 : 1);

            byte[] address = from.Address.GetAddressBytes();
            Buffer.BlockCopy(address, 0, packet, 4, address.Length);

            int cursor = 4 + address.Length;
            packet[cursor] = (byte)(from.Port >> 8);
            packet[cursor + 1] = (byte)(from.Port & 0xFF);

            Buffer.BlockCopy(data, 0, packet, head, data.Length);

            return packet;
        }

        /// <summary>Адрес для UDP-цели. Имя разбираем сами, IPv4 первым.</summary>
        private static IPAddress ResolveUdp(string host)
        {
            IPAddress parsed;

            if (IPAddress.TryParse(host, out parsed)) return parsed;

            // Через DoH: системный DNS на этой сети отдаёт подлог.
            List<IPAddress> doh = Doh.Resolve(host);

            if (doh.Count > 0)
            {
                foreach (IPAddress address in doh)
                    if (address.AddressFamily == AddressFamily.InterNetwork) return address;

                return doh[0];
            }

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);

                if (addresses == null) return null;

                foreach (IPAddress address in addresses)
                    if (address.AddressFamily == AddressFamily.InterNetwork) return address;

                return addresses.Length > 0 ? addresses[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Reply(NetworkStream stream, byte code)
        {
            try
            {
                // VER REP RSV ATYP(IPv4) BND.ADDR(0) BND.PORT(0)
                stream.Write(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                stream.Flush();
            }
            catch { }
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

        // ------------------------------------------------------------- потоки

        /// <summary>
        /// Подключиться к цели, разобрав имя сами.
        ///
        /// Почему не `TcpClient.BeginConnect(host, port)`: .NET возьмёт адреса
        /// в порядке, который вернула система. На телефоне это часто IPv6
        /// первым, а IPv6-маршрута может не быть — попытка висит до таймаута
        /// ОС (десятки секунд), и клиент получает «host unreachable».
        ///
        /// Поэтому: разбираем имя сами, IPv4 идём первым, на каждый адрес
        /// свой короткий таймаут, остальные пробуем следом.
        /// </summary>
        private TcpClient ConnectUpstream(string host, int port, out string error)
        {
            error = null;

            System.Net.IPAddress[] addresses;

            if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress direct))
            {
                addresses = new[] { direct };
            }
            else
            {
                // Сначала свой резолвер через DoH. Системный здесь нельзя:
                // оператор подменяет ответы (youtube -> 198.18.0.4), и мы
                // уходим в чёрную дыру. DoH подменить нельзя — там сертификат.
                List<System.Net.IPAddress> doh = Doh.Resolve(host);

                if (doh.Count > 0)
                {
                    addresses = doh.ToArray();
                }
                else
                {
                    // DoH не ответил (сеть злая, 443 закрыт) — системный DNS
                    // лучше, чем ничего. Но результат проверяем на подлог.
                    try
                    {
                        addresses = System.Net.Dns.GetHostAddresses(host);

                        if (Doh.HasPoison(addresses))
                        {
                            Say(host + ": системный DNS отдал подложный адрес, DoH тоже недоступен — " +
                                "иду по подлогу, соединение скорее всего не откроется");
                        }
                    }
                    catch (Exception ex)
                    {
                        error = "DNS: " + ex.GetType().Name;
                        return null;
                    }
                }
            }

            if (addresses == null || addresses.Length == 0)
            {
                error = "DNS: имя не разрешилось";
                return null;
            }

            // IPv4 вперёд: на мобильных сетях он есть почти всегда
            Array.Sort(addresses, delegate (System.Net.IPAddress a, System.Net.IPAddress b)
            {
                int av = a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
                int bv = b.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
                return av.CompareTo(bv);
            });

            string tried = "";
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);

            // Пишем, что именно разобрали: без этой строки «не удалось
            // подключиться к 87.250.250.68» не отличить от подлога DNS,
            // от блокировки по IP и от таймаута сети.
            string resolved = "";

            foreach (System.Net.IPAddress a in addresses)
            {
                if (resolved.Length > 0) resolved += ", ";
                resolved += a;
            }

            Say("резолв " + host + " -> " + resolved +
                (Doh.HasPoison(addresses) ? " (ПОДЛОГ)" : ""));

            // Внешний движок: отдаём ему уже разобранный IP, чтобы он не
            // резолвил сам через подменённый системный DNS.
            if (Config.ForwardPort > 0)
            {
                TcpClient forwarded = ConnectViaEngine(addresses, port, out error);

                if (forwarded != null) return forwarded;

                return null;
            }

            foreach (System.Net.IPAddress address in addresses)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    tried += " (время вышло)";
                    break;
                }

                // Сокет строго по семейству цели, как в byedpi
                // (`nb_socket(dst->sa.sa_family, type)`). Dual-mode сокет
                // (`new TcpClient()`) здесь вреден: для IPv4-цели он остаётся
                // AF_INET6, и семейство сокета перестаёт соответствовать
                // семейству пакета — а от этого зависит и TTL, и маршрут.
                TcpClient client = new TcpClient(address.AddressFamily);
                client.NoDelay = true;

                try
                {
                    IAsyncResult ar = client.BeginConnect(address, port, null, null);

                    DateTime started = DateTime.UtcNow;

                    // 6 секунд на адрес: больше — и клиент уже не ждёт
                    if (!ar.AsyncWaitHandle.WaitOne(6000))
                    {
                        Say("таймаут connect к " + address + ":" + port +
                            " через " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " мс");
                        tried += " " + address + ":таймаут";
                        client.Close();
                        continue;
                    }

                    client.EndConnect(ar);

                    Say("connect " + address + ":" + port + " за " +
                        (int)(DateTime.UtcNow - started).TotalMilliseconds + " мс");

                    client.ReceiveTimeout = 60000;
                    client.SendTimeout = 60000;

                    if (tried.Length > 0) Say(host + " → " + address + " (после " + tried.Trim() + ")");

                    return client;
                }
                catch (Exception ex)
                {
                    tried += " " + address + ":" + ex.GetType().Name;
                    try { client.Close(); } catch { }
                }
            }

            error = addresses.Length + " адрес(ов):" + tried;
            return null;
        }

        /// <summary>
        /// Подключиться через внешний движок (ciadpi) по SOCKS5.
        ///
        /// Отдаём ему IP, а не имя: имя он разберёт системным DNS, а тот
        /// подменён оператором (youtube -> 198.18.0.4). Наш DoH уже дал
        /// настоящий адрес, его и передаём.
        /// </summary>
        private TcpClient ConnectViaEngine(System.Net.IPAddress[] addresses, int port, out string error)
        {
            error = null;

            foreach (System.Net.IPAddress address in addresses)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;

                TcpClient client = new TcpClient();

                try
                {
                    IAsyncResult ar = client.BeginConnect(System.Net.IPAddress.Loopback, Config.ForwardPort, null, null);

                    if (!ar.AsyncWaitHandle.WaitOne(3000))
                    {
                        client.Close();
                        continue;
                    }

                    client.EndConnect(ar);

                    client.NoDelay = true;
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;

                    NetworkStream stream = client.GetStream();

                    // приветствие без аутентификации
                    stream.Write(new byte[] { 5, 1, 0 }, 0, 3);

                    byte[] hello = ReadExact(stream, 2);

                    if (hello == null || hello[1] != 0)
                    {
                        client.Close();
                        error = "движок не принял приветствие SOCKS5";
                        continue;
                    }

                    byte[] ip = address.GetAddressBytes();
                    byte[] request = new byte[10];

                    request[0] = 5;
                    request[1] = 1;   // CONNECT
                    request[2] = 0;
                    request[3] = 1;   // IPv4
                    Buffer.BlockCopy(ip, 0, request, 4, 4);
                    request[8] = (byte)(port >> 8);
                    request[9] = (byte)(port & 0xFF);

                    stream.Write(request, 0, request.Length);

                    byte[] reply = ReadExact(stream, 10);

                    if (reply == null || reply[1] != 0)
                    {
                        client.Close();
                        error = "движок отказал в CONNECT (код " +
                                (reply == null ? "?" : reply[1].ToString()) + ")";
                        continue;
                    }

                    return client;
                }
                catch (Exception ex)
                {
                    try { client.Close(); } catch { }
                    error = ex.GetType().Name;
                }
            }

            if (error == null) error = "движок: нет IPv4-адресов";

            return null;
        }

        private void PumpUp(NetworkStream fromClient, TcpClient upstream, string host)
        {
            NetworkStream toServer = upstream.GetStream();
            byte[] buffer = new byte[32 * 1024];
            bool first = true;

            try
            {
                while (_running)
                {
                    int n = fromClient.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;

                    if (first)
                    {
                        first = false;
                        SendFirst(upstream.Client, buffer, n, host);
                    }
                    else
                    {
                        toServer.Write(buffer, 0, n);
                    }

                    toServer.Flush();
                    Interlocked.Add(ref _bytesUp, n);
                }
            }
            catch { }
            finally
            {
                try { toServer.Close(); } catch { }
            }
        }

        private void PumpDown(NetworkStream fromServer, NetworkStream toClient)
        {
            byte[] buffer = new byte[32 * 1024];

            try
            {
                while (_running)
                {
                    int n = fromServer.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;

                    toClient.Write(buffer, 0, n);
                    toClient.Flush();

                    Interlocked.Add(ref _bytesDown, n);
                }
            }
            catch { }
            finally
            {
                try { toClient.Close(); } catch { }
            }
        }

        /// <summary>
        /// Отправить первый кусок по выбранной технике.
        ///
        /// Всё, что здесь происходит, — обычные `send()` на уже открытом
        /// сокете: никаких raw-сокетов и никакого root. Именно поэтому набор
        /// техник ограничен (см. `DesyncPresets`), но и его хватает, чтобы DPI
        /// не собрал ClientHello целиком.
        /// </summary>
        private void SendFirst(Socket socket, byte[] data, int length, string host)
        {
            bool tls = length >= 2 && data[0] == 0x16 && data[1] == 0x03;

            // Через внешний движок desync делает он сам — здесь только передача.
            if (Config.ForwardPort > 0)
            {
                SendAll(socket, data, 0, length);
                return;
            }

            if (!Config.Enabled || length < 3 || (Config.OnlyTls && !tls))
            {
                SendAll(socket, data, 0, length);
                return;
            }

            int[] cuts = NormalizePositions(Config.Positions, length, data, Config.PositionHint);

            // Фейк — первым, до настоящих кусков. Порядок важен: DPI должен
            // увидеть подставное рукопожатие РАНЬШЕ настоящего, иначе он
            // разберёт настоящее и заблокирует поток.
            if (Config.Fake) SendFake(socket, length, host);

            if (cuts.Length == 0)
            {
                SendAll(socket, data, 0, length);
                return;
            }

            if (Config.Disorder) SendSacrificial(socket, data, cuts[cuts.Length - 1], length);

            int start = 0;
            bool oobDone = false;

            foreach (int cut in cuts)
            {
                if (cut <= start) continue;

                SendAll(socket, data, start, cut - start);

                if (Config.Oob && !oobDone)
                {
                    oobDone = true;

                    try { socket.Send(data, cut, 1, SocketFlags.OutOfBand); }
                    catch { }

                    start = cut + 1;
                }
                else
                {
                    start = cut;
                }

                if (Config.DelayMs > 0) Thread.Sleep(Config.DelayMs);
            }

            if (start < length) SendAll(socket, data, start, length - start);
        }

        /// <summary>
        /// Отправить подставной ClientHello с низким TTL.
        ///
        /// Почему это работает (проверено на живой сети, а не в теории):
        /// DPI стоит в пределах восьми хопов и видит законченное рукопожатие
        /// с безобидным именем — и помечает соединение как проверенное.
        /// Настоящий ClientHello уходит следом и уже не разбирается.
        ///
        /// TTL восстанавливается сразу после отправки: иначе настоящие куски
        /// тоже умрут в пути, и соединение просто не откроется.
        /// </summary>
        private void SendFake(Socket socket, int length, string host)
        {
            try
            {
                // Берём снятый hello ДЛЯ ФЕЙКОВОГО ДОМЕНА и используем как есть.
                //
                // Раньше делалось наоборот: снимался hello целевого домена и
                // SNI в нём подменялся на безобидный с добивкой буквами 'a' до
                // прежней длины. Получалось «www.google.comaaaaaa» — не
                // существующий домен, и DPI такой SNI не считал своим. Фейк
                // уходил, но не срабатывал.
                //
                // Длина при этом не подгоняется: byedpi тоже отправляет фейк
                // фиксированного размера (295 байт) и это не мешает.
                byte[] fake = null;

                if (Config.UseRealHello)
                {
                    fake = HelloCapture.Cached(Config.FakeSni);

                    if (fake == null) HelloCapture.Warm(Config.FakeSni);
                }

                if (fake == null) fake = FakeTls.ClientHello(Config.FakeSni, 0);

                if (fake == null || fake.Length == 0) return;

                Say("фейк: " + fake.Length + " Б, TTL " + Config.FakeTtl +
                    ", SNI " + Config.FakeSni + ", настоящий " + length + " Б");

                int saved = GetTtl(socket);

                try
                {
                    SetTtl(socket, Config.FakeTtl);

                    // Проверяем, что TTL действительно применился. Если ядро
                    // его игнорирует, фейк дойдёт до сервера, тот получит два
                    // рукопожатия подряд и оборвёт соединение — снаружи это
                    // выглядит как «TLS не поднимается» без причины.
                    int applied = GetTtl(socket);

                    if (applied != Config.FakeTtl)
                    {
                        Say("ВНИМАНИЕ: TTL не применился (просили " + Config.FakeTtl +
                            ", стало " + applied + ") — фейк дойдёт до сервера");
                    }

                    SendAll(socket, fake, 0, fake.Length);
                }
                finally
                {
                    SetTtl(socket, saved);
                }

                // Фейк не считается полезной нагрузкой: он не доходит до
                // сервера, и прибавлять его к счётчику «вверх» нечестно.
            }
            catch
            {
                // Не смогли отправить фейк — продолжаем без него: соединение
                // должно открыться хотя бы так, как умеет.
            }
        }

        /// <summary>
        /// Жертвенный пакет для disorder: хвост ClientHello с TTL=1.
        ///
        /// Сервер его не получит — TTL кончится на первом маршрутизаторе. DPI
        /// обычно стоит ближе и увидит, что поток пришёл «не по порядку», после
        /// чего его сборка ClientHello ломается. Настоящие куски уходят следом
        /// с восстановленным TTL.
        /// </summary>
        private static void SendSacrificial(Socket socket, byte[] data, int from, int length)
        {
            if (from >= length) return;

            int saved = 64;

            try
            {
                saved = GetTtl(socket);

                SetTtl(socket, 1);

                try { socket.Send(data, from, length - from, SocketFlags.None); }
                catch { }
            }
            catch { }
            finally
            {
                try { SetTtl(socket, saved); } catch { }
            }
        }

        /// <summary>
        /// Прочитать TTL. Читаем IPv4-значение: цели у нас IPv4 (IPv4 сортируется
        /// первым), а на dual-mode сокете IPv6-значение к делу не относится —
        /// именно из-за этого проверка «TTL применился» раньше врала.
        /// </summary>
        private static int GetTtl(Socket socket)
        {
            try
            {
                return (int)socket.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive);
            }
            catch
            {
            }

            try
            {
                return (int)socket.GetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit);
            }
            catch
            {
            }

            return 64;
        }

        /// <summary>
        /// Поставить TTL.
        ///
        /// ВАЖНО: ставим И IPv4, И IPv6 — как делает byedpi. Сокет создаётся
        /// dual-mode (`new TcpClient()`), и его AddressFamily == InterNetworkV6
        /// даже когда пакет уходит по IPv4. Если выбрать уровень по семейству,
        /// то для IPv4-пакета выставится только IPV6_UNICAST_HOPS, а IP_TTL
        /// останется 64 — фейк дойдёт до сервера, тот получит два рукопожатия
        /// подряд и оборвёт соединение.
        ///
        /// Симптом ровно такой: TCP встаёт за 40 мс, а TLS рвётся, и в логе
        /// «TTL применился», потому что проверка читала то же IPv6-значение.
        /// </summary>
        private static void SetTtl(Socket socket, int ttl)
        {
            if (ttl < 1) ttl = 1;
            if (ttl > 255) ttl = 255;

            bool ok4 = false;
            bool ok6 = false;

            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                ok4 = true;
            }
            catch
            {
            }

            try
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, ttl);
                ok6 = true;
            }
            catch
            {
            }

            if (!ok4 && !ok6) throw new SocketException((int)SocketError.OperationAborted);
        }

        /// <summary>Позиции разреза: только осмысленные, по возрастанию.</summary>
        private static int[] NormalizePositions(int[] positions, int length)
        {
            if (positions == null || positions.Length == 0) return new int[0];

            List<int> valid = new List<int>();

            foreach (int pos in positions)
            {
                if (pos < 1 || pos >= length) continue;
                if (valid.Contains(pos)) continue;

                valid.Add(pos);
            }

            valid.Sort();

            return valid.ToArray();
        }

        /// <summary>
        /// Позиции разреза с учётом смысловых подсказок из конфигов zapret.
        ///
        /// `midsld` — середина домена второго уровня: DPI ищет имя целиком, а
        /// разрез ровно посередине не даёт собрать его даже склейкой соседних
        /// сегментов. `sniext+1` — сразу после расширения SNI: имя остаётся в
        /// первом сегменте, но разорвано от остального рукопожатия.
        ///
        /// Считаем по самому ClientHello: заранее эти числа не известны, они
        /// зависят от длины домена.
        /// </summary>
        private static int[] NormalizePositions(int[] positions, int length, byte[] data, string hint)
        {
            List<int> valid = new List<int>(NormalizePositions(positions, length));

            if (!string.IsNullOrEmpty(hint))
            {
                int computed = ComputeHint(hint, data, length);

                if (computed > 0 && computed < length && !valid.Contains(computed))
                {
                    valid.Add(computed);
                    valid.Sort();
                }
            }

            return valid.ToArray();
        }

        /// <summary>Посчитать позицию по подсказке. 0 — не вышло.</summary>
        private static int ComputeHint(string hint, byte[] data, int length)
        {
            try
            {
                int nameStart, nameLength;

                if (!FindServerName(data, length, out nameStart, out nameLength)) return 0;

                if (hint == "midsld")
                {
                    // Домен второго уровня — последние две метки: example.com.
                    // Середина имени и есть самое ценное место для разреза.
                    return nameStart + nameLength / 2;
                }

                if (hint == "sniext" || hint == "sniext+1" || hint == "host")
                {
                    // Конец расширения server_name: имя целиком остаётся слева.
                    int end = nameStart + nameLength;

                    if (hint == "sniext+1") end++;

                    return end;
                }
            }
            catch
            {
            }

            return 0;
        }

        /// <summary>
        /// Найти имя хоста в ClientHello. Разбор по структуре RFC 8446, а не
        /// поиском подстроки: имя может случайно совпасть с байтами в другом
        /// расширении.
        /// </summary>
        private static bool FindServerName(byte[] data, int length, out int offset, out int size)
        {
            offset = 0;
            size = 0;

            if (data == null || length < 44) return false;

            int p = 5;                                  // заголовок записи
            if (p + 4 > length) return false;
            if (data[p] != 0x01) return false;          // ClientHello

            p += 4;                                     // тип + длина
            p += 2;                                     // legacy_version
            p += 32;                                    // random

            if (p >= length) return false;

            int sessionId = data[p];
            p += 1 + sessionId;

            if (p + 2 > length) return false;

            int cipherLength = (data[p] << 8) | data[p + 1];
            p += 2 + cipherLength;

            if (p >= length) return false;

            int compression = data[p];
            p += 1 + compression;

            if (p + 2 > length) return false;

            int extensionsLength = (data[p] << 8) | data[p + 1];
            p += 2;

            int end = Math.Min(p + extensionsLength, length);

            while (p + 4 <= end)
            {
                int type = (data[p] << 8) | data[p + 1];
                int extSize = (data[p + 2] << 8) | data[p + 3];

                p += 4;

                if (type == 0x0000)
                {
                    if (p + 5 > length) return false;

                    int listLength = (data[p] << 8) | data[p + 1];
                    int q = p + 2;
                    int listEnd = Math.Min(q + listLength, length);

                    while (q + 3 <= listEnd)
                    {
                        int nameType = data[q];
                        int nameLength = (data[q + 1] << 8) | data[q + 2];

                        q += 3;

                        if (nameType == 0 && q + nameLength <= length)
                        {
                            offset = q;
                            size = nameLength;

                            return nameLength > 0;
                        }

                        q += nameLength;
                    }

                    return false;
                }

                p += extSize;
            }

            return false;
        }

        /// <summary>Отправить весь кусок: `send` может отправить меньше.</summary>
        private static void SendAll(Socket socket, byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                int sent = socket.Send(data, offset, count, SocketFlags.None);
                if (sent <= 0) return;

                offset += sent;
                count -= sent;
            }
        }

        private void Say(string message)
        {
            Action<string> handler = Log;
            if (handler == null) return;

            try { handler(message); } catch { }
        }
    }
}