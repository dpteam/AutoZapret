using System;
using System.Runtime.InteropServices;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>
    /// Тело службы Windows: то, что вызывает SCM.
    ///
    /// Почему свой P/Invoke, а не Microsoft.Extensions.Hosting.WindowsServices:
    /// NuGet запрещён (AGENTS.md п.2), а без него остаётся ровно три вызова.
    ///
    /// Почему нельзя просто `sc create` на нашем GUI-процессе: SCM требует,
    /// чтобы процесс вызвал StartServiceCtrlDispatcher в течение 30 секунд,
    /// иначе убивает его с ошибкой 1053 «не ответил вовремя». Окно и WebView2
    /// в службе создать нельзя вообще — session 0 isolation, окно не появится
    /// никогда, а попытка его создать вешает процесс.
    ///
    /// Порядок строго такой:
    ///   1. StartServiceCtrlDispatcher  — сразу, до любой инициализации;
    ///   2. наш ServiceMain            — регистрируем обработчик и рапортуем STARTING;
    ///   3. работа                    — движок;
    ///   4. STOPPED                   — иначе SCM будет считать службу висящей.
    /// </summary>
    public static class ServiceHost
    {
        // ---- константы SCM (winnt.h / winsvc.h)
        public const int ServiceWin32OwnProcess = 0x00000010;
        public const int ServiceAcceptStop = 0x00000001;
        public const int ServiceAcceptShutdown = 0x00000004;

        public const int ServiceStopped = 0x00000001;
        public const int ServiceStartPending = 0x00000002;
        public const int ServiceStopPending = 0x00000003;
        public const int ServiceRunning = 0x00000004;

        public const int ServiceControlStop = 0x00000001;
        public const int ServiceControlInterrogate = 0x00000004;
        public const int ServiceControlShutdown = 0x00000005;
        public const int ServiceControlPreshutdown = 0x0000000F;

        public const int ErrorFailedServiceControllerConnect = 1063;
        public const int ErrorServiceNotInstalled = 1060;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public int ServiceType;
            public int CurrentState;
            public int ControlsAccepted;
            public int Win32ExitCode;
            public int ServiceSpecificExitCode;
            public int CheckPoint;
            public int WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceTableEntry
        {
            [MarshalAs(UnmanagedType.LPStr)]
            public string ServiceName;

            public IntPtr ServiceProc;
        }

        private delegate void ServiceMainFunction(int argc, IntPtr argv);
        private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool StartServiceCtrlDispatcherW(IntPtr serviceTable);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerEx handler, IntPtr context);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

        // делегаты обязаны жить всё время работы службы: соберёт GC — SCM
        // вызовет освобождённую память и упадёт весь процесс
        private static ServiceMainFunction _mainCallback;
        private static HandlerEx _handlerCallback;

        private static IntPtr _statusHandle = IntPtr.Zero;
        private static ManualResetEvent _stop = new ManualResetEvent(false);
        private static bool _stopping;

        /// <summary>
        /// Точка входа режима `--service`. Блокируется до остановки службы.
        /// Возвращает код выхода для SCM.
        /// </summary>
        public static int Run(string serviceName)
        {
            Log.Info("SVC", "запуск режима службы: " + serviceName);

            _mainCallback = ServiceMain;
            _handlerCallback = HandleControl;

            IntPtr namePtr = Marshal.StringToHGlobalAnsi(serviceName);
            IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(_mainCallback);

            // массив из двух записей: наша и терминатор (нулевые поля)
            int entrySize = Marshal.SizeOf(typeof(ServiceTableEntry));
            IntPtr table = Marshal.AllocHGlobal(entrySize * 2);

            try
            {
                ServiceTableEntry entry = new ServiceTableEntry
                {
                    ServiceName = serviceName,
                    ServiceProc = procPtr
                };

                Marshal.StructureToPtr(entry, table, false);

                ServiceTableEntry terminator = new ServiceTableEntry
                {
                    ServiceName = null,
                    ServiceProc = IntPtr.Zero
                };

                Marshal.StructureToPtr(terminator, new IntPtr(table.ToInt64() + entrySize), false);

                bool ok = StartServiceCtrlDispatcherW(table);

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();

                    // 1063 = запустили руками из консоли, а не через SCM.
                    // Это не поломка: человеку надо сказать, что делать.
                    if (error == ErrorFailedServiceControllerConnect)
                    {
                        Log.Error("SVC", "это режим службы: запускать нужно через SCM, а не из консоли");

                        Console.WriteLine("Это тело службы: его запускает SCM, а не человек.");
                        Console.WriteLine("Установить и запустить: AutoZapret.exe --service install|start");
                        return 5;
                    }

                    Log.Error("SVC", "StartServiceCtrlDispatcher: код " + error);
                    return 6;
                }

                Log.Info("SVC", "служба остановлена штатно");
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
                Marshal.FreeHGlobal(namePtr);
            }
        }

        /// <summary>Тело службы: вызывается SCM. Работает до STOP.</summary>
        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = RegisterServiceCtrlHandlerExW("AutoZapret", _handlerCallback, IntPtr.Zero);

            if (_statusHandle == IntPtr.Zero)
            {
                Log.Error("SVC", "RegisterServiceCtrlHandlerEx не удался: " + Marshal.GetLastWin32Error());
                return;
            }

            Report(ServiceStartPending, 0, 3000);

            try
            {
                // Всё, что делает служба: движок. Ни UI, ни трея, ни прокси.
                ServiceRunner runner = new ServiceRunner();
                runner.Start();

                Report(ServiceRunning, 0, 0);

                Log.Info("SVC", "служба работает");

                // ждём STOP/SHUTDOWN — SCM сам нас не убьёт, пока мы не выйдем
                _stop.WaitOne();

                Report(ServiceStopPending, 0, 15000);

                Log.Info("SVC", "останавливаю движок");
                runner.Stop();

                Report(ServiceStopped, 0, 0);
            }
            catch (Exception ex)
            {
                Log.Exception("SVC", "работа службы", ex);

                // 1064 = исключение в службе; без этого SCM покажет «запущена»,
                // хотя внутри уже всё упало
                Report(ServiceStopped, 1064, 0);
            }
        }

        private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case ServiceControlStop:
                case ServiceControlShutdown:
                case ServiceControlPreshutdown:
                    if (_stopping) return 0;
                    _stopping = true;

                    Log.Info("SVC", "получен запрос остановки: " + control);
                    _stop.Set();
                    return 0;

                case ServiceControlInterrogate:
                    // SCM спрашивает состояние — отвечаем текущим, иначе решит,
                    // что служба не отвечает
                    Report(_stopping ? ServiceStopPending : ServiceRunning, 0, 0);
                    return 0;
            }

            return 0;
        }

        private static void Report(int state, int exitCode, int waitHint)
        {
            if (_statusHandle == IntPtr.Zero) return;

            ServiceStatus status = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = ServiceAcceptStop | ServiceAcceptShutdown,
                Win32ExitCode = exitCode,
                ServiceSpecificExitCode = 0,
                CheckPoint = 0,
                WaitHint = waitHint
            };

            // при STARTING/STOPPING чекпоинт обязан расти, иначе SCM посчитает
            // службу зависшей и убьёт её
            if (state == ServiceStartPending || state == ServiceStopPending)
                status.CheckPoint = (int)(DateTime.UtcNow.Ticks % 1000);

            SetServiceStatus(_statusHandle, ref status);
        }
    }
}