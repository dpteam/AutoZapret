using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Снимает НАСТОЯЩИЙ ClientHello с самого устройства.
    ///
    /// Идея простая и точная: поднимаем слушатель на 127.0.0.1, подключаемся к
    /// нему обычным `SslStream` и просим рукопожатие для нужного домена. Всё,
    /// что TLS-стек платформы отправит первым, — это подлинный ClientHello:
    /// тот же порядок расширений, тот же набор шифров, тот же key_share, что и
    /// у настоящего соединения с этого телефона. Мы просто читаем байты и
    /// закрываем соединение.
    ///
    /// Почему это лучше синтетики (`FakeTls`). Синтетический hello собран по
    /// RFC и выглядит правдоподобно, но его отпечаток (JA3/JA4) не совпадает
    /// ни с одним реальным клиентом. DPI, который сравнивает отпечатки, такой
    /// фейк отличит. Снятый — неотличим по определению: это и есть отпечаток
    /// этого устройства.
    ///
    /// Почему это лучше готовой библиотеки фейков (TLSpack, 1694 файла). В
    /// библиотеке снимки с чужих браузеров в чужой день. Нужен домен, которого
    /// в наборе нет — и приходится подставлять чужой SNI. Здесь свой домен
    /// подставляется в родной hello: SNI меняется на любой, остальное — своё.
    ///
    /// Результат кэшируется по домену: снятие занимает доли секунды, но делать
    /// его на каждое соединение незачем.
    /// </summary>
    public static class HelloCapture
    {
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object Gate = new object();

        /// <summary>Сколько ждём hello. Больше не нужно: он уходит первым же пакетом.</summary>
        private const int TimeoutMs = 1500;

        private static readonly Random Rng = new Random();

        /// <summary>
        /// Снять ClientHello для домена. null — не получилось (тогда вызывающий
        /// использует синтетику).
        /// </summary>
        public static byte[] For(string host)
        {
            string sni = FakeTls.NormalizeHost(host);

            if (sni.Length == 0) return null;

            lock (Gate)
            {
                byte[] cached;

                if (Cache.TryGetValue(sni, out cached)) return cached;
            }

            byte[] hello = Capture(sni);

            if (hello == null) return null;

            lock (Gate)
            {
                // Кэш без ограничения: домены в нём — те, куда реально ходили,
                // и их десятки, а не миллионы.
                Cache[sni] = hello;
            }

            return hello;
        }

        /// <summary>
        /// Только из кэша, без ожидания. null — ещё не снято.
        ///
        /// Зачем отдельный метод. Снятие занимает до полутора секунд (ждём
        /// таймаут рукопожатия). Если делать это прямо в потоке отправки, то
        /// на страницу с двадцатью доменами набегает полминуты пауз — снаружи
        /// это выглядит как «сайт открылся и висит». Поэтому в горячем пути
        /// берём только готовое, а снимаем заранее.
        /// </summary>
        public static byte[] Cached(string host)
        {
            string sni = FakeTls.NormalizeHost(host);

            if (sni.Length == 0) return null;

            lock (Gate)
            {
                byte[] cached;

                return Cache.TryGetValue(sni, out cached) ? cached : null;
            }
        }

        /// <summary>
        /// Снять в фоне, если ещё не снимали. Вызывается из горячего пути:
        /// возвращается сразу, работа идёт в отдельном потоке.
        /// </summary>
        public static void Warm(string host)
        {
            string sni = FakeTls.NormalizeHost(host);

            if (sni.Length == 0) return;

            lock (Gate)
            {
                if (Cache.ContainsKey(sni)) return;
                if (Pending.Contains(sni)) return;

                Pending.Add(sni);
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { For(sni); }
                catch { }

                lock (Gate) { Pending.Remove(sni); }
            });
        }

        /// <summary>
        /// Прогреть кэш для доменов, которые встречаются чаще всего.
        ///
        /// Вызывается при старте прокси, до первого трафика: к моменту, когда
        /// придёт настоящее соединение, снимок уже готов и фейк уходит родной,
        /// а не синтетический.
        /// </summary>
        public static void WarmPopular()
        {
            string[] hosts =
            {
                "www.youtube.com", "youtube.com", "discord.com", "twitch.tv",
                "www.google.com", "googlevideo.com", "api.telegram.org"
            };

            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (string host in hosts)
                {
                    try { Warm(host); } catch { }

                    // небольшая пауза: каждый снимок поднимает слушатель и
                    // соединение, гнать их пачкой незачем
                    Thread.Sleep(150);
                }
            });
        }

        /// <summary>Сколько доменов уже снято (для отчёта в UI).</summary>
        public static int CachedCount
        {
            get { lock (Gate) { return Cache.Count; } }
        }

        public static void Clear()
        {
            lock (Gate) { Cache.Clear(); }
        }

        /// <summary>
        /// Сам снимок. Слушатель + SslStream к нему.
        ///
        /// Все исключения здесь ожидаемы: рукопожатие заведомо не завершится —
        /// на том конце нет сервера. Нам нужны только первые байты, а не
        /// результат.
        /// </summary>
        private static byte[] Capture(string sni)
        {
            TcpListener listener = null;

            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();

                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                byte[] captured = null;
                Exception readerError = null;

                // Читаем в отдельном потоке: SslStream блокируется на ожидании
                // ответа сервера, и без второго потока мы бы ждали таймаут.
                Thread reader = new Thread(delegate ()
                {
                    TcpClient peer = null;

                    try
                    {
                        peer = listener.AcceptTcpClient();
                        peer.NoDelay = true;
                        peer.ReceiveTimeout = TimeoutMs;

                        NetworkStream stream = peer.GetStream();
                        byte[] buffer = new byte[8192];
                        int total = 0;

                        // ClientHello — одна TLS-запись: 5 байт заголовка, дальше
                        // длина из этих байт. Читаем ровно столько.
                        while (total < buffer.Length)
                        {
                            int n = stream.Read(buffer, total, buffer.Length - total);
                            if (n <= 0) break;

                            total += n;

                            if (total >= 5)
                            {
                                int recordLength = (buffer[3] << 8) | buffer[4];
                                int needed = 5 + recordLength;

                                if (total >= needed)
                                {
                                    byte[] result = new byte[needed];
                                    Buffer.BlockCopy(buffer, 0, result, 0, needed);

                                    captured = result;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        readerError = ex;
                    }
                    finally
                    {
                        try { if (peer != null) peer.Close(); } catch { }
                    }
                })
                {
                    IsBackground = true,
                    Name = "hello-capture"
                };

                reader.Start();

                TcpClient client = null;

                try
                {
                    client = new TcpClient();
                    client.NoDelay = true;

                    IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);

                    if (!ar.AsyncWaitHandle.WaitOne(TimeoutMs))
                    {
                        return null;
                    }

                    client.EndConnect(ar);

                    using (SslStream ssl = new SslStream(client.GetStream(), false))
                    {
                        try
                        {
                            // Заведомо провалится: на том конце нет TLS-сервера.
                            // Нужен сам факт отправки ClientHello.
                            ssl.AuthenticateAsClient(sni);
                        }
                        catch
                        {
                            // ожидаемо
                        }
                    }
                }
                catch
                {
                    // тоже ожидаемо
                }
                finally
                {
                    try { if (client != null) client.Close(); } catch { }
                }

                // Даём читателю мгновение закончить запись в captured.
                reader.Join(500);

                if (captured != null && FakeTls.LooksLikeClientHello(captured)) return captured;

                if (readerError != null) return null;

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (listener != null) listener.Stop(); } catch { }
            }
        }

        /// <summary>
        /// Подставить другой SNI в снятый hello.
        ///
        /// Зачем: hello снимается для одного домена, а нужен для другого. SNI —
        /// единственное поле, которое DPI и проверяет; всё остальное (порядок
        /// расширений, шифры, key_share) остаётся родным для устройства.
        ///
        /// Работает только если новый домен не длиннее старого: длины в TLS
        /// записаны в заголовках, и удлинение потребовало бы пересчёта всех
        /// вложенных длин. Более короткий домен добиваем «нулевым» доменом
        /// через padding — проще и безопаснее, чем править структуру.
        /// </summary>
        public static byte[] Retarget(byte[] hello, string newHost)
        {
            if (hello == null || hello.Length < 5) return hello;

            string host = FakeTls.NormalizeHost(newHost);

            if (host.Length == 0) return hello;

            try
            {
                int sniOffset, sniLength;

                if (!FindSni(hello, out sniOffset, out sniLength)) return hello;

                if (host.Length > sniLength) return hello;   // не влезет — оставляем как есть

                byte[] result = new byte[hello.Length];
                Buffer.BlockCopy(hello, 0, result, 0, hello.Length);

                byte[] name = System.Text.Encoding.ASCII.GetBytes(host);

                // Обнуляем старое имя целиком и пишем новое с начала. Остаток
                // нулей не ломает разбор: длина имени в заголовке остаётся
                // прежней, а хвост — это тот же домен с нулями, что DPI
                // прочитает как мусор после имени. Чтобы этого не было,
                // добиваем валидным доменом-заглушкой.
                for (int i = 0; i < sniLength; i++) result[sniOffset + i] = 0;

                Buffer.BlockCopy(name, 0, result, sniOffset, name.Length);

                // хвост: точки и буквы 'a', чтобы разбор остался валидным
                for (int i = name.Length; i < sniLength; i++)
                    result[sniOffset + i] = (byte)'a';

                return result;
            }
            catch
            {
                return hello;
            }
        }

        /// <summary>
        /// Найти в ClientHello позицию имени в расширении server_name.
        ///
        /// Разбираем по структуре RFC 8446, а не поиском подстроки: имя может
        /// случайно совпасть с байтами в другом расширении.
        /// </summary>
        private static bool FindSni(byte[] data, out int offset, out int length)
        {
            offset = 0;
            length = 0;

            try
            {
                // запись: 5 байт заголовка, дальше handshake
                int p = 5;

                if (data[p] != 0x01) return false;   // ClientHello
                p += 4;                              // тип + длина

                p += 2;                              // legacy_version
                p += 32;                             // random

                int sessionIdLength = data[p];
                p += 1 + sessionIdLength;

                int cipherLength = (data[p] << 8) | data[p + 1];
                p += 2 + cipherLength;

                int compressionLength = data[p];
                p += 1 + compressionLength;

                if (p + 2 > data.Length) return false;

                int extensionsLength = (data[p] << 8) | data[p + 1];
                p += 2;

                int end = Math.Min(p + extensionsLength, data.Length);

                while (p + 4 <= end)
                {
                    int type = (data[p] << 8) | data[p + 1];
                    int size = (data[p + 2] << 8) | data[p + 3];

                    p += 4;

                    if (type == 0x0000)   // server_name
                    {
                        // список: 2 байта длины, затем записи
                        int listLength = (data[p] << 8) | data[p + 1];
                        int q = p + 2;
                        int listEnd = Math.Min(q + listLength, data.Length);

                        while (q + 3 <= listEnd)
                        {
                            int nameType = data[q];
                            int nameLength = (data[q + 1] << 8) | data[q + 2];

                            q += 3;

                            if (nameType == 0 && q + nameLength <= data.Length)
                            {
                                offset = q;
                                length = nameLength;

                                return length > 0;
                            }

                            q += nameLength;
                        }

                        return false;
                    }

                    p += size;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
