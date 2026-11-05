using System;
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
        private int _split = 1;
        private int _delay;

        private volatile bool _busy;

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
        }

        // --------------------------------------------------------- состояние

        private void LoadSettings()
        {
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            _port = prefs.GetInt("port", 1080);
            _split = prefs.GetInt("split", 1);
            _delay = prefs.GetInt("delay", 0);
        }

        private void SaveSettings()
        {
            ISharedPreferences prefs = GetSharedPreferences("autozapret", FileCreationMode.Private);

            using (ISharedPreferencesEditor editor = prefs.Edit())
            {
                editor.PutInt("port", _port);
                editor.PutInt("split", _split);
                editor.PutInt("delay", _delay);
                editor.Apply();
            }
        }

        private void StartProxy()
        {
            if (_proxy != null && _proxy.IsRunning) return;

            _busy = true;
            PushState();

            _proxy = new Socks5Proxy();
            _proxy.Config.Port = _port;
            _proxy.Config.SplitPosition = _split;
            _proxy.Config.DelayMs = _delay;
            _proxy.Log = delegate (string line) { PushLog(line); };

            string error = _proxy.Start();

            _busy = false;
            PushState();

            PushLog(error == null
                ? "обход включён: SOCKS5 на 127.0.0.1:" + _proxy.ActualPort + " · " + _proxy.Config.Describe()
                : "ОШИБКА: " + error);
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
                            int split = msg.OptInt("split", _split);
                            int delay = msg.OptInt("delay", _delay);

                            if (port < 1024 || port > 65535) port = 1080;
                            if (split < 1) split = 1;
                            if (delay < 0) delay = 0;

                            bool changed = port != _port || split != _split || delay != _delay;

                            _port = port;
                            _split = split;
                            _delay = delay;
                            SaveSettings();

                            // Настройки применяются к новому запуску: менять
                            // разрез на ходу — значит мешать уже открытым
                            // соединениям.
                            if (changed && _proxy != null && _proxy.IsRunning)
                                PushLog("настройки сохранены: перезапустите обход, чтобы применить");
                            else if (changed)
                                PushLog("настройки: порт " + _port + ", разрез по " + _split +
                                        (_delay > 0 ? ", пауза " + _delay + " мс" : ""));

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
            state.Put("split", _split);
            state.Put("delay", _delay);

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