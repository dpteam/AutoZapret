using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoZapret.Core;
using AutoZapret.Linux;
using AutoZapret.Zapret;

namespace AutoZapret.Linux
{
    /// <summary>
    /// Точка входа для Linux.
    ///
    /// Интерфейс — тот же HTML, что на Windows, но показывается в браузере:
    /// WebView2 существует только на Windows, а тащить GTK/Avalonia ради
    /// окна значило бы добавить зависимость и переписать весь UI.
    ///
    /// Мост. На Windows это chrome.webview.postMessage. Здесь — WebSocket,
    /// и app.js об этом не знает: перед ним подставляется шим, который
    /// создаёт window.chrome.webview поверх сокета. Ровно поэтому правило
    /// «обмен только в двух функциях» и было записано.
    /// </summary>
    internal static class Program
    {
        private static int _port = 8080;
        private static bool _openBrowser = true;
        private static Bridge _bridge;
        private static HttpListener _listener;

        public static int Main(string[] args)
        {
            Paths.EnsureDirectories();

            Log.Info("APP", AppInfo.Summary());

            if (HasFlag(args, "--version"))
            {
                Console.WriteLine(AppInfo.Name + " " + AppInfo.Version);
                return 0;
            }

            _port = IntArg(args, "--port", 8080);
            if (HasFlag(args, "--no-browser")) _openBrowser = false;

            if (HasFlag(args, "--selftest")) return SelfTest();

            _bridge = new Bridge();

            string error = StartServer();

            if (error != null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            string url = "http://127.0.0.1:" + _port + "/";

            Console.WriteLine();
            Console.WriteLine("  " + AppInfo.Name + " " + AppInfo.Version + " — " + AppInfo.OsDescription);
            Console.WriteLine("  интерфейс: " + url);
            if (!AppInfo.IsAdmin) Console.WriteLine("  ВНИМАНИЕ: без root обход включить нельзя (нужен nfqws)");
            Console.WriteLine("  Ctrl+C — выход");
            Console.WriteLine();

            if (_openBrowser) OpenBrowser(url);

            ManualResetEventSlim stop = new ManualResetEventSlim(false);

            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stop.Set();
            };

            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };

            stop.Wait();

            Shutdown();
            return 0;
        }

