using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Linux
{
    /// <summary>
    /// Мост между страницей и ядром — та же роль, что AppHost на Windows.
    ///
    /// Сообщения те же: страница присылает { action }, получает { type }.
    /// Поэтому app.js не меняется ни на строку — это и было целью правила
    /// «обмен только в двух функциях».
    ///
    /// Чего здесь пока нет: диагностики, hosts, прокси, обновлений. Они
    /// появятся отдельными обработчиками; сейчас важно, что ядро, стратегии
    /// и включение обхода работают.
    /// </summary>
    internal sealed class Bridge
    {
        private readonly object _gate = new object();
        private WebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private readonly NfqwsEngine _engine = new NfqwsEngine();
        private readonly StrategyStore _store = new StrategyStore();
        private Settings _settings;
        private DateTime _startedAt = DateTime.MinValue;
        private bool _strategiesLoaded;
        private NetStats _lastNetSample;

        public Bridge()
        {
            _settings = Settings.Current;

            _engine.StateChanged += delegate(bool running, string error)
            {
                _startedAt = running ? DateTime.UtcNow : DateTime.MinValue;
                SendState();
            };

            LoadStrategies();
        }

        // ------------------------------------------------------------------ каталог

        /// <summary>
        /// Загрузка каталога стратегий: кэш рядом с программой, потом свои.
        ///
        /// Порядок важен: свои читаются ПОСЛЕ кэша, иначе обновление кэша
        /// перезапишет добавленное человеком (это уже ловили на Windows).
        /// </summary>
        private void LoadStrategies()
        {
            if (_strategiesLoaded) return;
            _strategiesLoaded = true;

            string v1 = Path.Combine(Paths.StrategiesDir, "strategies.v1.json");
            string v2 = Path.Combine(Paths.StrategiesDir, "strategies.v2.json");

            int loaded = 0;

            if (_store.Load(v1)) loaded += _store.ByEngine("v1").Count;
            if (_store.Load(v2)) loaded += _store.ByEngine("v2").Count;

            try { _store.LoadCustom(Paths.CustomStrategiesFile); }
            catch (Exception ex) { Log.Warn("LIST", "свои стратегии: " + ex.Message); }

            Log.Info("LIST", "стратегий загружено: " + loaded + ", всего " + _store.All.Count);
        }

        // ------------------------------------------------------------------ сокет

        public void Attach(WebSocket socket)
        {
            lock (_gate) { _socket = socket; }
        }

        public void Detach()
        {
            lock (_gate) { _socket = null; }
        }

        private void Send(Dictionary<string, object> message)
        {
            WebSocket socket;

            lock (_gate) { socket = _socket; }

            if (socket == null || socket.State != WebSocketState.Open) return;

            string text = Json.Stringify(message);
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            // Сериализуем отправку: два параллельных SendAsync переставляют
            // куски кадра, и страница получает битый JSON.
            _sendGate.Wait();

            try
            {
                socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                                 true, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "отправка: " + ex.Message);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private static Dictionary<string, object> Msg(string type)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "type", type } };
        }

        private static Dictionary<string, object> Msg(string type, params object[] pairs)
        {
            Dictionary<string, object> map = Msg(type);

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[Convert.ToString(pairs[i])] = pairs[i + 1];

            return map;
        }

        // ------------------------------------------------------------------ входящие

        public void Handle(string text)
        {
            Dictionary<string, object> message;

            try
            {
                message = Json.ParseObjectSafe(text);
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "сообщение не разобрано: " + ex.Message);
                return;
            }

            if (message == null) return;

            string action = Json.GetString(message, "action", "");

            try
            {
                Dispatch(action, message);
            }
            catch (Exception ex)
            {
                Log.Exception("APP", "действие " + action, ex);
                Send(Msg("error", "message", action + ": " + ex.Message));
            }
        }

        private void Dispatch(string action, Dictionary<string, object> message)
        {
            switch (action)
            {
                case "ready":
                    SendState();
                    SendStrategies();
                    break;

                case "state":
                    SendState();
                    break;

                case "strategy.list":
                    SendStrategies();
                    break;

                case "strategy.select":
                {
                    string id = Json.GetString(message, "id", "");
                    if (id.Length > 0)
                    {
                        _settings.StrategyId = id;
                        _settings.Save();
                        Log.Info("APP", "стратегия: " + id);
                    }

                    SendStrategies();
                    SendState();
                    break;
                }

                case "toggle":
                    Toggle();
                    break;

                case "engine.select":
                    // На Linux движок один — nfqws. Значение сохраняем, чтобы
                    // настройки не спорили с интерфейсом, но выбор игнорируем.
                    SendState();
                    break;

                case "app.quit":
                    Environment.Exit(0);
                    break;

                case "language.set":
                {
                    _settings.Language = Json.GetString(message, "language", "ru");
                    _settings.Save();
                    SendState();
                    break;
                }

                case "net.speed":
                {
                    // Главная опрашивает это раз в секунду. Если не ответить —
                    // индикатор скорости висит пустым, а лог забивается
                    // «действие не поддержано» каждую секунду.
                    NetStats current = NetStats.Sample();
                    long down, up;

                    NetStats.Rates(_lastNetSample, current, out down, out up);
                    _lastNetSample = current;

                    Send(Msg("net.speed",
                        "down", down,
                        "up", up,
                        "downText", NetStats.Format(down),
                        "upText", NetStats.Format(up),
                        "ok", current.BytesReceived > 0));
                    break;
                }

                case "theme.set":
                {
                    _settings.Theme = Json.GetString(message, "theme", "dark");
                    _settings.Save();
                    SendState();
                    break;
                }

                default:
                    // Остальные действия (диагностика, hosts, прокси, обновления)
                    // на Linux ещё не реализованы. Молчать нельзя: иначе кнопка
                    // выглядит нажатой, а ничего не происходит.
                    Log.Info("APP", "действие не поддержано на Linux: " + action);
                    Send(Msg("notice",
                        "text", "На Linux это действие пока не реализовано: " + action));
                    break;
            }
        }

        // ------------------------------------------------------------------ обход

        private void Toggle()
        {
            if (_engine.IsRunning)
            {
                _engine.Stop();
                SendState();
                return;
            }

            Strategy strategy = _store.Find(_settings.StrategyId);

            if (strategy == null)
            {
                List<Strategy> pool = _store.ByEngine("v1");

                if (pool.Count == 0)
                {
                    Send(Msg("error", "message", "нет доступных стратегий"));
                    return;
                }

                strategy = pool[0];
            }

            string error = _engine.Start(strategy, _settings.GameFilter);

            if (error != null)
            {
                Log.Warn("NFQWS", error);
                Send(Msg("error", "message", error));
            }

            SendState();
        }

        // ------------------------------------------------------------------ состояние

        public void SendState()
        {
            bool running = _engine.IsRunning;

            long uptime = running && _startedAt != DateTime.MinValue
                ? (long)(DateTime.UtcNow - _startedAt).TotalSeconds
                : 0;

            Send(Msg("state",
                "version", AppInfo.Version,
                "platform", "linux",
                "os", AppInfo.OsDescription,
                "admin", AppInfo.IsAdmin,
                "portable", Paths.IsPortable,
                "dataDir", Paths.DataDir,
                "webview2", "",
                "engineReady", NfqwsEngine.Ready,
                "engineProblem", NfqwsEngine.Problem ?? "",
                "engineVersion", NfqwsEngine.Ready ? "nfqws" : "",
                "engineKind", "v1",
                "engineV1", NfqwsEngine.Ready,
                "engineV2", false,
                "engineV2Reason", "на Linux используется nfqws",
                "engineOwn", false,
                "engineOwnReason", "свой движок есть только на Windows (WinDivert)",
                "protection", running ? "включён" : "выключен",
                "protectionError", "",
                "activeStrategy", _engine.StrategyName,
                "uptimeSec", uptime,
                "strategyId", _settings.StrategyId,
                "scan", "",
                "scanFresh", false,
                "scanGood", false,
                "scanCount", 0L,
                "gameFilter", _settings.GameFilter,
                "ipsetMode", _settings.IpsetMode,
                "autoPilot", false,
                "proxyEnabled", false,
                "proxyHost", _settings.ProxyHost,
                "proxyPort", (long)_settings.ProxyPort,
                "proxySecret", "",
                "proxyRelay", "",
                "proxyLink", "",
                "theme", _settings.Theme,
                "simpleMode", _settings.SimpleMode,
                "minimizeToTray", false,
                "startMinimized", false,
                "updateEngine", _settings.UpdateEngine,
                "updateApp", _settings.UpdateApp,
                "updateLists", _settings.UpdateLists,
                "updateChannel", _settings.UpdateChannel,
                "language", _settings.Language,
                "autoStartProtect", _settings.AutoStart,
                "reconnectOnStrategy", _settings.ReconnectOnStrategy,
                "serviceRestartOnStrategy", false));
        }

        public void SendStrategies()
        {
            List<object> items = new List<object>();

            foreach (Strategy s in _store.ByEngine("v1"))
            {
                List<object> targets = new List<object>();
                foreach (string t in s.Targets) targets.Add(t);

                items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", s.Id },
                    { "name", s.Name },
                    { "engine", s.Engine },
                    { "note", s.Note ?? "" },
                    { "recommended", s.Recommended },
                    { "custom", s.Custom },
                    { "source", s.Source ?? "" },
                    { "targets", targets }
                });
            }

            Send(Msg("strategies",
                "items", items,
                "engine", "v1",
                "total", (long)items.Count));
        }

        public void Shutdown()
        {
            try { _engine.Stop(); } catch { }
        }
    }
}
