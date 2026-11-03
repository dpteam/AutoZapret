using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Тип блокировки. Взято из dpi-detector (MIT): бинарное «открылось/нет»
    /// слишком грубо — от причины зависит, какая стратегия поможет.
    /// </summary>
    public enum BlockKind
    {
        Ok,             // открылось
        Redirect,       // легитимный редирект
        Timeout,        // тишина: ни RST, ни ответа
        TcpReset,       // TCP RST в ответ на ClientHello -> fake/fakedsplit
        TlsAlert,       // поддельный TLS-алерт -> fake-tls
        TlsSpoof,       // мусор вместо TLS -> FAKE TLS AUTO
        TlsEof,         // обрыв на handshake при 0 байт (обычно RST)
        DnsFail,        // DNS не отвечает
        IspPage,        // домен резолвится в заглушку провайдера -> DoH/hosts
        LocalIp,        // домен резолвится в приватный IP
        OsError,        // прочая ошибка сокета
        Unknown
    }

    public static class BlockKindText
    {
        public static string Short(BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.Ok: return "OK";
                case BlockKind.Redirect: return "REDIR";
                case BlockKind.Timeout: return "TIMEOUT";
                case BlockKind.TcpReset: return "TLS RST";
                case BlockKind.TlsAlert: return "TLS ALERT";
                case BlockKind.TlsSpoof: return "TLS SPOOF";
                case BlockKind.TlsEof: return "TLS EOF";
                case BlockKind.DnsFail: return "DNS FAIL";
                case BlockKind.IspPage: return "ISP PAGE";
                case BlockKind.LocalIp: return "LOCAL IP";
                case BlockKind.OsError: return "OS ERR";
                default: return "ERR";
            }
        }

        public static string Russian(BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.Ok: return "открывается";
                case BlockKind.Redirect: return "редирект";
                case BlockKind.Timeout: return "нет ответа (тихий DPI)";
                case BlockKind.TcpReset: return "RST на TLS-рукопожатии";
                case BlockKind.TlsAlert: return "поддельный TLS-алерт";
                case BlockKind.TlsSpoof: return "подмена TLS-ответа";
                case BlockKind.TlsEof: return "обрыв на рукопожатии";
                case BlockKind.DnsFail: return "DNS не отвечает";
                case BlockKind.IspPage: return "заглушка провайдера";
                case BlockKind.LocalIp: return "домен ведёт на локальный IP";
                case BlockKind.OsError: return "ошибка сети";
                default: return "неизвестно";
            }
        }

        public static bool IsOk(BlockKind kind)
        {
            return kind == BlockKind.Ok || kind == BlockKind.Redirect;
        }

        /// <summary>Какие семейства стратегий пробовать при таком типе блокировки.</summary>
        public static string[] Suggest(BlockKind kind)
        {
            switch (kind)
            {
                case BlockKind.TcpReset:
                case BlockKind.TlsEof:
                    return new[] { "SIMPLE FAKE", "general (ALT)", "general (ALT10)" };
                case BlockKind.TlsSpoof:
                    return new[] { "FAKE TLS AUTO", "FAKE TLS AUTO ALT", "FAKE TLS AUTO ALT3" };
                case BlockKind.TlsAlert:
                    return new[] { "general (ALT9)", "general (ALT13)", "general (ALT12)" };
                case BlockKind.Timeout:
                    return new[] { "general (ALT5)", "general (EXP)", "general (ALT7)" };
                case BlockKind.DnsFail:
                case BlockKind.IspPage:
                case BlockKind.LocalIp:
                    return new[] { "не zapret: включите DoH или добавьте домен в hosts" };
                default:
                    return new string[0];
            }
        }
    }

    /// <summary>
    /// Глубокая проверка одного домена: не «открылся ли», а «почему не открылся».
    ///
    /// Идея и эвристики — из dpi-detector (MIT), переписано на .NET:
    ///   * сначала DNS: если резолвится в приватный IP или в заглушку провайдера —
    ///     это не блокировка DPI, и zapret тут не поможет;
    ///   * потом сырой TCP+TLS: различаем RST / alert / мусор / тишину;
    ///   * EOF при 0 прочитанных байтах считаем RST: Windows маскирует RST под EOF,
    ///     и наивная проверка «нет данных = таймаут» врёт.
    /// </summary>
    public sealed class DeepProbe
    {
        private const int WsaConnectionReset = 10054;
        private const int WsaConnectionRefused = 10061;
        private const int WsaTimedOut = 10060;
        private const int WsaNetUnreach = 10051;
        private const int WsaHostUnreach = 10065;
        private const int WsaConnAborted = 10053;

        private readonly HashSet<string> _stubIps = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public BlockKind Classify(string host, int timeoutSec, out string detail)
        {
            detail = "";

            // 0) Быстрый и достоверный ответ: если curl открывает сайт — он не заблокирован.
            //    Это защита от ложных срабатываний: системный DNS и сырой TLS-хендшейк
            //    иногда врут там, где браузер открывает страницу.
            if (CurlSaysOk(host, timeoutSec))
            {
                detail = "открывается";
                return BlockKind.Ok;
            }

            // 1) DNS
            IPAddress[] addresses;
            BlockKind dnsKind = Resolve(host, out addresses, out detail);
            if (dnsKind != BlockKind.Ok) return dnsKind;

            // 2) TCP + TLS на каждый адрес, пока не получим внятный ответ.
            //    Ограничиваем двумя адресами: у youtube их восемь, и перебор всех
            //    превращает проверку одного домена в минуту ожидания.
            BlockKind last = BlockKind.Unknown;
            int tried = 0;
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork) continue;   // IPv4: IPv6 в РФ часто «мёртвый»
                if (tried++ >= 2) break;

                BlockKind kind = TlsHandshake(address, host, timeoutSec, out detail);
                if (BlockKindText.IsOk(kind)) return kind;

                if (kind == BlockKind.TlsAlert || kind == BlockKind.TlsSpoof || kind == BlockKind.TcpReset)
                    return kind;   // это уже осмысленный вердикт, дальше адреса не помогут

                last = kind;
            }

            return last;
        }

        // ------------------------------------------------------------------- dns

        /// <summary>
        /// Проверка через curl: он резолвит сам, умеет -4 и не страдает от особенностей
        /// .NET-стека. Используется как «источник правды» о доступности.
        /// </summary>
        private static bool CurlSaysOk(string host, int timeoutSec)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "curl.exe",
                    Arguments = "-4 -s -o NUL --max-time " + timeoutSec +
                                " -w \"%{http_code}\" https://" + host + "/",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutSec * 1000 + 2000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }

                    int status;
                    if (!int.TryParse(stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out status))
                        return false;

                    return status >= 200 && status < 500;
                }
            }
            catch
            {
                return false;
            }
        }

        private BlockKind Resolve(string host, out IPAddress[] addresses, out string detail)        {
            detail = "";
            addresses = new IPAddress[0];

            try
            {
                addresses = Dns.GetHostAddresses(host);
            }
            catch (SocketException ex)
            {
                detail = "DNS: " + ex.SocketErrorCode;
                return BlockKind.DnsFail;
            }
            catch (Exception ex)
            {
                detail = "DNS: " + ex.Message;
                return BlockKind.DnsFail;
            }

            if (addresses.Length == 0)
            {
                detail = "DNS вернул пусто";
                return BlockKind.DnsFail;
            }

            foreach (IPAddress address in addresses)
            {
                if (IsPrivate(address))
                {
                    detail = "резолвится в " + address + " (приватный)";
                    return BlockKind.LocalIp;
                }

                lock (_gate)
                {
                    if (_stubIps.Contains(address.ToString()))
                    {
                        detail = "резолвится в заглушку провайдера " + address;
                        return BlockKind.IspPage;
                    }
                }
            }

            return BlockKind.Ok;
        }

        private static bool IsPrivate(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;

            byte[] b = address.GetAddressBytes();
            if (b[0] == 10) return true;                                  // 10/8
            if (b[0] == 127) return true;                                 // loopback
            if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16/12
            if (b[0] == 169 && b[1] == 254) return true;                  // link-local
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;   // fake-ip (Clash)
            if (b[0] == 0) return true;
            return false;
        }

        /// <summary>
        /// Собирает IP-заглушки провайдера: резолвим заведомо заблокированные домены
        /// и ищем адреса, которые повторяются у разных доменов.
        /// </summary>
        public void LearnStubIps(IEnumerable<string> blockedHosts)
        {
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string host in blockedHosts)
            {
                try
                {
                    foreach (IPAddress address in Dns.GetHostAddresses(host))
                    {
                        if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string key = address.ToString();
                        int count;
                        seen.TryGetValue(key, out count);
                        seen[key] = count + 1;
                    }
                }
                catch { }
            }

            // адрес, который отдали минимум двум разным заблокированным доменам — почти наверняка заглушка
            lock (_gate)
            {
                _stubIps.Clear();
                foreach (KeyValuePair<string, int> kv in seen)
                {
                    if (kv.Value >= 2)
                    {
                        _stubIps.Add(kv.Key);
                        Log.Debug("DIAG", "IP-заглушка провайдера: " + kv.Key + " (у " + kv.Value + " доменов)");
                    }
                }
            }
        }

        public int StubIpCount
        {
            get { lock (_gate) return _stubIps.Count; }
        }

        // -------------------------------------------------------------- tcp/tls

        private BlockKind TlsHandshake(IPAddress address, string host, int timeoutSec, out string detail)
        {
            detail = "";

            using (TcpClient client = new TcpClient())
            {
                try
                {
                    IAsyncResult connect = client.BeginConnect(address, 443, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSec)))
                    {
                        detail = "TCP connect: таймаут";
                        return BlockKind.Timeout;
                    }

                    try { client.EndConnect(connect); }
                    catch (SocketException ex)
                    {
                        detail = "TCP: " + ex.SocketErrorCode;
                        return MapSocketError(ex.SocketErrorCode);
                    }

                    client.ReceiveTimeout = timeoutSec * 1000;
                    client.SendTimeout = timeoutSec * 1000;

                    // Настоящий TLS-handshake, а не самодельный ClientHello.
                    // Причина: сервер отвергает синтетический hello (alert 50 = decode_error),
                    // и рабочий сайт выглядит заблокированным. SslStream даёт достоверный
                    // результат, а тип ошибки всё равно различим по исключению.
                    using (System.Net.Security.SslStream ssl = new System.Net.Security.SslStream(
                               client.GetStream(), false, delegate { return true; }))
                    {
                        try
                        {
                            ssl.AuthenticateAsClient(host);
                            detail = "рукопожатие прошло (" + ssl.SslProtocol + ")";
                            return BlockKind.Ok;
                        }
                        catch (System.Security.Authentication.AuthenticationException ex)
                        {
                            return ClassifyAuthError(ex, out detail);
                        }
                        catch (IOException ex)
                        {
                            SocketException inner = ex.InnerException as SocketException;
                            if (inner != null)
                            {
                                detail = "TLS/IO: " + inner.SocketErrorCode;
                                return MapSocketError(inner.SocketErrorCode);
                            }
                            // Обрыв без данных при handshake — почти всегда RST,
                            // который Windows маскирует под EOF.
                            detail = "обрыв на рукопожатии (маскировка RST под EOF)";
                            return BlockKind.TcpReset;
                        }
                    }
                }
                catch (SocketException ex)
                {
                    detail = "сокет: " + ex.SocketErrorCode;
                    return MapSocketError(ex.SocketErrorCode);
                }
                catch (Exception ex)
                {
                    detail = ex.GetType().Name + ": " + ex.Message;
                    return BlockKind.Unknown;
                }
            }
        }

        /// <summary>
        /// Разбор ошибки TLS-рукопожатия по тексту. Эвристики из dpi-detector
        /// (utils/error_classifier.py), адаптированы под формулировки .NET.
        /// </summary>
        private static BlockKind ClassifyAuthError(Exception ex, out string detail)
        {
            string text = CollectText(ex);
            detail = "";

            // Windows: "Не удалось установить соединение: удаленный хост принудительно разорвал..."
            if (text.Contains("принудительно разорвал") || text.Contains("forcibly closed") ||
                text.Contains("connection was closed") || text.Contains("reset by peer") ||
                text.Contains("сброс"))
            {
                detail = "RST при рукопожатии";
                return BlockKind.TcpReset;
            }

            if (text.Contains("wrong version number") || text.Contains("неверный номер версии") ||
                text.Contains("record overflow") || text.Contains("decode_error") ||
                text.Contains("decode error") || text.Contains("не удается декодировать"))
            {
                detail = "подмена ответа (не-TLS данные)";
                return BlockKind.TlsSpoof;
            }

            if (text.Contains("unrecognized_name") || text.Contains("unrecognized name"))
            {
                detail = "SNI-блок (unrecognized_name)";
                return BlockKind.TlsAlert;
            }

            if (text.Contains("handshake_failure") || text.Contains("handshake failure"))
            {
                detail = "DPI-алерт (handshake_failure)";
                return BlockKind.TlsAlert;
            }

            if (text.Contains("protocol_version") || text.Contains("protocol version"))
            {
                detail = "блок версии протокола";
                return BlockKind.TlsAlert;
            }

            if (text.Contains("eof") || text.Contains("преждевременный") ||
                text.Contains("premature") || text.Contains("неожиданно закрыт"))
            {
                detail = "обрыв на рукопожатии";
                return BlockKind.TcpReset;
            }

            if (text.Contains("timed out") || text.Contains("таймаут") || text.Contains("истекло"))
            {
                detail = "таймаут рукопожатия";
                return BlockKind.Timeout;
            }

            // «Сертификат» мы не проверяем (валидатор всегда true), но если сюда
            // всё же пришло — это MITM.
            if (text.Contains("certificate") || text.Contains("сертификат"))
            {
                detail = "проблема с сертификатом (возможен MITM)";
                return BlockKind.TlsAlert;
            }

            detail = FirstLine(ex.Message);
            return BlockKind.TlsAlert;
        }

        private static string CollectText(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            Exception current = ex;
            for (int i = 0; i < 8 && current != null; i++)
            {
                sb.Append(current.Message.ToLowerInvariant()).Append(" | ");
                current = current.InnerException;
            }
            return sb.ToString();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int nl = text.IndexOfAny(new[] { '\r', '\n' });
            string line = nl < 0 ? text : text.Substring(0, nl);
            return line.Length > 90 ? line.Substring(0, 87) + "…" : line;
        }

        private static BlockKind MapSocketError(SocketError error)
        {
            switch (error)
            {
                case SocketError.ConnectionReset:
                    return BlockKind.TcpReset;
                case SocketError.ConnectionRefused:
                    return BlockKind.TcpReset;
                case SocketError.TimedOut:
                    return BlockKind.Timeout;
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return BlockKind.OsError;
                case SocketError.ConnectionAborted:
                    return BlockKind.TcpReset;
                default:
                    return BlockKind.OsError;
            }
        }

    }
}
