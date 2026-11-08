using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
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
    [IntentFilter(new[] { ActionSet, ActionState, ActionCheck, ActionApps, ActionVpnOn, ActionVpnOff, ActionStopVpn })]
    public sealed class CommandReceiver : BroadcastReceiver
    {
        public const string ActionSet = "ru.autozapret.android.SET";
        public const string ActionState = "ru.autozapret.android.STATE";
        public const string ActionCheck = "ru.autozapret.android.CHECK";
        public const string ActionApps = "ru.autozapret.android.APPS";
        public const string ActionVpnOn = "ru.autozapret.android.VPN.ON";
        public const string ActionVpnOff = "ru.autozapret.android.VPN.OFF";
        public const string ActionTune = "ru.autozapret.android.TUNE";

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
                else if (action == ActionCheck) HandleCheck(context);
                else if (action == ActionApps) HandleApps(context, intent);
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
            string technique = prefs.GetString("technique", DesyncPreset.Split1.Id);
            int delay = prefs.GetInt("delay", 0);
            bool blockQuic = prefs.GetBoolean("block_quic", true);

            bool changed = false;

            int newPort = intent.GetIntExtra("port", 0);
            if (newPort >= 1024 && newPort <= 65535) { port = newPort; changed = true; }

            string newTechnique = intent.GetStringExtra("technique");
            if (!string.IsNullOrEmpty(newTechnique))
            {
                technique = DesyncPreset.ById(newTechnique).Id;
                changed = true;
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

            if (changed)
            {
                using (ISharedPreferencesEditor editor = prefs.Edit())
                {
                    editor.PutInt("port", port);
                    editor.PutString("technique", technique);
                    editor.PutInt("delay", delay);
                    editor.PutBoolean("block_quic", blockQuic);
                    editor.Apply();
                }
            }

            Say("SET: техника=" + DesyncPreset.ById(technique).Name +
                " (" + technique + "), порт=" + port +
                ", пауза=" + delay + " мс, QUIC " + (blockQuic ? "закрыт" : "открыт"));

            if (ZapretVpnService.IsRunning)
                Say("SET: VPN работает — перезапустите сервис, чтобы применить");
        }

        // -------------------------------------------------------------- состояние

        private static void HandleState(Context context)
        {
            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);

            Socks5Proxy active = ZapretVpnService.Running;

            Say("STATE: vpn=" + (ZapretVpnService.IsRunning ? "on" : "off") +
                ", техника=" + DesyncPreset.ById(prefs.GetString("technique", "split1")).Name +
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

        private static void Say(string line)
        {
            global::Android.Util.Log.Info("AutoZapret", line);
        }
    }
}
