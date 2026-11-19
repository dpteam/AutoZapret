using System;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AutoZapret.Android.Engine;

namespace AutoZapret.Android
{
    /// <summary>
    /// VPN-режим: весь трафик приложений заворачивается в наш SOCKS5.
    ///
    /// Схема повторяет ByeByeDPI (см. refs/byebyedpi):
    ///
    ///   приложения → TUN (этот сервис) → hev-socks5-tunnel (tun2socks)
    ///              → SOCKS5 127.0.0.1 → интернет
    ///
    /// Desync живёт в SOCKS5 (наш Socks5Proxy), а не здесь: сервис — только
    /// транспорт. Поэтому один и тот же движок работает и в режиме «прокси»
    /// (приложения ходят в SOCKS сами), и в режиме VPN.
    /// </summary>
    [Service(Exported = false, Permission = "android.permission.BIND_VPN_SERVICE",
             ForegroundServiceType = ForegroundService.TypeSystemExempted)]
    [IntentFilter(new[] { "android.net.VpnService" })]
    public sealed class ZapretVpnService : VpnService
    {
        public const string ActionStart = "ru.autozapret.android.START";
        public const string ActionStop = "ru.autozapret.android.STOP";

        /// <summary>
        /// Режим «только прокси», без туннеля.
        ///
        /// Отдельные действия, потому что режим прокси ОБЯЗАН жить в
        /// foreground-сервисе. Проверено на телефоне: без FGS система ставит
        /// приложению `effective=APP_BACKGROUND` и firewall-правило
        /// `10331-background-default` — весь фоновый трафик режется, и любое
        /// соединение висит до таймаута. Снаружи это выглядело как «обход не
        /// работает», хотя сеть в порядке: из-под shell тот же запрос отвечал
        /// 200 за 0,5 с.
        /// </summary>
        public const string ActionProxyStart = "ru.autozapret.android.PROXY_START";
        public const string ActionProxyStop = "ru.autozapret.android.PROXY_STOP";

        /// <summary>Broadcast из кнопки в уведомлении: остановить туннель.</summary>
        public const string ActionStopBroadcast = "ru.autozapret.android.STOP_FROM_NOTIFICATION";

        private const int NotificationId = 42;
        private const string ChannelId = "autozapret-vpn";

        /// <summary>
        /// Техника обхода по умолчанию. ОДНО место для интерфейса и сервиса.
        ///
        /// Почему именно fake, а не разрез. Разрез (split) режет TLS
        /// ClientHello на два сегмента, и это помогает там, где DPI собирает
        /// рукопожатие из кусков. Fake отправляет подставной пакет, который
        /// DPI принимает за начало соединения, — и работает там, где разрез
        /// не пробивает.
        ///
        /// Проверено на Nothing A015 (Android 16), MTS LTE:
        ///   split1 / split2 / multisplit -> example.com ок, youtube/discord таймаут
        ///   disorder / oob               -> не проходит даже example.com
        ///   fake8                        -> example.com, youtube, discord,
        ///                                   google, cdn.discordapp — ок
        ///
        /// На Wi-Fi тот же результат, то есть дело не в провайдере, а в
        /// технике: разрез на этом устройстве не пробивает ничего, кроме
        /// незаблокированных сайтов.
        ///
        /// Раньше здесь стояло "fake8", а в интерфейсе — Split1: два разных
        /// дефолта. Человек видел в списке «Разрез 1», соглашался с ним, и
        /// сервис получал именно разрез. Теперь значение одно на оба места.
        /// </summary>
        public const string DefaultTechnique = "fake8";

        /// <summary>Адрес внутри туннеля: тот же, что ждёт hev в конфиге.</summary>
        private const string TunnelAddress = "10.10.10.10";

        /// <summary>
        /// MTU туннеля.
        ///
        /// ВАЖНО: hev сам выставляет MTU интерфейсу через ioctl(SIOCSIFMTU),
        /// поэтому значение из конфига hev перекрывает builder.SetMtu. Если
        /// там стоит 8500, а физический канал 1500, приложения считают MTU
        /// туннеля равным 8500, шлют сегменты по 8500 байт, и они рвутся в
        /// сети: мелкие пакеты проходят, крупные (картинки, тела ответов) —
        /// нет. Снаружи это выглядит как «страница нарисовала скелет и
        /// зависла» при полностью живых соединениях.
        ///
        /// 1500 — значение мобильных сетей.
        /// </summary>
        private const int TunnelMtu = HevTunnel.Mtu;

