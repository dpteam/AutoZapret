using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Webkit;
using Android.Widget;
using AutoZapret.Android.Engine;
using Java.Interop;

namespace AutoZapret.Android
{
    /// <summary>
    /// Единственный экран Android-версии.
    ///
    /// Внутри — WebView с обычной HTML-страницей, как и на десктопе: интерфейс
    /// остаётся тем же по духу и переиспользует вёрстку/палитру, а мост между
    /// Java и JS — одна функция `azDeliver` и один метод `post`.
    ///
    /// Движок сейчас — локальный SOCKS5 с разрезом TLS-рукопожатия. Он не
    /// требует ни root, ни VPN: Telegram умеет SOCKS5 сам.
    /// </summary>
    [Activity(Label = "AutoZapret", MainLauncher = true,
              Theme = "@android:style/Theme.Material.NoActionBar")]
    public sealed class MainActivity : Activity
    {
        private WebView _web;
        private Socks5Proxy _proxy;
        private readonly CiadpiEngine _engine = new CiadpiEngine();
        private System.Threading.Timer _timer;

        private int _port = 1080;
        private string _technique = DesyncPreset.Split1.Id;
        private string _zapretPreset = "";
        private string _networkMode = NetworkBinding.ModeAuto;
        private int _delay;
        private bool _blockQuic = true;

        private volatile bool _busy;
        private volatile bool _checking;
        private volatile bool _tuning;

        private const int VpnRequestCode = 1001;
        private const int NotificationRequestCode = 1002;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            LoadSettings();

            _web = new WebView(this);
            _web.Settings.JavaScriptEnabled = true;
            _web.Settings.DomStorageEnabled = true;
            _web.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#0D0F1A"));

            // file:///android_asset — единственный источник страницы, наружу
            // доступ не нужен; переходы по ссылкам запрещены
            _web.Settings.AllowFileAccess = true;
            _web.Settings.AllowContentAccess = false;
            _web.Settings.AllowFileAccessFromFileURLs = false;
            _web.Settings.AllowUniversalAccessFromFileURLs = false;
            _web.Settings.SetSupportZoom(false);

            _web.AddJavascriptInterface(new Bridge(this), "Android");

            SetContentView(_web);

            _web.LoadUrl("file:///android_asset/mobile.html");

            // Раз в секунду — свежие счётчики. UI-поток не трогаем: Deliver
            // сам переходит в него через RunOnUiThread.
            _timer = new System.Threading.Timer(delegate { PushState(); }, null, 1000, 1000);

            // Лог сервиса (VPN) показываем в том же окне, что и лог SOCKS5:
            // иначе «VPN включён» есть только в logcat, а человек видит пусто.
            ZapretVpnService.LogSink = PushLog;

            DescribeEnvironment();

            RequestNotificationPermission();

            // Обработчик headless-команд живёт, пока жив процесс, а не пока
            // виден экран. Иначе `adb ... VPN.ON` работает только при открытом
            // приложении: стоит экрану погаснуть, OnPause обнуляет Forward, и
            // команда отвечает «приложение закрыто» — хотя оно живо.
            CommandReceiver.Forward = delegate (string action, Intent intent)
            {
                RunOnUiThread(delegate
                {
                    if (action == CommandReceiver.ActionVpnOn) PrepareVpn();
                    else if (action == CommandReceiver.ActionVpnOff) StopVpn();
                    else if (action == CommandReceiver.ActionProxy)
                    {
                        bool enabled = intent != null && intent.GetBooleanExtra("enabled", false);

                        if (enabled) StartProxy();
                        else StopProxy();
                    }
                    else if (action == CommandReceiver.ActionTune) RunTune();

                    PushLog("headless: " + action);
                });
            };
        }

        /// <summary>
        /// Спросить разрешение на уведомления (Android 13+).
        ///
        /// Без него система ставит приложению importance=NONE, и уведомление
        /// сервиса не показывается вообще — вместе с кнопкой «Выключить».
        /// Объявления в манифесте недостаточно: с API 33 это разрешение
        /// рантаймовое. Проверено на телефоне: канал создавался, а в шторке
        /// ничего не было.
        /// </summary>
        private void RequestNotificationPermission()
        {
            try
            {
                if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;

                const string permission = "android.permission.POST_NOTIFICATIONS";

                if (CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted) return;

                RequestPermissions(new[] { permission }, NotificationRequestCode);
            }
            catch
            {
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
                                                       global::Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode != NotificationRequestCode) return;

            bool granted = grantResults != null && grantResults.Length > 0 &&
                           grantResults[0] == global::Android.Content.PM.Permission.Granted;

            PushLog(granted
                ? "уведомления разрешены: в шторке будет кнопка «Выключить»"
                : "уведомления запрещены: кнопка «Выключить» в шторке не появится");
        }

