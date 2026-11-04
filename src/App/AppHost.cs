using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AutoZapret.Core;
using AutoZapret.Diag;
using AutoZapret.Lists;
using AutoZapret.Orchestrator;
using AutoZapret.Payloads;
using AutoZapret.Proxy;
using AutoZapret.Update;
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
        private readonly TgProxyServer _proxy;
        private readonly AutoPilot _autoPilot;
        private readonly ScoreStore _scores;
        private bool _disposed;
        private bool _exiting;

        public AppHost()
        {
            _settings = Settings.Current;

            _form = new MainForm();
            _tray = new TrayController(_form);
            _zapret = new ZapretService();
            _proxy = new TgProxyServer();

            _scores = new ScoreStore();
            _scores.Load();
            _autoPilot = new AutoPilot(_zapret, _scores);

            WireForm();
            WireTray();
            WireEngine();
            WireProxy();
            WireAutoPilot();
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
            _zapret.StateChanged += (state, error) =>            {
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

                // автопилот живёт только вместе с запущенным обходом
                if (state == ProtectionState.Running)
                {
                    if (_settings.AutoPilot && !_autoPilot.IsRunning)
                        _autoPilot.Start(_settings.StrategyId);
                }
                else if (state == ProtectionState.Stopped || state == ProtectionState.Error)
                {
                    if (_autoPilot.IsRunning) _autoPilot.Stop();
                }

                PushState();
                SendAutoState();
            };
        }

        private void WireProxy()
        {
            _proxy.StateChanged += (state, error) =>
            {
                if (state == ProxyState.Error && !string.IsNullOrEmpty(error))
                    _tray.Notify(AppInfo.Name, "Прокси: " + error, ToolTipIcon.Error);

                SendProxyState();
            };
        }

        private void WireAutoPilot()
        {
            _autoPilot.StatusChanged += status => SendAutoState();

            // автопилот сменил стратегию — сохраняем, иначе после перезапуска вернётся старая
            _autoPilot.StrategySwitched += id =>
            {
                _settings.StrategyId = id;
                try { _settings.Save(); } catch { }

                Log.Info("AUTO", "стратегия переключена автопилотом: " + id);
                _tray.Notify(AppInfo.Name, "Автопилот выбрал другую стратегию", ToolTipIcon.Info);

                PushState();
                PushStrategies();
                SendAutoState();
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

            bridge.Register("engine.select", msg =>
            {
                string kind = Json.GetString(msg, "kind", _settings.EngineKind);
                if (kind != "v1" && kind != "v2") return;

                if (_zapret.State == ProtectionState.Running)
                {
                    Log.Warn("WINWS", "нельзя сменить движок при работающем обходе");
                    PushState();
                    return;
                }

                _settings.EngineKind = kind;

                // стратегия другого движка не подойдёт — сбрасываем на первую из нового
                Strategy current = _zapret.Strategies.Find(_settings.StrategyId);
                if (current == null || !string.Equals(current.Engine, kind, StringComparison.OrdinalIgnoreCase))
                {
                    List<Strategy> pool = _zapret.Strategies.ByEngine(kind);
                    if (pool.Count > 0) _settings.StrategyId = pool[0].Id;
                }

                _settings.Save();
                Log.Info("WINWS", "движок: " + kind);
                PushState();
                PushStrategies();
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

            // ---- диагностика
            bridge.Register("diag.probe", msg => RunInBackground("probe", RunProbeUi));
            bridge.Register("diag.deep", msg => RunInBackground("deep", RunDeepUi));
            bridge.Register("diag.test", msg =>
            {
                int count = Json.GetInt(msg, "count", 5);
                RunInBackground("test", () => RunTestUi(count));
            });

            // ---- списки
            bridge.Register("lists.get", msg =>
            {
                string kind = Json.GetString(msg, "kind", "general");
                SendList(kind);
            });

            bridge.Register("lists.save", msg =>
            {
                string kind = Json.GetString(msg, "kind", "general");
                string text = Json.GetString(msg, "text", "");
                SaveList(kind, text);
            });

            bridge.Register("lists.updateIpset", msg => RunInBackground("ipset", () =>
            {
                UpdateResult r = ListsUpdater.UpdateIpset();
                SendUpdateStatus(r);
                SendIpsetState();
            }));

            bridge.Register("lists.updateExclude", msg => RunInBackground("exclude", () =>
            {
                UpdateResult r = ListsUpdater.UpdateExcludeDomains();
                SendUpdateStatus(r);
                SendList("exclude");
            }));

            bridge.Register("lists.updateIpsetExclude", msg => RunInBackground("ipsecexcl", () =>
            {
                UpdateResult r = ListsUpdater.UpdateIpsetExclude();
                SendUpdateStatus(r);
            }));

            // ---- фейки
            bridge.Register("fakes.scan", msg => RunInBackground("fakes", SendFakes));
            bridge.Register("fakes.search", msg =>
            {
                string query = Json.GetString(msg, "query", "");
                SendFakesSearch(query);
            });

            // ---- hosts
            bridge.Register("hosts.apply", msg =>
            {
                bool verify = Json.GetBool(msg, "verify", true);
                RunInBackground("hosts-apply", () => ApplyHostsUi(verify));
            });
            bridge.Register("hosts.check", msg => RunInBackground("hosts-check", CheckHostsUi));
            bridge.Register("hosts.restore", msg => RunInBackground("hosts-restore", RestoreHostsUi));

            // ---- автопилот
            bridge.Register("auto.state", msg => SendAutoState());
            bridge.Register("auto.toggle", msg =>
            {
                bool enabled = Json.GetBool(msg, "enabled", false);

                if (enabled)
                {
                    if (_zapret.State != ProtectionState.Running)
                    {
                        _tray.Notify(AppInfo.Name,
                            "Сначала включите обход — автопилот работает с запущенным обходом",
                            ToolTipIcon.Warning);
                        SendAutoState();
                        return;
                    }

                    _autoPilot.Start(_settings.StrategyId);
                    _settings.AutoPilot = true;
                }
                else
                {
                    _autoPilot.Stop();
                    _settings.AutoPilot = false;
                }

                _settings.Save();
                SendAutoState();
            });

            // ---- прокси
            bridge.Register("proxy.state", msg => SendProxyState());
            bridge.Register("proxy.toggle", msg => RunInBackground("proxy-toggle", () =>
            {
                bool enabled = Json.GetBool(msg, "enabled", false);
                if (enabled) StartProxy();
                else StopProxy();
                SendProxyState();
            }));
            bridge.Register("proxy.copyLink", msg =>
            {
                try
                {
                    Clipboard.SetText(_settings.BuildProxyLink());
                    _tray.Notify(AppInfo.Name, "Ссылка скопирована", ToolTipIcon.Info);
                }
                catch (Exception ex) { Log.Warn("PROXY", "буфер обмена: " + ex.Message); }
            });
            bridge.Register("proxy.openLink", msg =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_settings.BuildProxyLink()) { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Warn("PROXY", "открыть ссылку: " + ex.Message); }
            });
            bridge.Register("proxy.test", msg => RunInBackground("proxy-test", RunProxyTestUi));

            // ---- обновления
            bridge.Register("upd.engine.check", msg => RunInBackground("upd-engine", CheckEngineUi));
            bridge.Register("upd.engine.install", msg => RunInBackground("upd-install", InstallEngineUi));
            bridge.Register("upd.app.check", msg => RunInBackground("upd-app", CheckAppUi));
        }

        // --------------------------------------------------------------- updates

        private void CheckEngineUi()
        {
            EngineUpdater updater = new EngineUpdater();
            updater.Progress += line => SendUpdateProgress(line, false, true);

            UpdateCheck check = updater.Check();

            _form.Bridge.Send(WebBridge.Msg("upd.state",
                "target", "engine",
                "ok", check.Ok,
                "hasUpdate", check.HasUpdate,
                "current", check.CurrentVersion ?? "",
                "latest", check.LatestVersion ?? "",
                "message", check.Message ?? ""));
        }

        private void InstallEngineUi()
        {
            EngineUpdater updater = new EngineUpdater();
            updater.Progress += line => SendUpdateProgress(line, false, true);

            // останавливаем обход перед заменой файлов
            string error = updater.Install(delegate
            {
                _zapret.Stop();
                return true;
            });

            if (error != null)
            {
                SendUpdateProgress("ошибка: " + error, true, false);
                _tray.Notify(AppInfo.Name, "Обновление движка не удалось: " + error, ToolTipIcon.Error);
            }
            else
            {
                SendUpdateProgress("движок обновлён", true, true);
                _tray.Notify(AppInfo.Name, "Движок обновлён", ToolTipIcon.Info);
            }

            CheckEngineUi();
        }

        private void CheckAppUi()
        {
            SelfUpdater updater = new SelfUpdater();
            updater.Progress += line => SendUpdateProgress(line, false, true);

            UpdateCheck check = updater.Check(true);

            _form.Bridge.Send(WebBridge.Msg("upd.state",
                "target", "app",
                "ok", check.Ok,
                "hasUpdate", check.HasUpdate,
                "current", check.CurrentVersion ?? "",
                "latest", check.LatestVersion ?? "",
                "message", check.Message ?? ""));
        }

        private void SendUpdateProgress(string line, bool done, bool ok)
        {
            _form.Bridge.Send(WebBridge.Msg("upd.progress",
                "line", line ?? "",
                "done", done,
                "ok", ok));
        }

        // ---------------------------------------------------------------- proxy

        private void StartProxy()
        {
            string error = _proxy.Start(_settings.ProxyHost, _settings.ProxyPort, _settings.ProxySecret);
            if (error != null)
            {
                Log.Error("PROXY", "не удалось запустить: " + error);
                _tray.Notify(AppInfo.Name, error, ToolTipIcon.Error);
                return;
            }

            _settings.ProxyEnabled = true;
            _settings.Save();
            _tray.Notify(AppInfo.Name, "Прокси запущен на порту " + _settings.ProxyPort, ToolTipIcon.Info);
        }

        private void StopProxy()
        {
            _proxy.Stop();
            _settings.ProxyEnabled = false;
            _settings.Save();
        }

        private void SendProxyState()
        {
            _form.Bridge.Send(WebBridge.Msg("proxy",
                "running", _proxy.IsRunning,
                "host", _settings.ProxyHost,
                "port", (long)_settings.ProxyPort,
                "statusLine", _proxy.StatusLine(),
                "active", (long)_proxy.Stats.Active,
                "total", _proxy.Stats.TotalConnections,
                "failed", _proxy.Stats.Failed,
                "error", _proxy.LastError ?? ""));
        }

        private void RunProxyTestUi()
        {
            List<ProxySelfTest.Step> steps = ProxySelfTest.Run(2, false);

            List<object> items = new List<object>();
            bool allOk = true;
            string firstFail = null;

            foreach (ProxySelfTest.Step step in steps)
            {
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", step.Name },
                    { "ok", step.Ok },
                    { "detail", step.Detail ?? "" }
                });

                if (!step.Ok)
                {
                    allOk = false;
                    if (firstFail == null) firstFail = step.Name;
                }
            }

            string advice = "";
            if (!allOk)
            {
                if (firstFail != null && firstFail.IndexOf("Telegram", StringComparison.Ordinal) >= 0)
                    advice = "Сеть не пускает до kws*.web.telegram.org. Это блокировка на уровне TCP — " +
                             "desync-стратегии её обычно не пробивают. Нужен VPN/прокси (см. план P11).";
                else if (firstFail != null && firstFail.IndexOf("WebSocket", StringComparison.Ordinal) >= 0)
                    advice = "Эндпоинт /apiws не отвечает. Telegram мог его изменить — попробуйте позже.";
                else
                    advice = "Прокси не смог ответить: проверьте порт и что он не занят другим приложением.";
            }

            _form.Bridge.Send(WebBridge.Msg("proxy.test", "steps", items, "advice", advice));
        }

        // ---------------------------------------------------------------- fakes

        // ---------------------------------------------------------------- hosts

        private void ApplyHostsUi(bool verify)
        {
            HostsUpdateResult built = HostsUpdater.Build(true, true, verify,
                line => SendUpdateProgress(line, false, true));

            if (!built.Ok)
            {
                SendHosts(false, true, built.Message, null);
                return;
            }

            string error = HostsFile.Apply(built.Preview);
            if (error != null)
            {
                SendHosts(false, true, "Не записано: " + error, built.Analysis);
                _tray.Notify(AppInfo.Name, "hosts: " + error, ToolTipIcon.Error);
                return;
            }

            string message = "Записано: " + built.Analysis.Summary();
            SendHosts(true, false, message, built.Analysis);

            _tray.Notify(AppInfo.Name, "hosts обновлён: " + built.Analysis.UniqueHosts + " имён",
                ToolTipIcon.Info);
        }

        private void CheckHostsUi()
        {
            HostsUpdateResult result = HostsUpdater.VerifyCurrent(
                line => SendUpdateProgress(line, false, true));

            SendHosts(result.Ok, !result.Ok, result.Message, null);
        }

        private void RestoreHostsUi()
        {
            string error = HostsFile.Restore();
            SendHosts(error == null, error != null,
                error ?? "hosts восстановлен из резервной копии", null);
        }

        // ---------------------------------------------------------------- autopilot

        private void SendAutoState()
        {
            AutoPilotStatus status = _autoPilot.Status;

            string stateName = status.State.ToString();
            string next = "";

            if (_autoPilot.IsRunning && status.NextCheck > DateTime.Now)
                next = status.NextCheck.ToString("HH:mm");

            _form.Bridge.Send(WebBridge.Msg("auto",
                "enabled", _autoPilot.IsRunning,
                "protectionRunning", _zapret.State == ProtectionState.Running,
                "state", stateName,
                "message", status.Message ?? "",
                "network", status.Network ?? "",
                "strategy", status.CurrentStrategy ?? "",
                "score", (long)status.LastScore,
                "failures", (long)status.ConsecutiveFailures,
                "probes", (long)status.ProbesDone,
                "nextCheck", next));
        }

        private void SendHosts(bool ok, bool warn, string message, HostsAnalysis analysis)        {
            List<object> conflicts = new List<object>();

            if (analysis != null && analysis.Conflicts.Count > 0)
            {
                int limit = Math.Min(analysis.Conflicts.Count, 40);
                for (int i = 0; i < limit; i++)
                {
                    HostsConflict c = analysis.Conflicts[i];

                    List<object> ips = new List<object>();
                    foreach (string ip in c.Ips) ips.Add(ip);

                    string reason = c.Verified
                        ? "проверен TCP: " + (c.VerifiedMs >= 0 ? c.VerifiedMs + " мс" : c.VerifiedNote)
                        : "по приоритету источника";

                    conflicts.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "host", c.Hostname },
                        { "ips", ips },
                        { "chosen", c.Chosen },
                        { "reason", reason }
                    });
                }
            }

            _form.Bridge.Send(WebBridge.Msg("hosts",
                "ok", ok,
                "warn", warn,
                "message", message ?? "",
                "conflicts", conflicts));
        }

        private void SendFakes()
        {
            // Shared() кеширует индекс: 1671 файл нельзя пересканировать на каждый запрос
            PayloadLibrary library = PayloadLibrary.Shared();

            List<object> items = new List<object>();

            // UI показывает первые 300; остальное доступно поиском
            List<Payload> visible = library.Search(null, 300);

            foreach (Payload p in visible)
            {
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "file", p.File },
                    { "kind", Payload.KindName(p.Kind) },
                    { "sni", p.Sni ?? "" },
                    { "size", (long)p.Size },
                    { "valid", p.Valid },
                    { "reason", p.InvalidReason ?? "" }
                });
            }

            _form.Bridge.Send(WebBridge.Msg("fakes",
                "items", items,
                "valid", (long)library.ValidCount,
                "total", (long)library.Count,
                "shown", (long)visible.Count));
        }

        /// <summary>Поиск по библиотеке фейков (для поля в UI).</summary>
        private void SendFakesSearch(string query)
        {
            PayloadLibrary library = PayloadLibrary.Shared();
            List<Payload> found = library.Search(query, 300);

            List<object> items = new List<object>();
            foreach (Payload p in found)
            {
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "file", p.File },
                    { "kind", Payload.KindName(p.Kind) },
                    { "sni", p.Sni ?? "" },
                    { "size", (long)p.Size },
                    { "valid", p.Valid },
                    { "reason", p.InvalidReason ?? "" }
                });
            }

            _form.Bridge.Send(WebBridge.Msg("fakes",
                "items", items,
                "valid", (long)library.ValidCount,
                "total", (long)library.Count,
                "shown", (long)found.Count,
                "query", query ?? ""));
        }

        // ---------------------------------------------------------------- lists

        private static string PathForKind(string kind)
        {
            return kind == "exclude" ? HostlistManager.ExcludePath : HostlistManager.GeneralPath;
        }

        private void SendList(string kind)
        {
            string path = PathForKind(kind);
            List<ListEntry> entries = HostlistManager.Read(path);

            StringBuilder text = new StringBuilder();
            int excluded = 0;

            foreach (ListEntry entry in entries)
            {
                text.Append(entry.ToLine()).Append('\n');
                if (!entry.Enabled) excluded++;
            }

            _form.Bridge.Send(WebBridge.Msg("lists",
                "kind", kind,
                "text", text.ToString(),
                "count", (long)entries.Count,
                "excluded", (long)excluded,
                "invalid", 0L));
        }

        private void SaveList(string kind, string text)
        {
            List<string> values = HostlistManager.ParseInput(text);
            List<ListEntry> entries = new List<ListEntry>();
            int invalid = 0;

            foreach (string value in values)
            {
                string v = value;

                // «!domain» допустим только в списке целей
                bool negated = v.StartsWith("!", StringComparison.Ordinal);
                if (negated && kind != "general")
                {
                    invalid++;
                    continue;
                }

                if (!HostlistManager.IsValidDomain(v))
                {
                    Log.Warn("LIST", "некорректный домен пропущен: " + v);
                    invalid++;
                    continue;
                }

                entries.Add(new ListEntry { Value = v, Enabled = true });
            }

            bool changed = HostlistManager.Write(PathForKind(kind), entries);

            Log.Info("LIST", kind + ": сохранено " + entries.Count +
                             (invalid > 0 ? ", отклонено " + invalid : "") +
                             (changed ? "" : " (без изменений)"));

            _form.Bridge.Send(WebBridge.Msg("lists",
                "kind", kind,
                "text", text,
                "count", (long)entries.Count,
                "excluded", 0L,
                "invalid", (long)invalid));

            SendUpdateStatus(new UpdateResult
            {
                Ok = true,
                Message = changed
                    ? "сохранено записей: " + entries.Count + (invalid > 0 ? ", отклонено: " + invalid : "")
                    : "без изменений"
            });
        }

        private void SendUpdateStatus(UpdateResult result)
        {
            _form.Bridge.Send(WebBridge.Msg("lists.status",
                "ok", result.Ok,
                "message", result.Message ?? ""));
        }

        private void SendIpsetState()
        {
            string path = ListsUpdater.IpsetPath;
            string text = "нет файла";

            try
            {
                if (System.IO.File.Exists(path))
                {
                    int lines = 0;
                    foreach (string line in System.IO.File.ReadAllLines(path))
                        if (line.Trim().Length > 0) lines++;

                    long size = new System.IO.FileInfo(path).Length;
                    text = lines + " подсетей, " + (size / 1024) + " КБ";
                }
            }
            catch { }

            _form.Bridge.Send(WebBridge.Msg("lists.ipset", "text", text));
        }

        // ------------------------------------------------------------ diagnostics

        /// <summary>
        /// Запуск долгой операции в фоне. UI не блокируется, результат приходит
        /// отдельным сообщением. Ошибки уходят в лог и в страницу, а не в никуда.
        /// </summary>
        private void RunInBackground(string name, Action work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Exception("DIAG", name, ex);
                    _form.Bridge.Send(WebBridge.Msg("error", "message", ex.Message, "action", name));
                }
            });
        }

        private void RunProbeUi()
        {
            ConnectivityProbe probe = new ConnectivityProbe();
            Baseline.Snapshot snapshot = Baseline.Measure(probe, 10, false);

            List<object> items = new List<object>();
            foreach (ProbeTarget target in ConnectivityProbe.DefaultTargets())
            {
                bool blocked = snapshot.Blocked.Contains(target.Name);
                bool working = snapshot.Working.Contains(target.Name);
                if (!blocked && !working) continue;

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", target.Name },
                    { "url", target.Url },
                    { "ok", working },
                    { "engine", target.Engine },
                    { "detail", target.Engine == "proxy" && blocked ? "нужен прокси" : "" }
                });
            }

            List<string> proxyOnly = snapshot.ProxyOnly();
            string summary = "работает " + snapshot.Working.Count + " из " + snapshot.Total +
                             ", заблокировано " + snapshot.Blocked.Count + " · сеть " + snapshot.Network;
            if (proxyOnly.Count > 0)
                summary += " · zapret'ом не лечится: " + string.Join(", ", proxyOnly.ToArray());

            _form.Bridge.Send(WebBridge.Msg("diag.probe", "items", items, "summary", summary));
        }

        private void RunDeepUi()
        {
            Baseline.Snapshot baseline = Baseline.Load();
            if (baseline == null)
            {
                _form.Bridge.Send(WebBridge.Msg("error",
                    "message", "Сначала выполните «Проверить сеть»", "action", "deep"));
                return;
            }

            DeepProbe probe = new DeepProbe();

            List<string> hosts = new List<string>();
            foreach (ProbeTarget t in ConnectivityProbe.DefaultTargets())
                if (baseline.Blocked.Contains(t.Name)) hosts.Add(new Uri(t.Url).Host);
            probe.LearnStubIps(hosts);

            List<object> items = new List<object>();
            Dictionary<BlockKind, List<string>> byKind = new Dictionary<BlockKind, List<string>>();

            foreach (ProbeTarget target in ConnectivityProbe.DefaultTargets())
            {
                bool blocked = baseline.Blocked.Contains(target.Name);
                if (!blocked) continue;

                string host = new Uri(target.Url).Host;
                string detail;
                BlockKind kind = probe.Classify(host, 8, out detail);

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", target.Name },
                    { "kind", BlockKindText.Short(kind) },
                    { "russian", BlockKindText.Russian(kind) },
                    { "ok", BlockKindText.IsOk(kind) },
                    { "detail", detail ?? "" }
                });

                List<string> list;
                if (!byKind.TryGetValue(kind, out list)) { list = new List<string>(); byKind[kind] = list; }
                list.Add(target.Name);
            }

            List<object> advice = new List<object>();
            foreach (KeyValuePair<BlockKind, List<string>> kv in byKind)
            {
                if (BlockKindText.IsOk(kv.Key)) continue;

                List<object> suggest = new List<object>();
                foreach (string s in BlockKindText.Suggest(kv.Key)) suggest.Add(s);

                List<object> targets = new List<object>();
                foreach (string t in kv.Value) targets.Add(t);

                advice.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "kind", BlockKindText.Short(kv.Key) },
                    { "russian", BlockKindText.Russian(kv.Key) + ": " + string.Join(", ", kv.Value.ToArray()) },
                    { "suggest", suggest }
                });
            }

            _form.Bridge.Send(WebBridge.Msg("diag.deep", "items", items, "advice", advice));
        }

        private void RunTestUi(int count)
        {
            Baseline.Snapshot baseline = Baseline.Load();
            if (baseline == null || baseline.Blocked.Count == 0)
            {
                _form.Bridge.Send(WebBridge.Msg("error",
                    "message", "Сначала выполните «Проверить сеть»", "action", "test"));
                return;
            }

            _zapret.EnsureStrategies();

            List<string> ids = new List<string>();
            foreach (Strategy s in _zapret.Strategies.All)
            {
                ids.Add(s.Id);
                if (ids.Count >= count) break;
            }

            StrategyTester tester = new StrategyTester(_zapret, new ConnectivityProbe());
            tester.Progress += line => _form.Bridge.Send(WebBridge.Msg("diag.progress", "line", line));

            List<StrategyScore> scores = tester.Run(ids, baseline, 5, 8);

            List<object> items = new List<object>();
            foreach (StrategyScore s in scores)
            {
                List<object> failed = new List<object>();
                foreach (string f in s.Failed) failed.Add(f);

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", s.Id },
                    { "name", s.Name },
                    { "okCount", (long)s.OkCount },
                    { "total", (long)s.Total },
                    { "avgMs", (long)s.AvgMs },
                    { "failed", failed },
                    { "error", s.Error ?? "" }
                });
            }

            string summary = scores.Count == 0
                ? "ничего не проверено"
                : "проверено " + scores.Count + " стратегий против " + baseline.Blocked.Count + " целей";

            _form.Bridge.Send(WebBridge.Msg("diag.test", "items", items, "summary", summary));
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
                "engineVersion", engine.VersionOf(_settings.EngineKind == "v2" ? EngineKind.V2 : EngineKind.V1),
                "engineKind", _settings.EngineKind,
                "engineV1", engine.HasV1,
                "engineV2", engine.HasV2,
                "engineV2Reason", engine.HasV2 ? "" : engine.UnavailableReason(EngineKind.V2),
                "protection", protectionText,
                "protectionError", _zapret.LastError ?? "",
                "activeStrategy", _zapret.ActiveStrategyName ?? "",
                "uptimeSec", (long)_zapret.Uptime.TotalSeconds,
                "strategyId", _settings.StrategyId,
                "gameFilter", _settings.GameFilter,
                "ipsetMode", _settings.IpsetMode,
                "autoPilot", _settings.AutoPilot,
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

            // UI показывает только стратегии выбранного движка
            string engine = _settings.EngineKind;
            List<object> items = new List<object>();
            foreach (Dictionary<string, object> s in list)
            {
                string itemEngine = s.ContainsKey("engine") ? Convert.ToString(s["engine"]) : "v1";
                if (!string.Equals(itemEngine, engine, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(s);
            }

            _form.Bridge.Send(WebBridge.Msg("strategies",
                "items", items,
                "engine", engine,
                "engines", _zapret.DescribeEngines()));
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
            try { _autoPilot.Dispose(); } catch (Exception ex) { Log.Exception("APP", "остановка автопилота", ex); }
            try { _zapret.Dispose(); } catch (Exception ex) { Log.Exception("APP", "остановка движка", ex); }
            try { _proxy.Dispose(); } catch (Exception ex) { Log.Exception("APP", "остановка прокси", ex); }

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

            try { _autoPilot.Dispose(); } catch { }
            try { _zapret.Dispose(); } catch { }
            try { _proxy.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
        }
    }
}
