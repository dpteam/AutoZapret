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

        private ParcelFileDescriptor _tun;
        private Socks5Proxy _proxy;
        private Thread _thread;
        private volatile bool _running;

        public static bool IsRunning { get; private set; }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            string action = intent == null ? ActionStart : intent.Action;

            if (action == ActionStop)
            {
                StopTunnel();
                return StartCommandResult.NotSticky;
            }

            StartForeground(NotificationId, BuildNotification());
            StartTunnel();

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

            // Себя из туннеля исключаем всегда: иначе SOCKS5-соединение к
            // 127.0.0.1 пойдёт в туннель и получится петля.
            try { builder.AddDisallowedApplication(PackageName); } catch { }

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

            string config = HevTunnel.BuildConfig(10, port, false);

            _running = true;
            IsRunning = true;

            _thread = new Thread(delegate () { TunnelLoop(_tun.Fd, config); })
            {
                IsBackground = true,
                Name = "hev-tunnel"
            };

            _thread.Start();

            LogLine("VPN включён: tun → SOCKS5 127.0.0.1:" + port);
        }

        private void TunnelLoop(int fd, string config)
        {
            try
            {
                string error = HevTunnel.Run(fd, config);
                if (error != null) LogLine(error);
            }
            catch (Exception ex)
            {
                LogLine("туннель упал: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                HevTunnel.Cleanup();
            }
        }

        private void StopTunnel()
        {
            if (!_running && _tun == null) return;

            _running = false;
            IsRunning = false;

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
        }
    }
}