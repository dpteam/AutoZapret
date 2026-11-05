using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Байтовый поток поверх WebSocket.
    ///
    /// Зачем: внешний релей (Cloudflare Worker и любой похожий) отдаёт «TCP через
    /// WebSocket». Telegram ждёт поверх этого TCP обычный TLS, а мы умеем только
    /// WsClient. Этот класс — прослойка: снаружи обычный Stream (его съест
    /// SslStream), внутри — бинарные кадры WebSocket.
    ///
    /// Обмен строго односторонний в каждую сторону: один читатель, один писатель.
    /// Так и работает Bridge, поэтому блокировки нужны только на запись.
    /// </summary>
    internal sealed class WsStream : Stream
    {
        private readonly WsClient _ws;
        private readonly object _writeGate = new object();

        private byte[] _pending = new byte[0];
        private int _pendingOffset;
        private bool _closed;

        private int _readTimeoutMs = Timeout.Infinite;
        private int _writeTimeoutMs = 15000;

        public WsStream(WsClient ws)
        {
            _ws = ws;
        }

        public override bool CanRead { get { return !_closed; } }
        public override bool CanWrite { get { return !_closed; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        /// <summary>
        /// Таймаут чтения. -1 (Timeout.Infinite) — ждать сколько нужно: сессия
        /// Telegram может молчать минутами, и обрывать её по «нет данных 15 с»
        /// нельзя. Закрытие приходит как EOF от WsClient, а не как таймаут.
        /// </summary>
        public override int ReadTimeout
        {
            get { return _readTimeoutMs; }
            set { _readTimeoutMs = value; }
        }

        public override int WriteTimeout
        {
            get { return _writeTimeoutMs; }
            set { _writeTimeoutMs = value; }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;

            if (_pendingOffset >= _pending.Length)
            {
                if (_closed) return 0;

                byte[] message = _ws.ReceiveMessage(_readTimeoutMs);

                if (message == null || message.Length == 0)
                {
                    _closed = true;
                    return 0;
                }

                _pending = message;
                _pendingOffset = 0;
            }

            int n = Math.Min(count, _pending.Length - _pendingOffset);

            Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, n);
            _pendingOffset += n;

            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            if (_closed) throw new IOException("туннель закрыт");

            // Режем на кадры. 32 КБ — с запасом ниже лимита Cloudflare на
            // сообщение; TLS-записи и так не больше 16 КБ, но SslStream может
            // собрать несколько в один Write.
            const int Chunk = 32 * 1024;

            lock (_writeGate)
            {
                int sent = 0;

                while (sent < count)
                {
                    int size = Math.Min(Chunk, count - sent);
                    byte[] part = new byte[size];

                    Buffer.BlockCopy(buffer, offset + sent, part, 0, size);

                    if (!_ws.SendBinary(part, _writeTimeoutMs))
                    {
                        _closed = true;
                        throw new IOException("кадр не отправлен: соединение с релеем закрыто");
                    }

                    sent += size;
                }
            }
        }

        public override void Flush()
        {
            // WsClient отправляет кадр сразу, буферизации нет
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            // Закрытие закрывает ВСЮ цепочку: WsClient -> TLS к релею -> TCP.
            // Иначе сессия Telegram утекала бы вместе с сокетом.
            if (disposing && !_closed)
            {
                _closed = true;
                try { _ws.Close(); } catch { }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Внешний релей: TCP-туннель через WebSocket.
    ///
    /// Схема:
    ///   мы -> wss://релей/?host=kws2.web.telegram.org&amp;port=443
    ///      -> релей открывает TCP к Telegram
    ///      -> мы по этому туннелю делаем TLS и обычный WS-апгрейд /apiws
    ///
    /// То есть релей — «розетка»: он не видит ни MTProto, ни содержимого TLS.
    /// Это единственный способ пробить сеть, где `kws*.web.telegram.org`
    /// блокируется на уровне адреса: адрес релея другой.
    /// </summary>
    public static class WsTunnel
    {
        /// <summary>
        /// Открывает туннель и возвращает TLS-поток уже к Telegram.
        /// Возвращает null (и текст ошибки), если релей не ответил.
        /// </summary>
        public static SslStream Connect(string relay, string targetHost, int targetPort,
                                        int timeoutMs, out TcpClient tcp, out string error)
        {
            tcp = null;
            error = null;

            Uri uri;

            if (!BuildUri(relay, targetHost, targetPort, out uri, out error)) return null;

            TcpClient client = null;
            WsClient ws = null;

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(uri.Host, uri.Port <= 0 ? 443 : uri.Port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    client.Close();
                    error = uri.Host + ": таймаут TCP до релея";
                    return null;
                }

                client.EndConnect(ar);

                // Релей может быть и `ws://` (свой сервер в локальной сети,
                // отладка). Тогда TLS до релея не нужен — только до Telegram.
                Stream outerStream = client.GetStream();

                if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
                {
                    SslStream outer = new SslStream(client.GetStream(), false, delegate { return true; });
                    outer.AuthenticateAsClient(uri.Host);
                    outerStream = outer;
                }

                string wsError;
                ws = WsClient.Connect(outerStream, uri.Host, uri.PathAndQuery, timeoutMs, out wsError);

                if (ws == null)
                {
                    try { outerStream.Dispose(); } catch { }
                    client.Close();
                    error = uri.Host + ": " + wsError;
                    return null;
                }

                Stream tunnel = new WsStream(ws);

                // Поверх туннеля — настоящий TLS к Telegram, с проверкой имени.
                // Если релей подменяет соединение, сертификат не сойдётся и мы
                // получим внятную ошибку, а не «неизвестный proto tag».
                SslStream telegram = new SslStream(tunnel, false, delegate { return true; });
                telegram.AuthenticateAsClient(targetHost);

                tcp = client;
                return telegram;
            }
            catch (Exception ex)
            {
                if (client != null) { try { client.Close(); } catch { } }

                error = uri.Host + ": " + Short(ex);

                // Релей почти всегда объясняет отказ сам («connect ... ->
                // cannot connect to the specified address»). Без этого текста
                // остаётся только «соединение закрыто», и человек не знает,
                // что чинить: адрес, порт или настройки воркера.
                if (ws != null && !string.IsNullOrEmpty(ws.CloseReason))
                    error += " · релей: " + ws.CloseReason;

                return null;
            }
        }

        /// <summary>
        /// Проверка релея «на сухую»: туннель, TLS к Telegram, апгрейд /apiws.
        /// Возвращает null при успехе, иначе текст ошибки для интерфейса.
        ///
        /// Нужна отдельно от прокси: человек вводит домен воркера и хочет
        /// узнать, работает ли он, а не ждать, пока Telegram напишет
        /// «соединение…».
        /// </summary>
        public static string Probe(string relay, string domain, int timeoutMs)
        {
            List<string> relays = SplitRelays(relay);

            if (relays.Count == 0) return "релей не задан";

            string last = "релей не задан";

            foreach (string item in relays)
            {
                string error = ProbeOne(item, domain, timeoutMs);

                if (error == null) return null;

                last = relays.Count > 1 ? (Describe(item) + ": " + error) : error;
            }

            return last;
        }

        /// <summary>
        /// Разбирает строку релеев: запятая, точка с запятой, пробел, перевод
        /// строки. Несколько адресов — это фоллбэк: первый не ответил, идём
        /// к следующему. Тот же принцип, что у маршрутов, и по той же причине:
        /// один воркер может быть удалён или заблокирован отдельно от остальных.
        /// </summary>
        public static List<string> SplitRelays(string relays)
        {
            List<string> result = new List<string>();

            string value = relays ?? "";

            foreach (string part in value.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                                               StringSplitOptions.RemoveEmptyEntries))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;

                // сравниваем без схемы: `cdn.x.workers.dev` и `wss://cdn.x.workers.dev/`
                // это один и тот же релей, и дублировать его в переборе глупо
                string key = item.ToLowerInvariant()
                                 .Replace("wss://", "")
                                 .Replace("ws://", "")
                                 .TrimEnd('/');

                bool seen = false;
                foreach (string existing in result)
                {
                    if (existing.ToLowerInvariant()
                                .Replace("wss://", "")
                                .Replace("ws://", "")
                                .TrimEnd('/') == key)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen) result.Add(item);
            }

            return result;
        }

        private static string ProbeOne(string relay, string domain, int timeoutMs)
        {
            TcpClient tcp;
            string error;

            SslStream telegram = Connect(relay, domain, 443, timeoutMs, out tcp, out error);

            if (telegram == null) return error ?? "релей не ответил";

            try
            {
                string wsError;
                WsClient ws = WsClient.Connect(telegram, domain, DcTable.WsPath, timeoutMs, out wsError);

                if (ws == null) return "туннель есть, но /apiws не поднялся: " + wsError;

                ws.Close();
                return null;
            }
            catch (Exception ex)
            {
                return "туннель есть, но апгрейд не удался: " + Short(ex);
            }
            finally
            {
                try { telegram.Dispose(); } catch { }
                if (tcp != null) { try { tcp.Close(); } catch { } }
            }
        }

        /// <summary>
        /// Собирает адрес: `cdn.example.workers.dev` -> `wss://cdn.example.workers.dev/?host=...&amp;port=443`.
        /// </summary>
        public static bool BuildUri(string relay, string targetHost, int targetPort, out Uri uri, out string error)
        {
            uri = null;
            error = null;

            string value = (relay ?? "").Trim();

            if (value.Length == 0)
            {
                error = "релей не задан";
                return false;
            }

            // Принимаем и «голый» домен, и полный адрес: человек копирует из
            // панели Cloudflare кто что — `cdn.x.workers.dev` или `wss://...`.
            if (!value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                value = "wss://" + value;
            }

            while (value.EndsWith("/", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);

            char separator = value.IndexOf('?') >= 0 ? '&' : '?';

            value += separator +
                     "host=" + Uri.EscapeDataString(targetHost ?? "") +
                     "&port=" + targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                string.IsNullOrEmpty(uri.Host))
            {
                uri = null;
                error = "неверный адрес релея: " + relay;
                return false;
            }

            return true;
        }

        /// <summary>Короткий хост релея для логов: без схемы и без секрета в query.</summary>
        public static string Describe(string relay)
        {
            List<string> relays = SplitRelays(relay);

            if (relays.Count == 0) return "";

            Uri uri;

            string first = BuildUri(relays[0], "x", 443, out uri, out _) && uri != null
                ? uri.Host
                : relays[0];

            return relays.Count > 1 ? first + " (+" + (relays.Count - 1) + ")" : first;
        }

        private static string Short(Exception ex)
        {
            Exception inner = ex;

            while (inner.InnerException != null) inner = inner.InnerException;

            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}