        /// <summary>
        /// IPv6-адрес туннеля. Нужен, только чтобы завернуть IPv6 внутрь: без
        /// маршрута `::/0` весь IPv6 у приложений идёт мимо туннеля, а значит
        /// и мимо обхода. На десктопе это вылечили так же (TcpPacket для обоих
        /// семейств). Адрес локальный (fc00::/7), в интернет он не уходит.
        /// </summary>
        private const string TunnelAddress6 = "fc00::1";

        private ParcelFileDescriptor _tun;
        private Socks5Proxy _proxy;
        private readonly CiadpiEngine _engine = new CiadpiEngine();
        private Thread _thread;
        private Thread _hevLogThread;
        private volatile bool _running;

        public static bool IsRunning { get; private set; }

        /// <summary>
        /// Живой экземпляр сервиса.
        ///
        /// Зачем статическая ссылка: кнопка «Выключить» в уведомлении не может
        /// запустить сервис повторно (он уже foreground, и `PendingIntent.GetService`
        /// такой intent просто не доставляет — проверено, в logcat тишина).
        /// А приложение живёт в одном процессе, поэтому приёмник команд видит
        /// этот экземпляр и может попросить его остановиться напрямую.
        /// </summary>
        public static ZapretVpnService Current { get; private set; }

        /// <summary>Прокси, который поднят внутри сервиса — чтобы UI видел счётчики.</summary>
        public static Socks5Proxy Running { get; private set; }

        /// <summary>Куда писать строки лога для интерфейса (ставит MainActivity).</summary>
        public static Action<string> LogSink;

