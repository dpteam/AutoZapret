using System;
using System.Collections.Generic;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Diag;
using AutoZapret.Zapret;

namespace AutoZapret.Orchestrator
{
    public enum AutoPilotState
    {
        Off,
        Waiting,      // ждём следующей проверки
        Probing,      // проверяем текущую стратегию
        Switching,    // переключаемся на другую
        Failed        // ничего не работает
    }

    public sealed class AutoPilotStatus
    {
        public AutoPilotState State = AutoPilotState.Off;
        public string Message = "";
        public string Network = "";
        public string CurrentStrategy = "";
        public int LastScore;
        public int ConsecutiveFailures;
        public DateTime NextCheck = DateTime.MinValue;
        public int ProbesDone;
    }

    /// <summary>
    /// Автопилот: держит обход рабочим без участия пользователя.
    ///
    /// Цикл (раз в CheckInterval, по умолчанию 20 минут):
    ///   1. проверить, что текущая стратегия реально пробивает цели;
    ///   2. записать результат в статистику (по сети!);
    ///   3. после N неудач подряд — переключиться на другую по бандиту;
    ///   4. если ни одна не работает — честно сообщить, а не крутиться молча.
    ///
    /// Чего здесь СОЗНАТЕЛЬНО нет:
    ///   * никаких LLM и внешних API — только статистика;
    ///   * генетической эволюции стратегий: она меняет аргументы winws,
    ///     а непроверенная комбинация легко ломает обход полностью. Перебор
    ///     готовых 44 стратегий даёт почти тот же эффект без этого риска.
    ///
    /// Антифлаппинг обязателен: одна турбулентная проба не должна ронять
    /// рабочую стратегию. Порог — 2 неудачи подряд и успех < 50%.
    /// </summary>
    public sealed class AutoPilot : IDisposable
    {
        private readonly ZapretService _zapret;
        private readonly ScoreStore _store;
        private readonly Bandit _bandit;

        private readonly object _gate = new object();
        private Thread _thread;
        private volatile bool _running;
        private bool _disposed;

        private AutoPilotStatus _status = new AutoPilotStatus();
        private string _network = "";
        private string _current = "";

        public AutoPilot(ZapretService zapret, ScoreStore store)
        {
            _zapret = zapret;
            _store = store;
            _bandit = new Bandit();
        }

        /// <summary>Интервал проверки в минутах.</summary>
        public int CheckIntervalMinutes = 20;

        /// <summary>Сколько неудач подряд терпим перед переключением.</summary>
        public int FailuresBeforeSwitch = 2;

        /// <summary>Порог «стратегия работает» — доля успешных целей.</summary>
        public double SuccessThreshold = 0.5;

        public event Action<AutoPilotStatus> StatusChanged;

        /// <summary>Стратегия сменилась (нужно обновить настройки и UI).</summary>
        public event Action<string> StrategySwitched;

        public bool IsRunning { get { return _running; } }

        public AutoPilotStatus Status
        {
            get { lock (_gate) return _status; }
        }

        // ---------------------------------------------------------------- start

        public void Start(string strategyId)
        {
            lock (_gate)
            {
                if (_running) return;

                _current = strategyId ?? "";
                _network = NetworkFingerprint.Hash();
                _running = true;

                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "auto-pilot"
                };
                _thread.Start();
            }

            Log.Info("AUTO", "автопилот запущен, сеть " + _network + ", стратегия " + strategyId);
            SetStatus(AutoPilotState.Waiting, "автопилот включён", 0);
        }

        public void Stop()
        {
            Thread thread;

            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                thread = _thread;
                _thread = null;
            }

            try { if (thread != null) thread.Join(3000); } catch { }

            try { _store.Save(); } catch { }

