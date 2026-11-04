using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    /// <summary>Пошаговая диагностика одной цели: DNS -> TCP -> TLS.</summary>
    public sealed class TargetStage
    {
        public string Name = "";
        public string Host = "";

        /// <summary>«1.2.3.4, 5.6.7.8» или причина отказа.</summary>
        public string Dns = "";

        /// <summary>«ok 120 мс» или причина отказа.</summary>
        public string Tcp = "";

        /// <summary>«ok» или текст ошибки TLS.</summary>
        public string Tls = "";

        public bool DnsOk;
        public bool TcpOk;
        public bool TlsOk;

        /// <summary>Адрес из fake-IP диапазона, если DNS отдал именно его.</summary>
        public string FakeIp = "";

        /// <summary>Где именно всё встало — одной строкой.</summary>
        public string Verdict()
        {
            if (!DnsOk) return "DNS не отвечает ни через DoH, ни через систему";

            // fake-IP: подключение к 198.18.x «успешно» за 1 мс, потому что это
            // локальная заглушка, а не сайт. Без этой ветки вывод врал:
            // «TCP есть, TLS рвётся — лечится desync», хотя desync тут бессилен.
            if (FakeIp.Length > 0)
                return "DNS отдаёт заглушку " + FakeIp + " — трафик не доходит до DPI, обходу нечего исправлять";

            if (!TcpOk) return "TCP не соединяется — похоже на блокировку по IP, desync тут не поможет";
            if (!TlsOk) return "TCP есть, TLS рвётся — DPI, лечится desync";
            return "открывается";
        }

        /// <summary>Подмена системного DNS, если она есть: показываем отдельно от вердикта.</summary>
        public string SubstitutionNote = "";

        public string ToLine()
        {
            return Name + ": DNS " + Dns + " | TCP " + Tcp + " | TLS " + Tls + " | " + Verdict();
        }
    }

    /// <summary>
    /// Пошаговая проверка цели без curl: где именно рвётся — в DNS, в TCP или в TLS.
    ///
    /// Зачем понадобилось. Проба через curl говорит «0/4» и всё; а отладочный вывод
    /// winws показал, что до него вообще не доходят пакеты к youtube — значит curl
    /// умирает раньше, чем начинается TLS. Разделить «нет DNS», «нет TCP» и «DPI
    /// рвёт рукопожатие» можно только по шагам, и это разные диагнозы с разным
    /// лечением: DNS — DoH, TCP — прокси (desync бессилен), TLS — desync.
    /// </summary>
    public static class TargetDiagnosis
    {
        public static List<TargetStage> Run(IEnumerable<ProbeTarget> targets, int timeoutMs,
                                            CancellationToken cancel = default(CancellationToken))
        {
            List<TargetStage> result = new List<TargetStage>();

            foreach (ProbeTarget t in targets)
            {
                if (cancel.IsCancellationRequested) break;

                TargetStage stage = new TargetStage { Name = t.Name, Host = SafeHost(t.Url) };
                result.Add(stage);

                Stage(stage, timeoutMs, cancel);

                Log.Info("DIAG", "цель " + stage.ToLine());
            }

            return result;
        }

        private static void Stage(TargetStage stage, int timeoutMs, CancellationToken cancel)
        {
            // ---- 1. DNS: сначала DoH по IP, системный — только как заплатка и
            //         как улика подмены. Провайдер перехватывает порт 53 целиком,
            //         поэтому «свой DNS 8.8.8.8» ничего не меняет.
            Resolver.Result resolved = Resolver.Resolve(stage.Host);

            List<IPAddress> v4 = new List<IPAddress>();

            foreach (IPAddress ip in resolved.Addresses)
                if (ip.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);

            stage.DnsOk = v4.Count > 0;
            stage.Dns = stage.DnsOk
                ? Resolver.Join(v4) + (resolved.Source.Length > 0 ? " (" + resolved.Source + ")" : "")
                : "пусто";

            // Заглушка, только если мы действительно будем к ней подключаться
            stage.FakeIp = DnsGuard.FakeAddress(v4);

            if (resolved.SystemLooksHijacked)
            {
                stage.SubstitutionNote = "системный DNS отдаёт " + resolved.SystemAnswer +
                                         ", DoH — " + Resolver.Join(v4) + ": DNS подменяется провайдером";

                Log.Warn("DIAG", "DNS подменяется: " + stage.Host + " — системный " +
                                 resolved.SystemAnswer + ", DoH " + Resolver.Join(v4));
            }

            if (!stage.DnsOk) return;

            // ---- 2. TCP: пробуем IPv4-адреса по очереди, как это делает браузер
            string tcpError = "";
            int connectMs = 0;

            foreach (IPAddress ip in v4)
            {
                if (cancel.IsCancellationRequested) return;

                string error;
                if (TryConnect(ip, 443, timeoutMs, out connectMs, out error))
                {
                    stage.TcpOk = true;
                    stage.Tcp = "ok " + connectMs + " мс (" + ip + ")";
                    break;
                }

                if (tcpError.Length == 0) tcpError = error + " (" + ip + ")";
            }

            if (!stage.TcpOk)
            {
                stage.Tcp = tcpError.Length > 0 ? tcpError : "таймаут";
                return;
            }

            // ---- 3. TLS с настоящим SNI, но по уже проверенному IP
            string tlsError;
            stage.TlsOk = TryTls(stage.Host, v4[0], timeoutMs, out tlsError);
            stage.Tls = stage.TlsOk ? "ok" : tlsError;
        }

        private static bool TryConnect(IPAddress ip, int port, int timeoutMs, out int elapsedMs, out string error)
        {
            elapsedMs = 0;
            error = "";

            Stopwatch watch = Stopwatch.StartNew();

            try
            {
                using (Socket socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    IAsyncResult ar = socket.BeginConnect(new IPEndPoint(ip, port), null, null);
                    bool done = ar.AsyncWaitHandle.WaitOne(timeoutMs);

                    elapsedMs = (int)watch.ElapsedMilliseconds;

                    if (!done)
                    {
                        error = "таймаут";
                        return false;
                    }

                    socket.EndConnect(ar);
                    return true;
                }
            }
            catch (SocketException ex)
            {
                elapsedMs = (int)watch.ElapsedMilliseconds;
                error = ex.SocketErrorCode.ToString();
                return false;
            }
            catch (Exception ex)
            {
                elapsedMs = (int)watch.ElapsedMilliseconds;
                error = ex.GetType().Name;
                return false;
            }
        }

        private static bool TryTls(string host, IPAddress ip, int timeoutMs, out string error)
        {
            error = "";

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    // подключаемся к адресу из DoH, а имя оставляем настоящим:
                    // так проверяется DPI, а не подменённый DNS
                    IAsyncResult ar = client.BeginConnect(ip, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        error = "таймаут TCP";
                        return false;
                    }

                    client.EndConnect(ar);

                    using (SslStream ssl = new SslStream(client.GetStream(), false))
                    {
                        ssl.ReadTimeout = timeoutMs;
                        ssl.WriteTimeout = timeoutMs;

                        ssl.AuthenticateAsClient(host);

                        // Нас интересует сам факт рукопожатия, а не содержимое сайта
                        return ssl.IsAuthenticated;
                    }
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;

                error = inner.GetType().Name + ": " + Shorten(inner.Message);
                return false;
            }
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }
    }
}