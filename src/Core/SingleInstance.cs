using System;
using System.Threading;

namespace AutoZapret.Core
{
    /// <summary>
    /// Один экземпляр приложения.
    ///
    /// Зачем: два процесса AutoZapret будут драться за один и тот же winws,
    /// за один профиль WebView2 (он блокируется) и за файлы настроек.
    ///
    /// Как работает: именованный мьютекс в сессии пользователя (Local\).
    /// Второй экземпляр видит, что мьютекс занят, дёргает событие-«покажи окно»
    /// и выходит. Первый слушает это событие в фоновом потоке.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\AutoZapret.SingleInstance";
        private const string ActivateEventName = @"Local\AutoZapret.Activate";

        private Mutex _mutex;
        private EventWaitHandle _activateEvent;
        private Thread _listener;
        private volatile bool _running;
        private bool _owned;

        /// <summary>Пришёл запрос «покажи окно» от второго экземпляра. Вызывается из фонового потока.</summary>
        public event Action ActivationRequested;

        public bool IsOwner { get { return _owned; } }

        /// <summary>
        /// Пытается стать единственным экземпляром.
        /// true  - мы первые, можно работать;
        /// false - уже кто-то есть, мы дёрнули его и должны выйти.
        ///
        /// waitMs &gt; 0 — подождать, пока мьютекс освободится. Нужно при перезапуске
        /// с повышением прав: старый процесс живёт ещё несколько сотен миллисекунд
        /// после старта нового, и без ожидания новый экземпляр молча выходит.
        /// </summary>
        public bool TryAcquire(int waitMs = 0)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, waitMs));

            while (true)
            {
                if (TryAcquireOnce()) return true;

                if (DateTime.UtcNow >= deadline)
                {
                    SignalActivate();
                    return false;
                }

                System.Threading.Thread.Sleep(120);
            }
        }

        private bool TryAcquireOnce()
        {
            try
            {
                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                if (!createdNew)
                {
                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }

                _owned = true;
                StartListener();
                return true;
            }
            catch (AbandonedMutexException)
            {
                // предыдущий процесс умер, не освободив мьютекс - это нормально, мы теперь владелец
                _owned = true;
                StartListener();
                return true;
            }
            catch (Exception ex)
            {
                // не смогли - работаем без защиты, но честно пишем в лог
                Log.Warn("APP", "single-instance недоступен: " + ex.Message);
                _owned = true;
                return true;
            }
        }

        private static void SignalActivate()
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out ev))
                {
                    using (ev) ev.Set();
                }
            }
            catch { }
        }

        private void StartListener()
        {
            try
            {
                bool createdNew;
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out createdNew);
                _running = true;
                _listener = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "single-instance"
                };
                _listener.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "не удалось слушать активацию: " + ex.Message);
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    if (!_activateEvent.WaitOne(500)) continue;
                    if (!_running) return;
                    Action handler = ActivationRequested;
                    if (handler != null)
                    {
                        try { handler(); } catch { }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("APP", "single-instance listener: " + ex.Message);
                    return;
                }
            }
        }

        public void Dispose()
        {
            _running = false;

            try { if (_activateEvent != null) _activateEvent.Set(); } catch { }
            try { if (_listener != null) _listener.Join(300); } catch { }
            try { if (_activateEvent != null) _activateEvent.Dispose(); } catch { }
            _activateEvent = null;

            if (_owned && _mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            try { if (_mutex != null) _mutex.Dispose(); } catch { }
            _mutex = null;
            _owned = false;
        }
    }
}
