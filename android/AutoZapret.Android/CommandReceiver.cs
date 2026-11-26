using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using AutoZapret.Android.Engine;

namespace AutoZapret.Android
{
    /// <summary>
    /// Управление приложением из adb, без единого тапа по экрану.
    ///
    /// Зачем это нужно. Тап по координатам — плохой способ проверять: при
    /// малейшем сдвиге верстки он попадает в соседнюю строку списка, и
    /// «проверка» показывает не то, что задумано. За сегодня так дважды
    /// выбиралась не та техника, и выводы приходилось переделывать.
    ///
    /// Как пользоваться (всё одной строкой, без открытия приложения):
    ///
    ///   adb shell am broadcast -a ru.autozapret.android.SET --es technique fake8
    ///   adb shell am broadcast -a ru.autozapret.android.STATE
    ///   adb shell am broadcast -a ru.autozapret.android.CHECK
    ///
    /// VPN включается и выключается напрямую через сервис:
    ///
    ///   adb shell am start-foreground-service -n ru.autozapret.android/.ZapretVpnService -a ru.autozapret.android.START
    ///   adb shell am startservice -n ru.autozapret.android/.ZapretVpnService -a ru.autozapret.android.STOP
    ///
    /// Всё, что делает приёмник, дублируется в logcat с тегом AutoZapret —
    /// оттуда результат и забирается.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { ActionSet, ActionState, ActionCheck, ActionApps, ActionVpnOn, ActionVpnOff, ActionStopVpn, ActionDns, ActionTry, ActionProxy, ActionTgProxy, ActionTune })]
    public sealed class CommandReceiver : BroadcastReceiver
    {
        public const string ActionSet = "ru.autozapret.android.SET";
        public const string ActionState = "ru.autozapret.android.STATE";
        public const string ActionCheck = "ru.autozapret.android.CHECK";
        public const string ActionApps = "ru.autozapret.android.APPS";
        public const string ActionVpnOn = "ru.autozapret.android.VPN.ON";
        public const string ActionVpnOff = "ru.autozapret.android.VPN.OFF";
        public const string ActionTune = "ru.autozapret.android.TUNE";

        /// <summary>Проверка DNS: что отдаёт системный резолвер и что DoH.</summary>
        public const string ActionDns = "ru.autozapret.android.DNS";

        /// <summary>Проверка обхода одним соединением до указанного хоста.</summary>
        public const string ActionTry = "ru.autozapret.android.TRY";

        /// <summary>Включить/выключить локальный SOCKS5 без VPN (для сравнения техник).</summary>
        public const string ActionProxy = "ru.autozapret.android.PROXY";

        /// <summary>
        /// Включить/выключить Telegram MTProto-прокси.
        ///
        /// Идёт напрямую в сервис, без активности: прокси не требует ни VPN,
        /// ни разрешений диалогом, поэтому команда должна работать из adb на
        /// заблокированном экране — это и есть способ проверить её на телефоне.
        /// </summary>
        public const string ActionTgProxy = "ru.autozapret.android.TGPROXY";

        /// <summary>Кнопка «Выключить» в уведомлении VPN-сервиса.</summary>
        public const string ActionStopVpn = ZapretVpnService.ActionStopBroadcast;

        /// <summary>
        /// Сюда активность кладёт свой обработчик, пока она жива.
        ///
        /// Зачем: VPN-сервис защищён `BIND_VPN_SERVICE`, и снаружи (из adb)
        /// его запустить нельзя — система отвечает «Requires permission not
        /// exported». А приложение имеет право. Поэтому команда идёт в
        /// активность, а она уже поднимает сервис.
        /// </summary>
        public static Action<string, Intent> Forward;