            Log.Info("AUTO", "автопилот остановлен");
            SetStatus(AutoPilotState.Off, "автопилот выключен", 0);
        }

        /// <summary>Разбудить немедленно (кнопка «проверить сейчас»).</summary>
        public void WakeUp()
        {
            lock (_gate)
            {
                if (!_running) return;
                _status.NextCheck = DateTime.Now;
            }
        }

        /// <summary>
        /// Один цикл синхронно — для CLI-проверки и кнопки «проверить сейчас».
        /// Требует, чтобы обход уже работал.
        /// </summary>
        public void RunOnce(string strategyId)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_current)) _current = strategyId ?? "";
                if (string.IsNullOrEmpty(_network)) _network = NetworkFingerprint.Hash();
            }

            RunCycle();
        }

        // ----------------------------------------------------------------- loop

        private void Loop()
        {
            // первая проверка — через минуту после включения, чтобы обход успел подняться
            DateTime next = DateTime.Now.AddMinutes(1);

            while (_running)
            {
                try
                {
                    DateTime now = DateTime.Now;
                    if (now < next)
                    {
                        lock (_gate) _status.NextCheck = next;
                        Thread.Sleep(1000);
                        continue;
                    }

                    RunCycle();

                    next = DateTime.Now.AddMinutes(Math.Max(1, CheckIntervalMinutes));
                    lock (_gate) _status.NextCheck = next;
                }
                catch (Exception ex)
                {
                    Log.Exception("AUTO", "цикл автопилота", ex);
                    Thread.Sleep(5000);
                }
            }
        }

        /// <summary>Один цикл: проверить, при необходимости переключить.</summary>
        private void RunCycle()
        {
            string strategy;
            string network;
            lock (_gate)
            {
                strategy = _current;
                network = _network;
            }

            if (string.IsNullOrEmpty(strategy)) return;

            // сеть могла смениться (дом -> мобильный интернет)
            string currentNetwork = NetworkFingerprint.Hash();
            if (!string.Equals(currentNetwork, network, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("AUTO", "сеть сменилась: " + network + " -> " + currentNetwork);
                lock (_gate)
                {
                    _network = currentNetwork;
                    network = currentNetwork;
                }
            }

            SetStatus(AutoPilotState.Probing, "проверяю стратегию…", 0);

            // проверяем против того, что реально заблокировано
            Baseline.Snapshot baseline = Baseline.Load();
            List<ProbeTarget> targets = baseline != null && baseline.Blocked.Count > 0
                ? baseline.BlockedTargets()
                : ConnectivityProbe.DefaultTargets();

            ConnectivityProbe probe = new ConnectivityProbe();
            ProbeReport report = probe.CheckAll(targets, 8, 4);

            bool ok = report.SuccessRate >= SuccessThreshold;
            int score = (int)Math.Round(report.SuccessRate * 100);

            _store.Register(strategy, network, ok, score, report.AvgMs,
                            ok ? "" : report.Summary());

            StrategyStats stats = _store.Get(strategy, network);

            Log.Info("AUTO", "проверка " + strategy + ": " + report.Summary() +
                             (ok ? " — работает" : " — не работает") +
                             ", неудач подряд " + stats.ConsecutiveFailures);

            lock (_gate) _status.ProbesDone++;

            if (ok)
            {
                SetStatus(AutoPilotState.Waiting,
                    "работает: " + report.OkCount + "/" + report.Total + " целей", score);
                try { _store.Save(); } catch { }
                return;
            }

            // не работает — сколько раз подряд?
            if (stats.ConsecutiveFailures < Math.Max(1, FailuresBeforeSwitch))
            {
                SetStatus(AutoPilotState.Waiting,
                    "не пробило (" + report.OkCount + "/" + report.Total +
                    "), жду подтверждения (" + stats.ConsecutiveFailures + "/" + FailuresBeforeSwitch + ")",
                    score);
                return;
            }

            // пора переключаться
            SwitchToBetter(network, strategy, report);
            try { _store.Save(); } catch { }
        }

        private void SwitchToBetter(string network, string currentStrategy, ProbeReport report)
        {
            SetStatus(AutoPilotState.Switching, "подбираю другую стратегию…", report.OkCount);

            _zapret.EnsureStrategies();

            string engine = Settings.Current.EngineKind;
            List<Strategy> pool = _zapret.Strategies.ByEngine(engine);
            if (pool.Count == 0) pool = new List<Strategy>(_zapret.Strategies.All);

            List<string> candidates = new List<string>();
            foreach (Strategy s in pool)
                if (!string.Equals(s.Id, currentStrategy, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(s.Id);

            if (candidates.Count == 0)
            {
                SetStatus(AutoPilotState.Failed, "других стратегий нет", report.OkCount);
                return;
            }

            string next = _bandit.Pick(candidates, _store, network);
            if (string.IsNullOrEmpty(next))
            {
                SetStatus(AutoPilotState.Failed, "не удалось выбрать стратегию", report.OkCount);
                return;
            }

            Log.Info("AUTO", "переключаюсь: " + currentStrategy + " -> " + next);

            try
            {
                _zapret.Stop();
                Thread.Sleep(1200);   // ждём выгрузки WinDivert

                string error = _zapret.Start(next);
                if (error != null)
                {
                    Log.Warn("AUTO", "не удалось запустить " + next + ": " + error);
                    _store.Register(next, network, false, 0, 0, error);
                    SetStatus(AutoPilotState.Failed, "не удалось запустить " + next, 0);
                    return;
                }

                lock (_gate) _current = next;

                Action<string> handler = StrategySwitched;
                if (handler != null) { try { handler(next); } catch { } }

                SetStatus(AutoPilotState.Waiting, "переключился на " + next, 0);
            }
            catch (Exception ex)
            {
                Log.Exception("AUTO", "переключение", ex);
                SetStatus(AutoPilotState.Failed, ex.Message, 0);
            }
        }

        // ---------------------------------------------------------------- state

        private void SetStatus(AutoPilotState state, string message, int score)
        {
            AutoPilotStatus snapshot;

            lock (_gate)
            {
                _status.State = state;
                _status.Message = message ?? "";
                _status.Network = _network;
                _status.CurrentStrategy = _current;
                _status.LastScore = score;
                _status.ConsecutiveFailures = string.IsNullOrEmpty(_current)
                    ? 0
                    : _store.Get(_current, _network).ConsecutiveFailures;

                snapshot = _status;
            }

            Action<AutoPilotStatus> handler = StatusChanged;
            if (handler != null) { try { handler(snapshot); } catch { } }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
        }
    }
}