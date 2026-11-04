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
            if (!DnsOk) return "DNS не отвечает";

            // fake-IP: подключение к 198.18.x «успешно» за 1 мс, потому что это
            // локальный туннель VPN, а не сайт. Без этой ветки вывод врал:
            // «TCP есть, TLS рвётся — лечится desync», хотя desync тут бессилен.
            if (FakeIp.Length > 0)
                return "DNS отдаёт fake-IP " + FakeIp + " — трафик в туннеле VPN, обход его не видит";

            if (!TcpOk) return "TCP не соединяется — похоже на блокировку по IP, desync тут не поможет";
            if (!TlsOk) return "TCP есть, TLS рвётся — DPI, лечится desync";
            return "открывается";
        }

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
            // ---- 1. DNS
            List<IPAddress> v4 = new List<IPAddress>();
            List<IPAddress> v6 = new List<IPAddress>();

            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(stage.Host))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(ip);
                }

                stage.DnsOk = v4.Count > 0 || v6.Count > 0;

                List<string> text = new List<string>();
                foreach (IPAddress ip in v4) text.Add(ip.ToString());
                foreach (IPAddress ip in v6) text.Add(ip.ToString() + " (v6)");

                stage.Dns = stage.DnsOk ? string.Join(", ", text.ToArray()) : "пусто";
                stage.FakeIp = DnsGuard.FakeAddress(v4);
            }
            catch (Exception ex)
            {
                stage.Dns = ex.GetType().Name + ": " + ex.Message;
                return;
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

            if (v4.Count == 0 && v6.Count > 0)
            {
                // IPv4 нет вовсе — говорим честно, а не «таймаут»
                stage.Tcp = "нет IPv4-адреса";
                return;
            }

            if (!stage.TcpOk)
            {
                stage.Tcp = tcpError.Length > 0 ? tcpError : "таймаут";
                return;
            }

            // ---- 3. TLS с настоящим SNI
            string tlsError;
            stage.TlsOk = TryTls(stage.Host, timeoutMs, out tlsError);
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

        private static bool TryTls(string host, int timeoutMs, out string error)
        {
            error = "";

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect(host, 443, null, null);
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