using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    public sealed class Tcp16Target
    {
        public string Id = "";
        public string Provider = "";
        public string Ip = "";
        public int Port = 443;
    }

    public sealed class Tcp16Result
    {
        public string Provider = "";
        public string Ip = "";
        public int Port;

        /// <summary>Сколько байт удалось протолкнуть до обрыва.</summary>
        public int BytesSent;

        /// <summary>true = соединение пережило целевой объём.</summary>
        public bool Survived;

        /// <summary>true = похоже на блокировку по объёму (обрыв в окне 12-36 КБ).</summary>
        public bool LooksBlocked;

        public int ElapsedMs;
        public string Detail = "";

        public string ToLine()
        {
            if (Survived) return Provider + ": пережило " + BytesSent + " Б";
            if (LooksBlocked) return Provider + ": ОБРЫВ на " + BytesSent + " Б";
            return Provider + ": " + Detail;
        }
    }

    /// <summary>
    /// Тест «обрыв на 16-20 КБ».
    ///
    /// Суть блокировки: TCP-соединение устанавливается нормально, TLS-рукопожатие
    /// проходит, но после передачи ~16-20 КБ провайдер рвёт поток. Сайт «открывается»
    /// и висит. Ни одна проверка по коду ответа это не ловит, а desync-стратегии
    /// с seqovl как раз лечат именно это.
    ///
    /// Как тестируем:
    ///   1. TCP до IP (не домена!) — чтобы исключить блокировку по SNI;
    ///   2. по одному keep-alive соединению шлём запросы с растущим телом
    ///      (заголовок X-Pad), проталкивая данные через поток;
    ///   3. смотрим, на каком объёме соединение умерло.
    ///
    /// ВАЖНО: тест идёт на «голом» HTTP. TLS здесь не нужен и мешает: нам важен
    /// объём, а не содержимое. Поэтому по 443 сначала пробуем TLS-запись, а если
    /// сервер её не принимает — падаем на HTTP.
    /// </summary>
    public static class Tcp16Test
    {
        /// <summary>Меньше этого объёма обрыв не считаем блокировкой по объёму.</summary>
        public const int MinBlockKb = 12;

        /// <summary>Больше этого — уже не «16-20 КБ блокировка».</summary>
        public const int MaxBlockKb = 36;

        /// <summary>Сколько всего проталкиваем.</summary>
        public const int TargetBytes = 40 * 1024;

        private const int ChunkSize = 4000;

        private static readonly Random Rng = new Random();

        /// <summary>Загружает список целей из tcp16.json (формат dpi-detector, MIT).</summary>
        public static List<Tcp16Target> LoadTargets(string path)
        {
            List<Tcp16Target> targets = new List<Tcp16Target>();

            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return targets;

            // файл — массив объектов; свой Json умеет массивы, разбираем вручную
            object parsed = Json.ParseSafe(text);
            List<object> items = parsed as List<object>;
            if (items == null) return targets;

            foreach (object item in items)
            {
                Dictionary<string, object> m = item as Dictionary<string, object>;
                if (m == null) continue;

                Tcp16Target target = new Tcp16Target
                {
                    Id = Json.GetString(m, "id", ""),
                    Provider = Json.GetString(m, "provider", ""),
                    Ip = Json.GetString(m, "ip", ""),
                    Port = Json.GetInt(m, "port", 443)
                };

                if (target.Ip.Length == 0) continue;
                if (target.Provider.Length == 0) target.Provider = target.Ip;

                targets.Add(target);
            }

            return targets;
        }

        /// <summary>Встроенный список на случай, если файла нет.</summary>
        public static List<Tcp16Target> BuiltinTargets()
        {
            return new List<Tcp16Target>
            {
                new Tcp16Target { Id = "HE-01", Provider = "Hetzner", Ip = "91.98.156.82", Port = 443 },
                new Tcp16Target { Id = "CF-01", Provider = "Cloudflare", Ip = "172.67.70.222", Port = 443 },
                new Tcp16Target { Id = "CDN-02", Provider = "CDN77", Ip = "195.181.167.155", Port = 443 },
                new Tcp16Target { Id = "AK-01", Provider = "Akamai", Ip = "23.222.76.4", Port = 443 },
                new Tcp16Target { Id = "AWS-02", Provider = "AWS", Ip = "3.165.188.250", Port = 443 },
                new Tcp16Target { Id = "DO-01", Provider = "DigitalOcean", Ip = "104.131.0.1", Port = 443 }
            };
        }

        /// <summary>
        /// Проверяет одну цель. Возвращает результат (никогда не null).
        /// </summary>
        public static Tcp16Result Probe(Tcp16Target target, int timeoutMs)
        {
            Tcp16Result result = new Tcp16Result
            {
                Provider = target.Provider,
                Ip = target.Ip,
                Port = target.Port
            };

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                IPAddress address;
                if (!IPAddress.TryParse(target.Ip, out address))
                {
                    result.Detail = "неверный IP";
                    return result;
                }

                using (TcpClient client = new TcpClient(address.AddressFamily))
                {
                    IAsyncResult ar = client.BeginConnect(address, target.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        result.Detail = "TCP таймаут";
                        return result;
                    }

                    try { client.EndConnect(ar); }
                    catch (SocketException ex)
                    {
                        result.Detail = "TCP: " + ex.SocketErrorCode;
                        return result;
                    }

                    client.NoDelay = true;
                    client.SendTimeout = timeoutMs;
                    client.ReceiveTimeout = timeoutMs;

                    using (NetworkStream stream = client.GetStream())
                    {
                        // один запрос, затем доливаем тело по частям
                        string host = target.Ip;
                        string request =
                            "POST / HTTP/1.1\r\n" +
                            "Host: " + host + "\r\n" +
                            "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                            "(KHTML, like Gecko) Chrome/120.0 Safari/537.36\r\n" +
                            "Content-Type: application/octet-stream\r\n" +
                            "Content-Length: " + TargetBytes.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                            "Connection: keep-alive\r\n\r\n";

                        byte[] header = Encoding.ASCII.GetBytes(request);
                        stream.Write(header, 0, header.Length);
                        stream.Flush();
                        result.BytesSent = header.Length;

                        byte[] chunk = new byte[ChunkSize];

                        while (result.BytesSent < TargetBytes)
                        {
                            int take = Math.Min(chunk.Length, TargetBytes - result.BytesSent);

                            // случайные данные: сжимаемый нулевой поток DPI может пропустить
                            for (int i = 0; i < take; i++) chunk[i] = (byte)Rng.Next(256);

                            try
                            {
                                stream.Write(chunk, 0, take);
                                stream.Flush();
                                result.BytesSent += take;
                            }
                            catch (IOException ex)
                            {
                                // обрыв — вот это и есть то, что мы ищем
                                SocketException inner = ex.InnerException as SocketException;
                                result.Detail = inner != null
                                    ? "обрыв: " + inner.SocketErrorCode
                                    : "обрыв записи";
                                break;
                            }
                            catch (SocketException ex)
                            {
                                result.Detail = "обрыв: " + ex.SocketErrorCode;
                                break;
                            }

                            // сервер мог ответить и закрыть — проверяем, жив ли сокет
                            if (client.Client.Poll(0, SelectMode.SelectRead))
                            {
                                if (client.Client.Available == 0)
                                {
                                    result.Detail = "сервер закрыл соединение";
                                    break;
                                }

                                // есть данные — вычитываем, чтобы не блокироваться.
                                // Считаем прочитанное: 0 означает, что соединение
                                // закрыто, и дальше писать бессмысленно.
                                try
                                {
                                    byte[] sink = new byte[Math.Min(client.Client.Available, 8192)];
                                    int drained = stream.Read(sink, 0, sink.Length);

                                    if (drained <= 0)
                                    {
                                        result.Detail = "сервер закрыл соединение";
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        sw.Stop();
                        result.ElapsedMs = (int)sw.ElapsedMilliseconds;

                        result.Survived = result.BytesSent >= TargetBytes;
                        result.LooksBlocked = !result.Survived &&
                                              result.BytesSent >= MinBlockKb * 1024 &&
                                              result.BytesSent <= MaxBlockKb * 1024;

                        if (result.Survived) result.Detail = "ok";
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = (int)sw.ElapsedMilliseconds;
                result.Detail = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        /// <summary>Проверяет несколько целей параллельно.</summary>
        public static List<Tcp16Result> ProbeAll(List<Tcp16Target> targets, int timeoutMs, int parallel)
        {
            List<Tcp16Result> results = new List<Tcp16Result>();
            if (targets == null || targets.Count == 0) return results;

            object gate = new object();
            int remaining = targets.Count;

            using (SemaphoreSlim limiter = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                foreach (Tcp16Target target in targets)
                {
                    Tcp16Target captured = target;

                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        limiter.Wait();
                        try
                        {
                            Tcp16Result r = Probe(captured, timeoutMs);
                            lock (gate) results.Add(r);
                        }
                        finally
                        {
                            limiter.Release();
                            System.Threading.Interlocked.Decrement(ref remaining);
                        }
                    });
                }

                DateTime until = DateTime.UtcNow.AddMilliseconds((long)timeoutMs * 6 + 5000);
                while (System.Threading.Volatile.Read(ref remaining) > 0 && DateTime.UtcNow < until)
                    System.Threading.Thread.Sleep(100);
            }

            results.Sort((a, b) => string.Compare(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        /// <summary>Короткий вердикт по всем результатам.</summary>
        public static string Verdict(List<Tcp16Result> results)
        {
            if (results.Count == 0) return "нет данных";

            int blocked = 0, survived = 0, other = 0;
            List<int> breakPoints = new List<int>();

            foreach (Tcp16Result r in results)
            {
                if (r.LooksBlocked) { blocked++; breakPoints.Add(r.BytesSent); }
                else if (r.Survived) survived++;
                else other++;
            }

            if (blocked == 0)
                return survived > 0
                    ? "обрыва по объёму не обнаружено (" + survived + " из " + results.Count + " прошли)"
                    : "не удалось проверить (нет соединений)";

            breakPoints.Sort();
            int median = breakPoints[breakPoints.Count / 2];

            return "ОБНАРУЖЕН обрыв по объёму: " + blocked + " из " + results.Count +
                   ", типично на " + (median / 1024) + " КБ. " +
                   "Лечится стратегиями с --dpi-desync-split-seqovl (ALT2, ALT7, general)";
        }
    }
}