        public override void OnCreate()
        {
            base.OnCreate();

            Current = this;
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            string action = intent == null ? ActionStart : intent.Action;

            if (action == ActionStop)
            {
                StopTunnel();
                return StartCommandResult.NotSticky;
            }

            if (action == ActionProxyStop)
            {
                StopProxyMode();
                return StartCommandResult.NotSticky;
            }

            if (action == ActionProxyStart)
            {
                try
                {
                    StartForeground(NotificationId, BuildNotification("Обход включён (прокси)"));
                }
                catch (Exception ex)
                {
                    LogLine("StartForeground: " + ex.GetType().Name + ": " + ex.Message);
                }

                try
                {
                    StartProxyOnly();
                }
                catch (Exception ex)
                {
                    LogLine("запуск прокси: " + ex.GetType().Name + ": " + ex.Message);
                }

                return StartCommandResult.NotSticky;
            }

            // StartForeground может бросить SecurityException (тип сервиса,
            // разрешения, политика прошивки). Раньше это убивало процесс
            // целиком и выглядело как «приложение вылетело». Теперь — в лог и
            // всё равно пытаемся поднять туннель.
            try
            {
                StartForeground(NotificationId, BuildNotification());
            }
            catch (Exception ex)
            {
                LogLine("StartForeground: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                StartTunnel();
            }
            catch (Exception ex)
            {
                LogLine("запуск VPN: " + ex.GetType().Name + ": " + ex.Message);
            }

            return StartCommandResult.NotSticky;
        }

        public override void OnRevoke()
        {
            // Пользователь отозвал разрешение VPN — обязаны остановиться сами
            LogLine("VPN: разрешение отозвано системой");
            StopTunnel();
            base.OnRevoke();
        }

        public override void OnDestroy()
        {
            LogLine("VPN: сервис уничтожается");
            StopTunnel();
            Current = null;
            base.OnDestroy();
        }

        /// <summary>
        /// Остановить по запросу извне (кнопка в уведомлении, headless-команда).
        ///
        /// Отдельный метод, а не повторный `startService`: для уже запущенного
        /// foreground-сервиса такой вызов система просто игнорирует, и кнопка
        /// «Выключить» не делает ничего. Проверено на телефоне.
        /// </summary>
        public void RequestStop()
        {
            try
            {
                StopTunnel();
            }
            catch (Exception ex)
            {
                LogLine("остановка по запросу: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------- запуск

        private void StartTunnel()
        {
            if (_running) return;

            // Предыдущий режим (прокси) должен быть полностью погашен, иначе
            // два прокси и два движка борются за порты и падают.
            if (_proxy != null)
            {
                try { _proxy.Stop(); } catch { }
                _proxy = null;
            }

            try { _engine.Stop(); } catch { }

            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            int port = prefs.GetInt("port", 1080);

            _proxy = new Socks5Proxy();
            _proxy.Config.Port = port;
            _proxy.Config.DelayMs = prefs.GetInt("delay", 0);
            _proxy.Config.BlockQuic = prefs.GetBoolean("block_quic", true);

            // Та же привязка, что и в режиме прокси: при двух сетях маршрут
            // выбирает система, и обход может уйти через заблокированную.
            string networkMode = prefs.GetString("network_mode", NetworkBinding.ModeAuto);

            LogLine("сеть: " + NetworkBinding.BindProcess(this, networkMode));

            // Профиль, подобранный под ЭТУ сеть, важнее ручного выбора: TTL
            // фейка зависит от числа хопов до DPI, и один и тот же на Wi-Fi и
            // в мобильной сети не работает. Если для текущей сети профиль есть
            // — берём его, иначе ручную настройку.
            TuneProfile tuned = NetworkTuner.ForCurrent(this);

            string technique = prefs.GetString("technique", ZapretVpnService.DefaultTechnique);

            if (technique == "auto" && tuned != null)
            {
                tuned.ApplyTo(_proxy.Config);
                LogLine("техника: авто, профиль сети — " + tuned.Describe());
            }
            else
            {
                DesyncPreset.ById(technique).ApplyTo(_proxy.Config);
                LogLine("техника: " + DesyncPreset.ById(technique).Name);
            }

            // Движок byedpi запускается ПОСЛЕ Establish, а не здесь.
            //
            // Порядок критичен: если запустить до поднятия VPN, сокеты движка
            // создаются при старом маршруте, а после установки туннеля их
            // соединения уходят в никуда — в логе `connect: Connection timed
            // out` на настоящий адрес, хотя тот же UID через наш прокси
            // качает мегабайты. Проверено на телефоне.

            // Лог прокси — в тот же журнал, что видит человек. Раньше Say()
            // уходил в пустоту (Log никто не выставил), и в logcat было видно
            // только «VPN включён», а что происходит с соединениями — нет.
            _proxy.Log = LogLine;

            // Вывести исходящие сокеты прокси из-под VPN.
            //
            // Без этого приложение, исключённое из туннеля, вообще не имеет
            // маршрута наружу: в таблице `main` только локальная подсеть, а
            // `default` живёт в таблице физической сети, куда ведёт fwmark.
            // Симптом: connect падает за 6 мс с SocketException, ни одного
            // пакета не уходит. protect() ставит этот fwmark.
            Socks5Proxy.ProtectFd = delegate (int fd)
            {
                try { return Protect(fd); } catch { return false; }
            };

            // Прогреть кэш настоящих ClientHello для основных доменов.
            //
            // Зачем: первый запрос к домену уходит с синтетическим фейком, а он
            // отличается от настоящего по отпечатку — и на живой сети не
            // сработал, хотя тот же TTL у byedpi работал. Снятие занимает до
            // полутора секунд, поэтому делаем это в фоне ДО того, как пойдёт
            // трафик, а не в момент первого соединения.
            HelloCapture.WarmPopular();

            string error = _proxy.Start();

            if (error != null)
            {
                LogLine("SOCKS5 не запустился: " + error);
                StopSelf();
                return;
            }

            VpnService.Builder builder = new VpnService.Builder(this);

            builder.SetSession("AutoZapret");
            builder.AddAddress(TunnelAddress, 32);
            builder.AddRoute("0.0.0.0", 0);
            builder.AddDnsServer("1.1.1.1");

            // MTU обязан совпадать с тем, что прописан в конфиге hev.
            //
            // Раньше здесь стояло только AddAddress, то есть VpnService брал
            // MTU по умолчанию (1500), а hev в своём конфиге ждал 8500. Такое
            // расхождение ломает крупные пакеты: картинки и тела ответов
            // приходят битыми, страница рисует скелет и не заполняется, хотя
            // соединения устанавливаются.
            //
            // 1500 — значение мобильных сетей: больше даёт фрагментацию,
            // меньше — лишнюю нарезку.
            builder.SetMtu(TunnelMtu);

            // Подложная сеть: через неё ходит трафик приложений, ИСКЛЮЧЁННЫХ
            // из VPN (в том числе нашего собственного процесса и его дочерних).
            //
            // ВАЖНО: именно ФИЗИЧЕСКАЯ сеть, а не cm.ActiveNetwork. В момент
            // Establish активной сетью может оказаться сам VPN, и тогда
            // «подложной» станет наш же туннель: исключённый трафик уходит в
            // него по кругу, и дочерний byedpi получает
            // `connect: Connection timed out` на настоящий адрес, хотя наш
            // прокси в том же UID качает мегабайты.
            try
            {
                global::Android.Net.ConnectivityManager cm =
                    GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager;

                global::Android.Net.Network physical = PickPhysicalNetwork(cm);

                if (physical != null)
                {
                    builder.SetUnderlyingNetworks(new[] { physical });
                    LogLine("подложная сеть выбрана (не VPN)");

                    // BindProcessToNetwork здесь НЕ вызываем, и это важно.
                    //
                    // Пробовали: привязка процесса к физической сети чинит
                    // дочерний движок (YouTube через прокси даёт 975 КБ под
                    // VPN), но отрезает сам VPN-сервис от его же туннеля —
                    // страница в браузере перестаёт загружаться вовсе
                    // (чёрный экран без ошибки). Процессу нужны ОБА пути:
                    // tun для hev и физическая сеть для движка.
                    //
                    // Поэтому выбор сети делаем только для движка — через его
                    // собственный флаг -I, а не привязкой процесса.
                }
                else
                {
                    LogLine("ВНИМАНИЕ: физическая сеть не найдена, UnderlyingNetworks не задан");
                }
            }
            catch (Exception ex)
            {
                LogLine("UnderlyingNetworks: " + ex.GetType().Name);
            }

            // IPv6 — целиком в туннель, но только если он в этой сети есть.
            // Без маршрута `::/0` весь IPv6-трафик приложений идёт мимо
            // обхода (утечка). С маршрутом, но без живого IPv6 у оператора —
            // hev сбрасывает такие соединения, и браузер на сброс не
            // откатывается на IPv4. Проверено на телефоне: wikipedia.org
            // перестал открываться. Поэтому решаем по факту, а не «на всякий».
            string tunnel6 = HasUsableIPv6() ? TunnelAddress6 : null;

            if (tunnel6 != null)
            {
                builder.AddAddress(TunnelAddress6, 128);
                builder.AddRoute("::", 0);
            }

            // ---- какие приложения пускать в туннель
            string mode = AppSelection.LoadMode(this);
            System.Collections.Generic.HashSet<string> selected = AppSelection.LoadApps(this);

            if (mode == AppSelection.ModeOnly && selected.Count == 0)
                mode = AppSelection.ModeAll;   // «только выбранные» без выбора = весь телефон

            if (mode == AppSelection.ModeOnly)
            {
                // Белый список: разрешаем ровно выбранные. Себя не добавляем —
                // иначе SOCKS-соединение к 127.0.0.1 уйдёт в собственный туннель.
                foreach (string package in selected)
                {
                    try { builder.AddAllowedApplication(package); }
                    catch { }
                }
            }
            else
            {
                // Весь телефон или чёрный список: себя исключаем всегда, чтобы
                // не поймать петлю.
                try { builder.AddDisallowedApplication(PackageName); } catch { }

                if (mode == AppSelection.ModeExcept)
                {
                    foreach (string package in selected)
                    {
                        try { builder.AddDisallowedApplication(package); }
                        catch { }
                    }
                }
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                builder.SetMetered(false);

            _tun = builder.Establish();

            if (_tun == null)
            {
                LogLine("VPN не разрешён системой");
                _proxy.Stop();
                StopSelf();
                return;
            }

            // Только теперь, когда туннель поднят и UnderlyingNetworks задан,
            // запускаем движок. До Establish его сокеты создаются при старом
            // маршруте и после установки VPN уходят в никуда: `connect:
            // Connection timed out` на настоящий адрес, хотя тот же UID через
            // наш прокси качает мегабайты.
            StartEngineIfAvailable(prefs, port);

            // Уровень логов hev задаётся настройкой: при разборе поломок нужен
            // debug, в обычной работе — warn, иначе журнал забивается.
            HevTunnel.LogLevel = prefs.GetString("hev_log", "warn");

            // hev умеет писать только в файл или в stdout/stderr. В приложении
            // stdout — это /dev/null, поэтому единственный способ увидеть его
            // решения — файл, который мы сами и читаем.
            string hevLog = System.IO.Path.Combine(FilesDir.AbsolutePath, "hev.log");

            try { if (System.IO.File.Exists(hevLog)) System.IO.File.Delete(hevLog); } catch { }

            string config = HevTunnel.BuildConfig(TunnelAddress, tunnel6, port, hevLog);

            _running = true;
            IsRunning = true;
            Running = _proxy;

            _thread = new Thread(delegate () { TunnelLoop(_tun.Fd, config); })
            {
                IsBackground = true,
                Name = "hev-tunnel"
            };

            _thread.Start();

            StartHevLogReader(hevLog);

            LogLine("VPN включён: tun → SOCKS5 127.0.0.1:" + port + ", " + _proxy.Config.Describe() +
                    ", режим приложений: " + mode +
                    (tunnel6 != null ? ", IPv6 внутри туннеля" : ", IPv6 нет в сети — только IPv4"));
        }

        private void TunnelLoop(int fd, string config)
        {
            try
            {
                string error = HevTunnel.Run(fd, config);
                if (error != null) LogLine(error);
                else LogLine("туннель остановлен сам");
            }
            catch (Exception ex)
            {
                LogLine("туннель упал: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Читать файл лога hev и отдавать строки в журнал приложения.
        ///
        /// Зачем это нужно: снаружи VPN-режим выглядит одинаково при десятке
        /// разных причин — «соединения есть, страница пустая». hev знает
        /// больше: он пишет, что прочитал из tun, какую сессию открыл и на
        /// чём именно споткнулся. Но писать он умеет только в файл или в
        /// stdout, а stdout в Android-приложении — /dev/null. Поэтому файл,
        /// а рядом этот поток.
        ///
        /// Чтение инкрементальное: запоминаем смещение, дочитываем только
        /// добавленное. Неполную последнюю строку держим до следующего круга.
        /// </summary>
        private void StartHevLogReader(string path)
        {
            _hevLogThread = new Thread(delegate ()
            {
                byte[] buffer = new byte[8192];
                long offset = 0;
                string pending = "";

                while (_running)
                {
                    bool readSomething = false;

                    try
                    {
                        if (System.IO.File.Exists(path))
                        {
                            using (System.IO.FileStream fs = new System.IO.FileStream(
                                path, System.IO.FileMode.Open,
                                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                            {
                                // Файл пересоздали — читаем с начала, старое не мешаем.
                                if (fs.Length < offset)
                                {
                                    offset = 0;
                                    pending = "";
                                }

                                if (fs.Length > offset)
                                {
                                    fs.Seek(offset, System.IO.SeekOrigin.Begin);

                                    int n;

                                    while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        offset += n;
                                        pending += System.Text.Encoding.UTF8.GetString(buffer, 0, n);
                                        readSomething = true;

                                        int nl;

                                        while ((nl = pending.IndexOf('\n')) >= 0)
                                        {
                                            string line = pending.Substring(0, nl).TrimEnd('\r');

                                            pending = pending.Substring(nl + 1);

                                            if (line.Length == 0) continue;

                                            // debug-строки hev пишутся по одной на
                                            // каждый шаг рукопожатия — их сотни, и
                                            // в logcat они вытесняют журнал самого
                                            // приложения (а он и нужен для разбора).
                                            // Поэтому в журнал идут только warn и
                                            // error; полный поток остаётся в файле.
                                            if (line.Contains(" [D] ")) continue;

                                            LogLine("hev: " + line);
                                        }

                                        // Строка без перевода строки не должна расти
                                        // бесконечно: hev пишет короткими строками,
                                        // значит это мусор — выбрасываем.
                                        if (pending.Length > 65536) pending = "";
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Файл могут удалить/пересоздать — это не повод падать.
                    }

                    Thread.Sleep(readSomething ? 100 : 300);
                }
            })
            {
                IsBackground = true,
                Name = "hev-log"
            };

            _hevLogThread.Start();
        }

        /// <summary>
        /// Поднять внешний движок byedpi и направить в него наш прокси.
        ///
        /// Порт движка внутренний (пользователь его не видит): снаружи слушает
        /// наш SOCKS5, он же отвечает за DoH и перехват DNS.
        /// </summary>
        /// <summary>
        /// Режим «только SOCKS5»: без tun, но в foreground-сервисе.
        ///
        /// Без сервиса система считает приложение фоновым и режет трафик
        /// (см. комментарий у ActionProxyStart), поэтому прокси обязан жить
        /// здесь, а не в активности.
        /// </summary>
        private void StartProxyOnly()
        {
            if (_proxy != null) return;

            // Гасим туннель и движок от предыдущего режима.
            try { if (_tun != null) { HevTunnel.Stop(); _tun.Close(); _tun = null; } } catch { }
            try { _engine.Stop(); } catch { }

            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            int port = prefs.GetInt("port", 1080);

            _proxy = new Socks5Proxy();
            _proxy.Config.Port = port;
            _proxy.Config.DelayMs = prefs.GetInt("delay", 0);
            _proxy.Config.BlockQuic = prefs.GetBoolean("block_quic", true);
            _proxy.Config.BindAddress = "";

            string technique = prefs.GetString("technique", ZapretVpnService.DefaultTechnique);

            if (technique == "auto")
            {
                TuneProfile tuned = NetworkTuner.ForCurrent(this);

                if (tuned != null) tuned.ApplyTo(_proxy.Config);
                else DesyncPreset.Split1.ApplyTo(_proxy.Config);
            }
            else
            {
                DesyncPreset.ById(technique).ApplyTo(_proxy.Config);
            }

            _proxy.Log = LogLine;

            // В режиме «только SOCKS5» VPN нет, и protect() вызывать не на что:
            // сокеты и так идут через обычный маршрут. Явно снимаем делегат,
            // чтобы он не остался от предыдущего VPN-запуска.
            Socks5Proxy.ProtectFd = null;

            HelloCapture.WarmPopular();

            // Внешний движок: раньше он не работал из приложения, потому что
            // без foreground-сервиса система резала фоновый трафик
            // (effective=APP_BACKGROUND). Теперь сервис есть, и проверка
            // покажет, снялось ли ограничение.
            StartEngineIfAvailable(prefs, port);

            string error = _proxy.Start();

            if (error != null)
            {
                LogLine("SOCKS5 не запустился: " + error);
                _proxy = null;
                StopProxyMode();
                return;
            }

            Running = _proxy;
            IsRunning = true;

            LogLine("прокси включён: " + _proxy.Config.Describe() + " на 127.0.0.1:" + _proxy.ActualPort);
        }

        private void StopProxyMode()
        {
            try { if (_proxy != null) _proxy.Stop(); } catch { }
            _proxy = null;

            try { _engine.Stop(); } catch { }

            // Если поднят туннель, сервис НЕ останавливаем: он обслуживает
            // VPN, а прокси здесь был лишь предыдущим режимом. Иначе остановка
            // прокси убивает работающий VPN.
            if (_tun != null)
            {
                LogLine("прокси выключен (VPN продолжает работать)");
                return;
            }

            Running = null;
            IsRunning = false;

            try { StopForeground(StopForegroundFlags.Remove); } catch { }

            LogLine("прокси выключен");
            StopSelf();
        }

        private void StartEngineIfAvailable(ISharedPreferences prefs, int userPort)
        {
            try
            {
                // Движок не должен занять пользовательский порт.
                int enginePort = userPort == 1081 ? 1082 : 1081;

                string technique = prefs.GetString("technique", DefaultTechnique);

                string args = EngineArgsFor(technique);

                // byedpi ВСЕГДА запускается с 0.0.0.0, без привязки к адресу
                // конкретной сети.
                //
                // Проверено на телефоне: с `-I 192.168.1.155` (адрес Wi-Fi)
                // движок стартует и не соединяется вообще — проверка движка
                // падает, и приложение откатывается на свой. С `-I 0.0.0.0`
                // тот же бинарь с теми же флагами работает.
                //
                // Причина та же, что у привязки сокета к локальному IP: пакет
                // с source-адресом одной сети попадает в таблицу маршрутов
                // другой и отбрасывается. Выбор сети поэтому делаем только
                // через BindProcessToNetwork для своего процесса, а движку
                // оставляем свободу.
                args = (args + " -I 0.0.0.0").Trim();

                if (!ShouldUseEngine(prefs, _proxy.Config)) return;

                string error = _engine.Start(this, enginePort, args, LogLine);

                if (error != null)
                {
                    LogLine("движок недоступен, работаю своим: " + error);
                    _proxy.Config.ForwardPort = 0;
                    return;
                }

                // Проверяем движок настоящим соединением — но это только
                // предупреждение, а не приговор.
                //
                // Причина: если техника требует fake, наш движок его выполнить
                // не может вообще. Откат «на свой» гарантирует провал, тогда
                // как движок может работать — проба ходит на 1.1.1.1, а он
                // бывает недоступен по причинам, к движку не относящимся.
                if (!MainActivity.EngineWorksFor(enginePort))
                    LogLine("ВНИМАНИЕ: проба движка не прошла, но оставляю его — техника требует fake");
                else
                    LogLine("движок проверен: соединение проходит");

                _proxy.Config.ForwardPort = enginePort;
                LogLine("обход через byedpi на 127.0.0.1:" + enginePort);
            }
            catch (Exception ex)
            {
                LogLine("движок: " + ex.GetType().Name + " — работаю своим");
                _proxy.Config.ForwardPort = 0;
            }
        }

        /// <summary>
        /// Нужен ли внешний движок для этой конфигурации.
        ///
        /// Режим auto (по умолчанию) выбирает по технике: `fake` наш движок
        /// выполнить не может — send() копирует байты в очередь ядра, и после
        /// отправки фейка их уже не переписать, а byedpi восстанавливает буфер
        /// и ретрансмиссия уносит настоящие байты. Поэтому для fake включаем
        /// byedpi, для split/disorder остаёмся на своём.
        ///
        /// Без этого правила выбор «Фейк + TTL 8» тихо не работал: движок был
        /// свой, fake в нём ломал TLS, и снаружи это выглядело как «обход
        /// перестал работать».
        /// </summary>
        internal static bool ShouldUseEngine(ISharedPreferences prefs, DesyncConfig config)
        {
            string mode = prefs.GetString("engine_mode", "auto");

            if (mode == "own") return false;
            if (mode == "byedpi") return true;

            // auto
            return config != null && config.Fake;
        }

        /// <summary>Перевод нашей техники в флаги byedpi.</summary>
        private static string EngineArgsFor(string technique)
        {
            if (technique == "off") return "";

            if (technique == "split1") return "--split 1";
            if (technique == "split2") return "--split 1+s";
            if (technique == "multisplit") return "--split 1+s --split 5+s";
            if (technique == "disorder") return "--disorder 1";
            if (technique == "oob") return "--oob 1";
            if (technique == "disorder-oob") return "--disoob 1";
            if (technique == "fake-split") return "--fake -1 --ttl 8 --split 1+s";
            if (technique == "fake-disorder") return "--fake -1 --ttl 8 --disorder 1";

            // fake8 и всё остальное: проверенная комбинация
            return "--fake -1 --ttl 8";
        }

        private void StopTunnel()
        {
            if (!_running && _tun == null) return;

            _running = false;
            IsRunning = false;
            Running = null;

            HevTunnel.Stop();

            // Снимаем protect вместе с туннелем: после Stop() VPN больше нет, а
            // делегат остался бы ссылаться на мёртвый сервис.
            Socks5Proxy.ProtectFd = null;

            try { if (_thread != null) _thread.Join(1500); } catch { }
            try { if (_hevLogThread != null) _hevLogThread.Join(500); } catch { }
            try { if (_tun != null) _tun.Close(); } catch { }
            _tun = null;

            try { if (_proxy != null) _proxy.Stop(); } catch { }
            _proxy = null;

            try { _engine.Stop(); } catch { }

            try { StopForeground(StopForegroundFlags.Remove); } catch { }

            LogLine("VPN выключен");
            StopSelf();
        }

        /// <summary>
        /// Найти физическую сеть с интернетом, исключив VPN.
        ///
        /// Именно физическую: если взять «активную», ей может оказаться наш же
        /// туннель, и исключённый трафик уйдёт в него по кругу.
        /// </summary>
        private static global::Android.Net.Network PickPhysicalNetwork(
            global::Android.Net.ConnectivityManager cm)
        {
            if (cm == null) return null;

            global::Android.Net.Network fallback = null;

            foreach (global::Android.Net.Network network in cm.GetAllNetworks())
            {
                global::Android.Net.NetworkCapabilities caps = cm.GetNetworkCapabilities(network);

                if (caps == null) continue;
                if (!caps.HasCapability(global::Android.Net.NetCapability.Internet)) continue;

                // VPN-сеть не годится ни при каких условиях: это наш туннель.
                if (caps.HasTransport(global::Android.Net.TransportType.Vpn)) continue;

                if (fallback == null) fallback = network;

                // Wi-Fi и мобильная — то, что нужно; берём первую подходящую
                if (caps.HasTransport(global::Android.Net.TransportType.Wifi) ||
                    caps.HasTransport(global::Android.Net.TransportType.Cellular))
                {
                    return network;
                }
            }

            return fallback;
        }

        /// <summary>
        /// Есть ли в сети настоящий IPv6. Спрашиваем именно активную сеть
        /// (ту, через которую телефон сейчас ходит в интернет), а не «любой
        /// интерфейс с адресом»: на телефоне одновременно живут Wi-Fi без
        /// IPv6 и мобильная сеть с ним, и по интерфейсам ответ был «да» —
        /// после чего сайты переставали открываться.
        ///
        /// Локальный `fe80::` и ULA `fc00::/7` не в счёт: с ними в интернет
        /// не выйти, а маршрут `::/0` в туннель уже навредит.
        /// </summary>
        private bool HasUsableIPv6()
        {
            try
            {
                global::Android.Net.ConnectivityManager manager =
                    GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager;

                if (manager == null) return false;

                global::Android.Net.Network network = manager.ActiveNetwork;

                if (network == null) return false;

                global::Android.Net.LinkProperties properties = manager.GetLinkProperties(network);

                if (properties == null) return false;

                foreach (global::Android.Net.LinkAddress link in properties.LinkAddresses)
                {
                    Java.Net.InetAddress address = link == null ? null : link.Address;

                    if (address == null) continue;

                    byte[] raw = address.GetAddress();

                    if (raw == null || raw.Length != 16) continue;   // 16 байт = IPv6

                    // fe80::/10 — только локальный сегмент
                    if (raw[0] == 0xFE && (raw[1] & 0xC0) == 0x80) continue;

                    // fc00::/7 — уникальные локальные адреса, тоже не выход
                    if ((raw[0] & 0xFE) == 0xFC) continue;

                    return true;
                }
            }
            catch
            {
                // не смогли узнать — считаем, что IPv6 нет: молча не ломаем
            }

            return false;
        }

        // ------------------------------------------------------------ уведомление

        private Notification BuildNotification()
        {
            return BuildNotification("Обход включён");
        }

        private Notification BuildNotification(string text)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                NotificationManager manager = GetSystemService(NotificationService) as NotificationManager;
                NotificationChannel channel = new NotificationChannel(ChannelId, "AutoZapret VPN",
                    NotificationImportance.Low);

                if (manager != null) manager.CreateNotificationChannel(channel);
            }

            Intent open = new Intent(this, typeof(MainActivity));
            PendingIntent pending = PendingIntent.GetActivity(this, 0, open,
                Build.VERSION.SdkInt >= BuildVersionCodes.M ? PendingIntentFlags.Immutable : 0);

            // Кнопка «Выключить» прямо в уведомлении. Зачем: заходить в
            // приложение ради одного нажатия — лишние три шага, а выключают
            // обход чаще, чем включают (сайт не открылся, банк ругается).
            //
            // Через broadcast, а не PendingIntent.GetService: для уже
            // запущенного foreground-сервиса система такой intent не
            // доставляет, и кнопка молча не работает. Приёмник живёт в этом же
            // процессе и дёргает RequestStop напрямую.
            Intent stop = new Intent(ActionStopBroadcast);
            stop.SetPackage(PackageName);

            PendingIntent stopPending = PendingIntent.GetBroadcast(this, 1, stop,
                Build.VERSION.SdkInt >= BuildVersionCodes.M ? PendingIntentFlags.Immutable : 0);

            Notification.Action stopAction = new Notification.Action.Builder(
                global::Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_stat_shield),
                new Java.Lang.String("Выключить"), stopPending).Build();

            Notification.Builder builder = new Notification.Builder(this, ChannelId)
                .SetContentTitle("AutoZapret")
                .SetContentText(text)
                .SetSmallIcon(Resource.Drawable.ic_stat_shield)
                .SetContentIntent(pending)
                .SetOngoing(true)
                .AddAction(stopAction);

            // Действие видно сразу, без разворота уведомления: у нас ровно
            // одно действие, и прятать его в «развернуть» незачем.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                builder.SetCustomBigContentView(null);

            return builder.Build();
        }

        private static void LogLine(string line)
        {
            global::Android.Util.Log.Info("AutoZapret", line);

            Action<string> sink = LogSink;

            if (sink != null)
            {
                try { sink(line); } catch { }
            }
        }
    }
}