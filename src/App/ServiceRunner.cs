using System;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;
using AutoZapret.Zapret.Own;

namespace AutoZapret.App
{
    /// <summary>
    /// Что именно делает служба: держит движок запущенным.
    ///
    /// Отдельный класс от AppHost намеренно. AppHost — это окно, мост в
    /// WebView2, трей, прокси, автопилот: в службе ничего этого быть не может.
    /// Здесь только «прочитать настройки и запустить обход», плюс присмотр:
    /// если движок умер — поднять заново, потому что служба без движка
    /// бесполезна, а человек в этот момент даже не смотрит в монитор.
    ///
    /// Прокси Telegram сюда не входит: он слушает порт и принадлежит сессии
    /// пользователя. Два независимых жизненных цикла в одном процессе — это
    /// гарантированные проблемы при остановке.
    ///
    /// Движок берётся из настроек, как и в интерфейсе: если человек выбрал
    /// «свой движок», служба обязана запускать именно его. Иначе переключение
    /// движка работало бы только при открытом окне — а служба как раз для
    /// случая, когда окна нет.
    /// </summary>
    public sealed class ServiceRunner
    {
        private readonly ZapretService _zapret = new ZapretService();
        private readonly object _gate = new object();

        private Thread _watch;
        private volatile bool _running;
        private int _restarts;

        // Свой движок: свой поток, своя отмена. winws и он не сосуществуют —
        // драйвер один, и второй старт получит «занято».
        private OwnEngine _ownEngine;
        private CancellationTokenSource _ownCancel;
        private Thread _ownThread;
        private volatile bool _ownAlive;
        private volatile string _ownError;

        /// <summary>Запустить обход. Ошибки пишем в лог: показать их некому.</summary>
        public void Start()
        {
            Settings settings = Settings.Load();
            Settings.SetCurrent(settings);

            _zapret.EnsureStrategies();

            string strategyId = settings.StrategyId;

            if (string.IsNullOrEmpty(strategyId))
            {
                Log.Error("SVC", "стратегия не выбрана: служба запущена, но обход не включён");
                return;
            }

            string error = StartSelected(settings);

            if (error != null)
            {
                Log.Error("SVC", "обход не запустился: " + error);
                return;
            }

            Log.Info("SVC", "обход запущен службой: " + strategyId +
                            " (" + EngineTitle(settings.EngineKind) + ")");

            _running = true;

            _watch = new Thread(Watch)
            {
                IsBackground = true,
                Name = "svc-watch"
            };

            _watch.Start();
        }

        public void Stop()
        {
            _running = false;

            try { if (_watch != null) _watch.Join(2000); } catch { }

            StopOwn();

            try { _zapret.Stop(); }
            catch (Exception ex) { Log.Exception("SVC", "остановка движка", ex); }

            try { _zapret.Dispose(); }
            catch (Exception ex) { Log.Exception("SVC", "dispose движка", ex); }

            Log.Info("SVC", "служба остановлена, движок погашен");
        }

        // ------------------------------------------------------------------ engine

        private static string EngineTitle(string kind)
        {
            if (string.Equals(kind, "v2", StringComparison.OrdinalIgnoreCase)) return "zapret 2";
            if (string.Equals(kind, "own", StringComparison.OrdinalIgnoreCase)) return "свой движок";

            return "zapret 1";
        }

        /// <summary>Запустить тот движок, который выбран в настройках.</summary>
        private string StartSelected(Settings settings)
        {
            if (string.Equals(settings.EngineKind, "own", StringComparison.OrdinalIgnoreCase))
                return StartOwn(settings);

            return _zapret.Start(settings.StrategyId);
        }

