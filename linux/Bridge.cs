using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoZapret.Core;
using AutoZapret.Diag;
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

        // Долгие операции (проверка, перебор) идут в своём потоке и должны
        // уметь останавливаться: перебор 141 стратегии — это часы, и
        // единственный способ прекратить его иначе — закрыть программу.
        private CancellationTokenSource _busy;
        private string _busyName = "";

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

                case "logs.get":
                {
                    List<object> lines = new List<object>();

                    foreach (LogEntry entry in Log.Snapshot()) lines.Add(entry.ToLine());

                    Send(Msg("logs", "lines", lines, "file", Log.CurrentFile ?? ""));
                    break;
                }

                case "logs.clear":
                {
                    Log.Clear();
                    Send(Msg("logs", "lines", new List<object>(), "file", Log.CurrentFile ?? ""));
                    break;
                }

                case "diag.probe":
                    StartProbe();
                    break;

                case "diag.test":
                    StartSweep(Json.GetInt(message, "count", 5));
                    break;

                case "diag.cancel":
                    CancelBusy();
                    break;

                case "diag.targets":
                {
                    // Сколько целей будет проверяться. Нужно ДО запуска: иначе
                    // «проверило не всё» выясняется после долгого ожидания.
                    List<ProbeTarget> targets = ConnectivityProbe.ZapretTargets();

                    Send(Msg("diag.targets",
                        "count", targets.Count,
                        "source", "стандартный список",
                        "note", targets.Count + " целей"));
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

        // ------------------------------------------------------------------ проверки

        /// <summary>
        /// Проверка доступности без обхода: что открывается, а что нет.
        ///
        /// Это первое, что нужно человеку на новой машине: пока неизвестно,
        /// что именно заблокировано, любая стратегия — угадывание.
        /// </summary>
        private void StartProbe()
        {
            if (!BeginBusy("проверка сети")) return;

            CancellationToken token = _busy.Token;

            Thread thread = new Thread(delegate()
            {
                bool wasRunning = _engine.IsRunning;
                Strategy previous = wasRunning ? _store.Find(_settings.StrategyId) : null;

                try
                {
                    // Кнопка говорит «проверка сети БЕЗ обхода», значит и мерить
                    // надо без него: с включённым nfqws результат показывает
                    // работу обхода, а не то, что блокирует провайдер.
                    if (wasRunning) _engine.Stop();

                    ConnectivityProbe probe = new ConnectivityProbe();

                    // Цели только «zapret»: Telegram по HTTPS десинком не лечится,
                    // и держать его в этой проверке значит обещать невозможное.
                    ProbeReport report = probe.CheckAll(ConnectivityProbe.ZapretTargets(), 10, 4, token);

                    if (token.IsCancellationRequested)
                    {
                        Send(Msg("diag.probe", "items", new List<object>(),
                                 "summary", "проверка остановлена", "cancelled", true));
                        return;
                    }

                    List<object> items = new List<object>();

                    foreach (ProbeResult r in report.Results)
                    {
                        items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "name", r.Name },
                            { "ok", r.Ok },
                            { "detail", r.Ok
                                ? r.Status + " · " + r.ElapsedMs + " мс"
                                : (r.Error.Length > 0 ? r.Error : "нет ответа") }
                        });
                    }

                    string summary = "работает " + report.OkCount + " из " + report.Total;

                    // «0 из 6» без причины не говорит ничего: то ли провайдер
                    // режет, то ли DNS не отвечает, то ли сети нет вовсе.
                    string why = report.FailureSummary();
                    if (why.Length > 0) summary += " · не открылось: " + why;

                    Send(Msg("diag.probe", "items", items, "summary", summary));
                }
                catch (Exception ex)
                {
                    Log.Exception("DIAG", "проверка сети", ex);
                    Send(Msg("error", "message", "проверка сети: " + ex.Message));
                }
                finally
                {
                    // Обход был включён до проверки — возвращаем его. Иначе
                    // человек нажал «Проверить» и остался без обхода, решив,
                    // что сломалась программа.
                    if (wasRunning && previous != null)
                    {
                        string error = _engine.Start(previous, _settings.GameFilter);

                        if (error != null) Log.Warn("DIAG", "обход не вернулся после проверки: " + error);
                    }

                    SendState();
                    EndBusy();
                }
            });

            thread.IsBackground = true;
            thread.Name = "probe-loop";
            thread.Start();
        }

        /// <summary>
        /// Перебор стратегий: включить каждую, проверить, выключить.
        ///
        /// Зачем на Linux: у другого провайдера рабочая стратегия другая, и
        /// узнать её можно только опытом. На Windows это делает StrategyTester,
        /// но он завязан на WinDivert и живёт в App; здесь — тот же порядок
        /// действий, только через nfqws.
        /// </summary>
        private void StartSweep(int count)
        {
            if (!AppInfo.IsAdmin)
            {
                // Без root nfqws не откроет сокет к ядру, и каждая стратегия
                // «не запустилась». Честнее сказать это сразу, чем показывать
                // таблицу из нулей и заставлять искать причину.
                //
                // Служба упомянута не для полноты: она держит очередь 200, и
                // пока она работает, перебор упрётся в «очередь занята» —
                // снаружи это «перебор не работает».
                string hint = "перебору нужны права root: остановите службу " +
                              "(sudo systemctl stop autozapret) и запустите программу через sudo";

                Log.Warn("DIAG", hint);
                Send(Msg("diag.test", "items", new List<object>(), "summary", hint));
                Send(Msg("notice", "text", hint));
                return;
            }

            if (!BeginBusy("перебор стратегий")) return;

            CancellationToken token = _busy.Token;

            Thread thread = new Thread(delegate() { Sweep(count, token); });

            thread.IsBackground = true;
            thread.Name = "sweep-loop";
            thread.Start();
        }

        private void Sweep(int count, CancellationToken token)
        {
            List<Strategy> pool = SweepOrder();

            int total = count <= 0 || count >= pool.Count ? pool.Count : count;

            List<ProbeTarget> targets = ConnectivityProbe.ZapretTargets();
            ConnectivityProbe probe = new ConnectivityProbe();

            List<object> items = new List<object>();

            DateTime started = DateTime.UtcNow;

            // Если обход уже работал, вернём его в конце: иначе после перебора
            // человек остаётся без обхода и не понимает, почему.
            string previousId = _settings.StrategyId;

            bool cancelled = false;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }

                    Strategy strategy = pool[i];

                    Send(Msg("diag.progress",
                        "index", i + 1,
                        "total", total,
                        "line", strategy.Name,
                        "elapsedSec", (long)(DateTime.UtcNow - started).TotalSeconds));

                    Dictionary<string, object> item = TryStrategy(strategy, probe, targets, token);

                    items.Add(item);

                    // Победителя подтверждаем сразу: перебирать дальше, зная,
                    // что уже есть рабочая, но не зная, держится ли она, —
                    // это способ найти «работает через раз».
                    int okCount = Convert.ToInt32(item["okCount"]);
                    int allTargets = Convert.ToInt32(item["total"]);

                    if (okCount == allTargets && allTargets > 0)
                    {
                        Confirm(strategy, probe, targets, item, token);
                        cancelled = token.IsCancellationRequested;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DIAG", "перебор", ex);
                Send(Msg("error", "message", "перебор: " + ex.Message));
            }
            finally
            {
                try { _engine.Stop(); } catch { }

                // Возвращаем прежнюю стратегию, если она была выбрана.
                if (previousId != null && previousId.Length > 0 && !token.IsCancellationRequested)
                {
                    Strategy previous = _store.Find(previousId);

                    if (previous != null)
                    {
                        string error = _engine.Start(previous, _settings.GameFilter);
                        if (error != null) Log.Warn("DIAG", "прежняя стратегия не вернулась: " + error);
                    }
                }

                SendState();
                EndBusy();
            }

            string summary = SweepSummary(items, cancelled);

            Send(Msg("diag.test", "items", items, "summary", summary, "cancelled", cancelled));

            Log.Info("DIAG", "перебор: " + summary);
        }

        /// <summary>
        /// Порядок перебора.
        ///
        /// Свои стратегии — первыми. На Windows это уже ловили: перебор брал
        /// первые N по алфавиту id, и импортированные стратегии не проверялись
        /// вообще — человек добавлял набор и не видел его в результатах.
        /// </summary>
        private List<Strategy> SweepOrder()
        {
            List<Strategy> all = _store.ByEngine("v1");
            List<Strategy> ordered = new List<Strategy>(all.Count);

            foreach (Strategy s in all) if (s.Custom) ordered.Add(s);
            foreach (Strategy s in all) if (!s.Custom) ordered.Add(s);

            return ordered;
        }

        private Dictionary<string, object> TryStrategy(Strategy strategy, ConnectivityProbe probe,
                                                       List<ProbeTarget> targets, CancellationToken token)
        {
            int okCount = 0;
            int total = targets.Count;
            int avgMs = 0;
            bool alive = false;
            string startError = null;
            List<string> failed = new List<string>();
            string reasons = "";

            try
            {
                startError = _engine.Start(strategy, _settings.GameFilter);

                if (startError == null)
                {
                    // Дать процессу подняться: правила уже стоят, но очередь
                    // подхватывается не мгновенно, и первая проба иначе
                    // показывает таймаут на рабочей стратегии.
                    for (int wait = 0; wait < 12 && !_engine.IsRunning; wait++) Thread.Sleep(100);
                    Thread.Sleep(400);

                    alive = _engine.IsRunning;

                    if (alive && !token.IsCancellationRequested)
                    {
                        ProbeReport report = probe.CheckAll(targets, 10, 4, token);

                        okCount = report.OkCount;
                        avgMs = report.AvgMs;
                        reasons = report.FailureSummary();

                        foreach (ProbeResult r in report.Results)
                            if (!r.Ok) failed.Add(r.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                startError = ex.Message;
            }
            finally
            {
                try { _engine.Stop(); } catch { }
            }

            if (startError != null) reasons = startError;

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", strategy.Id },
                { "name", strategy.Name },
                { "okCount", okCount },
                { "total", total },
                { "avgMs", avgMs },
                { "score", Score(okCount, total, avgMs, alive) },
                { "failed", failed },
                { "reasons", reasons },
                { "breakdown", "" },
                { "confirmRequired", 0 },
                { "confirmRounds", 0 },
                { "confirmTotal", 0 }
            };
        }

        /// <summary>
        /// Повторная проверка победителя.
        ///
        /// Один замер — это один замер: DPI мог не успеть распознать, сеть
        /// дёрнуться. На Windows без этого получалось «проверил — 6/6,
        /// поставил — не работает». Три захода с паузой, нужно два.
        /// </summary>
        private void Confirm(Strategy strategy, ConnectivityProbe probe, List<ProbeTarget> targets,
                             Dictionary<string, object> item, CancellationToken token)
        {
            const int rounds = 3;
            const int required = 2;

            int passed = 0;

            for (int round = 0; round < rounds; round++)
            {
                if (token.IsCancellationRequested) break;

                Send(Msg("diag.progress",
                    "line", "подтверждаю «" + strategy.Name + "»: заход " + (round + 1) + " из " + rounds));

                Thread.Sleep(700);

                string error = _engine.Start(strategy, _settings.GameFilter);

                if (error != null) continue;

                for (int wait = 0; wait < 12 && !_engine.IsRunning; wait++) Thread.Sleep(100);
                Thread.Sleep(400);

                try
                {
                    if (_engine.IsRunning)
                    {
                        ProbeReport report = probe.CheckAll(targets, 10, 4, token);

                        if (report.OkCount == report.Total && report.Total > 0) passed++;
                    }
                }
                finally
                {
                    try { _engine.Stop(); } catch { }
                }
            }

            item["confirmRequired"] = required;
            item["confirmRounds"] = passed;
            item["confirmTotal"] = rounds;
        }

        /// <summary>
        /// Оценка стратегии: сколько целей пробила, минус штраф за медленность.
        ///
        /// Простая и объяснимая арифметика намеренно: сложный балл, который
        /// нельзя пересчитать в голове, человеку ничего не говорит.
        /// </summary>
        private static int Score(int okCount, int total, int avgMs, bool alive)
        {
            if (!alive || total == 0) return 0;

            int score = (int)Math.Round(100.0 * okCount / total);

            if (okCount == 0) return 0;

            // Больше 3 секунд на цель — уже заметно в браузере.
            if (avgMs > 3000) score -= 10;
            else if (avgMs > 1500) score -= 5;

            return score < 0 ? 0 : score;
        }

        private static string SweepSummary(List<object> items, bool cancelled)
        {
            if (items.Count == 0) return cancelled ? "перебор остановлен" : "нет стратегий для перебора";

            Dictionary<string, object> best = null;
            int bestOk = -1;
            int bestScore = -1;

            foreach (object o in items)
            {
                Dictionary<string, object> item = o as Dictionary<string, object>;
                if (item == null) continue;

                int ok = Convert.ToInt32(item["okCount"]);
                int score = Convert.ToInt32(item["score"]);

                if (ok > bestOk || (ok == bestOk && score > bestScore))
                {
                    best = item;
                    bestOk = ok;
                    bestScore = score;
                }
            }

            string text = "проверено " + items.Count;

            if (best != null && bestOk > 0)
            {
                text += " · лучшая: «" + best["name"] + "» " + bestOk + "/" + best["total"] +
                        ", счёт " + bestScore;

                int rounds = Convert.ToInt32(best["confirmRounds"]);
                int required = Convert.ToInt32(best["confirmRequired"]);

                if (required > 0)
                    text += rounds >= required
                        ? " · подтверждена " + rounds + " из " + best["confirmTotal"]
                        : " · повторная проверка не прошла";
            }
            else
            {
                text += " · ни одна не пробила ни одной цели";
            }

            if (cancelled) text += " · остановлено";

            return text;
        }

        // ------------------------------------------------------------------ долгие операции

        /// <summary>
        /// Начать долгую операцию. Вторая одновременная не запускается: две
        /// пробы сразу делят канал и обе показывают неправду.
        /// </summary>
        private bool BeginBusy(string name)
        {
            lock (_gate)
            {
                if (_busy != null)
                {
                    Send(Msg("notice", "text", "уже идёт: " + _busyName));
                    return false;
                }

                _busy = new CancellationTokenSource();
                _busyName = name;
            }

            // diag.state, а не только подсказка: страница по нему блокирует
            // остальные кнопки проверок и включает «Отменить». Без этого
            // «Проверить» и «Подобрать» можно запустить одновременно, и они
            // начинают спорить за очередь nfqws.
            Send(Msg("diag.state", "running", true, "title", name));
            Send(Msg("diag.busy", "message", name + "…"));
            return true;
        }

        private void EndBusy()
        {
            lock (_gate)
            {
                if (_busy != null) { _busy.Dispose(); _busy = null; }
                _busyName = "";
            }

            // Снять «идёт проверка» обязательно на любом выходе, включая
            // ошибку: иначе кнопки остаются заблокированными навсегда, и
            // снаружи это «программа зависла».
            Send(Msg("diag.state", "running", false, "title", ""));
        }

        private void CancelBusy()
        {
            CancellationTokenSource busy;

            lock (_gate) { busy = _busy; }

            if (busy == null)
            {
                Send(Msg("notice", "text", "нечего останавливать"));
                return;
            }

            busy.Cancel();
            Log.Info("DIAG", "остановка: " + _busyName);
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
