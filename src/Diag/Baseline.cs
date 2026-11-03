using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Базовый замер «что заблокировано» — БЕЗ запущенного обхода.
    ///
    /// Зачем это первым шагом:
    ///   * если youtube и так открывается, не надо его тестировать;
    ///   * если заблокирован только discord, перебирать «всё подряд» бессмысленно;
    ///   * без базовой линии рейтинг стратегий врёт: «5/7 работает» может значить
    ///     «5/7 и так работали».
    ///
    /// Результат сохраняется в data\state\baseline.json и используется
    /// при переборе стратегий: проверяем только то, что реально сломано.
    /// </summary>
    public static class Baseline
    {
        public sealed class Snapshot
        {
            public DateTime At = DateTime.Now;
            public string Network = "";
            public List<string> Blocked = new List<string>();
            public List<string> Working = new List<string>();
            public int OkCount;
            public int Total;

            public string ToJson()
            {
                Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "at", At.ToString("o") },
                    { "network", Network ?? "" },
                    { "okCount", (long)OkCount },
                    { "total", (long)Total }
                };

                List<object> blocked = new List<object>();
                foreach (string s in Blocked) blocked.Add(s);
                root["blocked"] = blocked;

                List<object> working = new List<object>();
                foreach (string s in Working) working.Add(s);
                root["working"] = working;

                return Json.Stringify(root);
            }

            public static Snapshot FromJson(string text)
            {
                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                if (root == null) return null;

                Snapshot s = new Snapshot
                {
                    Network = Json.GetString(root, "network", ""),
                    OkCount = Json.GetInt(root, "okCount", 0),
                    Total = Json.GetInt(root, "total", 0)
                };

                List<object> blocked = Json.GetArray(root, "blocked");
                if (blocked != null)
                    foreach (object o in blocked)
                    {
                        string str = o as string;
                        if (str != null) s.Blocked.Add(str);
                    }

                List<object> working = Json.GetArray(root, "working");
                if (working != null)
                    foreach (object o in working)
                    {
                        string str = o as string;
                        if (str != null) s.Working.Add(str);
                    }

                return s;
            }

            /// <summary>Список целей, которые имеет смысл тестировать (заблокированные, лечащиеся zapret'ом).</summary>
            public List<ProbeTarget> BlockedTargets()
            {
                List<ProbeTarget> all = ConnectivityProbe.ZapretTargets();
                List<ProbeTarget> result = new List<ProbeTarget>();

                foreach (ProbeTarget t in all)
                {
                    // если в базовой линии он в blocked — берём; если её нет — берём все
                    if (Blocked.Count == 0 || Blocked.Contains(t.Name)) result.Add(t);
                }

                return result.Count > 0 ? result : all;
            }

            /// <summary>Заблокированные цели, которые zapret не лечит (нужен прокси/DoH).</summary>
            public List<string> ProxyOnly()
            {
                List<string> result = new List<string>();
                foreach (ProbeTarget t in ConnectivityProbe.DefaultTargets())
                    if (t.Engine == "proxy" && Blocked.Contains(t.Name)) result.Add(t.Name);
                return result;
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Paths.StateDir, "baseline.json"); }
        }

        public static Snapshot Load()
        {
            string text = AtomicFile.ReadAllTextOrNull(FilePath);
            return text == null ? null : Snapshot.FromJson(text);
        }

        public static void Save(Snapshot snapshot)
        {
            if (snapshot == null) return;
            AtomicFile.WriteAllText(FilePath, snapshot.ToJson());
        }

        /// <summary>
        /// Замеряет базовую линию. Требует, чтобы обход НЕ был запущен —
        /// иначе получим не «что блокируют», а «что пробивает текущая стратегия».
        /// </summary>
        public static Snapshot Measure(ConnectivityProbe probe, int timeoutSec, bool verbose)
        {
            Snapshot snapshot = new Snapshot();

            string foreign = WinwsSupervisor.DetectForeignWinws();
            if (foreign != null)
            {
                Log.Warn("DIAG", "базовый замер при работающем winws (" + foreign + ") — результат недостоверен");
                if (verbose) Console.WriteLine("ВНИМАНИЕ: winws уже запущен (" + foreign + "), замер будет неточным.");
            }

            List<ProbeTarget> targets = ConnectivityProbe.DefaultTargets();
            snapshot.Total = targets.Count;

            ProbeReport report = probe.CheckAll(targets, timeoutSec, 4);

            foreach (ProbeResult r in report.Results)
            {
                if (r.Ok) snapshot.Working.Add(r.Name);
                else snapshot.Blocked.Add(r.Name);

                if (verbose)
                    Console.WriteLine("  " + (r.Ok ? "[ok]  " : "[БЛОК]") + " " + r.Name.PadRight(14) + r.ToLine());
            }

            snapshot.OkCount = report.OkCount;
            snapshot.Network = NetworkFingerprint.Hash();

            Save(snapshot);

            Log.Info("DIAG", "базовая линия: " + report.OkCount + "/" + report.Total +
                             ", заблокировано: " + string.Join(", ", snapshot.Blocked.ToArray()));

            return snapshot;
        }
    }

    /// <summary>
    /// Отпечаток сети: чтобы понимать, что рейтинг стратегий относится к конкретной сети,
    /// а не «в среднем по интернету».
    /// </summary>
    public static class NetworkFingerprint
    {
        public static string Hash()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Environment.MachineName).Append('|');

                // шлюз по умолчанию: меняется при смене сети (дом/мобильный)
                string gateway = DefaultGateway();
                sb.Append(gateway).Append('|');

                // DNS-суффикс
                try { sb.Append(IPGlobalPropertiesHostName()); } catch { }

                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    StringBuilder hex = new StringBuilder(16);
                    for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    return hex.ToString();
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private static string IPGlobalPropertiesHostName()
        {
            try { return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName ?? ""; }
            catch { return ""; }
        }

        private static string DefaultGateway()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation gw in
                             ni.GetIPProperties().GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.ToString() != "0.0.0.0")
                            return gw.Address.ToString();
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
