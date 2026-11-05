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
        /// </summary>
        public string Run(CancellationToken cancel = default(CancellationToken))
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
                        if (error == 6 || error == 995) break;
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
            if (length < 20) return false;
            if ((packet[0] >> 4) != 4) return false;

            byte protocol = packet[9];

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
                inner = _options.Profiles.FilterExpression();
            }
            else
            {
                bool udpMode = _options.Technique != null && _options.Technique.Kind == Technique.UdpLen;

                inner = udpMode
                    ? "udp.DstPort == " + _options.Port + " and udp.PayloadLength > 20"
                    : "tcp.DstPort == " + _options.Port + " and tcp.PayloadLength > 40";
            }

            if (inner.Length == 0) return "";

            return "outbound and (" + inner + ")";
        }

        /// <summary>
        /// UDP-ветка: сейчас только `udplen` (QUIC).
        ///
        /// Проверка «наше ли это» проще, чем у TLS: QUIC Initial начинается с
        /// длинного заголовка, где первый байт имеет старшие биты 0b11 (long
        /// header form) и тип 0 (Initial). Проверять только порт мало — на 443
        /// по UDP ходит и QUIC, и всякий мусор вроде игр.
        /// </summary>
        private bool HandleUdp(IntPtr handle, IntPtr addressPtr, byte[] packet, int length)
        {
            UdpPacket udp = UdpPacket.Parse(packet, length);

            if (udp == null || !udp.HasPayload) return false;
            if (udp.PayloadLength < 20) return false;

            // long header form: 0b11xxxxxx, тип Initial = 0b1100xxxx
            byte first = packet[udp.PayloadOffset];

            if ((first & 0x80) == 0) return false;        // short header (1-RTT), не Initial
            if ((first & 0x30) != 0) return false;        // не Initial

            _stats.ClientHellos++;    // для UDP это QUIC Initial, счётчик общий

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

            // Профиль может быть TCP-only: тогда QUIC трогать нечем.
            if (technique == null || technique.Kind != Technique.UdpLen) return false;

            string error;
            List<Desync.Outgoing> outgoing = Desync.BuildUdp(udp, technique, out error);

            if (outgoing.Count == 0)
            {
                if (error.Length > 0 && _stats.Errors < 5)
                    Log.Warn("OWN", "udplen не применился (" + error + "), пакет уходит как есть");

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
                Log.Info("OWN", "udplen " + udp.SrcPort + "->" + udp.DstPort + ": " +
                                udp.PayloadLength + " байт → " + sent + " пакет(ов)");
            }

            return true;
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