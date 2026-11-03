using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>
    /// Проверка, отвечает ли IP по TCP.
    ///
    /// ЧТО ЭТО ДАЁТ. Когда два источника hosts указывают для одного домена разные
    /// IP, надо выбрать один. Приоритет источника — надёжный, но грубый способ:
    /// источник мог устареть. Проверка TCP показывает, какой адрес вообще жив
    /// прямо сейчас, с этой машины.
    ///
    /// ЧЕГО ЭТО НЕ ДАЁТ (важно, чтобы не обманывать себя):
    ///   * TCP-ответ не значит, что домен отдаст правильный контент — сервер
    ///     различает виртуальные хосты по SNI/заголовку Host;
    ///   * DPI может резать соединение не на SYN, а позже (16-20 КБ) —
    ///     тогда «отвечает» ничего не значит;
    ///   * сервис может быть временно недоступен, а IP — правильным.
    ///
    /// Поэтому проверка только ПОМОГАЕТ выбрать, но никогда не выкидывает
    /// единственный вариант: если не ответил никто, остаётся выбор по приоритету.
    /// </summary>
    public static class HostsVerifier
    {
        public sealed class Result
        {
            public string Ip = "";
            public int Port;
            public bool Ok;
            public int Ms;
            public string Error = "";

            public string ToLine()
            {
                if (Ok) return Ip + ":" + Port + " — " + Ms + " мс";
                return Ip + ":" + Port + " — " + (Error.Length > 0 ? Error : "нет ответа");
            }
        }

        /// <summary>
        /// Проверяет набор IP. Сначала 443, если не ответил — 80.
        /// Возвращает словарь ip -> лучший результат.
        /// </summary>
        public static Dictionary<string, Result> Check(IEnumerable<string> ips, int timeoutMs = 1500, int parallel = 8)
        {
            Dictionary<string, Result> results = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);

            List<string> list = new List<string>();
            foreach (string ip in ips)
            {
                if (string.IsNullOrEmpty(ip)) continue;
                if (list.Contains(ip)) continue;
                if (!IsTestable(ip)) continue;
                list.Add(ip);
            }

            if (list.Count == 0) return results;

            object gate = new object();
            int remaining = list.Count;

            using (SemaphoreSlim limiter = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                foreach (string ip in list)
                {
                    string captured = ip;

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        limiter.Wait();
                        try
                        {
                            Result best = ProbeOne(captured, 443, timeoutMs);
                            if (!best.Ok)
                            {
                                Result alt = ProbeOne(captured, 80, timeoutMs);
                                if (alt.Ok || best.Error.Length == 0) best = alt;
                            }

                            lock (gate) results[captured] = best;
                        }
                        finally
                        {
                            limiter.Release();
                            Interlocked.Decrement(ref remaining);
                        }
                    });
                }

                // ждём не дольше разумного: таймаут + запас
                DateTime until = DateTime.UtcNow.AddMilliseconds((long)timeoutMs * 3 + 2000);
                while (Volatile.Read(ref remaining) > 0 && DateTime.UtcNow < until)
                    Thread.Sleep(50);
            }

            return results;
        }

        /// <summary>Один IP: TCP-соединение на порт.</summary>
        public static Result ProbeOne(string ip, int port, int timeoutMs)
        {
            Result result = new Result { Ip = ip, Port = port };
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                IPAddress address;
                if (!IPAddress.TryParse(ip, out address))
                {
                    result.Error = "неверный IP";
                    return result;
                }

                using (TcpClient client = new TcpClient(address.AddressFamily))
                {
                    IAsyncResult ar = client.BeginConnect(address, port, null, null);

                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        sw.Stop();
                        result.Ms = (int)sw.ElapsedMilliseconds;
                        result.Error = "таймаут";
                        return result;
                    }

                    try
                    {
                        client.EndConnect(ar);
                        sw.Stop();
                        result.Ok = true;
                        result.Ms = (int)sw.ElapsedMilliseconds;
                        return result;
                    }
                    catch (SocketException ex)
                    {
                        sw.Stop();
                        result.Ms = (int)sw.ElapsedMilliseconds;
                        result.Error = ShortError(ex.SocketErrorCode);
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.Ms = (int)sw.ElapsedMilliseconds;
                result.Error = ex.GetType().Name;
                return result;
            }
        }

        /// <summary>
        /// Выбирает лучший IP из кандидатов.
        ///
        /// Правило: среди ответивших — самый быстрый; если не ответил никто —
        /// возвращаем fallback (выбор по приоритету источника) и НЕ выбрасываем.
        /// </summary>
        public static string PickBest(List<string> candidates, Dictionary<string, Result> results, string fallback)
        {
            if (candidates == null || candidates.Count == 0) return fallback;
            if (results == null || results.Count == 0) return fallback;

            string best = null;
            int bestMs = int.MaxValue;

            foreach (string ip in candidates)
            {
                Result r;
                if (!results.TryGetValue(ip, out r)) continue;
                if (!r.Ok) continue;

                if (r.Ms < bestMs)
                {
                    bestMs = r.Ms;
                    best = ip;
                }
            }

            return best ?? fallback;
        }

        /// <summary>Не проверяем заведомо бессмысленные адреса.</summary>
        private static bool IsTestable(string ip)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address)) return false;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                if (b[0] == 0) return false;
                if (b[0] == 127) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return false;
            }
            else
            {
                if (IPAddress.IsLoopback(address)) return false;
            }

            return true;
        }

        private static string ShortError(SocketError error)
        {
            switch (error)
            {
                case SocketError.ConnectionRefused: return "отказ (порт закрыт)";
                case SocketError.TimedOut: return "таймаут";
                case SocketError.HostUnreachable: return "хост недоступен";
                case SocketError.NetworkUnreachable: return "сеть недоступна";
                case SocketError.ConnectionReset: return "сброс";
                default: return error.ToString();
            }
        }
    }
}