        public override void OnReceive(Context context, Intent intent)
        {
            if (context == null || intent == null) return;

            string action = intent.Action;

            try
            {
                // Кнопка в уведомлении: останавливаем сервис напрямую. Активность
                // при этом закрыта (человек в шторке), поэтому Forward здесь не
                // годится — нужен сам экземпляр сервиса.
                if (action == ActionStopVpn)
                {
                    ZapretVpnService running = ZapretVpnService.Current;

                    if (running == null)
                    {
                        Say("VPN: уже выключен");
                        return;
                    }

                    Say("VPN: выключаю из уведомления");
                    running.RequestStop();

                    return;
                }

                // VPN — только через активность: см. комментарий у Forward.
                if (action == ActionVpnOn || action == ActionVpnOff)
                {
                    Action<string, Intent> forward = Forward;

                    if (forward == null)
                    {
                        Say("VPN: приложение закрыто, откройте его и повторите");
                        return;
                    }

                    forward(action, intent);
                    return;
                }

                if (action == ActionSet) HandleSet(context, intent);
                else if (action == ActionState) HandleState(context);
                else if (action == ActionTgProxy) HandleTgProxy(context, intent);
                else if (action == ActionCheck) HandleCheck(context);
                else if (action == ActionApps) HandleApps(context, intent);
                else if (action == ActionDns) HandleDns(context);
                else if (action == ActionTry) HandleTry(context, intent);
                else if (action == ActionProxy)
                {
                    Action<string, Intent> forward = Forward;

                    if (forward == null)
                    {
                        Say("PROXY: активность не создана, откройте приложение");
                        return;
                    }

                    forward(action, intent);
                }
                else if (action == ActionTune)
                {
                    Action<string, Intent> forward = Forward;

                    if (forward == null)
                    {
                        Say("TUNE: откройте приложение и повторите");
                        return;
                    }

                    forward(action, intent);
                }
            }
            catch (Exception ex)
            {
                Say("команда " + action + " упала: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------ настройки

        private static void HandleSet(Context context, Intent intent)
        {
            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);

            int port = prefs.GetInt("port", 1080);
            string technique = prefs.GetString("technique", ZapretVpnService.DefaultTechnique);
            string zapretPreset = prefs.GetString("zapret_preset", "");
            int delay = prefs.GetInt("delay", 0);
            bool blockQuic = prefs.GetBoolean("block_quic", true);
            string networkMode = prefs.GetString("network_mode", NetworkBinding.ModeAuto);

            bool changed = false;

            int newPort = intent.GetIntExtra("port", 0);
            if (newPort >= 1024 && newPort <= 65535) { port = newPort; changed = true; }

            string newTechnique = intent.GetStringExtra("technique");
            if (!string.IsNullOrEmpty(newTechnique))
            {
                technique = DesyncPreset.ById(newTechnique).Id;
                changed = true;

                // Ручной выбор техники снимает профиль zapret: иначе старый
                // профиль молча перекрывает выбор, и «выбрал fake8, а работает
                // ALT12» выглядит как поломка выбора.
                zapretPreset = "";
            }

            // Профиль zapret. Отдельный ключ, потому что пустая строка —
            // это законное «никакого профиля», и отличить её от «не передали»
            // через GetStringExtra нельзя. Раньше команда SET профиль вообще
            // не принимала: выбрать его можно было только пальцем в интерфейсе,
            // и проверка на телефоне шла не тем набором, что задумано.
            if (intent.HasExtra("zapretPreset"))
            {
                zapretPreset = (intent.GetStringExtra("zapretPreset") ?? "").Trim();
                changed = true;

                // Профиль перекрывает ручную технику: иначе в настройках
                // остаётся «Без обхода», и отчёт врёт о том, чем идёт обход.
                if (zapretPreset.Length > 0) technique = "auto";
            }

            int newDelay = intent.GetIntExtra("delay", -1);
            if (newDelay >= 0) { delay = newDelay; changed = true; }

            // Boolean нельзя отличить от «не передали» одним GetBooleanExtra,
            // поэтому отдельный ключ-признак.
            if (intent.HasExtra("blockQuic"))
            {
                blockQuic = intent.GetBooleanExtra("blockQuic", blockQuic);
                changed = true;
            }

            string requestedMode = intent.GetStringExtra("networkMode");
            if (!string.IsNullOrEmpty(requestedMode))
            {
                if (requestedMode != NetworkBinding.ModeWifi &&
                    requestedMode != NetworkBinding.ModeMobile)
                {
                    requestedMode = NetworkBinding.ModeAuto;
                }

                networkMode = requestedMode;
                changed = true;
            }

            string requestedHevLog = intent.GetStringExtra("hevLog");

            // Уровень логов hev — самостоятельная настройка. Раньше он
            // сохранялся только внутри `if (changed)`, поэтому команда
            // `SET --es hevLog debug` без других параметров молча ничего не
            // делала: `changed` оставался false, и значение не записывалось.
            if (!string.IsNullOrEmpty(requestedHevLog))
            {
                if (requestedHevLog != "debug" && requestedHevLog != "info" &&
                    requestedHevLog != "warn" && requestedHevLog != "error")
                {
                    requestedHevLog = "warn";
                }

                changed = true;
            }

            if (changed)
            {
                using (ISharedPreferencesEditor editor = prefs.Edit())
                {
                    editor.PutInt("port", port);
                    editor.PutString("technique", technique);
                    editor.PutString("zapret_preset", zapretPreset);
                    editor.PutInt("delay", delay);
                    editor.PutBoolean("block_quic", blockQuic);
                    editor.PutString("network_mode", networkMode);

                    string engineExtra = intent.GetStringExtra("engineExtra");

                    if (engineExtra != null) editor.PutString("engine_extra", engineExtra);

                    string engineMode = intent.GetStringExtra("engineMode");

                    if (!string.IsNullOrEmpty(engineMode)) editor.PutString("engine_mode", engineMode);

                    if (!string.IsNullOrEmpty(requestedHevLog))
                        editor.PutString("hev_log", requestedHevLog);

                    editor.Apply();
                }
            }

            Say("SET: техника=" + DesyncPreset.ById(technique).Name +
                " (" + technique + "), порт=" + port +
                ", сеть=" + NetworkBinding.ModeName(networkMode) +
                ", пауза=" + delay + " мс, QUIC " + (blockQuic ? "закрыт" : "открыт"));

            if (ZapretVpnService.IsRunning)
                Say("SET: VPN работает — перезапустите сервис, чтобы применить");
        }

        // -------------------------------------------------------------- состояние

        /// <summary>
        /// Включить/выключить Telegram MTProto-прокси.
        ///
        /// Параметры через extras: `enabled` (bool), `port` (int),
        /// `relay` (string), `cf` (bool). Всё пишется в prefs, чтобы сервис
        /// увидел настройки при старте — иначе команда меняла бы файл, а
        /// работающий прокси продолжал со старыми значениями.
        /// </summary>
        private static void HandleTgProxy(Context context, Intent intent)
        {
            bool enabled = intent != null && intent.GetBooleanExtra("enabled", false);

            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);
            ISharedPreferencesEditor editor = prefs.Edit();

            editor.PutBoolean("tg_proxy_enabled", enabled);

            if (intent != null)
            {
                int port = intent.GetIntExtra("port", 0);
                if (port > 0) editor.PutInt("tg_proxy_port", port);

                string relay = intent.GetStringExtra("relay");
                if (relay != null) editor.PutString("tg_proxy_relay", relay.Trim());

                if (intent.HasExtra("cf")) editor.PutBoolean("tg_proxy_cf", intent.GetBooleanExtra("cf", true));
            }

            editor.Apply();

            Intent service = new Intent(context, typeof(ZapretVpnService));
            service.SetAction(enabled ? ZapretVpnService.ActionTgProxyStart : ZapretVpnService.ActionTgProxyStop);

            try
            {
                if (enabled && Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    context.StartForegroundService(service);
                }
                else
                {
                    // Остановка — обычным StartService: StartForegroundService
                    // требует, чтобы сервис в течение 5 секунд вызвал
                    // StartForeground, а обработчик остановки этого не делает,
                    // и система убила бы приложение с ANR.
                    context.StartService(service);
                }

                Say("TGPROXY: " + (enabled ? "запускаю" : "останавливаю"));
            }
            catch (Exception ex)
            {
                Say("TGPROXY: не удалось " + (enabled ? "запустить" : "остановить") +
                    ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void HandleState(Context context)
        {
            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);

            Socks5Proxy active = ZapretVpnService.Running;

            // Дефолт ОДИН на всё приложение: ZapretVpnService.DefaultTechnique.
            //
            // Здесь стояло "split1", в сервисе — "fake8", в интерфейсе —
            // Split1.Id. Три разных дефолта означали, что отчёт «техника=»
            // показывал не то, чем реально работает обход: prefs пуст, сервис
            // взял fake8, а STATE напечатал «Разрез 1». По такому отчёту нельзя
            // понять, что происходит, — а он для этого и нужен.
            Say("STATE: vpn=" + (ZapretVpnService.IsRunning ? "on" : "off") +
                ", техника=" + ZapretVpnService.DescribeTechnique(context, prefs) +
                ", режим приложений=" + AppSelection.LoadMode(context) +
                ", выбранных приложений=" + AppSelection.LoadApps(context).Count);

            if (active != null)
            {
                Say("STATE: соединений=" + active.Connections +
                    ", активных=" + active.Active +
                    ", вверх=" + active.BytesUp +
                    ", вниз=" + active.BytesDown +
                    ", hello снято=" + HelloCapture.CachedCount);
            }

            // Telegram-прокси: состояние и готовая ссылка.
            //
            // Ссылку печатаем здесь же, потому что её надо откуда-то взять:
            // на телефоне нет консоли, а открывать её в Telegram нужно один раз.
            // Секрет при этом попадает в logcat — это осознанно: logcat читает
            // только владелец телефона через adb, а без ссылки функцию не
            // проверить. В интерфейсе секрет по-прежнему скрыт.
            Say("STATE: tgproxy=" + (TgProxyHost.IsRunning ? "on" : "off") +
                ", порт=" + (TgProxyHost.Port > 0 ? TgProxyHost.Port : 0));

            if (TgProxyHost.IsRunning)
            {
                Say("STATE: tgproxy link=" + TgProxyHost.BuildLink(context));
            }
        }

        // ---------------------------------------------------------------- проверка

        private static void HandleCheck(Context context)
        {
            Say("CHECK: старт, 6 провайдеров по IP");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<BlockCheckResult> results = BlockCheck.ProbeAll(BlockCheck.Targets(), 6000, 4);

                    foreach (BlockCheckResult r in results)
                        Say("CHECK: " + r.Provider + " (" + r.Ip + ") = " + r.Short());

                    Say("CHECK: ВЕРДИКТ " + BlockCheck.Verdict(results));
                }
                catch (Exception ex)
                {
                    Say("CHECK: ошибка " + ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        // ------------------------------------------------------------- приложения

        private static void HandleApps(Context context, Intent intent)
        {
            string mode = intent.GetStringExtra("mode");
            string package = intent.GetStringExtra("package");

            HashSet<string> selected = AppSelection.LoadApps(context);

            if (!string.IsNullOrEmpty(mode))
            {
                AppSelection.Save(context, mode, selected);
                Say("APPS: режим -> " + mode);
            }

            if (!string.IsNullOrEmpty(package))
            {
                if (selected.Contains(package)) selected.Remove(package);
                else selected.Add(package);

                AppSelection.Save(context, AppSelection.LoadMode(context), selected);
                Say("APPS: " + package + " -> " + (selected.Contains(package) ? "включено" : "выключено"));
            }

            Say("APPS: режим=" + AppSelection.LoadMode(context) +
                ", выбрано=" + AppSelection.LoadApps(context).Count +
                (ZapretVpnService.IsRunning ? " — перезапустите VPN, чтобы применить" : ""));
        }

        // ---------------------------------------------------------------- DNS

        /// <summary>
        /// Сравнить системный резолвер и DoH по нескольким именам.
        ///
        /// Это первое, что надо смотреть, когда «обход не работает»: если DoH
        /// тоже отдаёт подлог, дальше проверять нечего — мы стучимся в чёрную
        /// дыру, и никакая техника не поможет.
        /// </summary>
        private static void HandleDns(Context context)
        {
            string[] hosts = { "www.youtube.com", "discord.com", "twitch.tv", "ya.ru" };

            foreach (string host in hosts)
            {
                string system = "нет";

                try
                {
                    System.Net.IPAddress[] addresses = System.Net.Dns.GetHostAddresses(host);

                    if (addresses != null && addresses.Length > 0)
                    {
                        system = addresses[0].ToString();

                        if (Doh.IsPoisoned(addresses[0])) system += " ПОДЛОГ";
                    }
                }
                catch (Exception ex)
                {
                    system = "ошибка " + ex.GetType().Name;
                }

                List<System.Net.IPAddress> doh = Doh.Resolve(host);

                string dohText = doh.Count > 0 ? doh[0].ToString() : "НЕ РАЗРЕШИЛОСЬ";

                Say("DNS: " + host + " | системный " + system + " | DoH " + dohText);
            }

            Say("DNS: сервер DoH = " + (Doh.LastServer.Length > 0 ? Doh.LastServer : "не ответил ни один"));
        }

        // ------------------------------------------------------------- проба

        /// <summary>
        /// Проверить обход одним настоящим соединением: SOCKS5 → TLS.
        ///
        /// Идёт через работающий VPN-прокси, то есть ровно тем путём, которым
        /// ходит трафик приложений. Результат в лог, чтобы можно было сравнить
        /// техники без единого тапа.
        /// </summary>
        private static void HandleTry(Context context, Intent intent)
        {
            string host = intent.GetStringExtra("host");

            if (string.IsNullOrEmpty(host)) host = "www.youtube.com";

            string captured = host;

            ThreadPool.QueueUserWorkItem(delegate
            {
                Socks5Proxy proxy = ZapretVpnService.Running;

                if (proxy == null)
                {
                    Say("TRY: VPN не запущен");
                    return;
                }

                int port = proxy.ActualPort;

                Say("TRY: " + captured + " через 127.0.0.1:" + port + " (" + proxy.Config.Describe() + ")");

                bool ok = Trial(port, captured, out string detail);

                Say("TRY: " + captured + " = " + (ok ? "ОК" : "НЕ ПРОШЛО") + " (" + detail + ")");
            });
        }

        private static bool Trial(int port, string host, out string detail)
        {
            detail = "";

            System.Net.Sockets.TcpClient client = null;

            try
            {
                client = new System.Net.Sockets.TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(System.Net.IPAddress.Loopback, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(5000))
                {
                    detail = "прокси не принял";
                    return false;
                }

                client.EndConnect(ar);

                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;

                System.Net.Sockets.NetworkStream stream = client.GetStream();

                stream.Write(new byte[] { 5, 1, 0 }, 0, 3);

                byte[] hello = ReadExact(stream, 2);

                if (hello == null || hello[1] != 0)
                {
                    detail = "SOCKS5 приветствие";
                    return false;
                }

                byte[] name = System.Text.Encoding.ASCII.GetBytes(host);
                byte[] request = new byte[7 + name.Length];

                request[0] = 5;
                request[1] = 1;
                request[2] = 0;
                request[3] = 3;
                request[4] = (byte)name.Length;

                Buffer.BlockCopy(name, 0, request, 5, name.Length);

                request[5 + name.Length] = 0x01;
                request[6 + name.Length] = 0xBB;

                stream.Write(request, 0, request.Length);

                byte[] reply = ReadExact(stream, 10);

                if (reply == null || reply[1] != 0)
                {
                    detail = "CONNECT отказан, код " + (reply == null ? "?" : reply[1].ToString());
                    return false;
                }

                using (System.Net.Security.SslStream ssl =
                       new System.Net.Security.SslStream(stream, false, delegate { return true; }))
                {
                    ssl.AuthenticateAsClient(host);

                    detail = "TLS " + ssl.SslProtocol;

                    return ssl.IsAuthenticated;
                }
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
        }

        private static byte[] ReadExact(System.Net.Sockets.NetworkStream stream, int count)
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

        private static void Say(string line)
        {
            global::Android.Util.Log.Info("AutoZapret", line);
        }    }
}
