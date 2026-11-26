using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Публичный пул доменов-прослоек Cloudflare (CfProxy).
    ///
    /// ЗАЧЕМ
    ///   Провайдер режет адреса Telegram (149.154.167.x), а Cloudflare — нет.
    ///   Публичный пул — это чужие Cloudflare Worker'ы, которые принимают
    ///   WebSocket и перекидывают его в Telegram. Пока Telegram недоступен
    ///   напрямую, этот путь работает, потому что адрес назначения другой.
    ///
    /// МЕХАНИКА (проверена вживую 26.11.2026)
    ///   Подключаемся НЕ к базовому домену, а к поддомену с номером DC:
    ///
    ///     kws2.pclead.co.uk:443  → TLS (сертификат *.pclead.co.uk, Let's Encrypt)
    ///                            → GET /apiws с Upgrade: websocket
    ///                            → HTTP/1.1 101 Switching Protocols (152 мс)
    ///
    ///   Воркер понимает, куда пересылать, по имени хоста (`kws2.` = DC 2),
    ///   поэтому query-параметры вида `?host=…&amp;port=…` НЕ нужны — это другой
    ///   протокол, чем у нашего собственного релея.
    ///
    ///   Сам базовый домен A-записи не имеет (проверено: NOERROR без A,
    ///   NS и SOA на месте) — и это не поломка: записи есть только у
    ///   `kws{dc}.`-поддоменов. Проверять надо именно поддомен.
    ///
    /// ОБФУСКАЦИЯ СПИСКА
    ///   Домены в списке замаскированы: `virkgj.com` означает `pclead.co.uk`.
    ///   Правило: если строка кончается на `.com`, каждая латинская буква
    ///   префикса сдвигается назад на число букв в префиксе, суффикс `.co.uk`.
    ///
    /// ПРИВАТНОСТЬ
    ///   Трафик идёт через чужой воркер. Содержимое зашифровано TLS до
    ///   Telegram, но владелец воркера видит, к какому DC вы идёте, сколько
    ///   передаёте и когда. Поэтому это резерв, а не основной путь, и он
    ///   отключается настройкой.
    /// </summary>
    public static class CfProxyPool
    {
        /// <summary>Откуда берём список. Тот же адрес, что в оригинале.</summary>
        public const string ListUrl =
            "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";

        /// <summary>Сколько живёт скачанный список.</summary>
        public const int FetchTtlSec = 1800;

        /// <summary>Карантин домена после отказа.</summary>
        public const int CooldownSec = 30;

        /// <summary>Сколько доменов пробуем за одно соединение.</summary>
        public const int MaxPerPass = 3;

        /// <summary>
        /// Таймаут одной попытки. В оригинале ступенчатый (3, 3, 5, 5, 10 с);
        /// здесь проще: пул — резерв, и ждать его дольше нельзя.
        /// </summary>
        public const int ProbeTimeoutMs = 4000;

        /// <summary>Минимум доменов, чтобы считать список осмысленным.</summary>
        private const int MinValidDomains = 3;

        /// <summary>
        /// Встроенный резерв: три домена на самый первый запуск, когда ни
        /// сети до GitHub, ни кэша на диске ещё нет.
        ///
        /// Берём из того же списка, что и оригинал. Они замаскированы так же,
        /// поэтому проходят через Decode.
        /// </summary>
        private static readonly string[] FallbackEncoded =
        {
            "virkgj.com",
            "vmmzovy.com",
            "mkuosckvso.com"
        };

        // ---------------------------------------------------------------- состояние

        private static readonly object Gate = new object();

        private static List<string> _domains = new List<string>();
        private static DateTime _fetchedAt = DateTime.MinValue;

        private static readonly Dictionary<string, DateTime> Cooldowns =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Домен отвечал — помним, чтобы пробовать его первым.</summary>
        private static readonly Dictionary<string, DateTime> Verified =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Сколько доменов сейчас считаются рабочими.</summary>
        public static int WorkingCount
        {
            get { lock (Gate) return Verified.Count; }
        }

        /// <summary>Строка для журнала и интерфейса.</summary>
        public static string StatusText()
        {
            lock (Gate)
            {
                if (_domains.Count == 0) return "список не загружен";

                Prune();

                return "доменов " + _domains.Count + ", рабочих " + Verified.Count;
            }
        }

        /// <summary>Сколько доменов загружено. 0 — список не получен.</summary>
        public static int Count
        {
            get { lock (Gate) return _domains.Count; }
        }

        // ---------------------------------------------------------------- декодирование

        /// <summary>
        /// Декодирует строку из списка. Пустая строка — строка не домен.
        /// </summary>
        public static string Decode(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();

            if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) return "";

            if (!s.EndsWith(".com", StringComparison.Ordinal)) return s;

            string prefix = s.Substring(0, s.Length - 4);

            int letters = 0;
            foreach (char c in prefix)
                if (c >= 'a' && c <= 'z') letters++;

            if (letters == 0) return s;

            StringBuilder result = new StringBuilder(prefix.Length + 6);

            foreach (char c in prefix)
            {
                if (c >= 'a' && c <= 'z')
                {
                    // сдвиг назад по кругу: +26*N перед % гарантирует
                    // положительный результат для любой буквы
                    int shifted = ((c - 'a' - letters) % 26 + 26) % 26;
                    result.Append((char)('a' + shifted));
                }
                else
                {
                    result.Append(c);
                }
            }

            result.Append(".co.uk");
            return result.ToString();
        }

        /// <summary>Разбирает текст списка: декодирует, отбрасывает мусор и дубли.</summary>
        public static List<string> Parse(string text)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in (text ?? "").Split('\n'))
            {
                string domain = Decode(line);

                if (domain.Length == 0) continue;
                if (domain.IndexOf('.') <= 0) continue;
                if (domain.IndexOf(' ') >= 0) continue;
                if (!seen.Add(domain)) continue;

                result.Add(domain);
            }

            return result;
        }

        /// <summary>
        /// Имя хоста для подключения к DC через прослойку.
        ///
        /// Именно поддомен с номером DC: воркер определяет по нему адрес
        /// назначения. `kws2.pclead.co.uk` — это «DC 2 через pclead.co.uk».
        /// </summary>
        public static string HostFor(int dc, string baseDomain)
        {
            if (string.IsNullOrEmpty(baseDomain)) return "";

            // DC 203 ходит через WS-DC 2 — та же особенность, что и в DcTable.
            if (dc == 203) dc = 2;

            return "kws" + dc + "." + baseDomain;
        }

        // ---------------------------------------------------------------- загрузка

        private static string CachePath
        {
            get { return Path.Combine(Paths.StateDir, "cfproxy-domains.cache"); }
        }

        /// <summary>
        /// Список для работы — МГНОВЕННО, без сети.
        ///
        /// Порядок: память → кэш на диске → встроенный резерв. Сеть не
        /// трогаем никогда: этот метод вызывается в пути подключения, и
        /// блокирующая загрузка (до 10 секунд) означала бы, что Telegram
        /// висит на «соединение…» ровно из-за нашего же резерва.
        ///
        /// Свежий список подтягивается отдельно, в фоне (RefreshInBackground).
        /// </summary>
        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_domains.Count > 0) return;
            }

            List<string> loaded = LoadCache();

            if (loaded.Count == 0) loaded = Fallback();

            lock (Gate)
            {
                if (_domains.Count == 0)
                {
                    _domains = loaded;

                    // fetched_at не ставим: значит «список ещё не обновлялся»,
                    // и фоновое обновление сработает при первой возможности.
                    _fetchedAt = DateTime.MinValue;
                }
            }

            if (loaded.Count > 0)
                Log.Info("PROXY", "CfProxy: доменов для работы " + loaded.Count +
                                  (loaded.Count == Fallback().Count ? " (встроенный резерв)" : " (из кэша)"));
        }

        /// <summary>Встроенный резерв, декодированный.</summary>
        private static List<string> Fallback()
        {
            List<string> result = new List<string>();

            foreach (string raw in FallbackEncoded)
            {
                string domain = Decode(raw);
                if (domain.Length > 0) result.Add(domain);
            }

            return result;
        }

        /// <summary>
        /// Обновить список синхронно. Только для ручной проверки: человек
        /// нажал кнопку и ждёт результат, а не «список не загружен».
        ///
        /// В пути подключения этот метод НЕ вызывать — там EnsureLoaded.
        /// </summary>
        public static void RefreshNow()
        {
            lock (Gate)
            {
                if (_refreshing) return;
                _refreshing = true;
            }

            try
            {
                List<string> fetched = FetchRemote();

                if (fetched.Count > 0)
                {
                    lock (Gate)
                    {
                        _domains = fetched;
                        _fetchedAt = DateTime.UtcNow;
                        Cooldowns.Clear();
                    }

                    Log.Info("PROXY", "CfProxy: список обновлён вручную, доменов " + fetched.Count);
                }
                else
                {
                    lock (Gate) { _fetchedAt = DateTime.UtcNow; }
                }
            }
            finally
            {
                lock (Gate) { _refreshing = false; }
            }
        }

        /// <summary>
        /// Обновить список из сети В ФОНЕ. Не блокирует вызывающего.
        ///
        /// Зачем фон: прокси нужен в сети, где GitHub может быть недоступен
        /// или медленен. Если ждать загрузку в пути подключения, каждое
        /// первое соединение платит таймаут — и это ровно та сеть, где прокси
        /// важнее всего.
        ///
        /// Одновременно идёт не больше одного обновления: иначе десять
        /// соединений запустят десять загрузок одного и того же файла.
        /// </summary>
        public static void RefreshInBackground(bool force)
        {
            lock (Gate)
            {
                if (_refreshing) return;

                bool fresh = _fetchedAt != DateTime.MinValue &&
                             (DateTime.UtcNow - _fetchedAt).TotalSeconds < FetchTtlSec;

                if (fresh && !force) return;

                _refreshing = true;
            }

            System.Threading.Thread thread = new System.Threading.Thread(delegate()
            {
                try
                {
                    List<string> fetched = FetchRemote();

                    if (fetched.Count > 0)
                    {
                        lock (Gate)
                        {
                            _domains = fetched;
                            _fetchedAt = DateTime.UtcNow;

                            // Список сменился — старые пометки о доменах,
                            // которых больше нет, только мешают.
                            Cooldowns.Clear();
                        }

                        Log.Info("PROXY", "CfProxy: список обновлён, доменов " + fetched.Count);
                    }
                    else
                    {
                        // Не сбрасываем fetched_at: иначе следующее соединение
                        // снова полезет в сеть и снова будет ждать.
                        lock (Gate) { _fetchedAt = DateTime.UtcNow; }

                        Log.Debug("PROXY", "CfProxy: обновление не удалось, работаю с тем, что есть");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("PROXY", "CfProxy: фоновое обновление: " + ex.Message);
                }
                finally
                {
                    lock (Gate) { _refreshing = false; }
                }
            });

            thread.IsBackground = true;
            thread.Name = "cfproxy-refresh";
            thread.Start();
        }

        private static bool _refreshing;

        private static List<string> FetchRemote()
        {
            try
            {
                // HttpClient, а не WebRequest: на .NET 9/10 WebRequest
                // помечен устаревшим (SYSLIB0014), а WebClient не умеет
                // таймаут вовсе. Зависание здесь недопустимо: список
                // качается в сети, где GitHub может быть недоступен.
                using (System.Net.Http.HttpClient http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(10);

                    http.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent", "AutoZapret/" + AppInfo.Version);

                    // Nonce: Cloudflare кэширует по URL, и без него можно
                    // получить старый список из кэша CDN.
                    string url = ListUrl + "?" + Guid.NewGuid().ToString("N").Substring(0, 8);

                    string text = http.GetStringAsync(url).GetAwaiter().GetResult();

                    List<string> domains = Parse(text);

                    if (domains.Count < MinValidDomains)
                    {
                        Log.Warn("PROXY", "CfProxy: в списке всего " + domains.Count +
                                          " доменов — не доверяю");
                        return new List<string>();
                    }

                    SaveCache(text);
                    return domains;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("PROXY", "CfProxy: список из сети не получен (" + ex.Message + ")");
                return new List<string>();
            }
        }

        private static void SaveCache(string text)
        {
            try
            {
                Directory.CreateDirectory(Paths.StateDir);
                AtomicFile.WriteAllText(CachePath, text);
            }
            catch (Exception ex)
            {
                Log.Debug("PROXY", "CfProxy: кэш не записан (" + ex.Message + ")");
            }
        }

        private static List<string> LoadCache()
        {
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(CachePath);
                if (text == null) return new List<string>();

                List<string> domains = Parse(text);

                if (domains.Count > 0)
                    Log.Info("PROXY", "CfProxy: список из кэша, доменов " + domains.Count);

                return domains;
            }
            catch
            {
                return new List<string>();
            }
        }

        // ---------------------------------------------------------------- выбор

        /// <summary>
        /// Домены для очередной попытки: проверенные первыми, затем остальные.
        /// Карантинные пропускаем.
        ///
        /// Пустой список — нормальный ответ: значит пробовать нечего прямо
        /// сейчас, и вызывающий скажет это честно, а не будет ждать таймаутов.
        /// </summary>
        public static List<string> Routes(int dc, int limit)
        {
            List<string> snapshot;

            lock (Gate) { snapshot = new List<string>(_domains); }

            if (snapshot.Count == 0) return new List<string>();

            List<string> result = new List<string>();
            DateTime now = DateTime.UtcNow;

            lock (Gate)
            {
                Prune();

                // Проверенные рабочие — первыми: они уже отвечали.
                foreach (string domain in snapshot)
                {
                    DateTime until;

                    if (Verified.ContainsKey(domain) &&
                        (!Cooldowns.TryGetValue(domain, out until) || until <= now))
                    {
                        result.Add(HostFor(dc, domain));
                    }
                }

                // Остальные — если ещё есть место в проходе.
                foreach (string domain in snapshot)
                {
                    if (limit > 0 && result.Count >= limit) break;
                    if (Verified.ContainsKey(domain)) continue;

                    DateTime until;

                    if (Cooldowns.TryGetValue(domain, out until) && until > now) continue;

                    result.Add(HostFor(dc, domain));
                }
            }

            if (limit > 0 && result.Count > limit) result = result.GetRange(0, limit);

            return result;
        }

        /// <summary>Домен ответил — запоминаем и снимаем карантин.</summary>
        public static void MarkGood(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return;

            lock (Gate)
            {
                Verified[domain] = DateTime.UtcNow;
                Cooldowns.Remove(domain);
            }
        }

        /// <summary>Домен отказал — в карантин на 30 секунд.</summary>
        public static void MarkBad(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return;

            lock (Gate)
            {
                Cooldowns[domain] = DateTime.UtcNow.AddSeconds(CooldownSec);
                Verified.Remove(domain);
            }
        }

        /// <summary>Забыть всё: смена сети, ручная проверка, остановка прокси.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Cooldowns.Clear();
                Verified.Clear();
            }
        }

        /// <summary>
        /// Проверить весь список прямо сейчас.
        ///
        /// Проверяем НЕ TCP, а настоящий WS-апгрейд: домен может принимать
        /// соединение и не быть прослойкой (тогда апгрейд вернёт не 101).
        /// </summary>
        public static string VerifyAll(int dc, int timeoutMs)
        {
            EnsureLoaded();

            // Ручная проверка — единственное место, где загрузку ждём
            // синхронно: человек нажал кнопку и должен увидеть свежий
            // список, а не «список не загружен».
            RefreshNow();

            List<string> snapshot;

            lock (Gate) { snapshot = new List<string>(_domains); }

            if (snapshot.Count == 0) return "список не загружен";

            int alive = 0;

            foreach (string domain in snapshot)
            {
                if (ProbeWs(HostFor(dc, domain), timeoutMs)) { MarkGood(domain); alive++; }
                else MarkBad(domain);
            }

            Log.Info("PROXY", "CfProxy: проверено " + snapshot.Count + ", рабочих " + alive);

            return alive == 0
                ? "публичный пул недоступен: проверено " + snapshot.Count + ", ни один не ответил"
                : "публичный пул: рабочих " + alive + " из " + snapshot.Count;
        }

        /// <summary>
        /// Проверка: TLS + WS-апгрейд на /apiws. true — домен действительно
        /// прослойка и пускает.
        ///
        /// Сертификат проверяем как у релея (валидная цепочка, имя любое):
        /// это сертификат Cloudflare для домена прослойки, а не Telegram.
        /// </summary>
        public static bool ProbeWs(string host, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return false;

            TcpClient client = null;

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(host, 443, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;

                client.EndConnect(ar);

                System.Net.Security.SslStream ssl =
                    new System.Net.Security.SslStream(client.GetStream(), false, GatewayCheck.ValidateRelay);

                ssl.AuthenticateAsClient(host);

                string error;
                WsClient ws = WsClient.Connect(ssl, host, DcTable.WsPath, timeoutMs, out error);

                if (ws == null)
                {
                    try { ssl.Dispose(); } catch { }
                    return false;
                }

                ws.Close();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (client != null) { try { client.Close(); } catch { } }
            }
        }

        // ---------------------------------------------------------------- уборка

        private static void Prune()
        {
            DateTime now = DateTime.UtcNow;

            List<string> expired = new List<string>();

            foreach (KeyValuePair<string, DateTime> pair in Cooldowns)
                if (pair.Value <= now) expired.Add(pair.Key);

            foreach (string domain in expired) Cooldowns.Remove(domain);

            if (Cooldowns.Count > 256) Cooldowns.Clear();
        }
    }
}
