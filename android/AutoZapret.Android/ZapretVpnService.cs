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

        private const int NotificationId = 42;
        private const string ChannelId = "autozapret-vpn";

        /// <summary>Адрес внутри туннеля: тот же, что ждёт hev в конфиге.</summary>
        private const string TunnelAddress = "10.10.10.10";

        /// <summary>
        /// IPv6-адрес туннеля. Нужен, только чтобы завернуть IPv6 внутрь: без
        /// маршрута `::/0` весь IPv6 у приложений идёт мимо туннеля, а значит
        /// и мимо обхода. На десктопе это вылечили так же (TcpPacket для обоих
        /// семейств). Адрес локальный (fc00::/7), в интернет он не уходит.
        /// </summary>
        private const string TunnelAddress6 = "fc00::1";

        private ParcelFileDescriptor _tun;
        private Socks5Proxy _proxy;
        private Thread _thread;
        private volatile bool _running;

        public static bool IsRunning { get; private set; }

        /// <summary>Прокси, который поднят внутри сервиса — чтобы UI видел счётчики.</summary>
        public static Socks5Proxy Running { get; private set; }

        /// <summary>Куда писать строки лога для интерфейса (ставит MainActivity).</summary>
        public static Action<string> LogSink;

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            string action = intent == null ? ActionStart : intent.Action;

            if (action == ActionStop)
            {
                StopTunnel();
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
            StopTunnel();
            base.OnRevoke();
        }

        public override void OnDestroy()
        {
            StopTunnel();
            base.OnDestroy();
        }

        // ------------------------------------------------------------- запуск

        private void StartTunnel()
        {
            if (_running) return;

            int port = GetSharedPreferences("autozapret", FileCreationMode.Private).GetInt("port", 1080);

            _proxy = new Socks5Proxy();
            _proxy.Config.Port = port;

            // Лог прокси — в тот же журнал, что видит человек. Раньше Say()
            // уходил в пустоту (Log никто не выставил), и в logcat было видно
            // только «VPN включён», а что происходит с соединениями — нет.
            _proxy.Log = LogLine;

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

            string config = HevTunnel.BuildConfig(TunnelAddress, tunnel6, port);

            _running = true;
            IsRunning = true;
            Running = _proxy;

            _thread = new Thread(delegate () { TunnelLoop(_tun.Fd, config); })
            {
                IsBackground = true,
                Name = "hev-tunnel"
            };

            _thread.Start();

            LogLine("VPN включён: tun → SOCKS5 127.0.0.1:" + port + ", режим приложений: " + mode +
                    (tunnel6 != null ? ", IPv6 внутри туннеля" : ", IPv6 нет в сети — только IPv4"));
        }

        private void TunnelLoop(int fd, string config)
        {
            try
            {
                string error = HevTunnel.Run(fd, config);
                if (error != null) LogLine(error);
                else LogLine("туннель остановлен");
            }
            catch (Exception ex)
            {
                LogLine("туннель упал: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void StopTunnel()
        {
            if (!_running && _tun == null) return;

            _running = false;
            IsRunning = false;
            Running = null;

            HevTunnel.Stop();

            try { if (_thread != null) _thread.Join(1500); } catch { }
            try { if (_tun != null) _tun.Close(); } catch { }
            _tun = null;

            try { if (_proxy != null) _proxy.Stop(); } catch { }
            _proxy = null;

            try { StopForeground(StopForegroundFlags.Remove); } catch { }

            LogLine("VPN выключен");
            StopSelf();
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

            return new Notification.Builder(this, ChannelId)
                .SetContentTitle("AutoZapret")
                .SetContentText("Обход включён")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetContentIntent(pending)
                .SetOngoing(true)
                .Build();
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