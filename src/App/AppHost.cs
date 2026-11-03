using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.App
{
    /// <summary>
    /// Композиционный корень: собирает окно, трей, движок и мост, разводит их события.
    /// </summary>
    public sealed class AppHost : IDisposable
    {
        private readonly MainForm _form;
        private readonly TrayController _tray;
        private readonly Settings _settings;
        private readonly ZapretService _zapret;
        private bool _disposed;
        private bool _exiting;

        public AppHost()
        {
            _settings = Settings.Current;

            _form = new MainForm();
            _tray = new TrayController(_form);
            _zapret = new ZapretService();

            WireForm();
            WireTray();
            WireEngine();
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

        private void WireEngine()
        {
            _zapret.StateChanged += (state, error) =>
            {
                // состояние приходит из фонового потока — уведомляем трей и UI
                TrayState trayState;
                switch (state)
                {
                    case ProtectionState.Running: trayState = TrayState.On; break;
                    case ProtectionState.Starting:
                    case ProtectionState.Stopping: trayState = TrayState.Busy; break;
                    case ProtectionState.Error: trayState = TrayState.Error; break;
                    default: trayState = TrayState.Off; break;
                }

                _tray.SetState(trayState, DescribeState(state));
                _tray.SetToggleText(state == ProtectionState.Running ? "⏹ Выключить обход" : "▶ Включить обход");

                if (state == ProtectionState.Error && !string.IsNullOrEmpty(error))
                    _tray.Notify(AppInfo.Name, error, ToolTipIcon.Error);

                PushState();
            };
        }

        private static string DescribeState(ProtectionState state)
        {
            switch (state)
            {
                case ProtectionState.Running: return "Обход включён";
                case ProtectionState.Starting: return "Запуск…";
                case ProtectionState.Stopping: return "Остановка…";
                case ProtectionState.Error: return "Ошибка обхода";
                default: return "Обход выключен";
            }
        }

        private void RegisterBridgeHandlers()
        {
            WebBridge bridge = _form.Bridge;

            bridge.Register("ready", msg =>
            {
                Log.Info("UI", "страница загрузилась и подключилась к мосту");
                PushState();
                PushStrategies();
            });

            bridge.Register("state", msg => PushState());
            bridge.Register("settings.get", msg => PushState());
            bridge.Register("settings.set", msg => { ApplySettings(msg); PushState(); });

            bridge.Register("toggle", msg => ToggleProtection());

            bridge.Register("strategy.list", msg => PushStrategies());

            bridge.Register("strategy.select", msg =>
            {
                string id = Json.GetString(msg, "id", "");
                if (string.IsNullOrEmpty(id)) return;
                _settings.StrategyId = id;
                _settings.Save();
                Log.Info("WINWS", "выбрана стратегия: " + id);
                PushState();
            });

            bridge.Register("logs.get", msg =>
            {
                List<object> lines = new List<object>();
                foreach (LogEntry entry in Log.Snapshot()) lines.Add(entry.ToLine());
                bridge.Send(WebBridge.Msg("logs", "lines", lines, "file", Log.CurrentFile ?? ""));
            });

            bridge.Register("logs.clear", msg =>
            {
                Log.Clear();
                bridge.Send(WebBridge.Msg("logs", "lines", new List<object>(), "file", Log.CurrentFile ?? ""));
            });

            bridge.Register("logs.openFolder", msg => OpenPath(Paths.LogDir));
            bridge.Register("logs.openFile", msg => OpenPath(Log.CurrentFile));

            bridge.Register("tray.hide", msg => _form.HideToTray());
            bridge.Register("app.exit", msg => RequestExit());

            bridge.Register("app.info", msg => bridge.Send(WebBridge.Msg("info",
                "version", AppInfo.Version,
                "os", AppInfo.OsDescription,
                "admin", AppInfo.IsAdmin,
                "portable", Paths.IsPortable,
                "dataDir", Paths.DataDir,
                "webview2", AppInfo.WebView2RuntimeVersion() ?? "")));

            bridge.Register("secret.regenerate", msg =>
            {
                _settings.ProxySecret = Settings.NewSecret();
                _settings.Save();
                PushState();
            });
        }

        // --------------------------------------------------------------- control

        /// <summary>Включить/выключить обход. Выполняется в фоне: старт winws не быстрый.</summary>
        private void ToggleProtection()
        {
            ProtectionState state = _zapret.State;

            if (state == ProtectionState.Starting || state == ProtectionState.Stopping)
            {
                Log.Debug("WINWS", "переключение проигнорировано: идёт переход");
                return;
            }

            // Включение требует прав администратора: winws.exe и WinDivert без них не стартуют.
            if (state != ProtectionState.Running && !Elevation.IsElevated)
            {
                DialogResult answer = MessageBox.Show(
                    Elevation.ExplainWhy(), AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (answer == DialogResult.Yes)
                {
                    if (Elevation.RestartElevated()) RequestExit();
                    else _tray.Notify(AppInfo.Name, "Права администратора не получены.", ToolTipIcon.Warning);
                }
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (state == ProtectionState.Running)
                {
                    _zapret.Stop();
                    return;
                }

                string error = _zapret.Start(_settings.StrategyId);
                if (error != null)
                {
                    Log.Error("WINWS", "не удалось включить: " + error);
                    _tray.Notify(AppInfo.Name, error, ToolTipIcon.Error);
                }
            });
        }

        // ------------------------------------------------------------------ state

        private void PushState()
        {
            ProtectionState protection = _zapret.State;
            string protectionText;
            switch (protection)
            {
                case ProtectionState.Running: protectionText = "on"; break;
                case ProtectionState.Starting: protectionText = "starting"; break;
                case ProtectionState.Stopping: protectionText = "stopping"; break;
                case ProtectionState.Error: protectionText = "error"; break;
                default: protectionText = "off"; break;
            }

            EngineLocator engine = _zapret.Engine;

            Dictionary<string, object> state = WebBridge.Msg("state",
                "version", AppInfo.Version,
                "os", AppInfo.OsDescription,
                "admin", AppInfo.IsAdmin,
                "portable", Paths.IsPortable,
                "dataDir", Paths.DataDir,
                "webview2", AppInfo.WebView2RuntimeVersion() ?? "",
                "engineReady", engine.Ready,
                "engineProblem", engine.Problem ?? "",
                "engineVersion", engine.Version,
                "protection", protectionText,
                "protectionError", _zapret.LastError ?? "",
                "activeStrategy", _zapret.ActiveStrategyName ?? "",
                "uptimeSec", (long)_zapret.Uptime.TotalSeconds,
                "strategyId", _settings.StrategyId,
                "gameFilter", _settings.GameFilter,
                "ipsetMode", _settings.IpsetMode,
                "proxyEnabled", _settings.ProxyEnabled,
                "proxyHost", _settings.ProxyHost,
                "proxyPort", (long)_settings.ProxyPort,
                "proxySecret", _settings.ProxySecret,
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

        private void PushStrategies()
        {
            List<Dictionary<string, object>> list;
            try
            {
                list = _zapret.DescribeStrategies();
            }
            catch (Exception ex)
            {
                Log.Exception("WINWS", "список стратегий", ex);
                return;
            }

            List<object> items = new List<object>();
            foreach (Dictionary<string, object> s in list) items.Add(s);

            _form.Bridge.Send(WebBridge.Msg("strategies", "items", items));
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

            // сначала гасим winws, потом всё остальное
            try { _zapret.Dispose(); } catch (Exception ex) { Log.Exception("APP", "остановка движка", ex); }

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

            try { _zapret.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
        }
    }
}
