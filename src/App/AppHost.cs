using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>
    /// Композиционный корень: собирает окно, трей и мост, разводит их события.
    ///
    /// Здесь нет бизнес-логики обхода DPI - она появится в AutoZapret.Zapret (фаза P1).
    /// Задача этого класса сейчас: окно, трей, настройки, логи, аккуратный выход.
    /// </summary>
    public sealed class AppHost : IDisposable
    {
        private readonly MainForm _form;
        private readonly TrayController _tray;
        private readonly Settings _settings;
        private bool _disposed;
        private bool _exiting;

        public AppHost()
        {
            _settings = Settings.Current;

            _form = new MainForm();
            _tray = new TrayController(_form);

            WireForm();
            WireTray();
            RegisterBridgeHandlers();

            _tray.SetState(TrayState.Off);
        }

        // -------------------------------------------------------------------- run

        public void Run(bool startMinimized)
        {
            _form.VisibilityChanged += visible =>
                Log.Debug("APP", visible ? "окно показано" : "окно свёрнуто в трей");

            if (startMinimized || _settings.StartMinimized)
            {
                // стартуем в трее: окно создаётся, но не показывается
                _form.WindowState = FormWindowState.Minimized;
                _form.ShowInTaskbar = false;
                _form.Opacity = 0;
            }

            Application.Run(_form);
        }

        public void ShowWindow()
        {
            if (_form.InvokeRequired) { _form.BeginInvoke(new Action(ShowWindow)); return; }
            _form.Opacity = 1;
            _form.ShowWindow();
        }

        // ------------------------------------------------------------------ wiring

        private void WireForm()
        {
            _form.ExitRequested += RequestExit;
        }

        private void WireTray()
        {
            _tray.ShowRequested += ShowWindow;

            _tray.SettingsRequested += delegate
            {
                ShowWindow();
                _form.NavigateTo("settings");
            };

            _tray.LogsRequested += delegate
            {
                ShowWindow();
                _form.NavigateTo("logs");
            };

            _tray.ToggleRequested += ToggleProtection;

            _tray.ExitRequested += RequestExit;
        }

        /// <summary>
        /// Включение/выключение обхода.
        /// P0: движка ещё нет — честно сообщаем, а не делаем вид, что включили.
        /// В P1 здесь будет WinwsSupervisor.Start()/Stop().
        /// </summary>
        private void ToggleProtection()
        {
            _tray.Notify(AppInfo.Name,
                "Движок обхода появится в следующей версии. Пока работает только интерфейс.",
                ToolTipIcon.Info);
            ShowWindow();
        }

        private void RegisterBridgeHandlers()
        {
            WebBridge bridge = _form.Bridge;

            bridge.Register("ready", msg =>
            {
                Log.Info("UI", "страница загрузилась и подключилась к мосту");
                SendState();
            });

            bridge.Register("state", msg => SendState());

            bridge.Register("settings.get", msg => SendState());

            bridge.Register("settings.set", msg =>
            {
                ApplySettings(msg);
                SendState();
            });

            bridge.Register("logs.get", msg =>
            {
                List<object> lines = new List<object>();
                foreach (LogEntry entry in Log.Snapshot())
                    lines.Add(entry.ToLine());

                bridge.Send(WebBridge.Msg("logs",
                    "lines", lines,
                    "file", Log.CurrentFile ?? ""));
            });

            bridge.Register("logs.clear", msg =>
            {
                Log.Clear();
                bridge.Send(WebBridge.Msg("logs", "lines", new List<object>(), "file", Log.CurrentFile ?? ""));
            });

            bridge.Register("logs.openFolder", msg => OpenPath(Paths.LogDir));
            bridge.Register("logs.openFile", msg => OpenPath(Log.CurrentFile));

            bridge.Register("tray.hide", msg => _form.HideToTray());

            bridge.Register("toggle", msg => ToggleProtection());

            bridge.Register("app.exit", msg => RequestExit());

            bridge.Register("app.info", msg =>
            {
                bridge.Send(WebBridge.Msg("info",
                    "version", AppInfo.Version,
                    "os", AppInfo.OsDescription,
                    "admin", AppInfo.IsAdmin,
                    "portable", Paths.IsPortable,
                    "dataDir", Paths.DataDir,
                    "webview2", AppInfo.WebView2RuntimeVersion() ?? ""));
            });

            bridge.Register("secret.regenerate", msg =>
            {
                _settings.ProxySecret = Settings.NewSecret();
                _settings.Save();
                SendState();
            });
        }

        // --------------------------------------------------------------- settings

        private void SendState()
        {
            Dictionary<string, object> state = WebBridge.Msg("state",
                "version", AppInfo.Version,
                "os", AppInfo.OsDescription,
                "admin", AppInfo.IsAdmin,
                "portable", Paths.IsPortable,
                "engineReady", false,
                "protection", "off",
                "strategyId", _settings.StrategyId,
                "gameFilter", _settings.GameFilter,
                "ipsetMode", _settings.IpsetMode,
                "proxyEnabled", _settings.ProxyEnabled,
                "proxyHost", _settings.ProxyHost,
                "proxyPort", (long)_settings.ProxyPort,
                "proxyLink", _settings.BuildProxyLink(),
                "theme", _settings.Theme,
                "simpleMode", _settings.SimpleMode,
                "minimizeToTray", _settings.MinimizeToTray,
                "startMinimized", _settings.StartMinimized,
                "updateEngine", _settings.UpdateEngine,
                "updateApp", _settings.UpdateApp,
                "updateLists", _settings.UpdateLists,
                "logFile", Log.CurrentFile ?? "");

            _form.Bridge.Send(state);
        }

        private void ApplySettings(Dictionary<string, object> msg)
        {
            _settings.GameFilter = Json.GetString(msg, "gameFilter", _settings.GameFilter);
            _settings.IpsetMode = Json.GetString(msg, "ipsetMode", _settings.IpsetMode);
            _settings.ProxyEnabled = Json.GetBool(msg, "proxyEnabled", _settings.ProxyEnabled);
            _settings.ProxyPort = Json.GetInt(msg, "proxyPort", _settings.ProxyPort);
            _settings.Theme = Json.GetString(msg, "theme", _settings.Theme);
            _settings.SimpleMode = Json.GetBool(msg, "simpleMode", _settings.SimpleMode);
            _settings.MinimizeToTray = Json.GetBool(msg, "minimizeToTray", _settings.MinimizeToTray);
            _settings.StartMinimized = Json.GetBool(msg, "startMinimized", _settings.StartMinimized);
            _settings.UpdateEngine = Json.GetBool(msg, "updateEngine", _settings.UpdateEngine);
            _settings.UpdateApp = Json.GetBool(msg, "updateApp", _settings.UpdateApp);
            _settings.UpdateLists = Json.GetBool(msg, "updateLists", _settings.UpdateLists);

            _settings.Save();
            Log.Info("APP", "настройки сохранены");
        }

        // ------------------------------------------------------------------ exit

        private void RequestExit()
        {
            if (_exiting) return;
            _exiting = true;

            Log.Info("APP", "выход по запросу");

            try
            {
                _settings.LastRunVersion = AppInfo.Version;
                _settings.LastRunAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                _settings.Save();
            }
            catch (Exception ex) { Log.Exception("APP", "не удалось сохранить настройки при выходе", ex); }

            try { _tray.Dispose(); } catch { }
            try { _form.CloseForReal(); } catch { }
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn("APP", "не удалось открыть " + path + ": " + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _tray.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
        }
    }
}
