using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    public sealed class ProbeTarget
    {
        public string Name = "";
        public string Url = "";
        public bool Critical;   // без него результат считается провальным

        /// <summary>
        /// Каким движком лечится: "zapret" или "proxy".
        /// Telegram по HTTPS не пробивается ни одной desync-стратегией —
        /// ему нужен WS-прокси. Без этого разделения он портит рейтинг:
        /// все стратегии выглядят как «4/5», хотя пробивают всё, что могут.
        /// </summary>
        public string Engine = "zapret";

        public ProbeTarget(string name, string url, bool critical = false, string engine = "zapret")
        {
            Name = name;
            Url = url;
            Critical = critical;
            Engine = engine;
        }
    }

    public sealed class ProbeResult
    {
        public string Name = "";
        public bool Ok;
        public int Status;
        public int ElapsedMs;

        /// <summary>Короткая причина отказа: «таймаут», «DNS», «TLS», «сброс».</summary>
        public string Error = "";

        public string ToLine()
        {
            if (Ok) return Name + ": ok " + Status + " " + ElapsedMs + "ms";
            return Name + ": FAIL " + (Error.Length > 0 ? Error : "status " + Status);
        }
    }

    public sealed class ProbeReport
    {
        public List<ProbeResult> Results = new List<ProbeResult>();
        public int OkCount;
        public int Total;
        public int AvgMs;

        public double SuccessRate { get { return Total == 0 ? 0 : (double)OkCount / Total; } }

        public bool AnyCriticalFailed
        {
            get
            {
                foreach (ProbeResult r in Results)
                    if (!r.Ok && r.Name.Length > 0) return true;
                return false;
            }
        }

        public string Summary()
        {
            return OkCount + "/" + Total + " (" + (int)(SuccessRate * 100) + "%) " + AvgMs + "ms";
        }

        /// <summary>
        /// Одной строкой: почему провалились. «0/4» без причины ничего не объясняет,
        /// а «0/4 — таймаут ×4» сразу говорит, что дело не в стратегии, а в сети/DNS.
        /// </summary>
        public string FailureSummary()
        {
            if (OkCount >= Total) return "";

            Dictionary<string, int> byReason = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ProbeResult r in Results)
            {
                if (r.Ok) continue;

                string reason = string.IsNullOrEmpty(r.Error) ? "нет ответа" : r.Error;

                int n;
                if (!byReason.TryGetValue(reason, out n)) n = 0;
                byReason[reason] = n + 1;
            }

            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byReason)
                parts.Add(kv.Value > 1 ? kv.Key + " ×" + kv.Value : kv.Key);

            return string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>
    /// Проверка доступности целей.
    ///
    /// Приоритет — curl.exe: он есть в Windows 10 1803+, умеет -4 (только IPv4)
    /// и не страдает от .NET-предпочтения IPv6, из-за которого Invoke-WebRequest
    /// падает там, где браузер открывает сайт.
    ///
    /// Важно: успех = код 2xx-4xx. 404 на корне CDN — это нормально (сервер ответил),
    /// а вот таймаут/000 — это блокировка.
    /// </summary>
    public sealed class ConnectivityProbe
    {
        private static readonly HttpClient Http = CreateClient();

        /// <summary>Цели по умолчанию: то, что чаще всего блокируют.</summary>
        public static List<ProbeTarget> DefaultTargets()
        {
            return new List<ProbeTarget>
            {
                new ProbeTarget("youtube",  "https://www.youtube.com", true),
                new ProbeTarget("ytimg",    "https://i.ytimg.com"),
                new ProbeTarget("discord",  "https://discord.com", true),
                new ProbeTarget("discord-cdn", "https://cdn.discordapp.com"),
                new ProbeTarget("google",   "https://www.google.com"),
                new ProbeTarget("twitch",   "https://www.twitch.tv"),

                // Telegram по HTTPS не лечится desync: нужен WS-прокси (P6).
                // Держим отдельно, чтобы не портить рейтинг стратегий zapret.
                new ProbeTarget("telegram", "https://web.telegram.org", false, "proxy")
            };
        }

        /// <summary>Только цели, которые имеет смысл пробивать zapret'ом.</summary>
        public static List<ProbeTarget> ZapretTargets()
        {
            List<ProbeTarget> result = new List<ProbeTarget>();
            foreach (ProbeTarget t in DefaultTargets())
                if (t.Engine == "zapret") result.Add(t);
            return result;
        }

        public ProbeReport CheckAll(List<ProbeTarget> targets, int timeoutSec = 10, int parallel = 4,
                                    CancellationToken cancel = default(CancellationToken))
        {
            ProbeReport report = new ProbeReport();
            report.Total = targets.Count;

            object gate = new object();
            int done = 0;
            List<Task> tasks = new List<Task>();

            using (SemaphoreSlim limiter = new SemaphoreSlim(parallel))
            {
                foreach (ProbeTarget target in targets)
                {
                    ProbeTarget t = target;
                    tasks.Add(Task.Run(() =>
                    {
                        limiter.Wait();
                        try
                        {
                            // отмена до старта пробы: не тратим 10 секунд впустую
                            if (cancel.IsCancellationRequested) return;

                            ProbeResult r = CheckOne(t, timeoutSec);
                            lock (gate)
                            {
                                report.Results.Add(r);
                                if (r.Ok) report.OkCount++;
                                done++;
                            }
                        }
                        finally { limiter.Release(); }
                    }));
                }
                Task.WaitAll(tasks.ToArray());
            }

            int sum = 0, n = 0;
            foreach (ProbeResult r in report.Results)
                if (r.Ok) { sum += r.ElapsedMs; n++; }
            report.AvgMs = n == 0 ? 0 : sum / n;

            return report;
        }

        public ProbeResult CheckOne(ProbeTarget target, int timeoutSec = 10)
        {
            ProbeResult result = new ProbeResult { Name = target.Name };

            if (HasCurl()) return CheckWithCurl(target, timeoutSec);
            return CheckWithHttp(target, timeoutSec);
        }

        // ------------------------------------------------------------------ curl

        private static bool HasCurl()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "curl.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static ProbeResult CheckWithCurl(ProbeTarget target, int timeoutSec)
        {
            string host = SafeHost(target.Url);

            List<IPAddress> addresses = new List<IPAddress>();

            if (host.Length > 0)
            {
                try { addresses = Resolver.Resolve(host).Addresses; }
                catch { }
            }

            // Без адресов (нет DoH и нет системного DNS) — пусть curl решает сам
            if (addresses.Count == 0) return Name(RunCurl(target.Url, host, null, timeoutSec), target.Name);

            ProbeResult first = null;

            // Google/Discord отдают по 8 адресов, и часть может быть зарезана по IP.
            // Проверяем до двух: «TLS» значит, что пакеты дошли и DPI ответил —
            // второй адрес ничего не изменит, а два таймаута подряд стоят времени.
            int tried = 0;

            foreach (IPAddress ip in addresses)
            {
                if (tried++ >= 2) break;

                ProbeResult r = Name(RunCurl(target.Url, host, ip, timeoutSec), target.Name);

                if (r.Ok) return r;
                if (first == null) first = r;

                bool dpi = r.Error == "TLS" || r.Error == "сброс" ||
                           r.Error == "сертификат" || r.Error == "пустой ответ";

                if (dpi) return r;
            }

            return first;
        }

        /// <summary>
        /// Имя цели обязательно: по нему базовая линия и подбор понимают, что
        /// именно заблокировано. Без него в baseline.json попадали пустые строки
        /// и «заблокировано: , , , , ,» — а подбор терял список целей.
        /// </summary>
        private static ProbeResult Name(ProbeResult result, string name)
        {
            if (result != null) result.Name = name;
            return result;
        }

        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return ""; }
        }

        /// <summary>Один прогон curl: адрес прибит через --resolve, если он известен.</summary>
        private static ProbeResult RunCurl(string url, string host, IPAddress ip, int timeoutSec)
        {
            ProbeResult result = new ProbeResult { Name = "" };
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "curl.exe",
                    // -4: только IPv4. Без этого на машинах с «мёртвым» IPv6
                    // все проверки падают, хотя браузер открывает сайт.
                    // errormsg + код возврата: «none соединения» не говорит ничего,
                    // а «DNS» / «таймаут» / «TLS» сразу ведут к причине.
                    // --resolve + --noproxy: адрес берём из DoH, а не из системного
                    // DNS (его могут подменять), и не идём в случайный прокси из
                    // переменных окружения. Проверяем DPI, а не чужую заглушку.
                    Arguments = CurlArguments(url, host, ip, timeoutSec),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutSec * 1000 + 2000);

                    sw.Stop();
                    result.ElapsedMs = (int)sw.ElapsedMilliseconds;

                    string[] parts = stdout.Trim().Split('|');
                    int status = 0;
                    if (parts.Length > 0) int.TryParse(parts[0], out status);
                    result.Status = status;

                    // 200-499 = сервер ответил. 000 = не достучались.
                    result.Ok = status >= 200 && status < 500;

                    if (!result.Ok)
                    {
                        string detail = parts.Length > 2 ? parts[2].Trim() : "";

                        result.Error = status == 0
                            ? CurlReason(SafeExitCode(p), detail)
                            : "HTTP " + status;
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                result.Ok = false;
                result.Error = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Аргументы curl. Адрес берём из нашего резолвера (DoH), а не из системного
        /// DNS: если провайдер подменяет DNS, curl придёт на заглушку и «0/4»
        /// будет означать подмену, а не сломанный обход. `--resolve` подставляет
        /// адрес, оставляя правильный SNI и Host.
        /// </summary>
        private static string CurlArguments(string url, string host, IPAddress ip, int timeoutSec)
        {
            string common = "-4 -s -o NUL --noproxy \"*\" --max-time " + timeoutSec +
                            " -w \"%{http_code}|%{time_total}|%{errormsg}\"";

            if (!string.IsNullOrEmpty(host) && ip != null)
                return common + " --resolve \"" + host + ":443:" + ip + "\" \"" + url + "\"";

            return common + " \"" + url + "\"";
        }

        /// <summary>
        /// Код возврата curl после WaitForExit. В net48 нет свойства ExitCode
        /// до завершения процесса, поэтому берём безопасно.
        /// </summary>
        private static int SafeExitCode(Process p)
        {
            try { return p.HasExited ? p.ExitCode : -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Причина отказа по коду возврата curl. Разница принципиальна:
        /// «DNS» значит «стратегия тут вообще ни при чём», а «TLS» — что пакеты
        /// дошли и обход не сработал. Раньше оба случая выглядели как
        /// «нет соединения», и разобраться было невозможно.
        /// </summary>
        private static string CurlReason(int exitCode, string detail)
        {
            switch (exitCode)
            {
                case 6: return "DNS";
                case 7: return "нет подключения";
                case 28: return "таймаут";
                case 35: return "TLS";
                case 52: return "пустой ответ";
                case 55: return "отправка";
                case 56: return "сброс";
                case 60: return "сертификат";
                case 92: return "HTTP/2";
            }

            if (!string.IsNullOrEmpty(detail)) return detail;
            return exitCode < 0 ? "нет ответа" : "curl " + exitCode;
        }

        // ----------------------------------------------------------------- http

        private static ProbeResult CheckWithHttp(ProbeTarget target, int timeoutSec)
        {
            ProbeResult result = new ProbeResult { Name = target.Name };
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
                {
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, target.Url))
                    using (HttpResponseMessage response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
                    {
                        sw.Stop();
                        result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                        result.Status = (int)response.StatusCode;
                        result.Ok = result.Status >= 200 && result.Status < 500;
                        if (!result.Ok) result.Error = "HTTP " + result.Status;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                result.Ok = false;
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                result.Error = inner.GetType().Name;
            }

            return result;
        }

        private static HttpClient CreateClient()
        {
            try
            {
                // Явно IPv4-предпочтение недоступно через HttpClientHandler в net48,
                // поэтому просто короткий таймаут и отказ от редиректов на чужие хосты.
                HttpClientHandler handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseProxy = false
                };
                HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                return client;
            }
            catch
            {
                return new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            }
        }
    }
}
