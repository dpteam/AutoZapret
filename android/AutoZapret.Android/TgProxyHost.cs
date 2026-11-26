using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Android.Content;
using AutoZapret.Core;
using AutoZapret.Proxy;

namespace AutoZapret.Android
{
    /// <summary>
    /// Telegram MTProto-прокси на телефоне.
    ///
    /// ЧТО ЭТО
    ///   Тот же `TgProxyServer`, что на десктопе и на Linux — файлы берутся из
    ///   `src/Proxy` как есть, копий нет. Человек открывает ссылку `tg://proxy`
    ///   в Telegram, и трафик идёт: Telegram → наш прокси → (свой релей /
    ///   публичный пул CfProxy / напрямую) → серверы Telegram.
    ///
    /// ЗАЧЕМ НА ТЕЛЕФОНЕ
    ///   Провайдер режет `149.154.167.x`, а Cloudflare — нет. Прокси уводит
    ///   соединение на чужой адрес, и Telegram работает даже там, где прямой
    ///   путь закрыт. Ровно та же задача, что на десктопе.
    ///
    /// ЧЕМ ОТЛИЧАЕТСЯ ОТ ДЕСКТОПА
    ///   * слушаем только `127.0.0.1` — снаружи телефон не должен быть открыт;
    ///   * путь данных задаёт `Paths.SetDataDir(FilesDir)`: APK — это zip,
    ///     писать в него нельзя, а `LocalApplicationData` на Android указывает
    ///     в неочевидное место;
    ///   * живёт в foreground-сервисе (`ZapretVpnService`), иначе система режет
    ///     фоновый трафик и соединения висят до таймаута.
    /// </summary>
    public static class TgProxyHost
    {
        /// <summary>Порт по умолчанию. 1443 — как у десктопа, чтобы ссылки совпадали.</summary>
        public const int DefaultPort = 1443;

        private static readonly object Gate = new object();

        private static TgProxyServer _server;
        private static string _secret = "";
        private static bool _pathsReady;

        /// <summary>Прокси запущен.</summary>
        public static bool IsRunning
        {
            get { lock (Gate) return _server != null && _server.IsRunning; }
        }

        /// <summary>Порт, который реально слушается. 0 — не запущен.</summary>
        public static int Port
        {
            get { lock (Gate) return _server != null && _server.IsRunning ? _server.Port : 0; }
        }

        /// <summary>Секрет в hex без префикса dd. Пусто — ещё не создан.</summary>
        public static string Secret
        {
            get { lock (Gate) return _secret; }
        }

        /// <summary>Ошибка последнего запуска. Пусто — запуск удался.</summary>
        public static string LastError
        {
            get { lock (Gate) return _server == null ? "" : _server.LastError ?? ""; }
        }

        /// <summary>
        /// Привязать каталог данных к каталогу приложения.
        ///
        /// Без этого переносимый код (`Paths`, кэш списков CfProxy) не знает,
        /// куда писать: на Android `AppDomain.BaseDirectory` — это APK, и
        /// запись туда невозможна.
        /// </summary>
        public static void InitPaths(Context context)
        {
            lock (Gate)
            {
                if (_pathsReady) return;
                if (context == null) return;

                string dir = context.FilesDir != null
                    ? context.FilesDir.AbsolutePath
                    : context.CacheDir.AbsolutePath;

                Paths.SetDataDir(Path.Combine(dir, "data"));
                Paths.EnsureDirectories();

                _pathsReady = true;
            }
        }

        /// <summary>
        /// Взять секрет из настроек или создать новый.
        ///
        /// 16 случайных байт через `RandomNumberGenerator`: секрет — это ключ,
        /// и предсказуемый ключ означает, что прокси может использовать кто
        /// угодно. В журнал не пишем (правило проекта).
        /// </summary>
        public static string EnsureSecret(Context context)
        {
            lock (Gate)
            {
                if (_secret.Length > 0) return _secret;

                if (context != null)
                {
                    ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);
                    string saved = prefs.GetString("tg_proxy_secret", "");

                    if (saved != null && saved.Length == 32)
                    {
                        _secret = saved;
                        return _secret;
                    }
                }

                byte[] raw = new byte[16];

                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(raw);
                }