        private static string StartServer()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException)
                {
                    _port++;
                    _listener = null;

                    if (attempt == 19) return "не смог занять порт: попробуйте --port <номер>";
                }
                catch (Exception ex)
                {
                    return "не смог поднять сервер: " + ex.Message;
                }
            }

            if (_listener == null) return "не смог поднять сервер";

            Task.Run(() => AcceptLoop(_listener));

            return null;
        }

        private static async Task AcceptLoop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => Handle(context));
            }
        }

        private static async Task Handle(HttpListenerContext context)
        {
            try
            {
                string path = context.Request.Url.AbsolutePath;

                if (path == "/ws" && context.Request.IsWebSocketRequest)
                {
                    await HandleWebSocket(context);
                    return;
                }

                if (path == "/" || path == "/index.html")
                {
                    await WriteText(context, "text/html; charset=utf-8", BuildPage());
                    return;
                }

                if (path == "/health")
                {
                    await WriteText(context, "text/plain; charset=utf-8", "ok");
                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "запрос " + context.Request.Url + ": " + ex.Message);

                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        private static async Task WriteText(HttpListenerContext context, string type, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private static async Task HandleWebSocket(HttpListenerContext context)
        {
            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
            WebSocket socket = wsContext.WebSocket;

            Log.Info("APP", "мост подключён");
            _bridge.Attach(socket);

            byte[] buffer = new byte[64 * 1024];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        break;
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _bridge.Handle(text);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "сокет закрылся: " + ex.Message);
            }
            finally
            {
                _bridge.Detach();
                try { socket.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Собирает страницу: тот же ui.html, что на Windows, плюс шим моста.
        ///
        /// Шим вставляется ПЕРЕД app.js: тот захватывает window.chrome.webview
        /// в начале своего замыкания, и если определить мост позже — он не найдёт.
        /// </summary>
        private static string BuildPage()
        {
            string html = Resources.Text(Resources.UiHtml);

            // Политика безопасности в ui.html написана для WebView2: там мост —
            // это postMessage, сетевых запросов страница не делает вовсе, и
            // `default-src 'none'` — правильный минимум.
            //
            // Здесь мост работает по WebSocket, поэтому без connect-src браузер
            // блокирует соединение: «violates Content Security Policy directive:
            // default-src 'none'». Добавляем ровно один источник — свой же
            // localhost, — и ничего больше.
            html = html.Replace(
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:;",
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:; " +
                "connect-src ws://127.0.0.1:* ws://localhost:*;");

            html = html.Replace("/*{{THEME}}*/", Resources.Text(Resources.ThemeCss));
            html = html.Replace("//{{APP}}", WebSocketShim + "\n" + Resources.Text(Resources.AppJs));
            html = html.Replace("{{VERSION}}", AppInfo.Version);

            return html;
        }

        /// <summary>
        /// Мост: chrome.webview поверх WebSocket.
        ///
        /// Сообщения до открытия сокета копятся в очереди — app.js отправляет
        /// "ready" сразу при загрузке, и терять его нельзя.
        /// </summary>
        private const string WebSocketShim = @"
(function () {
    var queue = [];
    var listeners = [];
    var ready = false;

    var scheme = location.protocol === 'https:' ? 'wss://' : 'ws://';
    var ws = new WebSocket(scheme + location.host + '/ws');

    ws.onopen = function () {
        ready = true;
        while (queue.length > 0) ws.send(queue.shift());
    };

    ws.onmessage = function (event) {
        var data;
        try { data = JSON.parse(event.data); } catch (e) { return; }

        for (var i = 0; i < listeners.length; i++) {
            try { listeners[i]({ data: data }); } catch (e) { }
        }
    };

    ws.onclose = function () { ready = false; };

    // Для диагностики: снаружи видно, подключён ли мост и почему нет.
    // Без этого «список пуст» не отличить от «сокет не открылся».
    window.__azBridge = {
        socket: ws,
        state: function () {
            return ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED'][ws.readyState] || '?';
        },
        pending: function () { return queue.length; }
    };

    window.chrome = window.chrome || {};
    window.chrome.webview = {
        postMessage: function (message) {
            var text = JSON.stringify(message);
            if (ready) ws.send(text); else queue.push(text);
        },
        addEventListener: function (type, handler) {
            if (type === 'message' && typeof handler === 'function') listeners.push(handler);
        },
        removeEventListener: function (type, handler) {
            if (type !== 'message') return;
            var i = listeners.indexOf(handler);
            if (i >= 0) listeners.splice(i, 1);
        }
    };
})();
";

        private static void OpenBrowser(string url)
        {
            try
            {
                // Chromium умеет открывать страницу без адресной строки — это
                // ближе всего к отдельному окну без своей GUI-библиотеки.
                foreach (string browser in new[] { "chromium", "chromium-browser", "google-chrome", "microsoft-edge" })
                {
                    if (TryRun(browser, "--app=" + url)) return;
                }

                if (TryRun("xdg-open", url)) return;

                Log.Warn("APP", "не смог открыть браузер: откройте " + url + " вручную");
            }
            catch (Exception ex)
            {
                Log.Warn("APP", "браузер: " + ex.Message);
            }
        }

        private static bool TryRun(string file, string arg)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add(arg);

                Process p = Process.Start(psi);
                if (p == null) return false;

                // Если бинарника нет, Start бросит исключение; если он есть, но
                // не подходит — выйдет с ошибкой. Даём ему 300 мс и смотрим.
                if (p.WaitForExit(300))
                    return p.ExitCode == 0;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Shutdown()
        {
            try { if (_bridge != null) _bridge.Shutdown(); } catch { }

            try
            {
                if (_listener != null && _listener.IsListening) _listener.Stop();
            }
            catch { }

            Log.Info("APP", "выход");
        }

        // ------------------------------------------------------------------ аргументы

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static int IntArg(string[] args, string name, int fallback)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length) continue;

                int value;
                if (int.TryParse(args[i + 1], out value) && value > 0 && value < 65536) return value;
            }

            return fallback;
        }

        // ------------------------------------------------------------------ selftest

        /// <summary>
        /// Проверки, которые можно сделать без root и без сети.
        /// Главное здесь — что каталог стратегий читается и аргументы собираются.
        /// </summary>
        private static int SelfTest()
        {
            int failed = 0;
            int passed = 0;

            Action<string, bool, string> check = delegate(string name, bool ok, string detail)
            {
                if (ok) { passed++; Console.WriteLine("[ok]   " + name + (detail.Length > 0 ? " -> " + detail : "")); }
                else { failed++; Console.WriteLine("[FAIL] " + name + " -> " + detail); }
            };

            check("x64 процесс", Environment.Is64BitProcess, Environment.Is64BitProcess ? "ok" : "32-bit");
            check("версия", AppInfo.Version.Length > 0, AppInfo.Version);
            check("система", AppInfo.OsDescription.Length > 0, AppInfo.OsDescription);

            // hosts: на Linux это /etc/hosts, а не путь из Windows
            string hosts = AutoZapret.Lists.HostsFile.SystemPath;
            check("hosts — путь Linux", hosts == "/etc/hosts", hosts);

            // каталог стратегий
            StrategyStore store = new StrategyStore();
            store.Load(Path.Combine(Paths.StrategiesDir, "strategies.v1.json"));

            List<Strategy> all = new List<Strategy>(store.All);
            check("стратегии загружены", all.Count > 0, all.Count + " шт.");

            // аргументы для nfqws собираются и не содержат WinDivert-фильтров
            if (all.Count > 0)
            {
                List<string> args = NfqwsEngine.BuildArguments(all[0], "off");

                check("аргументы собраны", args.Count > 0, args.Count + " арг.");
                check("нет --wf-* (это WinDivert)",
                    !args.Exists(a => a.StartsWith("--wf-", StringComparison.Ordinal)),
                    "ok");

                check("плейсхолдеры раскрыты",
                    !args.Exists(a => a.Contains("${")),
                    args.Find(a => a.Contains("${")) ?? "ok");
            }

            check("nfqws найден", NfqwsEngine.Ready, NfqwsEngine.BinaryPath);

            // ---- firewall: команды строятся правильно
            //
            // Сами правила без root не поставить, но их состав проверить можно
            // и нужно: ошибка в строке правила означает, что обход не заработает
            // уже на живой системе, и искать её там дорого.
            string backend = Firewall.Backend;
            check("firewall найден", backend != null, backend ?? "ни nft, ни iptables");

            if (backend != null)
            {
                List<string[]> setup = Firewall.SetupCommands(backend, "80,443", "443");
                List<string[]> teardown = Firewall.TeardownCommands(backend);

                check("правила строятся", setup.Count > 0, setup.Count + " команд");
                check("снятие правил есть", teardown.Count > 0, teardown.Count + " команд");

                // Главное для безопасности: без bypass смерть nfqws = мёртвая сеть.
                bool hasBypass = setup.Exists(c => Array.IndexOf(c, "bypass") >= 0);
                check("в правилах есть bypass", hasBypass, hasBypass ? "ok" : "НЕТ — сеть умрёт при падении");

                // Главное для скорости: только первые пакеты соединения.
                bool hasLimit = setup.Exists(c =>
                    Array.IndexOf(c, "1-6") >= 0 || Array.IndexOf(c, "1:6") >= 0);
                check("обход только первых пакетов", hasLimit, hasLimit ? "ok" : "НЕТ — будет медленно");

                // Номер очереди должен совпадать с тем, что слушает nfqws.
                bool sameQueue = setup.Exists(c => Array.IndexOf(c, Firewall.QueueNumber.ToString()) >= 0);
                check("очередь совпадает с nfqws", sameQueue, Firewall.QueueNumber.ToString());
            }

            // ---- порты из аргументов стратегии
            string tcp, udp;
            Firewall.PortsFromArgs(
                new List<string> { "--filter-tcp=80,443", "--filter-udp=443", "--filter-tcp=12" },
                out tcp, out udp);

            check("порты: tcp собран", tcp == "443,80", tcp);
            check("порты: udp собран", udp == "443", udp);
            check("порт 12 (игровой фильтр выключен) отброшен",
                !tcp.Contains("12"), tcp);

            Console.WriteLine();
            Console.WriteLine("RESULT: " + (failed == 0 ? "ok" : failed + " failed") + ", " + passed + " ok");

            return failed == 0 ? 0 : 1;
        }
    }
}
