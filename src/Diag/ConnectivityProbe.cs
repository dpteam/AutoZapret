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

        public ProbeTarget(string name, string url, bool critical = false)
        {
            Name = name;
            Url = url;
            Critical = critical;
        }
    }

    public sealed class ProbeResult
    {
        public string Name = "";
        public bool Ok;
        public int Status;
        public int ElapsedMs;
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
                new ProbeTarget("telegram", "https://web.telegram.org")
            };
        }

        public ProbeReport CheckAll(List<ProbeTarget> targets, int timeoutSec = 10, int parallel = 4)
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
            ProbeResult result = new ProbeResult { Name = target.Name };
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "curl.exe",
                    // -4: только IPv4. Без этого на машинах с «мёртвым» IPv6
                    // все проверки падают, хотя браузер открывает сайт.
                    Arguments = "-4 -s -o NUL --max-time " + timeoutSec +
                                " -w \"%{http_code}|%{time_total}\" \"" + target.Url + "\"",
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
                    if (!result.Ok) result.Error = status == 0 ? "нет соединения" : "HTTP " + status;
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
