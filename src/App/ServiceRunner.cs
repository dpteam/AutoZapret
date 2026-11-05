using System;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

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
    /// </summary>
    public sealed class ServiceRunner
    {
        private readonly ZapretService _zapret = new ZapretService();
        private readonly object _gate = new object();

        private Thread _watch;
        private volatile bool _running;
        private int _restarts;

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

            string error = _zapret.Start(strategyId);

            if (error != null)
            {
                Log.Error("SVC", "обход не запустился: " + error);
                return;
            }

            Log.Info("SVC", "обход запущен службой: " + strategyId);

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

            try { _zapret.Stop(); }
            catch (Exception ex) { Log.Exception("SVC", "остановка движка", ex); }

            try { _zapret.Dispose(); }
            catch (Exception ex) { Log.Exception("SVC", "dispose движка", ex); }

            Log.Info("SVC", "служба остановлена, движок погашен");
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

                ProtectionState state;

                try { state = _zapret.State; }
                catch { continue; }

                if (state == ProtectionState.Running) continue;

                if (_restarts >= 5)
                {
                    Log.Error("SVC", "движок падает повторно (" + _restarts +
                                     " раз) — перезапуск прекращён, нужен человек");

                    _running = false;
                    break;
                }

                _restarts++;

                Log.Warn("SVC", "движок не работает (" + state + "), перезапускаю " +
                                _restarts + "/5");

                try
                {
                    Settings settings = Settings.Load();
                    Settings.SetCurrent(settings);

                    _zapret.Stop();
                    Thread.Sleep(1200);

                    string error = _zapret.Start(settings.StrategyId);

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
        }

        /// <summary>Строка состояния для интерфейса и лога.</summary>
        public string StatusLine()
        {
            return _zapret.State + (_restarts > 0 ? ", перезапусков " + _restarts : "");
        }
    }
}