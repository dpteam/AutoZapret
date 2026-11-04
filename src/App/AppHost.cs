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
        private readonly ProcessPresetWatcher _presets;
        private bool _disposed;
        private bool _exiting;
        private bool _startedMinimized;
        private bool _autoStartTried;
        private NetStats _lastNetSample;

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
            _presets = new ProcessPresetWatcher(_zapret);

            WireForm();
            WireTray();
            WireEngine();
            WireProxy();
            WireAutoPilot();
            WirePresets();
            RegisterBridgeHandlers();

            _tray.SetState(TrayState.Off);
        }

        // -------------------------------------------------------------------- run

        public void Run(bool startMinimized)
        {
            _startedMinimized = startMinimized || _settings.StartMinimized;
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

        private void WirePresets()
        {
            // Пресет сработал — говорим об этом вслух. Молчаливая смена стратегии
            // выглядит как случайный обрыв, и человек идёт искать проблему в провайдере.
            _presets.Changed += preset =>
            {
                string text = preset == null
                    ? "Пресет снят — вернул стратегию из настроек"
                    : "Пресет «" + preset.Name + "» применён";

                try { _tray.Notify(AppInfo.Name, text, ToolTipIcon.Info); } catch { }
                PushPresets();
            };

            _presets.Start();
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
                PushPresets();
                PushAutoStart();
                TryAutoStartProtection();
                CheckUpdatesOnStart();
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
                if (string.Equals(id, _settings.StrategyId, StringComparison.OrdinalIgnoreCase)) return;

                _settings.StrategyId = id;
                _settings.Save();
                Log.Info("WINWS", "выбрана стратегия: " + id);

                // Смена стратегии на лету: перезапускаем обход, иначе выбранная
                // стратегия начнёт работать только после ручного выключения.
                // Поведение настраиваемое — старт стоит ~1.5 с и рвёт соединения.
                bool running = _zapret.State == ProtectionState.Running ||
                               _zapret.State == ProtectionState.Starting;

                if (running && _settings.ReconnectOnStrategy)
                {
                    RunInBackground("strategy-switch", () => ReconnectWithStrategy(id));
                }

                PushState();
                PushStrategies();
            });

            bridge.Register("engine.select", msg =>
            {
                string kind = Json.GetString(msg, "kind", _settings.EngineKind);
                if (kind != "v1" && kind != "v2") return;

                if (string.Equals(kind, _settings.EngineKind, StringComparison.OrdinalIgnoreCase))
                {
                    PushState();
                    return;
                }

                // Раньше при включённом обходе смена движка просто отказывала, и
                // человек не понимал, почему zapret 2 «не выбирается».
                // Теперь переключаем по-настоящему: остановить, сменить, запустить.
                RunInBackground("engine-switch", () => SwitchEngine(kind));
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

            // перезапуск с правами по кнопке из баннера
            bridge.Register("app.restartElevated", msg =>
            {
                if (Elevation.IsElevated)
                {
                    Log.Info("APP", "перезапуск не нужен: права уже есть");
                    PushState();
                    return;
                }

                Log.Info("APP", "перезапуск от имени администратора по кнопке");
                if (Elevation.RestartElevated()) RequestExit();
            });

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
            bridge.Register("diag.system", msg => RunInBackground("system", RunSystemChecksUi));
            bridge.Register("diag.probe", msg => RunInBackground("probe", RunProbeUi));
            bridge.Register("diag.tcp16", msg =>
            {
                int count = Json.GetInt(msg, "count", 8);
                RunInBackground("tcp16", () => RunTcp16Ui(count));
            });
            bridge.Register("diag.deep", msg => RunInBackground("deep", RunDeepUi));
            bridge.Register("diag.bundle", msg => RunInBackground("bundle", RunBundleUi));
            bridge.Register("diag.bundleDeep", msg => RunInBackground("bundle-deep", RunBundleDeepUi));
            bridge.Register("diag.bundleFolder", msg => OpenPath(DiagnosticBundle.DefaultDirectory()));
            bridge.Register("diag.test", msg =>
            {
                int count = Json.GetInt(msg, "count", 5);
                RunInBackground("test", () => RunTestUi(count));
            });

            // ---- язык интерфейса: словарь вшит в exe, наружу ничего не пишется
            bridge.Register("i18n.get", msg =>
            {
                string lang = NormalizeLanguage(Json.GetString(msg, "lang", _settings.Language));
                string dict = lang == "en" ? Resources.Text(Resources.EnglishDict) : "";

                bridge.Send(WebBridge.Msg("i18n",
                    "lang", lang,
                    "json", dict ?? ""));
            });

            // ---- трафик (по запросу, раз в секунду пока открыта главная)
            bridge.Register("net.speed", msg =>
            {
                NetStats current = NetStats.Sample();
                long down, up;
                NetStats.Rates(_lastNetSample, current, out down, out up);
                _lastNetSample = current;

                bridge.Send(WebBridge.Msg("net.speed",
                    "down", down,
                    "up", up,
                    "downText", NetStats.Format(down),
                    "upText", NetStats.Format(up),
                    "ok", _lastNetSample != null && current.BytesReceived > 0));
            });

            // ---- автозапуск
            bridge.Register("autostart.get", msg => PushAutoStart());
            bridge.Register("autostart.set", msg => SetAutoStart(Json.GetBool(msg, "enabled", false)));

            // ---- пресеты по процессам
            bridge.Register("presets.get", msg => PushPresets());

            bridge.Register("presets.enable", msg =>
            {
                _settings.ProcessPresets = Json.GetBool(msg, "enabled", _settings.ProcessPresets);
                _settings.Save();
                Log.Info("PRSET", "пресеты по процессам: " +
                                  (_settings.ProcessPresets ? "включены" : "выключены"));
                PushPresets();
            });

            bridge.Register("presets.set", msg =>
            {
                string id = Json.GetString(msg, "id", "");
                if (string.IsNullOrEmpty(id)) return;

                bool enabled = Json.GetBool(msg, "enabled", true);

                List<ProcessPreset> presets = _presets.Presets;
                foreach (ProcessPreset preset in presets)
                {
                    if (!string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) continue;

                    preset.Enabled = enabled;
                    Log.Info("PRSET", "пресет «" + preset.Name + "» " + (enabled ? "включён" : "выключен"));
                }

                ProcessPresets.Save(presets, ProcessPresets.FilePath);
                _presets.Reload();
                PushPresets();
            });

            bridge.Register("presets.reload", msg =>
            {
                _presets.Reload();
                Log.Info("PRSET", "пресеты перечитаны с диска");
                PushPresets();
            });

            bridge.Register("presets.openFile", msg =>
            {
                if (!System.IO.File.Exists(ProcessPresets.FilePath))
                    ProcessPresets.Save(_presets.Presets, ProcessPresets.FilePath);

                OpenPath(ProcessPresets.FilePath);
            });

            // ---- генератор фейков: свой домен -> свой payload
            bridge.Register("fakes.generate", msg => RunInBackground("genfake", () =>
            {
                string sni = Json.GetString(msg, "sni", "");
                string type = Json.GetString(msg, "type", "tls");

                GeneratedFake fake = type == "quic"
                    ? FakeGenerator.QuicInitial(FakeGenerator.QuicSize, sni)
                    : FakeGenerator.TlsClientHello(sni, FakeGenerator.DefaultTlsSize);

                if (!fake.Valid)
                {
                    _form.Bridge.Send(WebBridge.Msg("fakes.generated",
                        "ok", false, "error", fake.Error ?? "не удалось собрать"));
                    return;
                }

                string error;
                string path = FakeGenerator.Save(fake, Paths.FakesDir, out error);

                if (path == null)
                {
                    _form.Bridge.Send(WebBridge.Msg("fakes.generated",
                        "ok", false, "error", error));
                    return;
                }

                // библиотека кеширует индекс — иначе новый файл не появится в списке
                PayloadLibrary.Invalidate();

                Payload check = PayloadValidator.Inspect(path);

                Log.Info("FAKE", "создан фейк " + System.IO.Path.GetFileName(path) + ": " + fake.Describe());

                _form.Bridge.Send(WebBridge.Msg("fakes.generated",
                    "ok", check.Valid,
                    "file", System.IO.Path.GetFileName(path),
                    "size", (long)fake.Size,
                    "sni", fake.Sni,
                    "keyShare", fake.KeyShare ?? "",
                    "type", fake.Kind == PayloadKind.QuicInitial ? "quic" : "tls",
                    "reason", check.Valid ? "" : check.InvalidReason));
            }));

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

                // hosts лежит в System32 — запись требует прав администратора
                if (!EnsureElevation()) return;

                RunInBackground("hosts-apply", () => ApplyHostsUi(verify));
            });
            bridge.Register("hosts.reset", msg =>
            {
                if (!EnsureElevation()) return;

                RunInBackground("hosts-reset", () =>
                {
                    string error = HostsFile.ResetToDefault();

                    if (error != null)
                    {
                        SendHosts(false, true, "Не получилось: " + error, null);
                        _tray.Notify(AppInfo.Name, "hosts: " + error, ToolTipIcon.Error);
                        return;
                    }

                    Log.Info("HOSTS", "hosts пересоздан с нуля");
                    SendHosts(true, false,
                        "hosts пересоздан: только localhost. Прежнее — через «Восстановить»", null);
                    _tray.Notify(AppInfo.Name, "hosts пересоздан с нуля", ToolTipIcon.Info);
                });
            });

            bridge.Register("hosts.check", msg => RunInBackground("hosts-check", CheckHostsUi));
            bridge.Register("hosts.restore", msg =>
            {
                if (!EnsureElevation()) return;
                RunInBackground("hosts-restore", RestoreHostsUi);
            });

            // Убрать только наш блок, сохранив записи пользователя. Это то, что
            // нужно, когда hosts после «Обновить» ломает именно обход: бэкап
            // может быть старее, а чужие записи трогать нельзя.
            bridge.Register("hosts.clean", msg =>
            {
                if (!EnsureElevation()) return;

                RunInBackground("hosts-clean", () =>
                {
                    string error = HostsFile.RemoveBlock();

                    if (error != null)
                    {
                        SendHosts(false, true, "Не получилось: " + error, null);
                        _tray.Notify(AppInfo.Name, "hosts: " + error, ToolTipIcon.Error);
                        return;
                    }

                    Log.Info("HOSTS", "блок AutoZapret убран");
                    SendHosts(true, false, "Блок AutoZapret убран, ваши записи на месте", null);
                    _tray.Notify(AppInfo.Name, "hosts очищен от записей AutoZapret", ToolTipIcon.Info);
                });
            });

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
            bridge.Register("upd.app.install", msg => RunInBackground("upd-app-install", InstallAppUi));

            // ---- первичная установка движка: без git, без консоли
            bridge.Register("engine.download", msg => RunInBackground("engine-download", DownloadEngineUi));
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

        /// <summary>
        /// Установка движка из приложения. Прогресс идёт в интерфейс строками:
        /// скачивание занимает до минуты, и молчащая кнопка выглядит как зависание.
        /// </summary>
        private void DownloadEngineUi()
        {
            EngineInstaller installer = new EngineInstaller();
            installer.Progress += line => _form.Bridge.Send(
                WebBridge.Msg("engine.progress", "line", line, "done", false));

            EngineInstallResult result = installer.Install();

            if (result.Ok)
            {
                // движок появился — перечитываем состояние и стратегии
                try { _zapret.Engine.Refresh(); } catch { }

                _form.Bridge.Send(WebBridge.Msg("engine.progress",
                    "line", "движок установлен" + (result.Version.Length > 0 ? ", версия " + result.Version : ""),
                    "done", true, "ok", true));

                Log.Info("ENGINE", "движок установлен из приложения");
                _tray.Notify(AppInfo.Name, "Движок установлен — можно включать обход", ToolTipIcon.Info);

                _zapret.EnsureStrategies();
                PushState();
                PushStrategies();
            }
            else
            {
                _form.Bridge.Send(WebBridge.Msg("engine.progress",
                    "line", result.Error ?? "не удалось",
                    "done", true, "ok", false, "error", result.Error ?? ""));

                Log.Warn("ENGINE", "установка движка не удалась: " + result.Error);
                _tray.Notify(AppInfo.Name, "Движок не скачался: " + result.Error, ToolTipIcon.Error);

                PushState();
            }
        }

        private void CheckAppUi()
        {
            SelfUpdater updater = new SelfUpdater { Channel = _settings.UpdateChannel };
            updater.Progress += line => SendUpdateProgress(line, false, true);

            UpdateCheck check = updater.Check(true);

            _form.Bridge.Send(WebBridge.Msg("upd.state",
                "target", "app",
                "ok", check.Ok,
                "hasUpdate", check.HasUpdate,
                "current", check.CurrentVersion ?? "",
                "latest", check.LatestVersion ?? "",
                "prerelease", check.Prerelease,
                "message", check.Message ?? ""));
        }

        /// <summary>
        /// Установка обновления приложения.
        ///
        /// Обход обязательно выключается заранее: скрипт-заменщик всё равно гасит
        /// winws, но если тот держит WinDivert в момент копирования, xcopy тихо
        /// не перезапишет часть файлов. Лучше остановить сами и дождаться.
        /// </summary>
        private void InstallAppUi()
        {
            SelfUpdater updater = new SelfUpdater { Channel = _settings.UpdateChannel };
            updater.Progress += line => SendUpdateProgress(line, false, true);

            UpdateCheck check = updater.Check(true);

            if (!check.Ok || !check.HasUpdate)
            {
                SendUpdateProgress("обновлять нечего", true, true);
                return;
            }

            if (_zapret.State == ProtectionState.Running || _zapret.State == ProtectionState.Starting)
            {
                SendUpdateProgress("останавливаю обход…", false, false);
                try { _zapret.Stop(); } catch { }
                Thread.Sleep(1500);
            }

            string error = updater.Install(check.LatestVersion,
                SelfUpdater.PortableZipUrl(check.LatestVersion));

            if (error != null)
            {
                SendUpdateProgress("ошибка: " + error, true, false);
                _tray.Notify(AppInfo.Name, "Обновление не удалось: " + error, ToolTipIcon.Error);
                return;
            }

            SendUpdateProgress("обновление запущено, перезапускаюсь…", true, true);
            _tray.Notify(AppInfo.Name, "Обновление " + check.LatestVersion + " устанавливается",
                ToolTipIcon.Info);

            // заменщик ждёт именно нашего завершения
            RequestExit();
        }

        /// <summary>
        /// Тихая проверка обновлений при старте (P5). Ничего не устанавливает —
        /// только сообщает, что есть новая версия: молчаливое обновление
        /// под пользователем недопустимо.
        /// </summary>
        private void CheckUpdatesOnStart()
        {
            if (!_settings.UpdateApp && !_settings.UpdateEngine) return;

            RunInBackground("upd-start", () =>
            {
                List<string> found = new List<string>();

                if (_settings.UpdateApp)
                {
                    try
                    {
                        SelfUpdater updater = new SelfUpdater { Channel = _settings.UpdateChannel };
                        UpdateCheck check = updater.Check();
                        if (check.Ok && check.HasUpdate)
                            found.Add("программа " + check.LatestVersion);
                    }
                    catch (Exception ex) { Log.Debug("UPD", "автопроверка приложения: " + ex.Message); }
                }

                if (_settings.UpdateEngine)
                {
                    try
                    {
                        UpdateCheck check = new EngineUpdater().Check();
                        if (check.Ok && check.HasUpdate)
                            found.Add("движок " + check.LatestVersion);
                    }
                    catch (Exception ex) { Log.Debug("UPD", "автопроверка движка: " + ex.Message); }
                }

                if (found.Count == 0) return;

                string text = "Доступно обновление: " + string.Join(", ", found.ToArray());
                Log.Info("UPD", text);
                _tray.Notify(AppInfo.Name, text, ToolTipIcon.Info);
            });
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

        /// <summary>
        /// Какие из «обходящихся» сайтов попали в hosts. Их перенаправление почти
        /// всегда ломает именно то, что мы пытаемся чинить: браузер идёт на
        /// захардкоженный IP мимо обхода, и получает чужой сертификат или таймаут.
        /// </summary>
        private static List<string> RiskyHostsRedirects(string hostsText)
        {
            List<string> found = new List<string>();

            if (string.IsNullOrEmpty(hostsText)) return found;

            string[] risky =
            {
                "youtube.com", "googlevideo.com", "ytimg.com",
                "discord.com", "discordapp.com",
                "telegram.org", "web.telegram.org", "t.me"
            };

            foreach (string site in risky)
            {
                if (hostsText.IndexOf(" " + site, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hostsText.IndexOf("\t" + site, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(site);
            }

            return found;
        }

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

            // hosts, перенаправляющий сайты, которые и так обходит движок, —
            // частая причина «обход включён, а браузер не открывает». Говорим
            // это прямо в результате, а не только в лог.
            List<string> redirected = RiskyHostsRedirects(built.Preview);

            if (redirected.Count > 0)
            {
                message += " · ВНИМАНИЕ: hosts перенаправляет " + string.Join(", ", redirected.ToArray()) +
                           " — если сайт не открывается, нажмите «Восстановить»";

                Log.Warn("HOSTS", "перенаправлены сайты, которые обходит движок: " +
                                  string.Join(", ", redirected.ToArray()));
            }

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

        private void RunBundleUi()
        {
            BundleResult result = DiagnosticBundle.Create(
                DiagnosticBundle.DefaultDirectory(), _settings, _zapret.Strategies);

            if (!result.Ok)
            {
                _form.Bridge.Send(WebBridge.Msg("diag.bundle",
                    "ok", false,
                    "error", result.Error ?? "неизвестная ошибка"));
                return;
            }

            _form.Bridge.Send(WebBridge.Msg("diag.bundle",
                "ok", true,
                "path", result.Path,
                "size", result.SizeText(),
                "entries", result.Entries,
                "folder", DiagnosticBundle.DefaultDirectory()));
        }

        /// <summary>
        /// Настройка «включать обход при старте» (settings.zapret.autoStart).
        ///
        /// Осторожность здесь важнее функции:
        ///   * без прав администратора обход включить нельзя — но показывать UAC
        ///     при каждом входе в систему unacceptable, поэтому просто пишем в лог;
        ///   * уведомление в трей — только если приложение стартовало свёрнутым
        ///     (то есть из автозапуска), иначе это шум при каждом обычном запуске;
        ///   * пробуем один раз за сессию, чтобы не бороться с пользователем,
        ///     который только что выключил обход вручную.
        /// </summary>
        private void TryAutoStartProtection()
        {
            if (_autoStartTried) return;
            _autoStartTried = true;

            if (!_settings.AutoStart) return;
            if (_zapret.State != ProtectionState.Stopped) return;

            if (!Elevation.IsElevated)
            {
                Log.Info("WINWS", "автозапуск обхода пропущен: нет прав администратора");

                if (_startedMinimized)
                    _tray.Notify(AppInfo.Name,
                        "Обход не включён автоматически: нужны права администратора. " +
                        "Включите его вручную.",
                        ToolTipIcon.Warning);

                return;
            }

            Log.Info("WINWS", "автозапуск обхода: " + _settings.StrategyId);

            RunInBackground("autostart-protection", () =>
            {
                string error = _zapret.Start(_settings.StrategyId);
                if (error != null)
                {
                    Log.Error("WINWS", "автозапуск обхода не удался: " + error);
                    _tray.Notify(AppInfo.Name, error, ToolTipIcon.Error);
                }
            });
        }

        /// <summary>Допустимые языки интерфейса. Всё остальное — русский.</summary>
        public static string NormalizeLanguage(string value)
        {
            return Settings.NormalizeLanguage(value);
        }

        private void PushAutoStart()
        {
            AutoStartStatus status = AutoStartManager.Query();

            _form.Bridge.Send(WebBridge.Msg("autostart",
                "enabled", status.Enabled,
                "method", status.Method.ToString(),
                "where", status.Where ?? "",
                "target", status.Target ?? "",
                "stale", status.Stale,
                "problem", status.Problem ?? "",
                "text", status.Text(),
                "exe", AutoStartManager.ExePath,
                // «включать обход при старте» — отдельная настройка, не путать с автозапуском
                "protectAtStart", _settings.AutoStart));
        }

        /// <summary>
        /// Включение/выключение автозапуска.
        ///
        /// Задача планировщика требует прав, а GUI работает asInvoker (AGENTS.md:
        /// повышение — только на операцию). Поэтому создание задачи идёт отдельным
        /// короткоживущим процессом, а сам интерфейс права не получает.
        /// </summary>
        private void SetAutoStart(bool want)
        {
            AutoStartResult result = want
                ? AutoStartManager.Enable(true)
                : AutoStartManager.Disable();

            if (result.NeedsElevation && !Elevation.IsElevated)
            {
                string question = want
                    ? "Автозапуск без запроса UAC возможен только через планировщик заданий, " +
                      "а для его создания нужны права администратора.\r\n\r\n" +
                      "Повысить права на эту операцию?\r\n\r\n" +
                      "«Нет» — автозапуск через реестр: без прав, но Windows будет " +
                      "спрашивать UAC при каждом включении обхода."
                    : "Чтобы убрать задачу планировщика, нужны права администратора.\r\n\r\n" +
                      "Повысить права на эту операцию?";

                DialogResult answer = MessageBox.Show(question, AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (answer == DialogResult.Yes)
                {
                    int code = Elevation.RunElevatedOperation(
                        "--autostart " + (want ? "enable" : "disable"));

                    result = new AutoStartResult
                    {
                        Ok = code == 0,
                        Error = code == 0 ? "" : "повышенная операция: код " + code,
                        Status = AutoStartManager.Query()
                    };

                    if (result.Ok) result.Status = AutoStartManager.Query();
                }
                else if (want)
                {
                    // отказ от прав — не отказ от автозапуска: ставим через реестр
                    result = AutoStartManager.Enable(false);
                }
                else
                {
                    result = new AutoStartResult
                    {
                        Ok = false,
                        Error = "без прав администратора задачу не удалить",
                        Status = AutoStartManager.Query()
                    };
                }
            }

            if (result.Ok) Log.Info("APP", "автозапуск: " + result.Status.Text());
            else Log.Warn("APP", "автозапуск не изменён: " + result.Text());

            PushAutoStart();
        }

        private void PushPresets()
        {
            List<object> items = new List<object>();
            ProcessPreset active = _presets.Active;

            foreach (ProcessPreset preset in _presets.Presets)
            {
                List<object> processes = new List<object>();
                foreach (string p in preset.Processes) processes.Add(p);

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", preset.Id },
                    { "name", preset.Name },
                    { "processes", processes },
                    { "strategy", preset.Strategy ?? "" },
                    { "gameFilter", preset.GameFilter ?? "" },
                    { "enabled", preset.Enabled },
                    { "changes", preset.ChangesSomething },
                    { "active", active != null && string.Equals(active.Id, preset.Id, StringComparison.OrdinalIgnoreCase) },
                    { "running", ProcessPresets.IsRunning(preset.Processes.Count > 0 ? preset.Processes[0] : "") }
                });
            }

            _form.Bridge.Send(WebBridge.Msg("presets",
                "items", items,
                "enabled", _settings.ProcessPresets,
                "active", active == null ? "" : active.Name,
                "file", ProcessPresets.FilePath));
        }

        /// <summary>
        /// Расширенный отчёт: то же, что обычный архив, плюс preflight, состояние
        /// hosts, полные аргументы всех стратегий, снимок сети и пофайловый
        /// список движка. Для случая «работать не хочет, а почему — непонятно».
        /// </summary>
        private void RunBundleDeepUi()
        {
            BundleResult result = DiagnosticBundle.Create(
                DiagnosticBundle.DefaultDirectory(), _settings, _zapret.Strategies,
                DeepReportOptions.Extended(line => _form.Bridge.Send(
                    WebBridge.Msg("diag.bundleProgress", "line", line))));

            if (!result.Ok)
            {
                _form.Bridge.Send(WebBridge.Msg("diag.bundle",
                    "ok", false, "error", result.Error ?? "неизвестная ошибка"));
                return;
            }

            Log.Info("DIAG", "расширенный отчёт: " + result.Entries.Count + " файлов, " + result.SizeText());

            _form.Bridge.Send(WebBridge.Msg("diag.bundle",
                "ok", true,
                "path", result.Path,
                "size", result.SizeText(),
                "entries", result.Entries,
                "folder", DiagnosticBundle.DefaultDirectory(),
                "deep", true));
        }

        private void RunSystemChecksUi()
        {
            List<CheckResult> results = SystemChecks.RunAll();

            List<object> items = new List<object>();
            int problems = 0, warnings = 0;

            foreach (CheckResult r in results)
            {
                if (r.Status == CheckStatus.Fail) problems++;
                else if (r.Status == CheckStatus.Warn) warnings++;

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", r.Name },
                    { "status", r.Status.ToString() },
                    { "detail", r.Detail ?? "" },
                    { "advice", r.Advice ?? "" }
                });
            }

            string summary;
            if (problems == 0 && warnings == 0) summary = "всё в порядке — 12 проверок пройдено";
            else if (problems == 0) summary = "предупреждений: " + warnings;
            else summary = "проблем: " + problems + ", предупреждений: " + warnings;

            _form.Bridge.Send(WebBridge.Msg("diag.system",
                "items", items,
                "problems", (long)problems,
                "warnings", (long)warnings,
                "summary", summary));
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

        private void RunTcp16Ui(int count)
        {
            string path = System.IO.Path.Combine(Paths.BaseDir, "assets", "diag", "tcp16.json");
            List<Tcp16Target> targets = Tcp16Test.LoadTargets(path);

            string source = "assets\\diag\\tcp16.json";
            if (targets.Count == 0)
            {
                targets = Tcp16Test.BuiltinTargets();
                source = "встроенный список";
            }

            // разные провайдеры, а не первые N подряд
            List<Tcp16Target> picked = new List<Tcp16Target>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Tcp16Target t in targets)
            {
                if (!seen.Add(t.Provider)) continue;
                picked.Add(t);
                if (picked.Count >= Math.Max(1, count)) break;
            }

            List<Tcp16Result> results = Tcp16Test.ProbeAll(picked, 6000, 4);

            List<object> items = new List<object>();
            foreach (Tcp16Result r in results)
            {
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "provider", r.Provider },
                    { "ip", r.Ip },
                    { "port", (long)r.Port },
                    { "bytes", (long)r.BytesSent },
                    { "survived", r.Survived },
                    { "blocked", r.LooksBlocked },
                    { "detail", r.Detail ?? "" }
                });
            }

            _form.Bridge.Send(WebBridge.Msg("diag.tcp16",
                "items", items,
                "verdict", Tcp16Test.Verdict(results) + " · источник: " + source));
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

        /// <summary>
        /// Завершить подбор стратегий с объяснением.
        ///
        /// Раньше при любом раннем выходе кнопка оставалась в состоянии «…»
        /// навсегда: экран ждал `diag.test`, а его никто не отправлял. Пользователь
        /// видел «не стартует» и не знал, что на самом деле сработала блокировка.
        /// </summary>
        private void AbortTest(string reason)
        {
            Log.Warn("DIAG", "подбор стратегий отменён: " + reason);

            _form.Bridge.Send(WebBridge.Msg("diag.test",
                "items", new List<object>(),
                "summary", reason));
        }

        private void RunTestUi(int count)
        {
            // Подбор стратегий запускает winws для каждой стратегии — без прав
            // это падало сырым Win32Exception, и выглядело как «подбор сломан».
            if (!EnsureElevation())
            {
                AbortTest("нужны права администратора");
                return;
            }

            // Окружение проверяем без привязки к стратегии: перебирать будем разные
            if (!RunPreflight(null))
            {
                AbortTest("проверка готовности нашла проблемы — см. список на главной");
                return;
            }

            Baseline.Snapshot baseline = Baseline.Load();

            // Замера нет или он пуст — не отфутболиваем человека «сначала нажмите
            // Проверить сеть», а замеряем сами. Пустой бывает и после hosts-правок:
            // сайт «открывается» через захардкоженный IP, значит «ничего не
            // заблокировано», и подбор отменялся ни с того ни с сего.
            if (baseline == null || baseline.Blocked.Count == 0)
            {
                // При включённом обходе замер бессмысленен — глушим движок
                if (_zapret.State == ProtectionState.Running)
                {
                    _form.Bridge.Send(WebBridge.Msg("diag.progress",
                        "line", "останавливаю обход для замера…", "done", false));

                    _zapret.Stop();
                    Thread.Sleep(1200);
                }

                _form.Bridge.Send(WebBridge.Msg("diag.progress",
                    "line", "замеряю, что заблокировано (это ~20–40 с)…", "done", false));

                baseline = Baseline.Measure(new ConnectivityProbe(), 10, true);
            }

            if (baseline == null || baseline.Blocked.Count == 0)
            {
                AbortTest("ничего не заблокировано — похоже, обход не нужен или уже работает");
                return;
            }

            _zapret.EnsureStrategies();

            // Перебираем стратегии ТЕКУЩЕГО движка: у v2 другой exe и lua,
            // стратегии v1 под ним просто не запустятся.
            List<string> ids = new List<string>();
            foreach (Strategy s in _zapret.Strategies.ByEngine(_settings.EngineKind))
            {
                ids.Add(s.Id);
                if (ids.Count >= count) break;
            }

            if (ids.Count == 0)
            {
                AbortTest("для движка " + _settings.EngineKind + " нет стратегий");
                return;
            }

            StrategyTester tester = new StrategyTester(_zapret, new ConnectivityProbe());

            // Прогресс с числами: человек должен видеть, что идёт 3 из 12 и сколько
            // уже прошло, а не «крутится что-то непонятное пять минут».
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            tester.StrategyStarted += (index, total, name) =>
                _form.Bridge.Send(WebBridge.Msg("diag.progress",
                    "line", "проверяю " + name,
                    "index", (long)index,
                    "total", (long)total,
                    "elapsedSec", (long)watch.Elapsed.TotalSeconds,
                    "done", false));

            tester.Progress += line => _form.Bridge.Send(WebBridge.Msg("diag.progress",
                "line", line,
                "elapsedSec", (long)watch.Elapsed.TotalSeconds,
                "done", false));

            List<StrategyScore> scores = tester.Run(ids, baseline, 5, 8);

            _form.Bridge.Send(WebBridge.Msg("diag.progress",
                "line", "готово", "elapsedSec", (long)watch.Elapsed.TotalSeconds, "done", true));

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
                    { "score", (long)s.Score },
                    { "breakdown", s.Breakdown() },
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

        /// <summary>
        /// Единая точка «нужны права на запуск движка».
        ///
        /// Раньше проверка была только в кнопке питания: подбор стратегий и
        /// автопилот просто пытались стартовать winws и получали сырое
        /// «Запрошенная операция требует повышения» в лог. Пользователь видел
        /// «подбор не работает» и не понимал причины.
        ///
        /// Теперь без модального окна: на главной всегда висит баннер «нет прав»
        /// с кнопкой перезапуска, а мы лишь подсвечиваем его и пишем в трей.
        /// Модалка на каждое нажатие — это то самое раздражение, из-за которого
        /// люди перестают читать предупреждения.
        ///
        /// Возвращает true, если права есть.
        /// </summary>
        private bool EnsureElevation()
        {
            if (Elevation.IsElevated) return true;

            Log.Warn("APP", "действие требует прав администратора");

            _form.Bridge.Send(WebBridge.Msg("needs.elevation"));

            _tray.Notify(AppInfo.Name,
                "Нужны права администратора. На главной есть кнопка «Перезапустить от админа»",
                ToolTipIcon.Warning);

            return false;
        }

        /// <summary>
        /// Быстрая проверка готовности перед запуском движка. Возвращает false,
        /// если есть блокеры. Список уходит в интерфейс — человек должен видеть,
        /// чего именно не хватает, а не «не удалось запустить».
        /// </summary>
        private bool RunPreflight(string strategyId)
        {
            List<PreflightProblem> problems = Preflight.Run(
                _zapret.Engine, _zapret.Strategies, strategyId,
                _settings.GameFilter);

            List<object> items = new List<object>();

            foreach (PreflightProblem p in problems)
            {
                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "level", p.Level == PreflightLevel.Blocker ? "blocker" : "warning" },
                    { "title", p.Title ?? "" },
                    { "hint", p.Hint ?? "" }
                });

                Log.Warn("CHECK", p.Level + ": " + p.Text());
            }

            _form.Bridge.Send(WebBridge.Msg("preflight",
                "items", items,
                "blockers", (long)CountBlockers(problems),
                "summary", Preflight.Summary(problems)));

            return !Preflight.HasBlockers(problems);
        }

        private static int CountBlockers(List<PreflightProblem> problems)
        {
            int count = 0;
            foreach (PreflightProblem p in problems)
                if (p.Level == PreflightLevel.Blocker) count++;
            return count;
        }

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
            if (state != ProtectionState.Running && !EnsureElevation()) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (state == ProtectionState.Running)
                {
                    _zapret.Stop();
                    return;
                }

                // Проверка готовности ДО запуска: 200 мс вместо тихого «не сработало»
                if (!RunPreflight(_settings.StrategyId))
                {
                    Log.Warn("WINWS", "включение отменено: проверка готовности нашла проблемы");
                    _tray.Notify(AppInfo.Name, "Обход не запущен: см. список проблем на главной",
                        ToolTipIcon.Warning);
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

        /// <summary>
        /// Смена движка, в том числе на ходу.
        ///
        /// Человек выбирает zapret 2 и ожидает, что он включится, а не «нельзя:
        /// обход работает». Поэтому при включённом обходе честно останавливаем,
        /// меняем движок и поднимаем заново с первой стратегией нового движка.
        /// Стратегии разных движков несовместимы: у v2 другие аргументы и lua.
        /// </summary>
        /// <summary>
        /// Перезапуск обхода на выбранной стратегии. Вызывается при смене
        /// стратегии, если включена настройка «переподключать».
        /// </summary>
        private void ReconnectWithStrategy(string strategyId)
        {
            Log.Info("WINWS", "переподключение на стратегию " + strategyId);

            try
            {
                _zapret.Stop();
                Thread.Sleep(1200);      // WinDivert должен выгрузиться

                if (!RunPreflight(strategyId)) return;

                string error = _zapret.Start(strategyId);
                if (error != null)
                {
                    Log.Error("WINWS", "переподключение не удалось: " + error);
                    _tray.Notify(AppInfo.Name, error, ToolTipIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("WINWS", "переподключение", ex);
            }

            PushState();
        }

        private void SwitchEngine(string kind)
        {
            bool wasRunning = _zapret.State == ProtectionState.Running ||
                              _zapret.State == ProtectionState.Starting;

            Log.Info("WINWS", "смена движка на " + kind + (wasRunning ? " (обход был включён)" : ""));

            // Проверяем, что движок вообще установлен. Раньше переключение
            // проходило молча, настройка сохранялась, и только потом выяснялось,
            // что нет lua или exe — уже в виде непонятной ошибки стратегии.
            EngineKind wanted = string.Equals(kind, "v2", StringComparison.OrdinalIgnoreCase)
                ? EngineKind.V2
                : EngineKind.V1;

            try { _zapret.Engine.Refresh(); } catch { }

            if (!_zapret.Engine.IsAvailable(wanted))
            {
                string reason = _zapret.Engine.UnavailableReason(wanted);

                Log.Warn("WINWS", "движок " + kind + " недоступен: " + reason);
                _tray.Notify(AppInfo.Name, "Движок не переключён: " + reason, ToolTipIcon.Warning);

                PushState();
                PushStrategies();
                return;
            }

            if (wasRunning)
            {
                _zapret.Stop();
                Thread.Sleep(1500);     // WinDivert должен выгрузиться
            }

            _settings.EngineKind = kind;

            Strategy current = _zapret.Strategies.Find(_settings.StrategyId);
            if (current == null || !string.Equals(current.Engine, kind, StringComparison.OrdinalIgnoreCase))
            {
                List<Strategy> pool = _zapret.Strategies.ByEngine(kind);
                if (pool.Count > 0) _settings.StrategyId = pool[0].Id;
            }

            _settings.Save();

            if (wasRunning)
            {
                string error = _zapret.Start(_settings.StrategyId);
                if (error != null)
                {
                    Log.Error("WINWS", "движок " + kind + " не запустился: " + error);
                    _tray.Notify(AppInfo.Name, "Движок " + kind + ": " + error, ToolTipIcon.Error);
                }
                else
                {
                    _tray.Notify(AppInfo.Name, "Движок переключён: " + kind, ToolTipIcon.Info);
                }
            }

            PushState();
            PushStrategies();
        }

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
                "updateChannel", _settings.UpdateChannel,
                "language", _settings.Language,
                "autoStartProtect", _settings.AutoStart,
                "reconnectOnStrategy", _settings.ReconnectOnStrategy,
                "processPresets", _settings.ProcessPresets,
                "logFile", Log.CurrentFile ?? "");

            _form.Bridge.Send(state);
        }

        private void PushStrategies()
        {
            List<Dictionary<string, object>> list;
            try
            {
                list = _zapret.DescribeStrategies(_settings.EngineKind);
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
            _settings.AutoStart = Json.GetBool(msg, "autoStart", _settings.AutoStart);
            _settings.ReconnectOnStrategy = Json.GetBool(msg, "reconnectOnStrategy", _settings.ReconnectOnStrategy);
            _settings.Language = NormalizeLanguage(Json.GetString(msg, "language", _settings.Language));
            _settings.UpdateEngine = Json.GetBool(msg, "updateEngine", _settings.UpdateEngine);
            _settings.UpdateApp = Json.GetBool(msg, "updateApp", _settings.UpdateApp);
            _settings.UpdateLists = Json.GetBool(msg, "updateLists", _settings.UpdateLists);
            _settings.UpdateChannel = Json.GetString(msg, "updateChannel", _settings.UpdateChannel);
            if (_settings.UpdateChannel != "beta" && _settings.UpdateChannel != "stable")
                _settings.UpdateChannel = "stable";

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
            try { _presets.Dispose(); } catch { }
            try { _zapret.Dispose(); } catch { }
            try { _proxy.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
        }
    }
}