        // --------------------------------------------------------- состояние

        private void LoadSettings()
        {
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            _port = prefs.GetInt("port", 1080);
            _technique = DesyncPreset.ById(prefs.GetString("technique", DesyncPreset.Split1.Id)).Id;
            _zapretPreset = prefs.GetString("zapret_preset", "");
            _delay = prefs.GetInt("delay", 0);
            _blockQuic = prefs.GetBoolean("block_quic", true);
        }

        private void SaveSettings()
        {
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            using (ISharedPreferencesEditor editor = prefs.Edit())
            {
                editor.PutInt("port", _port);
                editor.PutString("technique", _technique);
                editor.PutString("zapret_preset", _zapretPreset);
                editor.PutString("network_mode", _networkMode);
                editor.PutInt("delay", _delay);
                editor.PutBoolean("block_quic", _blockQuic);
                editor.Apply();
            }
        }

        private void StartProxy()
        {
            if (_proxy != null && _proxy.IsRunning) return;

            _busy = true;
            PushState();

            _proxy = new Socks5Proxy();
            ApplyTechnique(_proxy);
            _proxy.Log = delegate (string line) { PushLog(line); };

            // Внешний движок byedpi: если бинарник есть, desync делает он,
            // наш прокси остаётся спереди ради честного DNS через DoH.
            StartEngine(_proxy);

            string error = _proxy.Start();

            _busy = false;
            PushState();

            PushLog(error == null
                ? "обход включён: SOCKS5 на 127.0.0.1:" + _proxy.ActualPort + " · " + _proxy.Config.Describe()
                : "ОШИБКА: " + error);
        }

        private static string NormalizeMode(string mode)
        {
            if (mode == NetworkBinding.ModeWifi) return NetworkBinding.ModeWifi;
            if (mode == NetworkBinding.ModeMobile) return NetworkBinding.ModeMobile;

            return NetworkBinding.ModeAuto;
        }

        /// <summary>Перенести выбранную технику в конфигурацию прокси.</summary>
        private void ApplyTechnique(Socks5Proxy proxy)
        {
            // Настройки читаем из SharedPreferences, а не из полей в памяти.
            // Иначе headless-команда SET (adb) меняет файл, но запущенный
            // прокси продолжает работать со старым значением из поля — и в
            // логе видно «фейк TTL 8» в настройках и «разрез 1» в работе.
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            _port = prefs.GetInt("port", _port);
            _technique = DesyncPreset.ById(prefs.GetString("technique", _technique)).Id;
            _zapretPreset = prefs.GetString("zapret_preset", _zapretPreset);
            _networkMode = NormalizeMode(prefs.GetString("network_mode", _networkMode));
            _delay = prefs.GetInt("delay", _delay);
            _blockQuic = prefs.GetBoolean("block_quic", _blockQuic);

            proxy.Config.Port = _port;
            proxy.Config.DelayMs = _delay;
            proxy.Config.BlockQuic = _blockQuic;

            // При двух активных сетях (Wi-Fi + мобильная) маршрут выбирает
            // система, и не всегда тот, что нужен. Привязываем процесс.
            string bindResult = NetworkBinding.BindProcess(this, _networkMode);

            PushLog("сеть: " + bindResult +
                    " · доступно: " + string.Join(", ", NetworkBinding.Describe(this).ToArray()));

            // Порядок важности:
            //   1. профиль, подобранный под ЭТУ сеть — только если техника
            //      явно «auto». Раньше подобранный профиль перекрывал ручной
            //      выбор ВСЕГДА, и это был молчаливый баг: человек выбирал
            //      «Фейк + TTL 8», а работал старый «разрез 1» из прошлого
            //      автоподбора — снаружи выглядело как «выбор не действует»;
            //   2. профиль zapret, если выбран;
            //   3. ручная техника.
            if (_technique == "auto")
            {
                TuneProfile tuned = NetworkTuner.ForCurrent(this);

                if (tuned != null)
                {
                    tuned.ApplyTo(proxy.Config);
                    PushLog("техника: авто, профиль сети «" +
                            NetworkTuner.NameOf(NetworkTuner.CurrentKey(this)) + "» — " + tuned.Describe());
                    return;
                }
            }

            if (!string.IsNullOrEmpty(_zapretPreset))
            {
                ZapretPreset preset = ZapretPresets.ById(this, _zapretPreset);

                if (preset != null && preset.Works)
                {
                    preset.ApplyTo(proxy.Config);
                    PushLog("техника: профиль zapret «" + preset.Name + "»");
                    return;
                }
            }

            DesyncPreset chosen = DesyncPreset.ById(_technique);

            chosen.ApplyTo(proxy.Config);
            PushLog("техника: " + chosen.Name);
        }

