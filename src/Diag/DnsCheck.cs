using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using AutoZapret.Core;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Проверка DNS на подмену.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ DnsGuard
    ///   DnsGuard — это сторож: он молча возвращает одну строку предупреждения
    ///   перед запуском стратегии и ловит только fake-IP от VPN-клиентов
    ///   (198.18.0.0/15). Здесь другое: человек нажал «проверить DNS» и должен
    ///   увидеть по каждому домену, что ответил системный DNS, что ответил DoH
    ///   и в чём именно расхождение.
    ///
    /// ЧТО СЧИТАЕТСЯ ДОКАЗАТЕЛЬСТВОМ, А ЧТО НЕТ
    ///   Это главное в файле. Наивное «адреса не совпали — подмена» даёт ложные
    ///   срабатывания: google.com и github.com отдают разные адреса через
    ///   разные резолверы, потому что у них geo-DNS и anycast. Проверено на
    ///   живой сети: www.google.com через три резолвера дал три разных адреса,
    ///   и все три настоящие.
    ///
    ///   Поэтому «подмена» ставится только по признакам, которые не объясняются
    ///   geo-DNS:
    ///     * ответ — адрес, которого не может быть у настоящего сайта
    ///       (0.0.0.0, 127.x, приватные сети, 198.18/15, CGNAT 100.64/10);
    ///     * системный DNS не ответил вовсе, а DoH ответил;
    ///     * DoH недоступен, хотя должен быть (его режут — это тоже находка).
    ///
    ///   «Просто разные адреса» — это отдельный вердикт «расходится», и он
    ///   подан как сведение, а не как тревога.
    /// </summary>
    public sealed class DnsCheckRow
    {
        public string Host = "";

        public List<IPAddress> System = new List<IPAddress>();
        public List<IPAddress> Doh = new List<IPAddress>();

        /// <summary>ok | stub | differs | no-system | no-doh | no-answer</summary>
        public string Verdict = "";

        public string Detail = "";

        public bool Problem
        {
            get { return Verdict == "stub" || Verdict == "no-system" || Verdict == "no-doh"; }
        }

        public string SystemText() { return DnsCheck.Join(System); }
        public string DohText() { return DnsCheck.Join(Doh); }
    }

    public sealed class DnsCheckReport
    {
        public List<DnsCheckRow> Rows = new List<DnsCheckRow>();

        public string DohServer = "";

        public bool DohWorks;

        public int ProblemCount
        {
            get
            {
                int n = 0;
                foreach (DnsCheckRow row in Rows) if (row.Problem) n++;
                return n;
            }
        }

        public int Total { get { return Rows.Count; } }

        /// <summary>Одной строкой: что показала проверка и что с этим делать.</summary>
        public string Summary()
        {
            if (Rows.Count == 0) return "нечего проверять";

            if (!DohWorks)
                return "DoH недоступен — проверить подмену нечем. Это само по себе находка: " +
                       "если DoH режет провайдер, браузер тоже не сможет им пользоваться";

            if (ProblemCount == 0)
            {
                bool differs = false;
                foreach (DnsCheckRow row in Rows) if (row.Verdict == "differs") differs = true;

                if (differs)
                    return "подмены не найдено. У части доменов адреса разные — это geo-DNS " +
                           "(у крупных сайтов свои серверы в каждой сети), а не подмена";

                return "подмены не найдено: системный DNS и DoH отвечают одинаково";
            }

            bool stub = false;
            bool noSystem = false;

            foreach (DnsCheckRow row in Rows)
            {
                if (row.Verdict == "stub") stub = true;
                if (row.Verdict == "no-system") noSystem = true;
            }

            if (stub)
                return "НАЙДЕНА ПОДМЕНА: " + ProblemCount + " из " + Total +
                       " доменов получают адрес-заглушку. Включите DoH в браузере " +
                       "(или в системе, если белые списки не нужны)";

            if (noSystem)
                return "системный DNS не отвечает на " + ProblemCount + " из " + Total +
                       " доменов, а DoH отвечает. Похоже на перехват порта 53";

            return "проверка не прошла";
        }
    }

    public static class DnsCheck
    {
        /// <summary>
        /// Домены по умолчанию: те, которые чаще всего и подменяют.
        ///
        /// Специально разные: если бы все были от одной компании, одинаковое
        /// поведение ничего бы не доказывало.
        /// </summary>
        public static List<string> DefaultHosts()
        {
            return new List<string>
            {
                "www.youtube.com",
                "discord.com",
                "www.google.com",
                "github.com",
                "www.twitch.tv",
                "cloudflare.com"
            };
        }

        public static string Join(List<IPAddress> addresses)
        {
            if (addresses == null || addresses.Count == 0) return "";

            List<string> parts = new List<string>();
            foreach (IPAddress ip in addresses) parts.Add(ip.ToString());
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Почему адрес не может быть настоящим. Пустая строка — обычный адрес.
        ///
        /// Список намеренно только про «так не бывает», без догадок: каждый
        /// добавленный сюда диапазон — это обещание, что настоящий сайт туда
        /// не попадёт. Приватные диапазоны и 198.18/15 проверены практикой
        /// (AGENTS.md: набор hosts со stub-адресом сломал 148 имён).
        /// </summary>
        public static string StubReason(IPAddress ip)
        {
            if (ip == null) return "";

            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                // Для IPv6 «заглушек» в этом смысле нет: ::1 не отдают вместо
                // сайта, а приватные fc00::/7 в DNS почти не встречаются.
                return "";
            }

            byte[] b = ip.GetAddressBytes();

            if (b[0] == 0) return "0.0.0.0";
            if (b[0] == 127) return "loopback (127.x)";
            if (b[0] == 10) return "приватный (10.x)";

            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return "приватный (172.16-31.x)";
            if (b[0] == 192 && b[1] == 168) return "приватный (192.168.x)";

            if (b[0] == 169 && b[1] == 254) return "link-local (169.254.x)";
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return "fake-IP (198.18/15)";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "CGNAT (100.64/10)";

            return "";
        }

        /// <summary>
        /// Прогнать проверку по списку доменов.
        ///
        /// DoH кэш сбрасывается: иначе проверка покажет то, что уже лежит
        /// в памяти с прошлого раза, и «сейчас» превратится в «когда-то».
        /// </summary>
        public static DnsCheckReport Run(List<string> hosts, int timeoutMs)
        {
            DnsCheckReport report = new DnsCheckReport();

            if (hosts == null || hosts.Count == 0) hosts = DefaultHosts();

            Doh.Reset();

            foreach (string host in hosts)
            {
                DnsCheckRow row = new DnsCheckRow { Host = host };

                row.System = SystemAddresses(host);
                row.Doh = Doh.Resolve(host, timeoutMs);

                if (row.Doh.Count > 0)
                {
                    report.DohWorks = true;
                    if (report.DohServer.Length == 0) report.DohServer = Doh.LastServer;
                }

                Judge(row);

                report.Rows.Add(row);
            }

            return report;
        }

        /// <summary>Ответ системного DNS. Пустой список — не ответил.</summary>
        private static List<IPAddress> SystemAddresses(string host)
        {
            List<IPAddress> result = new List<IPAddress>();

            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(host))
                    if (ip.AddressFamily == AddressFamily.InterNetwork) result.Add(ip);
            }
            catch (Exception ex)
            {
                Log.Debug("DNS", "системный " + host + ": " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Вынести вердикт по одному домену.
        ///
        /// public, а не private, специально: это решение «подмена или нет» —
        /// самое дорогое место в файле, и его надо проверять тестом на
        /// подготовленных данных, а не надеяться на удачную сеть.
        /// </summary>
        public static void Judge(DnsCheckRow row)
        {
            // 1. Заглушка в ответе системного DNS — самый сильный признак.
            foreach (IPAddress ip in row.System)
            {
                string why = StubReason(ip);

                if (why.Length > 0)
                {
                    row.Verdict = "stub";
                    row.Detail = "системный DNS вернул " + ip + " (" + why + ")";
                    return;
                }
            }

            // 2. DoH не ответил — судить не о чем, и это отдельная находка.
            if (row.Doh.Count == 0)
            {
                row.Verdict = row.System.Count == 0 ? "no-answer" : "no-doh";
                row.Detail = row.System.Count == 0
                    ? "ни системный DNS, ни DoH не ответили"
                    : "DoH не ответил, проверить нечем";
                return;
            }

            // 3. Системный молчит, а DoH отвечает: похоже на перехват порта 53.
            if (row.System.Count == 0)
            {
                row.Verdict = "no-system";
                row.Detail = "системный DNS не ответил, DoH дал " + Join(row.Doh);
                return;
            }

            // 4. Сравнение наборов. Пересечение хотя бы по одному адресу
            //    означает, что ответ настоящий: подмена подменяет ВЕСЬ ответ,
            //    а не часть. Полное несовпадение — «расходится», но это может
            //    быть geo-DNS, поэтому и вердикт отдельный.
            bool overlap = false;

            foreach (IPAddress sys in row.System)
                foreach (IPAddress doh in row.Doh)
                    if (sys.Equals(doh)) overlap = true;

            if (overlap)
            {
                row.Verdict = "ok";
                row.Detail = "";
            }
            else
            {
                row.Verdict = "differs";
                row.Detail = "адреса разные (может быть geo-DNS, а не подмена)";
            }
        }

        /// <summary>Строка для журнала и CLI: домен, что ответили, вердикт.</summary>
        public static string ToLine(DnsCheckRow row)
        {
            string sys = row.SystemText();
            string doh = row.DohText();

            return row.Host.PadRight(20) +
                   " системный: " + (sys.Length > 0 ? sys : "—").PadRight(34) +
                   " DoH: " + (doh.Length > 0 ? doh : "—").PadRight(34) +
                   " " + row.Verdict + (row.Detail.Length > 0 ? " (" + row.Detail + ")" : "");
        }
    }

    /// <summary>
    /// CLI-режим проверки DNS: `AutoZapret --dns-check [домен ...]`.
    ///
    /// Живёт отдельно от DiagCommand, потому что не зависит ни от Windows, ни
    /// от движка: это обычные DNS-запросы. Поэтому один и тот же код работает
    /// на Windows, на Linux и в termux на телефоне.
    /// </summary>
    public static class DnsCheckCommand
    {
        public static int Run(string[] args)
        {
            List<string> hosts = new List<string>();

            foreach (string a in args)
            {
                if (a.StartsWith("-", StringComparison.Ordinal)) continue;

                // Аргументы — имена хостов; пути и имена файлов пропускаем.
                if (a.IndexOf('.') > 0 && a.IndexOf('\\') < 0 && a.IndexOf('/') < 0 &&
                    !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    hosts.Add(a);
                }
            }

            if (hosts.Count == 0) hosts = DnsCheck.DefaultHosts();

            Console.WriteLine("проверка DNS на подмену");
            Console.WriteLine("сравниваю ответ системного DNS с ответом DoH");
            Console.WriteLine("");

            DnsCheckReport report = DnsCheck.Run(hosts, 5000);

            foreach (DnsCheckRow row in report.Rows)
            {
                string sys = row.SystemText();
                string doh = row.DohText();

                string mark;

                switch (row.Verdict)
                {
                    case "ok": mark = "[ok]  "; break;
                    case "differs": mark = "[~]   "; break;
                    case "no-answer": mark = "[?]   "; break;
                    default: mark = "[!]   "; break;
                }

                Console.WriteLine(mark + row.Host);
                Console.WriteLine("        системный: " + (sys.Length > 0 ? sys : "нет ответа"));
                Console.WriteLine("        DoH:       " + (doh.Length > 0 ? doh : "нет ответа"));

                if (row.Detail.Length > 0) Console.WriteLine("        " + row.Detail);
            }

            Console.WriteLine("");

            Console.WriteLine(report.DohWorks
                ? "DoH-сервер: " + report.DohServer
                : "DoH-сервер: недоступен");

            Console.WriteLine(report.Summary());

            if (!report.DohWorks)
            {
                Console.WriteLine("");
                Console.WriteLine("Проверьте DoH вручную:");
                Console.WriteLine("  curl -v -I --doh-url https://cloudflare-dns.com/dns-query https://github.com");
            }

            return report.ProblemCount == 0 && report.DohWorks ? 0 : 1;
        }
    }
}