        /// <summary>
        /// Свой движок в службе: разбираем стратегию и запускаем.
        ///
        /// Отказ честный: если из стратегии нечего исполнять, служба об этом
        /// скажет в лог, а не подменит движок на winws молча. Молчаливая
        /// подмена — худший вариант: человек выбрал один движок, работает
        /// другой, и узнает он об этом никогда.
        /// </summary>
        private string StartOwn(Settings settings)
        {
            Strategy strategy = _zapret.Strategies.Find(settings.StrategyId);

            if (strategy == null) return "стратегия не найдена: " + settings.StrategyId;

            if (string.Equals(strategy.Engine, "v2", StringComparison.OrdinalIgnoreCase))
                return "стратегии zapret 2 написаны на Lua — своим движком их не исполнить";

            CompatResult compat = WinwsCompat.Parse(
                strategy, _zapret.Engine.BuildPlaceholders(settings.GameFilter));

            Log.Info("SVC", "стратегия " + strategy.Id + ": " + compat.Summary());

            if (!compat.Ok) return "из стратегии нечего исполнять: " + compat.Summary();

            if (!_zapret.Engine.IsAvailable(EngineKind.Own))
                return _zapret.Engine.UnavailableReason(EngineKind.Own);

            OwnEngineOptions options = new OwnEngineOptions
            {
                Profiles = compat.Profiles,
                Seconds = 0,
                DryRun = false
            };

            CancellationTokenSource cancel;
            OwnEngine engine = new OwnEngine(options);

            lock (_gate)
            {
                _ownCancel = new CancellationTokenSource();
                cancel = _ownCancel;
                _ownEngine = engine;
                _ownError = null;
            }

            ManualResetEventSlim started = new ManualResetEventSlim(false);
            string startupError = null;
            bool failed = false;

            Thread worker = new Thread(delegate ()
            {
                string error;

                try
                {
                    error = engine.Run(cancel.Token, () => started.Set());
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Exception("SVC", "свой движок", ex);
                }

                if (!started.IsSet)
                {
                    lock (_gate) { startupError = error; failed = true; }

                    started.Set();
                    return;
                }

                // сюда пришли, когда движок остановился сам (ошибка драйвера,
                // выгрузка) — присмотр увидит это по _ownAlive
                _ownAlive = false;
                _ownError = error;

                Log.Warn("SVC", "свой движок остановился: " +
                                (error ?? engine.Stats.Summary()));
            })
            {
                IsBackground = true,
                Name = "svc-own-engine"
            };

            lock (_gate) _ownThread = worker;

            worker.Start();

            started.Wait(10000);

            lock (_gate)
            {
                if (failed)
                {
                    _ownEngine = null;
                    _ownCancel = null;
                    _ownThread = null;

                    return startupError ?? "свой движок не запустился";
                }
            }

            _ownAlive = true;

            return null;
        }

        private void StopOwn()
        {
            CancellationTokenSource cancel;
            Thread thread;

            lock (_gate)
            {
                cancel = _ownCancel;
                thread = _ownThread;

                _ownEngine = null;
                _ownCancel = null;
                _ownThread = null;
            }

            if (cancel == null) return;

            try { cancel.Cancel(); } catch { }
            try { if (thread != null) thread.Join(4000); } catch { }

            _ownAlive = false;
        }

        /// <summary>
        /// Присмотр: раз в 5 секунд проверяем, жив ли движок.
        ///
        /// Зачем: служба работает без человека. Если winws упал (а он падает,
        /// например при смене сети), без присмотра обход просто выключится,
        /// и никто этого не заметит. Но и бесконечно перезапускать нельзя:
        /// при систематической ошибке это будет цикл из падений в лог.
        /// </summary>
        private void Watch()
        {
            while (_running)
            {
                Thread.Sleep(5000);
                if (!_running) break;

                if (!IsEngineAlive())
                {
                    if (_restarts >= 5)
                    {
                        Log.Error("SVC", "движок падает повторно (" + _restarts +
                                         " раз) — перезапуск прекращён, нужен человек");

                        _running = false;
                        break;
                    }

                    _restarts++;

                    Log.Warn("SVC", "движок не работает (" + StateText() + "), перезапускаю " +
                                    _restarts + "/5");

                    Restart();
                }
            }
        }

        private bool IsEngineAlive()
        {
            Settings settings = Settings.Current;

            bool own = settings != null &&
                       string.Equals(settings.EngineKind, "own", StringComparison.OrdinalIgnoreCase);

            if (own) return _ownAlive;

            ProtectionState state;

            try { state = _zapret.State; }
            catch { return true; }     // не смогли прочитать — не повод перезапускать

            return state == ProtectionState.Running;
        }

        private string StateText()
        {
            Settings settings = Settings.Current;

            bool own = settings != null &&
                       string.Equals(settings.EngineKind, "own", StringComparison.OrdinalIgnoreCase);

            if (own) return _ownAlive ? "работает" : (_ownError ?? "остановлен");

            try { return _zapret.State.ToString(); }
            catch { return "неизвестно"; }
        }

        private void Restart()
        {
            try
            {
                Settings settings = Settings.Load();
                Settings.SetCurrent(settings);

                StopOwn();

                try { _zapret.Stop(); } catch { }

                Thread.Sleep(1200);

                string error = StartSelected(settings);

                if (error != null)
                    Log.Error("SVC", "перезапуск не удался: " + error);
                else
                    Log.Info("SVC", "движок поднят заново");
            }
            catch (Exception ex)
            {
                Log.Exception("SVC", "перезапуск", ex);
            }
        }

        /// <summary>Строка состояния для интерфейса и лога.</summary>
        public string StatusLine()
        {
            string state = _ownAlive ? "работает (свой движок)" : _zapret.State.ToString();

            return state + (_restarts > 0 ? ", перезапусков " + _restarts : "");
        }
    }
}
