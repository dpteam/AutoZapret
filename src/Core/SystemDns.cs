using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Системный DNS: подсунуть Windows наш локальный резолвер и вернуть как было.
    ///
    /// Windows 10 не умеет DoH вообще (в одиннадцатой это настройка «Шифрование
    /// DNS»), поэтому единственный путь — свой DNS на 127.0.0.1:53 и смена
    /// системного адреса на него. Так DoH получают все программы сразу: Discord,
    /// игры, Telegram, командная строка, что угодно.
    ///
    /// Правила:
    ///   * трогаем ТОЛЬКО адаптер, через который идёт интернет (у машины с
    ///     Hyper-V/VPN их бывает шесть, и портить остальные незачем);
    ///   * перед изменением запоминаем, что было (автоматически или какие
    ///     серверы), в data\state\sysdns.json — «Вернуть» восстановит точно;
    ///   * если приложение упало, следующий запуск видит флаг и предлагает
    ///     вернуть, а не оставляет систему с мёртвым DNS.
    /// </summary>
    public static class SystemDns
    {
        private sealed class Saved
        {
            public string Adapter = "";
            public bool Automatic = true;
            public List<string> Servers = new List<string>();
        }

        private static string StatePath
        {
            get { return Path.Combine(Paths.StateDir, "sysdns.json"); }
        }

        /// <summary>Текущее состояние: включено ли, на каком адаптере.</summary>
        public static bool IsEnabled
        {
            get { return File.Exists(StatePath); }
        }

        public static string StatusText()
        {
            if (!IsEnabled) return "выключен (системный DNS провайдера)";

            Saved saved = Load();

            return saved == null
                ? "включён"
                : "включён: " + saved.Adapter + " → 127.0.0.1";
        }

        /// <summary>
        /// Включить: запомнить текущее и переключить адаптер на 127.0.0.1.
        /// Возвращает null или текст ошибки.
        /// </summary>
        public static string Enable()
        {
            string adapter = DefaultRouteAdapter(out string localIp);

            if (adapter == null)
                return "не нашёл адаптер с интернетом (нет маршрута по умолчанию)";

            Saved saved = new Saved { Adapter = adapter };
            List<string> current = StaticServers(adapter);

            saved.Automatic = current.Count == 0;
            saved.Servers = current;

            Save(saved);

            string error = Netsh("interface ipv4 set dnsservers name=\"" + adapter + "\" static 127.0.0.1 primary");

            if (error != null)
            {
                try { File.Delete(StatePath); } catch { }
                return error;
            }

            Log.Info("DNS", "системный DNS → 127.0.0.1 на адаптере «" + adapter + "» (было: " +
                            (saved.Automatic ? "автоматически" : string.Join(", ", saved.Servers.ToArray())) + ")");

            return null;
        }

        /// <summary>Вернуть: автоматически или прежние серверы.</summary>
        public static string Disable()
        {
            Saved saved = Load();

            if (saved == null) return null;

            string error;

            if (saved.Automatic)
            {
                error = Netsh("interface ipv4 set dnsservers name=\"" + saved.Adapter + "\" dhcp");
            }
            else
            {
                error = Netsh("interface ipv4 set dnsservers name=\"" + saved.Adapter +
                              "\" static " + saved.Servers[0] + " primary");

                for (int i = 1; i < saved.Servers.Count; i++)
                {
                    Netsh("interface ipv4 add dnsservers name=\"" + saved.Adapter +
                          "\" address=" + saved.Servers[i] + " index=" + (i + 1));
                }
            }

            if (error != null) return error;

            try { File.Delete(StatePath); } catch { }

            Log.Info("DNS", "системный DNS возвращён на адаптере «" + saved.Adapter + "»");

            return null;
        }

        /// <summary>Адрес DNS-серверов адаптера, если они заданы вручную.</summary>
        public static List<string> StaticServers(string adapter)
        {
            List<string> result = new List<string>();

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, adapter, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(dns)) continue;

                        result.Add(dns.ToString());
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Адаптер, через который реально идёт интернет. Определяем по локальному
        /// адресу исходящего маршрута: connect на UDP только выбирает маршрут,
        /// пакетов не отправляет.
        /// </summary>
        public static string DefaultRouteAdapter(out string localIp)
        {
            localIp = "";

            try
            {
                using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect("8.8.8.8", 65530);

                    IPEndPoint end = probe.LocalEndPoint as IPEndPoint;
                    if (end != null) localIp = end.Address.ToString();
                }
            }
            catch
            {
                return null;
            }

            if (localIp.Length == 0) return null;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation address in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address == null) continue;
                        if (!string.Equals(address.Address.ToString(), localIp, StringComparison.Ordinal)) continue;

                        return ni.Name;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string Netsh(string arguments)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c chcp 437 >nul & netsh " + arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(8000);

                    if (p.ExitCode == 0) return null;

                    Log.Warn("DNS", "netsh: " + output.Trim());
                    return "netsh вернул код " + p.ExitCode +
                           ": " + (output.Trim().Length > 0 ? FirstLine(output) : "без вывода");
                }
            }
            catch (Exception ex)
            {
                return "netsh: " + ex.Message;
            }
        }

        private static string FirstLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 120 ? t.Substring(0, 120) : t;
            }

            return "";
        }

        private static Saved Load()
        {
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(StatePath);
                if (string.IsNullOrEmpty(text)) return null;

                Dictionary<string, object> map = Json.ParseObjectSafe(text);
                if (map == null) return null;

                Saved saved = new Saved
                {
                    Adapter = Json.GetString(map, "adapter", ""),
                    Automatic = Json.GetBool(map, "automatic", true)
                };

                List<object> servers = Json.GetArray(map, "servers");
                if (servers != null)
                    foreach (object item in servers)
                    {
                        string s = item as string;
                        if (!string.IsNullOrEmpty(s)) saved.Servers.Add(s);
                    }

                return saved.Adapter.Length == 0 ? null : saved;
            }
            catch
            {
                return null;
            }
        }

        private static void Save(Saved saved)
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "adapter", saved.Adapter },
                    { "automatic", saved.Automatic }
                };

                List<object> servers = new List<object>();
                foreach (string s in saved.Servers) servers.Add(s);
                root["servers"] = servers;

                AtomicFile.WriteAllText(StatePath, Json.Stringify(root));
            }
            catch (Exception ex)
            {
                Log.Warn("DNS", "не сохранил состояние sysdns: " + ex.Message);
            }
        }
    }
}