        /// <summary>Поднять движок byedpi и направить в него прокси.</summary>
        private void StartEngine(Socks5Proxy proxy)
        {
            try
            {
                int enginePort = _port == 1081 ? 1082 : 1081;

                string args = EngineArgsFor(_technique);

                // Дочерний процесс не наследует привязку процесса, поэтому при
                // двух сетях ему нужен явный адрес.
                string address = NetworkBinding.AddressOf(this, _networkMode);

                // Без явного адреса byedpi привязывает исходящие к `::` (IPv6
                // any). У приложения IPv6 может отсутствовать, и тогда connect
                // висит до таймаута — проверено: тот же движок из-под shell
                // работает, а из приложения нет. 0.0.0.0 возвращает его на IPv4.
                if (address.Length == 0) address = "0.0.0.0";

                args = (args + " -I " + address).Trim();

                string error = _engine.Start(this, enginePort, args, PushLog);

                if (error != null)
                {
                    PushLog("движок недоступен, работаю своим: " + error);
                    return;
                }

                // Проверяем движок настоящим соединением, прежде чем на него
                // переключать весь трафик. Проверено на телефоне: движок может
                // запуститься и при этом не иметь выхода в сеть (из-под shell
                // тот же бинарник работает) — тогда лучше сразу откатиться на
                // свой движок, чем отдавать ему все соединения.
                if (!EngineWorks(enginePort))
                {
                    PushLog("движок запустился, но не соединяется — работаю своим");
                    _engine.Stop();
                    return;
                }

                proxy.Config.ForwardPort = enginePort;
                PushLog("обход через byedpi на 127.0.0.1:" + enginePort);
            }
            catch (Exception ex)
            {
                PushLog("движок: " + ex.GetType().Name + " — работаю своим");
            }
        }

        /// <summary>Проверка движка — общая для активности и сервиса.</summary>
        internal static bool EngineWorksFor(int port)
        {
            return EngineWorks(port);
        }

        /// <summary>
        /// Проверить, что движок действительно соединяется.
        ///
        /// Берём 1.1.1.1:443 — он доступен почти всегда и не зависит от
        /// блокировок. Если движок не может до него достучаться, он не годится
        /// для трафика вообще.
        /// </summary>
        private static bool EngineWorks(int port)
        {
            System.Net.Sockets.TcpClient client = null;

            try
            {
                client = new System.Net.Sockets.TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(System.Net.IPAddress.Loopback, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(2000)) return false;

                client.EndConnect(ar);

                client.ReceiveTimeout = 4000;
                client.SendTimeout = 4000;

                System.Net.Sockets.NetworkStream stream = client.GetStream();

                stream.Write(new byte[] { 5, 1, 0 }, 0, 3);

                byte[] hello = new byte[2];

                if (stream.Read(hello, 0, 2) != 2 || hello[1] != 0) return false;

                // CONNECT 1.1.1.1:443
                stream.Write(new byte[] { 5, 1, 0, 1, 1, 1, 1, 1, 0x01, 0xBB }, 0, 10);

                byte[] reply = new byte[10];
                int got = 0;

                while (got < 10)
                {
                    int n = stream.Read(reply, got, 10 - got);
                    if (n <= 0) break;
                    got += n;
                }

                return got == 10 && reply[1] == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
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

            // fake8 и всё остальное: проверенная на живой сети комбинация
            return "--fake -1 --ttl 8";
        }

        private void StopProxy()
        {
            if (_proxy == null) return;

            _busy = true;
            PushState();

            try { _proxy.Stop(); } catch { }
            _proxy = null;

            try { _engine.Stop(); } catch { }

            _busy = false;
            PushState();
        }

        // ------------------------------------------------------------- VPN

        /// <summary>
        /// Спросить у системы разрешение VPN и запустить сервис.
        ///
        /// Разрешение выдаётся один раз на приложение; повторно диалог появится
        /// только после отзыва. Если система разрешение уже дала, Prepare
        /// возвращает null и мы стартуем сразу.
        /// </summary>
        private void PrepareVpn()
        {
            // Локальный SOCKS5 в активности и VPN-сервис не должны делить порт
            StopProxy();

            Intent prepare = VpnService.Prepare(this);

            if (prepare != null)
            {
                PushLog("система спрашивает разрешение VPN…");
                StartActivityForResult(prepare, VpnRequestCode);
                return;
            }

            StartVpnService();
        }

        private void StartVpnService()
        {
            PushLog("запускаю VPN…");

            Intent intent = new Intent(this, typeof(ZapretVpnService));
            intent.SetAction(ZapretVpnService.ActionStart);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(intent);
            else
                StartService(intent);

            PushState();
        }

        private void StopVpn()
        {
            Intent intent = new Intent(this, typeof(ZapretVpnService));
            intent.SetAction(ZapretVpnService.ActionStop);

            try { StartService(intent); } catch { }
            PushState();
        }

        protected override void OnResume()
        {
            base.OnResume();
        }

        protected override void OnPause()
        {
            base.OnPause();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != VpnRequestCode) return;

            if (resultCode == Result.Ok) StartVpnService();
            else PushLog("разрешение VPN не выдано — режим остался выключенным");

            PushState();
        }

