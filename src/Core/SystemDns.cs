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
        /// Есть ли запасной сервер рядом с нашим 127.0.0.1.
        ///
        /// Проверяем по факту, а не по нашим записям: адрес мог не добавиться
        /// (netsh отказал), и тогда «запасной есть» было бы враньём в самый
        /// неподходящий момент.
        /// </summary>
        public static bool HasFallback()
        {
            return FallbackList().Count > 0;
        }

        /// <summary>
        /// Запасные серверы, реально прописанные у адаптера сейчас (без нашего
        /// 127.0.0.1). Пустой список — запасного нет, и падение программы
        /// оставит систему без DNS.
        ///
        /// Читаем с адаптера, а не из нашего state-файла: адрес мог не
        /// добавиться (netsh отказал), и «запасной есть» было бы враньём
        /// в самый неподходящий момент.
        /// </summary>
        public static List<string> FallbackList()
        {
            List<string> result = new List<string>();

            Saved saved = Load();
            if (saved == null) return result;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, saved.Adapter, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(dns)) continue;

                        string value = dns.ToString();
                        if (!result.Contains(value)) result.Add(value);
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Включить: запомнить текущее и переключить адаптер на 127.0.0.1.
        /// Возвращает null или текст ошибки.
        ///
        /// ВАЖНО: вместе с 127.0.0.1 ставим ВТОРОЙ сервер — запасной.
        ///
        /// Без него получается катастрофа, которая уже случилась: наш резолвер
        /// живёт в этом процессе. Закрыли программу, она упала, порт 53 заняли —
        /// и система остаётся с единственным адресом 127.0.0.1, где никто не
        /// отвечает. Интернета нет вовсе, и «выключить обход» не помогает:
        /// обход тут ни при чём, сломан DNS.
        ///
        /// Запасной адрес — прежний сервер пользователя (он и так им пользовался),
        /// а если адрес выдавался по DHCP и мы его не знаем — публичный 1.1.1.1.
        /// Windows переключится на него, если первый не ответил за секунду.
        /// Запросы при этом не «утекают»: второй сервер спрашивается только
        /// когда первый молчит, то есть когда выбора всё равно нет.
        /// </summary>
        public static string Enable()
        {
            string adapter = DefaultRouteAdapter(out string localIp);

            if (adapter == null)
                return "не нашёл адаптер с интернетом (нет маршрута по умолчанию)";

            Saved saved = new Saved { Adapter = adapter };
            List<string> current = DnsServers(adapter);

            saved.Automatic = IsAutomatic(adapter);
            saved.Servers = current;

            Save(saved);

            string error = Netsh("interface ipv4 set dnsservers name=\"" + adapter + "\" static 127.0.0.1 primary");

            if (error != null)
            {
                try { File.Delete(StatePath); } catch { }
                return error;
            }

            // Запасные серверы: молчание нашего резолвера не должно оставлять
            // человека без DNS. Ошибку не считаем фатальной — основной адрес
            // уже стоит, а без запасных будет просто «как раньше».
            List<string> fallbacks = FallbackServers(saved, adapter);

            for (int i = 0; i < fallbacks.Count; i++)
            {
                // index=2, 3, ... — сразу после нашего 127.0.0.1
                string fallbackError = Netsh("interface ipv4 add dnsservers name=\"" + adapter +
                                             "\" address=" + fallbacks[i] + " index=" + (i + 2));

                if (fallbackError != null)
                {
                    Log.Warn("DNS", "запасной DNS " + fallbacks[i] + " не добавлен: " + fallbackError);
                    break;      // дальше добавлять бессмысленно: netsh уже не слушается
                }
            }

            Log.Info("DNS", "системный DNS → 127.0.0.1 (+ запасные " +
                            string.Join(", ", fallbacks.ToArray()) + ") на адаптере «" + adapter +
                            "» (было: " + (saved.Automatic ? "автоматически" :
                                string.Join(", ", saved.Servers.ToArray())) + ")");

            return null;
        }

        /// <summary>
        /// Набор запасных DNS-серверов рядом с нашим 127.0.0.1.
        ///
        /// Порядок — от «самого родного» к «самому универсальному»:
        ///   1. прежние серверы пользователя (они заведомо работали в этой сети);
        ///   2. шлюз по умолчанию — на домашних роутерах он почти всегда и есть
        ///      DNS-сервер; если нет — Windows просто пойдёт к следующему;
        ///   3. публичный 77.88.8.8 (Яндекс) — он доступен там, где 1.1.1.1
        ///      уже подменён или заблокирован; это последний рубеж.
        ///
        /// Проверять доступность заранее не нужно и вредно: это лишние запросы
        /// при каждом включении, а Windows сам перебирает адреса, пока кто-то
        /// не ответит. Наша задача — чтобы список не был пуст.
        ///
        /// Почему не 1.1.1.1 по умолчанию: в российских сетях он часто сам
        /// попадает под подмену, и «запасной», который не работает, — это
        /// отсутствие запасного.
        /// </summary>
        private static List<string> FallbackServers(Saved saved, string adapter)
        {
            List<string> result = new List<string>();

            if (saved != null && saved.Servers != null)
            {
                foreach (string server in saved.Servers)
                {
                    if (string.IsNullOrEmpty(server)) continue;

                    // TryParse, а не Parse: в state-файле мог оказаться мусор
                    // после ручной правки, а падать на этом нельзя.
                    IPAddress parsed;
                    if (!IPAddress.TryParse(server, out parsed)) continue;
                    if (IPAddress.IsLoopback(parsed)) continue;

                    if (!result.Contains(server)) result.Add(server);
                }
            }

            string gateway = DefaultGateway(adapter);

            if (gateway.Length > 0 && !result.Contains(gateway)) result.Add(gateway);

            if (!result.Contains("77.88.8.8")) result.Add("77.88.8.8");

            return result;
        }

        /// <summary>
        /// Система смотрит на 127.0.0.1, а нашего состояния нет.
        ///
        /// Это ровно сломанное состояние с живого ПК: единственный DNS —
        /// 127.0.0.1, второй пуст, программы, которая его обслуживает, нет.
        /// Показываем красным на карточке DNS и чиним кнопкой «Вернуть DNS».
        /// </summary>
        public static bool IsBrokenWithoutState(out string adapter, out string current)
        {
            adapter = "";
            current = "";

            if (IsEnabled) return false;      // есть состояние — это нормальный режим

            string found = DefaultRouteAdapter(out _);
            if (found == null) return false;

            bool pointsToUs = false;
            List<string> servers = new List<string>();

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, found, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;

                        servers.Add(dns.ToString());
                        if (IPAddress.IsLoopback(dns)) pointsToUs = true;
                    }
                }
            }
            catch { }

            if (!pointsToUs) return false;

            adapter = found;
            current = string.Join(", ", servers.ToArray());

            return true;
        }

        /// <summary>Шлюз по умолчанию адаптера — обычно это и есть DNS роутера.</summary>
        public static string DefaultGateway(string adapter)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, adapter, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (GatewayIPAddressInformation gw in ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address == null) continue;
                        if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(gw.Address)) continue;

                        return gw.Address.ToString();
                    }
                }
            }
            catch { }

            return "";
        }

        /// <summary>Вернуть: автоматически или прежние серверы.</summary>
        public static string Disable()
        {
            Saved saved = Load();

            if (saved == null)
            {
                // Состояния нет — но система всё равно может смотреть на
                // 127.0.0.1: файл потеряли, чистили data, или DNS сломали
                // прошлой версией программы (именно так и случилось вживую:
                // единственный адрес 127.0.0.1, второй пуст, интернета нет).
                //
                // «Вернуть DNS» обязана чинить и это, иначе кнопка бесполезна
                // ровно в той ситуации, ради которой её нажимают.
                return RepairIfPointingAtUs();
            }

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

        /// <summary>
        /// Ремонт сломанного состояния без нашего state-файла.
        ///
        /// Признак поломки: у адаптера с интернетом DNS — это ТОЛЬКО 127.0.0.1
        /// (или 127.0.0.1 + что-то, что не отвечает). Тогда единственный
        /// разумный шаг — вернуть адаптер на автоматический DNS: он почти
        /// всегда правильный, а руками прописанные серверы восстановить всё
        /// равно неоткуда.
        /// </summary>
        private static string RepairIfPointingAtUs()
        {
            string adapter = DefaultRouteAdapter(out _);

            if (adapter == null) return null;

            List<string> current = DnsServers(adapter);

            // нас интересует именно «смотрит на loopback»
            bool pointsToUs = false;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, adapter, StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (IPAddress dns in ni.GetIPProperties().DnsAddresses)
                    {
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(dns)) pointsToUs = true;
                    }
                }
            }
            catch { }

            if (!pointsToUs) return null;

            Log.Warn("DNS", "у адаптера «" + adapter + "» DNS указывает на 127.0.0.1 без нашего состояния — " +
                            "возвращаю автоматический (было: " +
                            (current.Count == 0 ? "пусто" : string.Join(", ", current.ToArray())) + ")");

            string error = Netsh("interface ipv4 set dnsservers name=\"" + adapter + "\" dhcp");

            if (error != null) return error;

            Log.Info("DNS", "DNS адаптера «" + adapter + "» возвращён на автоматический");

            return null;
        }

        /// <summary>Адрес DNS-серверов адаптера (включая выданные по DHCP).</summary>
        public static List<string> DnsServers(string adapter)
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
        /// DNS адаптера выдан по DHCP (а не прописан руками).
        ///
        /// Нужно ровно для «Вернуть»: раньше мы считали «автоматически» по
        /// пустому списку адресов, а .NET отдаёт и DHCP-адреса. В итоге после
        /// «Вернуть DNS» человек оставался со статическими серверами навсегда,
        /// хотя до нас у него был DHCP.
        ///
        /// Признак ищем в выводе netsh: «DHCP» — латиница и в русской, и в
        /// английской версии Windows, поэтому ищем именно её.
        /// </summary>
        public static bool IsAutomatic(string adapter)
        {
            string output = NetshOutput("interface ipv4 show dnsservers name=\"" + adapter + "\"");

            if (output.Length == 0) return false;

            return output.IndexOf("dhcp", StringComparison.OrdinalIgnoreCase) >= 0;
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

        /// <summary>
        /// Вывод netsh без проверки кода возврата. Нужен для «показать», а не
        /// «изменить»: netsh show возвращает 1, когда список пуст, и это норма.
        /// </summary>
        private static string NetshOutput(string arguments)
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

                    return output ?? "";
                }
            }
            catch (Exception ex)
            {
                Log.Debug("DNS", "netsh show: " + ex.Message);
                return "";
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