using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AutoZapret.Android.Engine
{
    public sealed class BlockCheckTarget
    {
        public string Provider = "";
        public string Ip = "";
        public int Port = 443;
    }

    public sealed class BlockCheckResult
    {
        public string Provider = "";
        public string Ip = "";

        /// <summary>Сколько байт удалось протолкнуть до обрыва.</summary>
        public int BytesSent;

        /// <summary>true = соединение пережило целевой объём.</summary>
        public bool Survived;

        /// <summary>true = похоже на блокировку по объёму (обрыв в окне 12–36 КБ).</summary>
        public bool LooksBlocked;

        public int ElapsedMs;
        public string Detail = "";

        public string Short()
        {
            if (Survived) return "ок";
            if (LooksBlocked) return "обрыв " + (BytesSent / 1024) + " КБ";
            return Detail.Length > 0 ? Detail : "нет данных";
        }
    }

    /// <summary>
    /// Тест «обрыв на 16–20 КБ» — тот же, что на десктопе
    /// (`src/Diag/Tcp16Test.cs`) и на сайте hyperion-cs.github.io/dpi-checkers.
    ///
    /// Зачем это нужно отдельной кнопкой в приложении. Блокировка по объёму
    /// выглядит как «сайт открылся и висит»: TCP поднялся, TLS прошёл, а на
    /// 16–20 КБ провайдер рвёт поток. Ни проверка по коду ответа, ни «страница
    /// загрузилась» этого не показывают. Плюс это единственный тест, который
    /// говорит, ЧЕМ лечить: обрыв по объёму лечится seqovl, а не разрезом.
    ///
    /// Почему по IP, а не по домену: так мы отделяем блокировку по объёму от
    /// блокировки по SNI. Если бы запрос шёл на домен, обрыв мог бы случиться
    /// из-за имени, и тест врал бы.
    /// </summary>
    public static class BlockCheck
    {
        /// <summary>Меньше этого объёма обрыв не считаем блокировкой по объёму.</summary>
        public const int MinBlockKb = 12;

        /// <summary>Больше этого — уже не «16–20 КБ блокировка».</summary>
        public const int MaxBlockKb = 36;

        /// <summary>Сколько всего проталкиваем.</summary>
        public const int TargetBytes = 40 * 1024;

        private const int ChunkSize = 4000;

        private static readonly Random Rng = new Random();

        /// <summary>
        /// Цели встроены в приложение, а не скачиваются: проверка должна
        /// работать на первом запуске, без сети до нашего сервера и без
        /// «списка, который кто-то обновит».
        /// </summary>
        public static List<BlockCheckTarget> Targets()
        {
            return new List<BlockCheckTarget>
            {
                new BlockCheckTarget { Provider = "Hetzner", Ip = "91.98.156.82", Port = 443 },
                new BlockCheckTarget { Provider = "Cloudflare", Ip = "172.67.70.222", Port = 443 },
                new BlockCheckTarget { Provider = "CDN77", Ip = "195.181.167.155", Port = 443 },
                new BlockCheckTarget { Provider = "Akamai", Ip = "23.222.76.4", Port = 443 },
                new BlockCheckTarget { Provider = "AWS", Ip = "3.165.188.250", Port = 443 },
                new BlockCheckTarget { Provider = "DigitalOcean", Ip = "104.131.0.1", Port = 443 }
            };
        }

        /// <summary>Одна проба. Возвращает результат, а не бросает.</summary>
        public static BlockCheckResult Probe(BlockCheckTarget target, int timeoutMs)
        {
            BlockCheckResult result = new BlockCheckResult
            {
                Provider = target.Provider,
                Ip = target.Ip
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
                        // Заголовок объявляет большое тело, дальше доливаем его
                        // кусками. Обрыв случится на объёме, а не на разборе.
                        string request =
                            "POST / HTTP/1.1\r\n" +
                            "Host: " + target.Ip + "\r\n" +
                            "User-Agent: Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 " +
                            "(KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36\r\n" +
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

                            // Случайные данные: сжимаемый нулевой поток DPI
                            // может пропустить, и тест соврёт.
                            for (int i = 0; i < take; i++) chunk[i] = (byte)Rng.Next(256);

                            try
                            {
                                stream.Write(chunk, 0, take);
                                stream.Flush();

                                result.BytesSent += take;
                            }
                            catch (IOException ex)
                            {
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

                            // Сервер мог ответить и закрыть — проверяем, жив ли сокет.
                            if (client.Client.Poll(0, SelectMode.SelectRead))
                            {
                                if (client.Client.Available == 0)
                                {
                                    result.Detail = "сервер закрыл соединение";
                                    break;
                                }

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

        /// <summary>Проверить несколько целей параллельно.</summary>
        public static List<BlockCheckResult> ProbeAll(List<BlockCheckTarget> targets, int timeoutMs, int parallel)
        {
            List<BlockCheckResult> results = new List<BlockCheckResult>();

            if (targets == null || targets.Count == 0) return results;

            object gate = new object();
            int remaining = targets.Count;

            using (SemaphoreSlim limiter = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                foreach (BlockCheckTarget target in targets)
                {
                    BlockCheckTarget captured = target;

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        limiter.Wait();

                        try
                        {
                            BlockCheckResult r = Probe(captured, timeoutMs);

                            lock (gate) results.Add(r);
                        }
                        finally
                        {
                            limiter.Release();
                            Interlocked.Decrement(ref remaining);
                        }
                    });
                }

                DateTime until = DateTime.UtcNow.AddMilliseconds((long)timeoutMs * 6 + 5000);

                while (Volatile.Read(ref remaining) > 0 && DateTime.UtcNow < until)
                    Thread.Sleep(100);
            }

            results.Sort(delegate (BlockCheckResult a, BlockCheckResult b)
            {
                return string.Compare(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase);
            });

            return results;
        }

        /// <summary>Короткий вердикт по всем результатам.</summary>
        public static string Verdict(List<BlockCheckResult> results)
        {
            if (results == null || results.Count == 0) return "нет данных";

            int blocked = 0, survived = 0;
            List<int> breakPoints = new List<int>();

            foreach (BlockCheckResult r in results)
            {
                if (r.LooksBlocked) { blocked++; breakPoints.Add(r.BytesSent); }
                else if (r.Survived) survived++;
            }

            if (blocked == 0)
            {
                return survived > 0
                    ? "обрыва по объёму не обнаружено (" + survived + " из " + results.Count + " прошли)"
                    : "не удалось проверить (нет соединений)";
            }

            breakPoints.Sort();

            int median = breakPoints[breakPoints.Count / 2];

            return "ОБРЫВ по объёму: " + blocked + " из " + results.Count +
                   ", типично на " + (median / 1024) + " КБ. " +
                   "Лечится перекрытием кусков (seqovl), а не разрезом";
        }
    }
}
