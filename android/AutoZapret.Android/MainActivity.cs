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
        private System.Threading.Timer _timer;

        private int _port = 1080;
        private string _technique = DesyncPreset.Split1.Id;
        private int _delay;
        private bool _blockQuic = true;

        private volatile bool _busy;
        private volatile bool _checking;

        private const int VpnRequestCode = 1001;

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
        }

        // --------------------------------------------------------- состояние

        private void LoadSettings()
        {
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            _port = prefs.GetInt("port", 1080);
            _technique = DesyncPreset.ById(prefs.GetString("technique", DesyncPreset.Split1.Id)).Id;
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

            string error = _proxy.Start();

            _busy = false;
            PushState();

            PushLog(error == null
                ? "обход включён: SOCKS5 на 127.0.0.1:" + _proxy.ActualPort + " · " + _proxy.Config.Describe()
                : "ОШИБКА: " + error);
        }

        /// <summary>Перенести выбранную технику в конфигурацию прокси.</summary>
        private void ApplyTechnique(Socks5Proxy proxy)
        {
            proxy.Config.Port = _port;
            proxy.Config.DelayMs = _delay;
            proxy.Config.BlockQuic = _blockQuic;

            DesyncPreset.ById(_technique).ApplyTo(proxy.Config);
        }

        private void StopProxy()
        {
            if (_proxy == null) return;

            _busy = true;
            PushState();

            try { _proxy.Stop(); } catch { }
            _proxy = null;

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

            // Пока активность на экране — принимаем headless-команды из adb.
            // VPN-сервис снаружи не запустить (BIND_VPN_SERVICE), а отсюда можно.
            CommandReceiver.Forward = delegate (string action, Intent intent)
            {
                RunOnUiThread(delegate
                {
                    if (action == CommandReceiver.ActionVpnOn) PrepareVpn();
                    else if (action == CommandReceiver.ActionVpnOff) StopVpn();

                    PushLog("headless: " + action);
                });
            };
        }

        protected override void OnPause()
        {
            CommandReceiver.Forward = null;

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
                            int delay = msg.OptInt("delay", _delay);
                            bool blockQuic = msg.OptBoolean("blockQuic", _blockQuic);

                            if (port < 1024 || port > 65535) port = 1080;
                            if (delay < 0) delay = 0;

                            technique = DesyncPreset.ById(technique).Id;

                            bool changed = port != _port || technique != _technique ||
                                           delay != _delay || blockQuic != _blockQuic;

                            _port = port;
                            _technique = technique;
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