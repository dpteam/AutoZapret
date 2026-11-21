using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Linux
{
    /// <summary>
    /// Правила перехвата: nftables, если он есть, иначе iptables.
    ///
    /// Почему обе системы. nftables — по умолчанию в современных дистрибутивах
    /// (Debian 10+, Fedora, Arch), а iptables там лишь совместимый слой. Но на
    /// старых системах и в контейнерах nft может отсутствовать, и тогда
    /// остаются только iptables. Проверяем наличие, а не угадываем по версии.
    ///
    /// Почему отдельная таблица и цепочки, а не правила прямо в POSTROUTING:
    ///   * убрать за собой можно одной командой, не разбирая чужие правила;
    ///   * чужие правила не трогаются вовсе — на машине может быть свой firewall;
    ///   * видно в `nft list ruleset`, что это наше.
    ///
    /// Почему `packets 1-6` (connbytes 1:6). Это главное для скорости: обход
    /// нужен только на первых пакетах соединения — там, где идёт рукопожатие и
    /// DPI принимает решение. Если гнать в userspace весь поток, каждое
    /// скачивание проходит через nfqueue, и сеть заметно замедляется. Именно на
    /// это жалуются в подобных обвязках: «работает, но медленно».
    /// </summary>
    internal static class Firewall
    {
        public const int QueueNumber = 200;

        /// <summary>Метка, которой помечаем свои пакеты, чтобы не зациклиться.</summary>
        private const string Mark = "0x40000000";

        private const string NftTable = "ip autozapret";
        private const string NftChainOut = "out";
        private const string NftChainIn = "in";

        private const string IptChain = "AUTOZAPRET";
        private const string IptChainIn = "AUTOZAPRET_IN";
        private const string IptTable = "mangle";

        /// <summary>Какой backend используем: `nft`, `iptables` или null (нет ни одного).</summary>
        public static string Backend
        {
            get
            {
                if (Exists("nft")) return "nft";
                if (Exists("iptables")) return "iptables";
                return null;
            }
        }

        private static bool Exists(string tool)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("which", tool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(2000);
                    return p.ExitCode == 0 && output.Trim().Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ команды

        /// <summary>
        /// Команды установки правил. Возвращает список argv-массивов.
        ///
        /// Отдельным методом, чтобы это можно было проверить без root: строки
        /// правил — самая ошибкоопасная часть, и «выглядит правильно» здесь не
        /// годится.
        /// </summary>
        public static List<string[]> SetupCommands(string backend, string tcpPorts, string udpPorts)
        {
            List<string[]> list = new List<string[]>();

            if (backend == "nft")
            {
                // Таблица своя, inet — работает и для IPv4, и для IPv6 сразу.
                list.Add(new[] { "nft", "delete", "table", "ip", "autozapret" });   // на случай остатков
                list.Add(new[] { "nft", "add", "table", NftTable });

                list.Add(new[] { "nft", "add", "chain", NftTable, NftChainOut,
                                 "{", "type", "filter", "hook", "postrouting", "priority", "mangle", ";", "}" });

                list.Add(new[] { "nft", "add", "chain", NftTable, NftChainIn,
                                 "{", "type", "filter", "hook", "prerouting", "priority", "filter", ";", "}" });

                if (!string.IsNullOrEmpty(tcpPorts))
                {
                    list.Add(new[]
                    {
                        "nft", "add", "rule", NftTable, NftChainOut,
                        "meta", "mark", "and", Mark, "==", "0",
                        "tcp", "dport", "{" + tcpPorts + "}",
                        "ct", "original", "packets", "1-6",
                        "queue", "num", QueueNumber.ToString(), "bypass"
                    });
                }

                if (!string.IsNullOrEmpty(udpPorts))
                {
                    list.Add(new[]
                    {
                        "nft", "add", "rule", NftTable, NftChainOut,
                        "meta", "mark", "and", Mark, "==", "0",
                        "udp", "dport", "{" + udpPorts + "}",
                        "ct", "original", "packets", "1-6",
                        "queue", "num", QueueNumber.ToString(), "bypass"
                    });
                }

                // Входящие пакеты: нужны только для правки окна TCP (первый SYN-ACK).
                if (!string.IsNullOrEmpty(tcpPorts))
                {
                    list.Add(new[]
                    {
                        "nft", "add", "rule", NftTable, NftChainIn,
                        "tcp", "sport", "{" + tcpPorts + "}",
                        "ct", "reply", "packets", "1-3",
                        "queue", "num", QueueNumber.ToString(), "bypass"
                    });
                }

                return list;
            }

            // ---- iptables
            list.Add(new[] { "iptables", "-t", IptTable, "-D", "POSTROUTING", "-j", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-F", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-X", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-D", "PREROUTING", "-j", IptChainIn });
            list.Add(new[] { "iptables", "-t", IptTable, "-F", IptChainIn });
            list.Add(new[] { "iptables", "-t", IptTable, "-X", IptChainIn });

            list.Add(new[] { "iptables", "-t", IptTable, "-N", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-A", "POSTROUTING", "-j", IptChain });

            if (!string.IsNullOrEmpty(tcpPorts))
            {
                list.Add(new[]
                {
                    "iptables", "-t", IptTable, "-A", IptChain,
                    "-p", "tcp", "-m", "multiport", "--dports", tcpPorts,
                    "-m", "connbytes", "--connbytes-dir=original",
                    "--connbytes-mode=packets", "--connbytes", "1:6",
                    "-m", "mark", "!", "--mark", Mark,
                    "-j", "NFQUEUE", "--queue-num", QueueNumber.ToString(), "--queue-bypass"
                });
            }

            if (!string.IsNullOrEmpty(udpPorts))
            {
                list.Add(new[]
                {
                    "iptables", "-t", IptTable, "-A", IptChain,
                    "-p", "udp", "-m", "multiport", "--dports", udpPorts,
                    "-m", "connbytes", "--connbytes-dir=original",
                    "--connbytes-mode=packets", "--connbytes", "1:6",
                    "-m", "mark", "!", "--mark", Mark,
                    "-j", "NFQUEUE", "--queue-num", QueueNumber.ToString(), "--queue-bypass"
                });
            }

            if (!string.IsNullOrEmpty(tcpPorts))
            {
                list.Add(new[] { "iptables", "-t", IptTable, "-N", IptChainIn });
                list.Add(new[] { "iptables", "-t", IptTable, "-A", "PREROUTING", "-j", IptChainIn });

                list.Add(new[]
                {
                    "iptables", "-t", IptTable, "-A", IptChainIn,
                    "-p", "tcp", "-m", "multiport", "--sports", tcpPorts,
                    "-m", "connbytes", "--connbytes-dir=reply",
                    "--connbytes-mode=packets", "--connbytes", "1:3",
                    "-m", "mark", "!", "--mark", Mark,
                    "-j", "NFQUEUE", "--queue-num", QueueNumber.ToString(), "--queue-bypass"
                });
            }

            return list;
        }

        /// <summary>Команды снятия. Всегда выполняется, даже если процесс упал.</summary>
        public static List<string[]> TeardownCommands(string backend)
        {
            List<string[]> list = new List<string[]>();

            if (backend == "nft")
            {
                list.Add(new[] { "nft", "delete", "table", NftTable });
                return list;
            }

            list.Add(new[] { "iptables", "-t", IptTable, "-D", "POSTROUTING", "-j", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-F", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-X", IptChain });
            list.Add(new[] { "iptables", "-t", IptTable, "-D", "PREROUTING", "-j", IptChainIn });
            list.Add(new[] { "iptables", "-t", IptTable, "-F", IptChainIn });
            list.Add(new[] { "iptables", "-t", IptTable, "-X", IptChainIn });

            return list;
        }

        /// <summary>Порты из аргументов стратегии: --filter-tcp / --filter-udp.</summary>
        public static void PortsFromArgs(List<string> args, out string tcp, out string udp)
        {
            tcp = "";
            udp = "";

            foreach (string a in args)
            {
                if (a.StartsWith("--filter-tcp=", StringComparison.Ordinal))
                    tcp = JoinPorts(tcp, a.Substring("--filter-tcp=".Length));

                else if (a.StartsWith("--filter-udp=", StringComparison.Ordinal))
                    udp = JoinPorts(udp, a.Substring("--filter-udp=".Length));
            }
        }

        private static string JoinPorts(string current, string value)
        {
            if (string.IsNullOrEmpty(value) || value == "*") return current;

            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);

            foreach (string part in current.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(part.Trim());

            foreach (string part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string v = part.Trim();

                // Порт «12» у zapret означает «ничего»: так выключен игровой фильтр.
                if (v == "12" || v.Length == 0) continue;

                set.Add(v);
            }

            List<string> sorted = new List<string>(set);
            sorted.Sort();

            return string.Join(",", sorted.ToArray());
        }

        public static string Run(string[] args, out string error)
        {
            error = null;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(args[0])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);

                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = args[0] + " не запустился"; return null; }

                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    if (p.ExitCode != 0)
                    {
                        error = string.IsNullOrEmpty(stderr) ? "код " + p.ExitCode : stderr.Trim();
                        return null;
                    }

                    return stdout;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
