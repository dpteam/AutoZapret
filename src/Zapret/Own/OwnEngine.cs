using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Zapret.Own
{
    /// <summary>Настройки своего движка.</summary>
    public sealed class OwnEngineOptions
    {
        /// <summary>Какая техника применяется к ClientHello.</summary>
        public TechniqueOptions Technique = new TechniqueOptions();

        /// <summary>Сколько секунд работать (0 — пока не отменят).</summary>
        public int Seconds = 0;

        /// <summary>Ничего не отправлять и не отбрасывать: только показать, что бы сделали.</summary>
        public bool DryRun;

        /// <summary>Дополнительные порты не нужны: первый шаг — только TCP/443.</summary>
        public int Port = 443;

        /// <summary>
        /// Что трогать. null — трогать всё (режим проверки техники).
        /// Задан — работаем как winws: только цели из списков.
        /// </summary>
        public OwnFilter Filter;

        /// <summary>
        /// Набор профилей: разные техники для разных целей, как `--new` у winws.
        /// null — один профиль из Technique (режим проверки).
        /// </summary>
        public OwnProfileSet Profiles;

        /// <summary>Порт назначения для UDP (QUIC). Обычно тот же 443.</summary>
        public int UdpPort = 443;
    }

    /// <summary>Что движок сделал — для лога и отчёта.</summary>
    public sealed class OwnEngineStats
    {
        public int Packets;
        public int ClientHellos;
        public int Applied;
        public int Errors;

        /// <summary>Сколько соединений отброшено фильтром (не наши цели).</summary>
        public int Skipped;

        public string Summary()
        {
            return "пакетов " + Packets + ", ClientHello " + ClientHellos +
                   ", применено " + Applied +
                   (Skipped > 0 ? ", пропущено " + Skipped : "") +
                   ", ошибок " + Errors;
        }
    }

    /// <summary>
    /// Первый шаг своего движка (ROADMAP P10.3): перехват TCP/443 и техника
    /// `split` — ClientHello режется на две части с правильными seq.
    ///
    /// Почему именно split: это самая простая техника, которую DPI не умеет
    /// собирать, и на ней видно, работает ли вообще наш конвейер — перехват,
    /// правка байтов, пересчёт сумм, отправка, отбрасывание оригинала.
    /// Если здесь всё сходится, остальные техники (fake, multisplit, hostfakesplit)
    /// добавляются как отдельные правила, а не как новый движок.
    ///
    /// Безопасность важнее возможностей: любая ошибка разбора или отправки
    /// заканчивается тем, что пакет уходит НЕИЗМЕНЁННЫМ. Движок не должен
    /// уметь «сломать интернет» — этим он и отличается от самодельных фильтров.
    /// </summary>
    public sealed class OwnEngine
    {
        private readonly OwnEngineOptions _options;
        private readonly OwnEngineStats _stats = new OwnEngineStats();

        public OwnEngine(OwnEngineOptions options)
        {
            _options = options ?? new OwnEngineOptions();
        }

        public OwnEngineStats Stats { get { return _stats; } }

        /// <summary>
        /// Запустить. Возвращает null при успехе, иначе текст ошибки.
        ///
        /// onStarted вызывается, когда драйвер уже открыт и перехват пошёл.
        /// Нужен тому, кто запускает движок в отдельном потоке и обязан
        /// отличить «уже работает» от «сейчас упадёт на занятом драйвере»:
        /// Run() блокируется до остановки, и без сигнала вызывающий не узнает,
        /// что старт удался, иначе как по таймауту.
        /// </summary>
        public string Run(CancellationToken cancel = default(CancellationToken),
                          Action onStarted = null)
        {
            if (!WinDivertInterop.IsAvailable())
                return "WinDivert.dll не найден в " + Paths.EngineBinDir;

            // Фильтр WinDivert — не оптимизация, а условие работы: пакет,
            // который ядро не отдало, мы не увидим никогда. Поэтому порты
            // собираются со ВСЕХ профилей, а не только с основного 443:
            // у discord это 2053/2083/2087/2096/8443, и профиль молча не
            // сработал бы, если бы фильтр остался на 443.
            string filter = BuildFilter();

            if (filter.Length == 0)
                return "нет ни одного порта для перехвата: проверьте профили";

            IntPtr handle;

            try
            {
                handle = WinDivertInterop.WinDivertOpen(
                    filter, WinDivertInterop.LayerNetwork, 0, 0);
            }
            catch (Exception ex)
            {
                return "WinDivert не открылся: " + ex.GetType().Name + ": " + ex.Message;
            }

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();

                return error == 5
                    ? "нужны права администратора (WinDivert код 5)"
                    : "WinDivert не открылся (код " + error + "): драйвер занят другим перехватчиком";
            }

            Log.Info("OWN", "движок запущен: " +
                            (_options.Profiles != null && _options.Profiles.Count > 0
                                ? "профили: " + _options.Profiles.Describe()
                                : _options.Technique.Describe()) +
                            (_options.DryRun ? " (только показ)" : "") + ", фильтр: " + filter);

            // Драйвер открыт и перехват пошёл — только теперь старт можно
            // считать состоявшимся.
            if (onStarted != null)
            {
                try { onStarted(); }
                catch (Exception ex) { Log.Warn("OWN", "сигнал о старте: " + ex.Message); }
            }

            ManualResetEvent stop = new ManualResetEvent(false);

            try
            {
                // приём в отдельном потоке: WinDivertRecv блокируется до пакета,
                // и без этого остановка по времени не сработала бы
                Thread reader = new Thread(delegate () { Loop(handle, stop, cancel); })
                {
                    IsBackground = true,
                    Name = "own-engine"
                };

                reader.Start();

                // Пульс раз в 30 секунд.
                //
                // Без него «в логе тишина» неотличима от «движок перестал
                // получать пакеты»: при нуле применений движок молчит по
                // построению, и человек видит «не логируется» вместо ответа
                // на вопрос «он вообще жив?». Теперь видно и число пакетов,
                // и сколько из них применено.
                Thread heartbeat = new Thread(delegate ()
                {
                    while (!stop.WaitOne(30000))
                        Log.Info("OWN", "пульс: " + _stats.Summary());
                })
                {
                    IsBackground = true,
                    Name = "own-heartbeat"
                };

                heartbeat.Start();

                if (_options.Seconds > 0)
                {
                    WaitHandle.WaitAny(new[] { cancel.WaitHandle }, _options.Seconds * 1000);
                }
                else
                {
                    WaitHandle.WaitAny(new[] { cancel.WaitHandle });
                }

                stop.Set();
                WinDivertInterop.WinDivertClose(handle);
                reader.Join(1500);

                return null;
            }
            finally
            {
                try { WinDivertInterop.WinDivertClose(handle); } catch { }

                Log.Info("OWN", "движок остановлен: " + _stats.Summary());
            }
        }

        private void Loop(IntPtr handle, ManualResetEvent stop, CancellationToken cancel)
        {
            byte[] packet = new byte[WinDivertInterop.MaxPacketSize];
            byte[] address = new byte[WinDivertInterop.AddressSize];

            IntPtr addressPtr = Marshal.AllocHGlobal(WinDivertInterop.AddressSize);

            try
            {
                while (!stop.WaitOne(0) && !cancel.IsCancellationRequested)
                {
                    uint recvLen;

                    if (!WinDivertInterop.WinDivertRecv(handle, packet, (uint)packet.Length, out recvLen, addressPtr))
                    {
                        int error = Marshal.GetLastWin32Error();

                        // 6 = хэндл закрыт нами по времени, 995 = прервано — штатно
                        if (error == 6 || error == 995)
                        {
                            Log.Info("OWN", "приём остановлен: хэндл закрыт (" + error + ")");
                            break;
                        }
                        if (error == WinDivertInterop.ErrorTimeout || error == WinDivertInterop.ErrorNoData)
                        {
                            Thread.Sleep(20);
                            continue;
                        }

                        _stats.Errors++;
                        Log.Warn("OWN", "ошибка приёма: " + error);
                        break;
                    }

                    _stats.Packets++;

                    if (!Handle(handle, addressPtr, packet, (int)recvLen, address))
                    {
                        // не смогли обработать — отдаём пакет как есть, иначе рвём трафик
                        if (!Reinject(handle, addressPtr, packet, (int)recvLen)) _stats.Errors++;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(addressPtr);
            }
        }

        /// <summary>
        /// Разобрать и, если это ClientHello, применить технику.
        /// false — «ничего не делал, отправьте пакет как есть».
        ///
        /// Все техники живут в Desync: здесь только перехват, отправка и
        /// отбрасывание оригинала. Разделение важное — иначе каждая новая
        /// техника лезла бы в цикл приёма пакетов, где ошибка стоит дорого.
        /// </summary>
        private bool Handle(IntPtr handle, IntPtr addressPtr, byte[] packet, int length, byte[] address)
        {
            // UDP и TCP — разные ветки целиком: у них разная проверка «наше ли
            // это», разные техники и разные поля. Одна общая функция с ветвлением
            // внутри стала бы нечитаемой и опасной в горячем пути.
            //
            // Протокол смотрим ДО разбора TCP: TcpPacket.Parse на UDP вернёт
            // null, и без этой проверки UDP-профиль молча не сработал бы —
            // «профиль есть, техника выбрана, обхода нет».
            //
            // IPv6 поддерживается для UDP: Discord нередко уводит медиа в v6,
            // а фильтр WinDivert ловит его наравне с v4. Раньше такие пакеты
            // отбрасывались на первой же проверке версии — и голос с экраном
            // оставались без обхода на сетях с IPv6. TCP-ветка по-прежнему
            // только IPv4 (см. TcpPacket).
            if (length < 20) return false;

            int version = (packet[0] >> 4) & 0x0F;
            if (version != 4 && version != 6) return false;

            byte protocol = version == 4 ? packet[9] : packet[6];

            bool udpWanted = _options.Profiles != null
                ? _options.Profiles.HasUdp
                : (_options.Technique != null && _options.Technique.Kind == Technique.UdpLen);

            if (protocol == 17) return udpWanted && HandleUdp(handle, addressPtr, packet, length);

            if (protocol != 6) return false;

            TcpPacket tcp = TcpPacket.Parse(packet, length);

            if (tcp == null || !tcp.HasPayload) return false;

            // Какую технику применять: решает профиль, а не глобальная настройка.
            // Первый подходящий выигрывает — порядок профилей и есть приоритет.
            //
            // Профиль выбирается ДО проверки «это TLS?»: у профиля может стоять
            // `--dpi-desync-any-protocol`, и тогда решение зависит от профиля,
            // а не от наших догадок о содержимом.
            bool isTls = packet[tcp.PayloadOffset] == 0x16 &&
                         tcp.PayloadLength >= 5 &&
                         packet[tcp.PayloadOffset + 1] == 0x03;

            string sni = isTls
                ? Payloads.PayloadValidator.ExtractSni(Payload(tcp), Payloads.PayloadKind.TlsClientHello)
                : "";

            TechniqueOptions technique = _options.Technique;

            if (_options.Profiles != null && _options.Profiles.Count > 0)
            {
                OwnProfile profile = _options.Profiles.MatchTcp(
                    tcp.DstPort, sni, tcp.DestinationAddress());

                if (profile == null)
                {
                    _stats.Skipped++;

                    // Почему пропустили — в лог, но не бесконечно.
                    //
                    // «пропущено 67» без имён бесполезно: непонятно, это чужой
                    // трафик (правильно пропустили) или наши цели, которых нет
                    // ни в hostlist, ни в ipset (и тогда обхода для них нет
                    // вовсе). Первые двадцать показывают, что именно.
                    LogSkippedTcp(tcp.DstPort, sni, tcp.DestinationAddress());

                    return false;   // не наш трафик — уходит как есть
                }

                // Не TLS и профиль этого не разрешал: трогать нечего. Без этой
                // проверки мы бы «резали» случайные данные и ломали соединения,
                // которые работали.
                if (!isTls && (profile.Technique == null || !profile.Technique.AnyProtocol))
                {
                    _stats.Skipped++;
                    return false;
                }

                profile.Applied++;
                technique = profile.Technique;
            }
            else
            {
                // Без профилей — только TLS: это режим проверки техники.
                if (!isTls) return false;

                if (_options.Filter != null &&
                    !_options.Filter.ShouldDesync(sni, tcp.DestinationAddress()))
                {
                    _stats.Skipped++;
                    return false;
                }
            }

            _stats.ClientHellos++;

            // Ретрансмиссия того же ClientHello — не повод резать его второй раз.
            //
            // В живом логе один и тот же seq разрезался дважды подряд, и это
            // не «дубль в логе»: вторые копии уходили в сеть с теми же seq,
            // сервер получал лишние сегменты, а соединение с медиасервером
            // Discord иногда не поднималось (ошибка 2012). Повторная отправка
            // того же пакета означает, что первые куски не дошли — пусть
            // ретрансмиссия уйдёт как есть, целой.
            if (!AllowTcpDesync(tcp)) return false;

            if (technique == null) return false;

            string error;
            List<Desync.Outgoing> outgoing = Desync.Build(tcp, technique, out error);

            if (outgoing.Count == 0)
            {
                // Техника не сработала (нет фейка, пустой payload) — пакет уйдёт
                // как есть. Тихо ломать соединение мы не имеем права.
                if (error.Length > 0 && _stats.Errors < 5)
                    Log.Warn("OWN", "техника не применилась (" + error + "), пакет уходит как есть");

                return false;
            }

            if (_options.DryRun)
            {
                Log.Info("OWN", "сделал бы " + tcp.SrcPort + "->" + tcp.DstPort + ": " +
                                technique.Describe() + " → " +
                                outgoing.Count + " пакет(ов), payload " + tcp.PayloadLength);

                foreach (Desync.Outgoing o in outgoing)
                    Log.Info("OWN", "   " + o.Note + ", " + o.Length + " байт");

                return false;   // в режиме показа пакет уходит как есть
            }

            int sent = 0;

            foreach (Desync.Outgoing o in outgoing)
            {
                if (!Send(handle, addressPtr, o.Data, o.Length))
                {
                    _stats.Errors++;

                    // Если не ушёл хоть один — не отбрасываем оригинал: лучше
                    // отправить ClientHello целиком, чем оставить соединение
                    // без рукопожатия вовсе.
                    if (sent == 0) return false;

                    break;
                }

                sent++;
            }

            if (sent == 0) return false;

            _stats.Applied++;

            if (_stats.Applied <= 20 || _stats.Applied % 100 == 0)
            {
                Log.Info("OWN", technique.Describe() + " " +
                                tcp.SrcPort + "->" + tcp.DstPort + ": " +
                                tcp.PayloadLength + " байт → " + sent + " пакет(ов), seq " + tcp.Seq);
            }

            // Оригинал НЕ отправляем: он уже отправлен частями или после фейка.
            return true;
        }

        /// <summary>
        /// Фильтр WinDivert по всем портам, которые кто-то собирается ловить.
        ///
        /// Пустая строка — ловить нечего. Это лучше, чем открыть перехват
        /// «на всё»: движок без целей, который трогает весь трафик, — это не
        /// обход, а поломка интернета.
        /// </summary>
        private string BuildFilter()
        {
            string inner;

            if (_options.Profiles != null && _options.Profiles.Count > 0)
            {
                // Сужение до ClientHello решается ПОРТАМИ внутри набора:
                // профили с any-protocol получают широкое условие только на
                // своих портах, остальные — байтовую проверку.
                inner = _options.Profiles.FilterExpression(true);
            }
            else
            {
                bool udpMode = _options.Technique != null && _options.Technique.Kind == Technique.UdpLen;

                // Без профилей — только ClientHello, если это не UDP-режим и не
                // any-protocol. Тот же принцип, что и у профилей: ловим ровно
                // один пакет на соединение, а не весь исходящий поток.
                bool anyProtocol = _options.Technique != null && _options.Technique.AnyProtocol;

                inner = udpMode
                    ? "udp.DstPort == " + _options.Port + " and udp.PayloadLength >= 20"
                    : (anyProtocol
                        ? "tcp.DstPort == " + _options.Port + " and tcp.PayloadLength > 40"
                        : "tcp.DstPort == " + _options.Port +
                          " and tcp.PayloadLength >= 6 and tcp.Payload[0] == 0x16 and " +
                          "tcp.Payload[1] == 0x03 and tcp.Payload[5] == 0x01");
            }

            if (inner.Length == 0) return "";

            return "outbound and (" + inner + ")";
        }

        // ------------------------------------------------- фейки в UDP-потоке

        private readonly object _flowGate = new object();

        private sealed class UdpFlowState
        {
            public DateTime Last;
            public DateTime WindowStart;
            public int Fakes;
        }

        private readonly Dictionary<string, UdpFlowState> _udpFlows =
            new Dictionary<string, UdpFlowState>();

        /// <summary>Сколько помним поток, прежде чем снова считать его новым.</summary>
        private const int UdpFlowSeconds = 30;

        /// <summary>
        /// Почему именно этот пакет не узнан — в лог, но не бесконечно.
        ///
        /// Первые десять показывают, что за протокол идёт на порту профиля.
        /// Дальше молчим: иначе один медиапоток утопит лог.
        /// </summary>
        private void LogUnrecognizedUdp(UdpPacket udp, byte[] packet)
        {
            int n = System.Threading.Interlocked.Increment(ref _unrecognizedUdp);

            if (n > 10) return;

            int count = Math.Min(8, udp.PayloadLength);
            string head = "";

            for (int i = 0; i < count; i++)
            {
                if (i > 0) head += " ";
                head += packet[udp.PayloadOffset + i].ToString("x2");
            }

            Log.Info("OWN", "udp не распознан: порт " + udp.DstPort + ", " +
                            udp.PayloadLength + " байт, начало: " + head +
                            (n == 10 ? " (дальше молчу)" : ""));
        }

        private int _unrecognizedUdp;

        private int _skippedTcp;

        /// <summary>Какие ClientHello остались без профиля (первые двадцать).</summary>
        private void LogSkippedTcp(int port, string sni, System.Net.IPAddress address)
        {
            int n = System.Threading.Interlocked.Increment(ref _skippedTcp);

            if (n > 20) return;

            string target = string.IsNullOrEmpty(sni)
                ? (address == null ? "?" : address.ToString())
                : sni;

            Log.Info("OWN", "пропущено (нет профиля): " + target + ":" + port +
                            (n == 20 ? " (дальше молчу)" : ""));
        }

        /// <summary>Можно ли применять технику к этому TCP-пакету.</summary>
        private bool AllowTcpDesync(TcpPacket tcp)
        {
            string key = tcp.SrcPort + ">" + tcp.DstPort + "@" + tcp.Seq;

            DateTime now = DateTime.UtcNow;

            lock (_flowGate)
            {
                TcpDesyncState state;

                if (!_tcpDesynced.TryGetValue(key, out state) ||
                    (now - state.Last).TotalSeconds >= UdpFlowSeconds)
                {
                    state = new TcpDesyncState();
                    _tcpDesynced[key] = state;
                }

                state.Last = now;

                // Две попытки, а не «одна навсегда» и не «бесконечно».
                //
                // Одна попытка опасна: если разрезанные куски не дошли, любая
                // ретрансмиссия уходила бы ЦЕЛЫМ ClientHello — ровно то, что
                // DPI и ждёт. Бесконечно — тоже плохо: сервер получает всё
                // новые копии с тем же seq. Две попытки закрывают оба случая.
                if (state.Count >= 2) return false;

                if (_tcpDesynced.Count > 4096)
                {
                    List<string> dead = new List<string>();

                    foreach (KeyValuePair<string, TcpDesyncState> pair in _tcpDesynced)
                        if ((now - pair.Value.Last).TotalSeconds >= UdpFlowSeconds) dead.Add(pair.Key);

                    foreach (string old in dead) _tcpDesynced.Remove(old);

                    if (_tcpDesynced.Count > 8192) _tcpDesynced.Clear();
                }

                state.Count++;
                return true;
            }
        }

        private sealed class TcpDesyncState
        {
            public DateTime Last;
            public int Count;
        }

        private readonly Dictionary<string, TcpDesyncState> _tcpDesynced =
            new Dictionary<string, TcpDesyncState>();

        /// <summary>
        /// Сколько фейков максимум на один поток за окно.
        ///
        /// Не «один», как было раньше. Discord пересогласовывает DTLS прямо
        /// во время звонка (переинициализация, смена ключей, ICE restart), и
        /// каждый такой ClientHello — новый пакет, который DPI тоже проверяет.
        /// Старое правило «фейк только первому пакету типа» оставляло все
        /// последующие рукопожатия без обхода: звук начинал рваться, а показ
        /// экрана пропадал посреди сеанса.
        ///
        /// 6 за окно — с большим запасом: за 30 секунд реальных рукопожатий
        /// бывает два-три, а случайный мусор не превращается в поток фейков.
        /// </summary>
        private const int MaxUdpFakesPerFlow = 6;

        /// <summary>
        /// Можно ли отправить фейк перед этим пакетом.
        ///
        /// `repeatable` — пакет сам по себе является началом рукопожатия
        /// (DTLS ClientHello, STUN Binding Request). Такие повторяются в
        /// течение звонка, и каждый должен получить обход.
        ///
        /// Для QUIC и прочего — по-прежнему только первый пакет потока:
        /// угадать «ещё одно рукопожатие» там нельзя, а фейк в середине
        /// медиапотока ломает соединение.
        ///
        /// `kind` входит в ключ, потому что у одного пятикортежа типов бывает
        /// несколько: голос начинается со STUN, показ экрана — с DTLS.
        /// </summary>
        private bool AllowUdpFake(UdpPacket udp, string kind, bool repeatable)
        {
            string key = udp.SrcPort + ">" + udp.DstPort + "@" +
                         (UdpDestination(udp) == null ? "?" : UdpDestination(udp).ToString()) +
                         "/" + kind;

            DateTime now = DateTime.UtcNow;

            lock (_flowGate)
            {
                UdpFlowState state;

                if (!_udpFlows.TryGetValue(key, out state) ||
                    (now - state.Last).TotalSeconds >= UdpFlowSeconds)
                {
                    state = new UdpFlowState { Last = now, WindowStart = now, Fakes = 0 };
                    _udpFlows[key] = state;
                }

                state.Last = now;

                // Бюджет — скользящее окно от ПЕРВОГО фейка, а не от последнего
                // пакета.
                //
                // Иначе при непрерывном медиапотоке (пакеты идут постоянно)
                // счётчик не сбрасывался никогда: шесть фейков за звонок — и
                // всё, дальнейшие DTLS ClientHello оставались без обхода,
                // поэтому показ экрана отваливался через несколько минут.
                if ((now - state.WindowStart).TotalSeconds >= UdpFlowSeconds)
                {
                    state.WindowStart = now;
                    state.Fakes = 0;
                }

                if (!repeatable && state.Fakes > 0) return false;

                if (state.Fakes >= MaxUdpFakesPerFlow)
                {
                    int n = System.Threading.Interlocked.Increment(ref _budgetDenied);

                    if (n <= 5)
                        Log.Warn("OWN", "бюджет фейков исчерпан (" + kind + ", " +
                                        state.Fakes + " за " + UdpFlowSeconds + " с) — " +
                                        "пакет уходит без обхода" + (n == 5 ? " (дальше молчу)" : ""));

                    return false;
                }

                state.Fakes++;
                return true;
            }
        }

        private int _budgetDenied;

        /// <summary>Подчистить старые потоки, чтобы словарь не рос без предела.</summary>
        private void PruneUdpFlows(DateTime now)
        {
            lock (_flowGate)
            {
                if (_udpFlows.Count <= 4096) return;

                List<string> dead = new List<string>();

                foreach (KeyValuePair<string, UdpFlowState> pair in _udpFlows)
                    if ((now - pair.Value.Last).TotalSeconds >= UdpFlowSeconds) dead.Add(pair.Key);

                foreach (string old in dead) _udpFlows.Remove(old);

                // всё ещё много — значит потоки текут быстрее, чем стареют:
                // тогда просто начинаем с чистого листа, это не состояние,
                // а подсказка «уже трогали»
                if (_udpFlows.Count > 8192) _udpFlows.Clear();
            }
        }

        /// <summary>
        /// Похоже ли на QUIC Initial.
        ///
        /// Старшие ДВА бита первого байта равны 11 (long header form), а тип —
        /// 00 (Initial). Именно 0xC0, а не 0x80: с 0x80 проходил любой RTP,
        /// у которого первый байт 0x80 (версия 2). Из-за этого движок принимал
        /// медиапоток Discord за QUIC и подсовывал в него STUN-фейки — голос
        /// от этого и не поднимался.
        /// </summary>
        public static bool LooksLikeQuic(byte[] packet, int offset, int length)
        {
            if (packet == null || length < 20) return false;
            if (offset < 0 || offset + 20 > packet.Length) return false;

            byte first = packet[offset];

            return (first & 0xC0) == 0xC0 && (first & 0x30) == 0;
        }

        /// <summary>Похоже ли на STUN (RFC 5389).</summary>
        public static bool LooksLikeStun(byte[] packet, int offset, int length)
        {
            return IsStun(packet, offset, length);
        }

        private static bool IsStun(byte[] packet, int offset, int payloadLength)
        {
            if (payloadLength < 20) return false;
            if (offset < 0 || offset + 20 > packet.Length) return false;

            // старшие два бита типа всегда 0, младшие — один из запросов
            if ((packet[offset] & 0xC0) != 0) return false;
            if (packet[offset + 1] > 0x04) return false;

            return packet[offset + 4] == 0x21 &&
                   packet[offset + 5] == 0x12 &&
                   packet[offset + 6] == 0xA4 &&
                   packet[offset + 7] == 0x42;
        }

        /// <summary>
        /// DTLS-запись: 0x16 (handshake) и версия 0xFE.. (1.0 / 1.2 / 1.3).
        ///
        /// Зачем отдельная проверка. Демонстрация экрана в Discord идёт
        /// собственным медиапотоком, и первым пакетом там уходит DTLS
        /// ClientHello, а не STUN. Раньше UDP-ветка узнавала только QUIC и
        /// STUN — DTLS молча не считался «нашим», фейк не отправлялся, и
        /// показ экрана не поднимался, хотя голос работал: у голоса первый
        /// пакет как раз STUN.
        ///
        /// Проверяем ровно сигнатуру записи, без угадываний: 0x16 и старший
        /// байт версии 0xFE. TLS-ClientHello (0x16 0x03) сюда не попадает —
        /// это TCP, и у него своя ветка.
        /// </summary>
        internal static bool IsDtls(byte[] packet, int offset, int payloadLength)
        {
            if (payloadLength < 13) return false;
            if (offset < 0 || offset + 13 > packet.Length) return false;

            return packet[offset] == 0x16 && packet[offset + 1] == 0xFE;
        }

        /// <summary>
        /// DTLS ClientHello: запись рукопожатия и тип 0x01 в первом байте
        /// handshake-сообщения (он идёт сразу за 13-байтовым заголовком записи).
        ///
        /// Только ClientHello, а не любая DTLS-запись: ChangeCipherSpec и
        /// Finished идут уже внутри защищённого канала, фейк перед ними ничего
        /// не даёт и только добавляет мусор в поток.
        /// </summary>
        internal static bool IsDtlsClientHello(byte[] packet, int offset, int payloadLength)
        {
            if (payloadLength < 14) return false;
            if (offset < 0 || offset + 14 > packet.Length) return false;

            return packet[offset] == 0x16 &&
                   packet[offset + 1] == 0xFE &&
                   packet[offset + 13] == 0x01;
        }

        /// <summary>
        /// Discord IP discovery в формате bencode: payload начинается с
        /// `d<цифра>:` — например `d1:ad2:i…` или `d2:ip6:…`.
        ///
        /// Именно этот пакет Discord шлёт ПЕРВЫМ на порт медиасервера, и
        /// именно его winws ловит своим классификатором `discord_ip_discovery`
        /// (он так и назван в zapret2-стратегии). Мы его не узнавали, поэтому
        /// обход для голоса и показа экрана не включался вовсе: в логе было
        /// «udp не распознан: порт 50000, начало: 64 31 3a 61 64 32 3a 69»
        /// (`d1:ad2:i`).
        /// </summary>
        internal static bool LooksLikeDiscordDiscovery(byte[] packet, int offset, int payloadLength)
        {
            if (payloadLength < 5) return false;
            if (offset < 0 || offset + 5 > packet.Length) return false;

            return packet[offset] == (byte)'d' &&
                   packet[offset + 1] >= (byte)'0' && packet[offset + 1] <= (byte)'9' &&
                   packet[offset + 2] == (byte)':' &&
                   ((packet[offset + 3] >= (byte)'a' && packet[offset + 3] <= (byte)'z') ||
                    (packet[offset + 3] >= (byte)'A' && packet[offset + 3] <= (byte)'Z'));
        }

        /// <summary>
        /// STUN Binding Request (тип 0x0001) — единственный запрос, на который
        /// имеет смысл подсовывать фейк. Ответы и прочие типы (allocate,
        /// refresh) идут после и к обходу отношения не имеют.
        /// </summary>
        internal static bool IsStunRequest(byte[] packet, int offset, int payloadLength)
        {
            if (!IsStun(packet, offset, payloadLength)) return false;

            return packet[offset] == 0x00 && packet[offset + 1] == 0x01;
        }

        /// <summary>
        /// UDP-ветка: `udplen` (QUIC) и `fake` (STUN/DTLS/QUIC/игры).
        ///
        /// Проверка «наше ли это» проще, чем у TLS, но не «любой UDP»:
        ///   * QUIC Initial — long header form (старшие два бита 11) и тип 0;
        ///   * STUN — magic cookie 0x2112A442 в байтах 4..7, тип 0x0000..0x0004;
        ///   * DTLS — запись 0x16 с версией 0xFE (медиа и показ экрана Discord).
        /// Голосовой Discord идёт STUN-ом, показ экрана — DTLS. Без второй
        /// проверки экран не обходился: профиль есть, техника выбрана, а пакет
        /// не узнан.
        /// </summary>
        private bool HandleUdp(IntPtr handle, IntPtr addressPtr, byte[] packet, int length)
        {
            UdpPacket udp = UdpPacket.Parse(packet, length);

            if (udp == null || !udp.HasPayload) return false;
            if (udp.PayloadLength < 20) return false;

            // QUIC long header: старшие ДВА бита 11, тип 00 (Initial).
            // Подробности — в LooksLikeQuic: именно здесь ломался голос.
            bool quic = LooksLikeQuic(packet, udp.PayloadOffset, udp.PayloadLength);

            bool stun = IsStun(packet, udp.PayloadOffset, udp.PayloadLength);

            bool dtls = IsDtls(packet, udp.PayloadOffset, udp.PayloadLength);

            bool discord = LooksLikeDiscordDiscovery(packet, udp.PayloadOffset, udp.PayloadLength);

            if (!quic && !stun && !dtls && !discord)
            {
                // Диагностика: пакет пришёл на порт профиля, но протокол не
                // узнан. Раньше такие уходили молча, и по логу нельзя было
                // понять, почему медиа осталось без обхода. Первые байты
                // называют протокол лучше любого описания.
                LogUnrecognizedUdp(udp, packet);

                return false;
            }

            // Отличаем НАЧАЛО рукопожатия от всего остального.
            //
            // Именно на этих двух типах DPI проверяет соединение, и именно они
            // повторяются в течение звонка (пересогласование DTLS, ICE restart).
            // Раньше фейк отправлялся только первому пакету типа за 30 секунд —
            // и повторное рукопожатие проходило без обхода: звук рвался, показ
            // экрана пропадал на середине сеанса.
            bool dtlsHello = dtls && IsDtlsClientHello(packet, udp.PayloadOffset, udp.PayloadLength);
            bool stunRequest = stun && IsStunRequest(packet, udp.PayloadOffset, udp.PayloadLength);

            _stats.ClientHellos++;    // для UDP это первый пакет соединения

            // Какую технику применять — решает профиль (если он есть).
            // У QUIC SNI зашифрован, поэтому профиль выбирается по порту и
            // подсетям: домены здесь не помогут.
            TechniqueOptions technique = _options.Technique;

            if (_options.Profiles != null && _options.Profiles.Count > 0)
            {
                OwnProfile profile = _options.Profiles.MatchUdp(udp.DstPort, UdpDestination(udp));

                if (profile == null)
                {
                    _stats.Skipped++;
                    return false;
                }

                profile.Applied++;
                technique = profile.Technique;
            }
            else if (_options.Filter != null)
            {
                // Старый режим: общий фильтр. У QUIC нет SNI в открытом виде,
                // поэтому решение только по подсетям. Если в фильтре есть
                // ТОЛЬКО домены, UDP не трогаем вовсе — иначе обойдём всё подряд.
                System.Net.IPAddress destination = UdpDestination(udp);

                bool domainOnly = _options.Filter.HostCount > 0 && _options.Filter.IpCount == 0;

                if (domainOnly || !_options.Filter.ShouldDesync("", destination))
                {
                    _stats.Skipped++;
                    return false;
                }
            }

            // Профиль может быть TCP-only: тогда UDP трогать нечем.
            if (technique == null) return false;
            if (technique.Kind != Technique.UdpLen && technique.Kind != Technique.Fake) return false;

            // Только осмысленные пакеты и не чаще бюджета.
            //
            // У TCP рамки задаёт ClientHello: он один на соединение, и техника
            // применяется к нему. У UDP якоря нет, поэтому:
            //
            //   * DTLS ClientHello — можно повторять: Discord пересогласовывает
            //     DTLS во время звонка, и каждый новый ClientHello DPI проверяет
            //     заново;
            //   * Discord IP discovery (`d1:ad2:i…`) — первый пакет потока,
            //     один раз: именно его ждёт DPI и именно его ловит winws
            //     классификатором `discord_ip_discovery`;
            //   * STUN Binding Request — ТОЛЬКО первый на поток. Это была
            //     ошибка: тот же тип 0x0001 используется для consent freshness
            //     каждые ~5 секунд, то есть в установленном медиапотоке. Фейк
            //     перед каждым таким запросом — это мусор посреди звонка, он
            //     ещё и съедал бюджет, из-за чего следующему DTLS ClientHello
            //     фейка уже не доставалось (показ экрана не поднимался);
            //   * всё остальное (QUIC, прочий STUN) — тоже только первый пакет.
            string kind = dtlsHello ? "dtls-hello"
                        : dtls ? "dtls"
                        : discord ? "discord-discovery"
                        : stunRequest ? "stun-req"
                        : stun ? "stun"
                        : "quic";

            PruneUdpFlows(DateTime.UtcNow);

            if (!AllowUdpFake(udp, kind, dtlsHello)) return false;

            // `fake` для UDP — это поддельный payload, а не «сдвиг seq»:
            // в UDP его нет. `udplen` — игра с полем длины.
            string error;
            List<Desync.Outgoing> outgoing = Desync.BuildUdp(udp, technique, out error);

            if (outgoing.Count == 0)
            {
                if (error.Length > 0 && _stats.Errors < 5)
                    Log.Warn("OWN", "UDP-техника не применилась (" + error + "), пакет уходит как есть");

                return false;
            }

            if (_options.DryRun)
            {
                Log.Info("OWN", "сделал бы udp " + udp.SrcPort + "->" + udp.DstPort + ": " +
                                outgoing.Count + " пакет(ов), payload " + udp.PayloadLength);

                foreach (Desync.Outgoing o in outgoing)
                    Log.Info("OWN", "   " + o.Note + ", " + o.Length + " байт");

                return false;
            }

            int sent = 0;

            foreach (Desync.Outgoing o in outgoing)
            {
                if (!Send(handle, addressPtr, o.Data, o.Length))
                {
                    _stats.Errors++;
                    if (sent == 0) return false;
                    break;
                }

                sent++;
            }

            if (sent == 0) return false;

            _stats.Applied++;

            if (_stats.Applied <= 20 || _stats.Applied % 100 == 0)
            {
                Log.Info("OWN", technique.Describe() + " udp " +
                                udp.SrcPort + "->" + udp.DstPort + ": " +
                                udp.PayloadLength + " байт → " + sent + " пакет(ов)");
            }

            // ВАЖНО: false, а не true.
            //
            // `true` означает «я обработал пакет, оригинал отправлять не надо» —
            // это верно для TCP, где куски разрезанного ClientHello УЖЕ содержат
            // его байты. Для UDP fake — это ДОПОЛНИТЕЛЬНЫЙ пакет перед настоящим:
            // сервер должен получить и фейк, и оригинал.
            //
            // Здесь была ошибка, стоившая показа экрана: после отправки фейка
            // оригинальный DTLS ClientHello выбрасывался. Рукопожатие не
            // начиналось вообще, клиент повторял ClientHello — и мы выбрасывали
            // каждую копию, пока не кончался бюджет. Со стороны: «показ экрана
            // не подключается», «голос хандрит».
            return false;
        }

        private bool Send(IntPtr handle, IntPtr addressPtr, byte[] data, int length)
        {
            uint sendLen;
            return WinDivertInterop.WinDivertSend(handle, data, (uint)length, out sendLen, addressPtr);
        }

        /// <summary>Копия payload TCP-пакета — для извлечения SNI.</summary>
        private static byte[] Payload(TcpPacket tcp)
        {
            if (tcp == null || !tcp.HasPayload) return new byte[0];

            byte[] data = new byte[tcp.PayloadLength];
            Array.Copy(tcp.Buffer, tcp.PayloadOffset, data, 0, tcp.PayloadLength);

            return data;
        }

        /// <summary>Адрес назначения UDP-пакета.</summary>
        private static System.Net.IPAddress UdpDestination(UdpPacket udp)
        {
            if (udp == null) return null;

            try
            {
                if (!udp.IsIPv6)
                {
                    byte[] bytes =
                    {
                        udp.Buffer[16], udp.Buffer[17], udp.Buffer[18], udp.Buffer[19]
                    };

                    return new System.Net.IPAddress(bytes);
                }

                byte[] v6 = new byte[16];
                Array.Copy(udp.Buffer, 24, v6, 0, 16);

                return new System.Net.IPAddress(v6);
            }
            catch
            {
                return null;
            }
        }

        private bool Reinject(IntPtr handle, IntPtr addressPtr, byte[] packet, int length)
        {
            return Send(handle, addressPtr, packet, length);
        }
    }
}