                StringBuilder hex = new StringBuilder(32);

                foreach (byte b in raw) hex.Append(b.ToString("x2"));

                _secret = hex.ToString();

                if (context != null)
                {
                    ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);
                    ISharedPreferencesEditor editor = prefs.Edit();
                    editor.PutString("tg_proxy_secret", _secret);
                    editor.Apply();
                }

                return _secret;
            }
        }

        /// <summary>
        /// Запустить. Возвращает null при успехе, иначе текст ошибки.
        ///
        /// relay пусто — путь выберется сам: свой релей, затем публичный пул
        /// CfProxy, затем прямой. Порядок задан в `TgProxyServer.OpenUpstream`.
        ///
        /// upstreamSocks — локальный SOCKS5 обхода. На Android он ОБЯЗАТЕЛЕН,
        /// если обход включён: там это не системный перехват, а прокси, и без
        /// него наши соединения уходят мимо обхода. Проверено на телефоне:
        /// TCP:443 до Cloudflare не проходит вообще, а на ПК с работающим
        /// winws2 — проходит.
        /// </summary>
        public static string Start(Context context, int port, string relay, bool cfProxy,
                                   string upstreamSocks = null)
        {
            lock (Gate)
            {
                if (_server != null && _server.IsRunning) return null;

                InitPaths(context);

                string secret = EnsureSecret(context);

                TgProxyServer server = new TgProxyServer();

                // Слушаем только петлю: снаружи телефон открывать нельзя.
                // Это не настройка, а правило — на десктопе 0.0.0.0 выбирают
                // осознанно и с предупреждением, на телефоне такой нужды нет.
                string error = server.Start("127.0.0.1", port <= 0 ? DefaultPort : port, secret,
                                            relay, cfProxy, upstreamSocks);

                if (error != null)
                {
                    server.Dispose();
                    return error;
                }

                _server = server;
                return null;
            }
        }

        /// <summary>
        /// Сообщить прокси, через какой SOCKS5 ходить наружу.
        ///
        /// Вызывается, когда обход включают или выключают: прокси может быть
        /// уже запущен, и перезапускать его ради этого не нужно.
        /// </summary>
        public static void UpdateUpstreamSocks(string socks)
        {
            lock (Gate)
            {
                if (_server == null) return;

                try { _server.UpdateUpstreamSocks(socks); } catch { }
            }
        }

        /// <summary>Остановить. Безопасно вызывать повторно.</summary>
        public static void Stop()
        {
            lock (Gate)
            {
                if (_server == null) return;

                try { _server.Stop(); } catch { }
                try { _server.Dispose(); } catch { }

                _server = null;
            }
        }

        /// <summary>
        /// Ссылка для Telegram.
        ///
        /// `127.0.0.1` — потому что Telegram на том же телефоне, и порт наружу
        /// не открыт. Секрет с префиксом `dd`: это obfuscated2, как на десктопе.
        /// </summary>
        public static string BuildLink(Context context)
        {
            int port = Port;

            if (port <= 0) port = DefaultPort;

            string secret = EnsureSecret(context);

            return "tg://proxy?server=127.0.0.1&port=" + port + "&secret=dd" + secret;
        }

        /// <summary>Строка состояния для экрана и журнала.</summary>
        public static string StatusLine(Context context)
        {
            lock (Gate)
            {
                if (_server == null || !_server.IsRunning) return "Telegram-прокси: выключен";

                ProxyStats stats = _server.Stats;

                return "Telegram-прокси: 127.0.0.1:" + _server.Port +
                       ", соединений " + stats.TotalConnections +
                       ", отказов " + stats.Rejected;
            }
        }
    }
}