        // ------------------------------------------------------------- мост

        /// <summary>Один метод наружу: страница присылает JSON, как и на десктопе.</summary>
        private void OnMessage(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                Org.Json.JSONObject msg = new Org.Json.JSONObject(json);
                string action = msg.OptString("action", "");

                switch (action)
                {
                    case "ready":
                        SendTechniques();
                        PushState();
                        SendTuneState();
                        PushLog("Android-версия " + PackageManager.GetPackageInfo(PackageName, 0).VersionName);
                        break;

                    case "proxy.toggle":
                        if (msg.OptBoolean("enabled", false)) StartProxy();
                        else StopProxy();
                        break;

                    case "vpn.toggle":
                        if (msg.OptBoolean("enabled", false)) PrepareVpn();
                        else StopVpn();
                        break;

                    case "proxy.state":
                        PushState();
                        break;

                    case "settings.set":
                        {
                            int port = msg.OptInt("port", _port);
                            string technique = msg.OptString("technique", _technique);
                            string zapret = msg.OptString("zapretPreset", _zapretPreset);
                            int delay = msg.OptInt("delay", _delay);
                            bool blockQuic = msg.OptBoolean("blockQuic", _blockQuic);
                            string networkMode = NormalizeMode(msg.OptString("networkMode", _networkMode));

                            if (port < 1024 || port > 65535) port = 1080;
                            if (delay < 0) delay = 0;

                            technique = DesyncPreset.ById(technique).Id;

                            bool changed = port != _port || technique != _technique ||
                                           zapret != _zapretPreset || networkMode != _networkMode ||
                                           delay != _delay || blockQuic != _blockQuic;

                            _port = port;
                            _technique = technique;
                            _zapretPreset = zapret ?? "";
                            _networkMode = networkMode;
                            _delay = delay;
                            _blockQuic = blockQuic;
                            SaveSettings();

                            // Настройки применяются к новому запуску: менять
                            // разрез на ходу — значит мешать уже открытым
                            // соединениям.
                            if (changed && ZapretVpnService.IsRunning)
                                PushLog("настройки сохранены: перезапустите VPN, чтобы применить");
                            else if (changed && _proxy != null && _proxy.IsRunning)
                                PushLog("настройки сохранены: перезапустите обход, чтобы применить");
                            else if (changed)
                                PushLog("техника: " + DesyncPreset.ById(_technique).Name +
                                        ", порт " + _port +
                                        (_delay > 0 ? ", пауза " + _delay + " мс" : "") +
                                        (_blockQuic ? ", QUIC закрыт" : ""));

                            break;
                        }

                    case "check.run":
                        RunBlockCheck();
                        break;

                    case "tune.run":
                        RunTune();
                        break;

                    case "tune.list":
                        SendTuneState();
                        break;

                    case "tune.auto":
                        {
                            bool enabled = msg.OptBoolean("enabled", NetworkTuner.AutoApplyEnabled(this));

                            NetworkTuner.SetAutoApply(this, enabled);
                            PushLog("автопрофиль: " + (enabled ? "включён" : "выключен"));
                            SendTuneState();

                            break;
                        }

                    case "bundle.share":
                        ShareSupportBundle();
                        break;

                    case "bundle.log":
                        {
                            // Голова списка техник для headless-режима.
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine("--- сводка для отчёта ---");
                            sb.Append(AppInfo.Describe());
                            PushLog(sb.ToString());
                            break;
                        }

                    case "apps.list":
                        SendApps();
                        break;

                    case "apps.mode":
                        {
                            string mode = msg.OptString("mode", AppSelection.ModeAll);

                            AppSelection.Save(this, mode, AppSelection.LoadApps(this));
                            PushState();

                            PushLog("режим приложений: " + ModeLabel(mode) +
                                    (ZapretVpnService.IsRunning ? " — перезапустите VPN" : ""));

                            break;
                        }

                    case "apps.toggle":
                        {
                            string package = msg.OptString("package", "");
                            if (package.Length == 0) break;

                            System.Collections.Generic.HashSet<string> selected = AppSelection.LoadApps(this);

                            if (selected.Contains(package)) selected.Remove(package);
                            else selected.Add(package);

                            AppSelection.Save(this, AppSelection.LoadMode(this), selected);
                            PushState();

                            break;
                        }

                    default:
                        PushLog("не поддерживается на Android: " + action);
                        break;
                }
            }
            catch (Exception ex)
            {
                PushLog("ОШИБКА разбора сообщения: " + ex.Message);
            }
        }

        // -------------------------------------------------------- отправка в JS

        /// <summary>Список установленных приложений плюс текущий выбор — для экрана.</summary>
        private void SendApps()
        {
            Org.Json.JSONArray items = new Org.Json.JSONArray();
            System.Collections.Generic.HashSet<string> selected = AppSelection.LoadApps(this);

            foreach (AppEntry entry in AppSelection.Installed(this))
            {
                Org.Json.JSONObject item = new Org.Json.JSONObject();
                item.Put("package", entry.Package);
                item.Put("label", entry.Label);
                item.Put("on", selected.Contains(entry.Package));

                items.Put(item);
            }

            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "apps");
            msg.Put("mode", AppSelection.LoadMode(this));
            msg.Put("items", items);

            Deliver(msg.ToString());
        }

        private static string ModeLabel(string mode)
        {
            if (mode == AppSelection.ModeOnly) return "только выбранные";
            if (mode == AppSelection.ModeExcept) return "все, кроме выбранных";
            return "весь телефон";
        }

        /// <summary>Список техник — из движка, а не из разметки.</summary>
        private void SendTechniques()
        {
            Org.Json.JSONArray items = new Org.Json.JSONArray();

            foreach (DesyncPreset preset in DesyncPreset.All)
            {
                Org.Json.JSONObject item = new Org.Json.JSONObject();
                item.Put("id", preset.Id);
                item.Put("name", preset.Name);
                item.Put("hint", preset.Hint);

                items.Put(item);
            }

            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "techniques");
            msg.Put("items", items);

            Deliver(msg.ToString());

            // Профили zapret — отдельным списком: это не наши пресеты, а
            // переведённые стратегии winws. Смешивать их в один список нельзя:
            // у них разная природа, и человек должен видеть, откуда что.
            Org.Json.JSONArray zapret = new Org.Json.JSONArray();

            foreach (ZapretPreset preset in ZapretPresets.All(this))
            {
                Org.Json.JSONObject item = new Org.Json.JSONObject();
                item.Put("id", preset.Id);
                item.Put("name", preset.Name);
                item.Put("source", preset.Source);
                item.Put("note", preset.Note);
                item.Put("works", preset.Works);
                item.Put("reason", preset.Unsupported);
                item.Put("describe", preset.Describe());

                zapret.Put(item);
            }

            Org.Json.JSONObject zmsg = new Org.Json.JSONObject();
            zmsg.Put("type", "zapret-presets");
            zmsg.Put("items", zapret);

            Deliver(zmsg.ToString());
        }

        /// <summary>
        /// Прогнать тест «обрыв на 16–20 КБ» в фоне и отдать результат в UI.
        ///
        /// В фоновом потоке, потому что проба идёт секунды: если делать её в
        /// UI-потоке, кнопка «проверить» замирает и выглядит как зависание.
        /// </summary>
        private void RunBlockCheck()
        {
            if (_checking) return;

            _checking = true;
            PushLog("проверка блокировки: 6 провайдеров, до 40 КБ на соединение…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                List<BlockCheckResult> results;

                try
                {
                    results = BlockCheck.ProbeAll(BlockCheck.Targets(), 6000, 4);
                }
                catch (Exception ex)
                {
                    _checking = false;

                    PushLog("проверка не удалась: " + ex.GetType().Name + ": " + ex.Message);
                    SendCheckError(ex.Message);

                    return;
                }

                _checking = false;

                string verdict = BlockCheck.Verdict(results);

                PushLog("проверка: " + verdict);

                SendCheck(results, verdict);
            });
        }

        private void SendCheck(List<BlockCheckResult> results, string verdict)
        {
            Org.Json.JSONArray items = new Org.Json.JSONArray();

            foreach (BlockCheckResult r in results)
            {
                Org.Json.JSONObject item = new Org.Json.JSONObject();
                item.Put("provider", r.Provider);
                item.Put("ip", r.Ip);
                item.Put("bytes", r.BytesSent);
                item.Put("survived", r.Survived);
                item.Put("blocked", r.LooksBlocked);
                item.Put("detail", r.Short());

                items.Put(item);
            }

            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "check");
            msg.Put("verdict", verdict);
            msg.Put("items", items);

            Deliver(msg.ToString());
        }

        private void SendCheckError(string error)
        {
            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "check");
            msg.Put("verdict", "ошибка: " + error);
            msg.Put("items", new Org.Json.JSONArray());

            Deliver(msg.ToString());
        }

        /// <summary>
        /// Заполнить сведения об устройстве и сети — они попадут в архив
        /// поддержки. Собираем при старте, а сеть — ещё и по запросу: она
        /// меняется (Wi-Fi, мобильная, VPN).
        /// </summary>
        private void DescribeEnvironment()
        {
            try
            {
                AppInfo.Version = PackageManager.GetPackageInfo(PackageName, 0).VersionName;
                AppInfo.AndroidVersion = Build.VERSION.Release;
                AppInfo.Sdk = (int)Build.VERSION.SdkInt;
                AppInfo.Device = Build.Device;
                AppInfo.Model = Build.Model;
                AppInfo.Abi = Build.SupportedAbis != null && Build.SupportedAbis.Count > 0
                    ? Build.SupportedAbis[0]
                    : "?";
            }
            catch { }

            AppInfo.Settings = delegate
            {
                return "техника: " + DesyncPreset.ById(_technique).Name + " (" + _technique + ")" +
                       "\nпорт SOCKS5: " + _port +
                       "\nпауза: " + _delay + " мс" +
                       "\nQUIC: " + (_blockQuic ? "закрыт" : "открыт") +
                       "\nрежим приложений: " + AppSelection.LoadMode(this) +
                       "\nвыбрано приложений: " + AppSelection.LoadApps(this).Count +
                       "\nVPN: " + (ZapretVpnService.IsRunning ? "включён" : "выключен");
            };

            AppInfo.Network = delegate { return DescribeNetwork(); };
            AppInfo.DnsState = delegate { return DescribeDns(); };
        }

        private string DescribeNetwork()
        {
            try
            {
                global::Android.Net.ConnectivityManager manager =
                    GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager;

                if (manager == null) return "нет доступа к сведениям о сети";

                global::Android.Net.Network network = manager.ActiveNetwork;

                if (network == null) return "активной сети нет";

                global::Android.Net.NetworkCapabilities caps = manager.GetNetworkCapabilities(network);
                global::Android.Net.LinkProperties props = manager.GetLinkProperties(network);

                string transport = "?";

                if (caps != null)
                {
                    if (caps.HasTransport(global::Android.Net.TransportType.Wifi)) transport = "Wi-Fi";
                    else if (caps.HasTransport(global::Android.Net.TransportType.Cellular)) transport = "мобильная";
                    else if (caps.HasTransport(global::Android.Net.TransportType.Ethernet)) transport = "Ethernet";
                }

                string addresses = "";

                if (props != null)
                {
                    foreach (global::Android.Net.LinkAddress link in props.LinkAddresses)
                    {
                        if (link == null || link.Address == null) continue;
                        if (addresses.Length > 0) addresses += ", ";
                        addresses += link.Address.HostAddress;
                    }
                }

                return "тип: " + transport +
                       "\nадреса: " + (addresses.Length > 0 ? addresses : "нет") +
                       "\nIPv6 в сети: " + (HasGlobalIPv6(props) ? "есть" : "нет");
            }
            catch (Exception ex)
            {
                return "ошибка чтения сети: " + ex.GetType().Name;
            }
        }

        /// <summary>Есть ли у активной сети глобальный IPv6 (тот же критерий, что в сервисе).</summary>
        private static bool HasGlobalIPv6(global::Android.Net.LinkProperties props)
        {
            if (props == null) return false;

            try
            {
                foreach (global::Android.Net.LinkAddress link in props.LinkAddresses)
                {
                    Java.Net.InetAddress address = link == null ? null : link.Address;

                    if (address == null) continue;

                    byte[] raw = address.GetAddress();

                    if (raw == null || raw.Length != 16) continue;
                    if (raw[0] == 0xFE && (raw[1] & 0xC0) == 0x80) continue;   // fe80::/10
                    if ((raw[0] & 0xFE) == 0xFC) continue;                      // fc00::/7

                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Что отдаёт системный DNS по нашим «показательным» именам.
        ///
        /// Это самая важная строка отчёта: если тут 198.18.x.x, значит у
        /// человека подлог DNS, и «не работает YouTube» — не про наш обход.
        /// </summary>
        private string DescribeDns()        {
            StringBuilder text = new StringBuilder();

            string[] hosts = { "www.youtube.com", "discord.com", "twitch.tv" };

            foreach (string host in hosts)
            {
                try
                {
                    System.Net.IPAddress[] system = System.Net.Dns.GetHostAddresses(host);

                    string systemText = system != null && system.Length > 0
                        ? system[0].ToString()
                        : "нет";

                    if (Doh.HasPoison(system)) systemText += " (ПОДЛОГ)";

                    List<System.Net.IPAddress> ours = Doh.Resolve(host);

                    string ourText = ours.Count > 0 ? ours[0].ToString() : "нет";

                    text.AppendLine(host + ": системный " + systemText + ", через DoH " + ourText);
                }
                catch (Exception ex)
                {
                    text.AppendLine(host + ": ошибка " + ex.GetType().Name);
                }
            }

            text.AppendLine("DoH: " + (Doh.LastServer.Length > 0 ? Doh.LastServer : "не использовался") +
                            ", в кэше " + Doh.CachedCount + " имён");

            return text.ToString();
        }

        /// <summary>
        /// Собрать ZIP для поддержки и открыть системный «поделиться».
        ///
        /// Именно «поделиться», а не «сохранить»: человек должен иметь
        /// возможность сразу отправить файл в мессенджер, не ища его в
        /// файловом менеджере.
        /// </summary>
        private void ShareSupportBundle()
        {
            PushLog("собираю архив для поддержки…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                string error = null;

                try
                {
                    string dir = GetExternalFilesDir(null) != null
                        ? GetExternalFilesDir(null).AbsolutePath
                        : FilesDir.AbsolutePath;

                    path = SupportBundle.Build(dir, delegate (string line) { PushLog(line); });
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }

                if (path == null)
                {
                    PushLog("архив не собрался: " + (error ?? "неизвестная причина"));
                    SendBundle(null, null);
                    return;
                }

                long size = 0;

                try { size = new System.IO.FileInfo(path).Length; } catch { }

                PushLog("архив готов: " + path + " (" + (size / 1024) + " КБ)");

                SendBundle(path, null);
            });
        }

        private void SendBundle(string path, string error)
        {
            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "bundle");
            msg.Put("path", path ?? "");
            msg.Put("error", error ?? "");

            Deliver(msg.ToString());

            if (string.IsNullOrEmpty(path)) return;

            // Открываем системное «поделиться» — человек должен сразу отправить
            // файл в мессенджер, а не искать его в файловом менеджере.
            RunOnUiThread(delegate { ShareFile(path); });
        }

        private void ShareFile(string path)
        {
            try
            {
                // FileProvider требует AndroidX, а сторонние библиотеки у нас
                // запрещены. MediaStore есть в системе с API 29 (наш минимум):
                // кладём файл в «Загрузки» и делимся его content:// адресом.
                global::Android.Net.Uri uri = CopyToDownloads(path);

                if (uri == null)
                {
                    PushLog("файл готов, но положить в «Загрузки» не вышло: " + path);
                    return;
                }

                Intent share = new Intent(Intent.ActionSend);
                share.SetType("application/zip");
                share.PutExtra(Intent.ExtraStream, uri);
                share.PutExtra(Intent.ExtraSubject, "AutoZapret — лог для поддержки");
                share.AddFlags(ActivityFlags.GrantReadUriPermission);

                StartActivity(Intent.CreateChooser(share, "Отправить архив"));
            }
            catch (Exception ex)
            {
                PushLog("поделиться не вышло: " + ex.GetType().Name + " — файл лежит: " + path);
            }
        }

        /// <summary>Положить ZIP в общие «Загрузки» и вернуть content:// адрес.</summary>
        private global::Android.Net.Uri CopyToDownloads(string path)
        {
            try
            {
                global::Android.Content.ContentValues values = new global::Android.Content.ContentValues();

                string name = System.IO.Path.GetFileName(path);

                values.Put(global::Android.Provider.MediaStore.IMediaColumns.DisplayName, name);
                values.Put(global::Android.Provider.MediaStore.IMediaColumns.MimeType, "application/zip");
                values.Put(global::Android.Provider.MediaStore.IMediaColumns.RelativePath,
                           global::Android.OS.Environment.DirectoryDownloads + "/AutoZapret");

                global::Android.Net.Uri collection =
                    global::Android.Provider.MediaStore.Downloads.GetContentUri(
                        global::Android.Provider.MediaStore.VolumeExternalPrimary);

                global::Android.Net.Uri uri = ContentResolver.Insert(collection, values);

                if (uri == null) return null;

                using (System.IO.Stream input = System.IO.File.OpenRead(path))
                using (System.IO.Stream output = ContentResolver.OpenOutputStream(uri))
                {
                    if (output == null) return null;

                    input.CopyTo(output);
                }

                return uri;
            }
            catch (Exception ex)
            {
                PushLog("MediaStore: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Подобрать технику под текущую сеть.
        ///
        /// В фоне: перебор занимает до минуты (14 кандидатов × до 3,5 с), и
        /// делать это в UI-потоке нельзя — кнопка замрёт и всё выглядит как
        /// поломка.
        /// </summary>
        private void RunTune()
        {
            if (_tuning) return;

            _tuning = true;

            PushLog("подбор профиля: перебираю техники под текущую сеть…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                TuneOutcome outcome = null;
                Exception failure = null;

                try
                {
                    outcome = NetworkTuner.Tune(this, delegate (string line) { PushLog(line); });
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                _tuning = false;

                if (failure != null)
                {
                    PushLog("подбор не удался: " + failure.GetType().Name + ": " + failure.Message);
                    SendTune(null, "ошибка: " + failure.Message);
                    return;
                }

                PushLog("подбор: " + outcome.Detail);

                SendTune(outcome, outcome.Detail);
            });
        }

        private void SendTune(TuneOutcome outcome, string detail)
        {
            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "tune");
            msg.Put("detail", detail ?? "");
            msg.Put("ok", outcome != null && outcome.Ok);

            Org.Json.JSONArray rows = new Org.Json.JSONArray();

            if (outcome != null)
            {
                foreach (string row in outcome.Rows) rows.Put(row);
            }

            msg.Put("rows", rows);

            Deliver(msg.ToString());

            SendTuneState();
        }

        /// <summary>Что уже подобрано для каждой сети — для карточки в интерфейсе.</summary>
        private void SendTuneState()
        {
            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "tune-state");
            msg.Put("current", NetworkTuner.CurrentKey(this));
            msg.Put("currentName", NetworkTuner.NameOf(NetworkTuner.CurrentKey(this)));
            msg.Put("auto", NetworkTuner.AutoApplyEnabled(this));

            Org.Json.JSONArray networks = new Org.Json.JSONArray();

            foreach (string key in new[] { "wifi", "mobile" })
            {
                TuneProfile profile = NetworkTuner.Load(this, key);

                Org.Json.JSONObject item = new Org.Json.JSONObject();
                item.Put("key", key);
                item.Put("name", NetworkTuner.NameOf(key));
                item.Put("profile", profile == null ? "" : profile.Describe());
                item.Put("at", NetworkTuner.LoadedAt(this, key));

                networks.Put(item);
            }

            msg.Put("networks", networks);

            Deliver(msg.ToString());
        }

        private void PushState()
        {
            Socks5Proxy active = _proxy != null && _proxy.IsRunning ? _proxy : ZapretVpnService.Running;

            bool running = active != null;
            bool vpn = ZapretVpnService.IsRunning;

            Org.Json.JSONObject state = new Org.Json.JSONObject();
            state.Put("type", "state");
            state.Put("running", running);
            state.Put("vpn", vpn);
            state.Put("busy", _busy);
            state.Put("port", _port);
            state.Put("technique", _technique);
            state.Put("zapretPreset", _zapretPreset);
            state.Put("networkMode", _networkMode);
            state.Put("delay", _delay);
            state.Put("blockQuic", _blockQuic);
            state.Put("appMode", AppSelection.LoadMode(this));

            if (running)
            {
                state.Put("connections", active.Connections);
                state.Put("active", active.Active);
                state.Put("bytesUp", active.BytesUp);
                state.Put("bytesDown", active.BytesDown);
            }

            Deliver(state.ToString());
        }

        private void PushLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // В кольцевой буфер: он уйдёт в архив поддержки. Без этого в ZIP
            // остался бы только logcat, который обычному приложению недоступен.
            AppLog.Add(line);

            // И в logcat тоже. Раньше строка уходила только в WebView, из-за
            // чего при разборе с adb было видно сообщения сервиса, но НЕ
            // сообщения прокси — а именно там причина отказа. Полчаса
            // диагностики ушли на то, чтобы понять: логов просто нет.
            global::Android.Util.Log.Info("AutoZapret", line);

            Org.Json.JSONObject msg = new Org.Json.JSONObject();
            msg.Put("type", "log");
            msg.Put("line", line);

            Deliver(msg.ToString());
        }

        private void Deliver(string json)
        {
            if (_web == null) return;

            string quoted = Org.Json.JSONObject.Quote(json);
            string script = "(window.azDeliver && window.azDeliver(" + quoted + "))";

            RunOnUiThread(delegate
            {
                try { _web.EvaluateJavascript(script, null); } catch { }
            });
        }

        // ----------------------------------------------------------- жизненный цикл

        protected override void OnDestroy()
        {
            ZapretVpnService.LogSink = null;

            try { if (_timer != null) _timer.Dispose(); } catch { }
            try { if (_proxy != null) _proxy.Dispose(); } catch { }

            base.OnDestroy();
        }

        /// <summary>Объект, который видит JavaScript как `window.Android`.</summary>
        private sealed class Bridge : Java.Lang.Object
        {
            private readonly WeakReference<MainActivity> _owner;

            public Bridge(MainActivity owner)
            {
                _owner = new WeakReference<MainActivity>(owner);
            }

            [JavascriptInterface]
            [Export("post")]
            public void Post(string json)
            {
                MainActivity owner;

                if (_owner.TryGetTarget(out owner))
                    owner.OnMessage(json);
            }
        }
    }
}