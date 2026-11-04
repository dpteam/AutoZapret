using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;
using Microsoft.Win32;

namespace AutoZapret.Diag
{
    public enum CheckStatus
    {
        Ok,
        Warn,
        Fail,
        Skip
    }

    public sealed class CheckResult
    {
        public string Name = "";
        public CheckStatus Status = CheckStatus.Skip;
        public string Detail = "";
        public string Advice = "";

        public string StatusText()
        {
            switch (Status)
            {
                case CheckStatus.Ok: return "OK";
                case CheckStatus.Warn: return "ВНИМАНИЕ";
                case CheckStatus.Fail: return "ПРОБЛЕМА";
                default: return "пропущено";
            }
        }
    }

    /// <summary>
    /// Проверки окружения: почему обход не работает.
    ///
    /// Все внешние команды запускаются как `cmd /c chcp 437 >nul &amp; &lt;команда&gt;`
    /// и декодируются как CP437. Причина: `netsh`, `sc` и `tasklist` ЛОКАЛИЗОВАНЫ,
    /// и на русской Windows вывод выглядит как «Метки времени: включены».
    /// Парсить это без chcp 437 невозможно — ломается на каждой локали по-своему.
    ///
    /// Список проверок взят из практики: 90% обращений «не работает» закрываются
    /// первыми шестью пунктами.
    /// </summary>
    public static class SystemChecks
    {
        public static List<CheckResult> RunAll(Action<string> progress = null)
        {
            List<CheckResult> results = new List<CheckResult>();

            results.Add(Safe("BFE (Base Filtering Engine)", CheckBfe, progress));
            results.Add(Safe("Служба WinDivert", CheckWinDivertService, progress));
            results.Add(Safe("Файлы драйвера", CheckDriverFiles, progress));
            results.Add(Safe("TCP timestamps", CheckTcpTimestamps, progress));
            results.Add(Safe("Системный прокси", CheckSystemProxy, progress));
            results.Add(Safe("Свой hosts", CheckHosts, progress));
            results.Add(Safe("Конфликтующие обходчики", CheckCompetitors, progress));
            results.Add(Safe("VPN и туннели", CheckVpn, progress));
            results.Add(Safe("Антивирус-перехватчики", CheckAntivirus, progress));
            results.Add(Safe("Права администратора", CheckAdmin, progress));
            results.Add(Safe("Свободное место", CheckDiskSpace, progress));
            results.Add(Safe("Время системы", CheckSystemTime, progress));

            return results;
        }

        // ------------------------------------------------------------- checks

        /// <summary>
        /// BFE обязательна для WinDivert: без неё драйвер не может фильтровать.
        /// Отключённая BFE = «winws запускается, но ничего не делает».
        /// </summary>
        private static CheckResult CheckBfe()
        {
            CheckResult r = new CheckResult { Name = "BFE (Base Filtering Engine)" };

            string output = Cmd("sc query BFE");
            if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "работает";
            }
            else if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Status = CheckStatus.Fail;
                r.Detail = "остановлена";
                r.Advice = "WinDivert не может фильтровать без BFE. Запустите: sc start BFE";
            }
            else
            {
                r.Status = CheckStatus.Warn;
                r.Detail = FirstLine(output);
                r.Advice = "Не удалось определить состояние. Проверьте вручную: services.msc";
            }

            return r;
        }

        private static CheckResult CheckWinDivertService()
        {
            CheckResult r = new CheckResult { Name = "Служба WinDivert" };

            string output = Cmd("sc query WinDivert");

            if (output.IndexOf("не найдена", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("FAILED 1060", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "не установлена (нормально, если обход не запущен)";
                return r;
            }

            if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "работает";
                return r;
            }

            if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "остановлена";
                r.Advice = "Драйвер выгружен. Если обход не запускается — «освободите» его в настройках.";
                return r;
            }

            r.Status = CheckStatus.Ok;
            r.Detail = FirstLine(output);
            return r;
        }

        private static CheckResult CheckDriverFiles()
        {
            CheckResult r = new CheckResult { Name = "Файлы драйвера" };

            string bin = Paths.EngineBinDir;
            string[] required = { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" };

            List<string> missing = new List<string>();
            foreach (string name in required)
            {
                if (!File.Exists(Path.Combine(bin, name))) missing.Add(name);
            }

            if (missing.Count == 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "все на месте";
            }
            else if (missing.Count == required.Length)
            {
                r.Status = CheckStatus.Skip;
                r.Detail = "движок не установлен";
                r.Advice = "Запустите tools\\fetch-engine.ps1";
            }
            else
            {
                r.Status = CheckStatus.Fail;
                r.Detail = "нет: " + string.Join(", ", missing.ToArray());
                r.Advice = "Антивирус мог удалить файлы. Перекачайте: tools\\fetch-engine.ps1 -Force";
            }

            return r;
        }

        /// <summary>
        /// timestamps нужны для fooling=ts. По умолчанию в Windows они ВЫКЛЮЧЕНЫ,
        /// и стратегии с ts просто не работают — а выглядят рабочими.
        /// </summary>
        private static CheckResult CheckTcpTimestamps()
        {
            CheckResult r = new CheckResult { Name = "TCP timestamps" };

            string output = Cmd("netsh interface tcp show global");

            // на английской локали: "RFC 1323 Timestamps : enabled"
            Match match = Regex.Match(output, @"Timestamps[^:]*:\s*(\w+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "не удалось определить";
                r.Advice = "Проверьте: netsh interface tcp show global";
                return r;
            }

            string value = match.Groups[1].Value.ToLowerInvariant();

            if (value.StartsWith("enabled") || value.StartsWith("allow") || value.StartsWith("yes"))
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "включены";
            }
            else
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "выключены";
                r.Advice = "Стратегии с fooling=ts не будут работать. Включите: " +
                           "netsh interface tcp set global timestamps=enabled (нужен админ)";
            }

            return r;
        }

        private static CheckResult CheckSystemProxy()
        {
            CheckResult r = new CheckResult { Name = "Системный прокси" };

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null)
                    {
                        r.Status = CheckStatus.Ok;
                        r.Detail = "не настроен";
                        return r;
                    }

                    object enabled = key.GetValue("ProxyEnable");
                    bool on = enabled != null && Convert.ToInt32(enabled, CultureInfo.InvariantCulture) != 0;

                    if (!on)
                    {
                        r.Status = CheckStatus.Ok;
                        r.Detail = "выключен";
                        return r;
                    }

                    string server = key.GetValue("ProxyServer") as string;
                    r.Status = CheckStatus.Warn;
                    r.Detail = "включён: " + (server ?? "?");
                    r.Advice = "Системный прокси может перехватывать трафик раньше zapret. " +
                               "Если что-то не работает — отключите его.";
                }
            }
            catch (Exception ex)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = ex.Message;
            }

            return r;
        }

        private static CheckResult CheckHosts()
        {
            CheckResult r = new CheckResult { Name = "Свой hosts" };

            string text = Lists.HostsFile.ReadSystem();
            if (text == null)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "не удалось прочитать";
                return r;
            }

            int blockLines;
            bool hasBlock = Lists.HostsFile.HasBlock(text, out blockLines);

            // записи, которые ломают популярные сайты
            List<string> suspicious = new List<string>();
            foreach (string host in new[] { "youtube.com", "www.youtube.com", "discord.com", "googlevideo.com" })
            {
                if (Regex.IsMatch(text, @"^\s*[\d\.:]+\s+.*\b" + Regex.Escape(host) + @"\b",
                                  RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    suspicious.Add(host);
            }

            if (suspicious.Count > 0)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "перенаправлены: " + string.Join(", ", suspicious.ToArray());
                r.Advice = "Записи в hosts могут мешать. Если сайт не открывается — " +
                           "вкладка «Списки» → «Восстановить hosts».";
                return r;
            }

            r.Status = CheckStatus.Ok;
            r.Detail = hasBlock ? "блок AutoZapret: " + blockLines + " строк" : "без изменений";
            return r;
        }

        /// <summary>
        /// Чужие обходчики дерутся за WinDivert и порты. Это причина №1
        /// «запустил, а ничего не изменилось».
        /// </summary>
        private static CheckResult CheckCompetitors()
        {
            CheckResult r = new CheckResult { Name = "Конфликтующие обходчики" };

            // имя процесса -> понятное название
            Dictionary<string, string> known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "goodbyedpi", "GoodbyeDPI" },
                { "goodbye-dpi", "GoodbyeDPI" },
                { "byedpi", "ByeDPI" },
                { "spoofdpi", "SpoofDPI" },
                { "dpiblocker", "DPI-Blocker" },
                { "dpi-blocker", "DPI-Blocker" },
                { "powertunnel", "PowerTunnel" },
                { "greentunnel", "Green Tunnel" },
                { "zapret", "zapret (другая сборка)" },
                { "nfqws", "zapret (nfqws)" },
                { "winws", "winws" },
                { "winws2", "winws2" },
                { "tgwsproxy", "tg-ws-proxy" }
            };

            List<string> found = new List<string>();

            foreach (KeyValuePair<string, string> pair in known)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(pair.Key);
                    if (procs.Length > 0) found.Add(pair.Value);
                    foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                }
                catch { }
            }

            if (found.Count == 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "не найдено";
                return r;
            }

            // свои winws/winws2 — это норма, если обход включён через нас
            List<string> foreign = found.FindAll(n =>
                !string.Equals(n, "winws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(n, "winws2", StringComparison.OrdinalIgnoreCase));

            if (foreign.Count == 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "только наш winws";
                return r;
            }

            r.Status = CheckStatus.Fail;
            r.Detail = "запущены: " + string.Join(", ", foreign.ToArray());
            r.Advice = "Остановите их: два обходчика одновременно не работают. " +
                       "Для службы zapret: sc stop zapret";
            return r;
        }

        private static CheckResult CheckVpn()
        {
            CheckResult r = new CheckResult { Name = "VPN и туннели" };

            string[] services = { "WireGuard", "WireGuardTunnel", "OpenVPNService", "OpenVPN", "TAP-Windows" };
            List<string> running = new List<string>();

            foreach (string service in services)
            {
                string output = Cmd("sc query " + service);
                if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                    running.Add(service);
            }

            // процессы, которые тоже заворачивают трафик
            string[] processes = { "openvpn", "wireguard", "hiddify", "sing-box", "mihomo", "xray", "warp-svc", "cloudflarewarp", "tailscale" };
            foreach (string name in processes)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0 && !running.Contains(name)) running.Add(name);
                    foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                }
                catch { }
            }

            if (running.Count == 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "не найдено";
                return r;
            }

            r.Status = CheckStatus.Warn;
            r.Detail = "активны: " + string.Join(", ", running.ToArray());
            r.Advice = "VPN уже шифрует трафик, и DPI его не видит — обход не нужен. " +
                       "Одновременная работа может конфликтовать.";
            return r;
        }

        /// <summary>
        /// Перехватчики трафика помимо антивируса: они стоят между приложением и
        /// сетью и ломают обход так, что winws работает, а результата нет.
        ///
        /// Killer и SmartByte особенно злые: они агрегируют трафик и меняют
        /// маршрутизацию на уровне драйвера. Check Point и Intel Connectivity —
        /// корпоративные фильтры. AdGuard перехватывает DNS.
        ///
        /// Список сервисов, а не только процессов: у службы может не быть
        /// процесса в момент проверки, но она уже держит фильтр в ядре.
        /// </summary>
        private static CheckResult CheckAntivirus()
        {
            CheckResult r = new CheckResult { Name = "Перехватчики трафика" };

            // сервис -> понятное имя
            string[][] services =
            {
                new[] { "AdguardSvc", "AdGuard" },
                new[] { "AdGuard Service", "AdGuard" },
                new[] { "KillerNetworkService", "Killer" },
                new[] { "Killer Network Service", "Killer" },
                new[] { "SmartByteNetworkService", "SmartByte" },
                new[] { "SmartByte Telemetry", "SmartByte" },
                new[] { "IntelConnectivityNetworkService", "Intel Connectivity" },
                new[] { "CheckPointEndpointSecurity", "Check Point" },
                new[] { "CPDA", "Check Point" }
            };

            // процесс -> понятное имя
            string[][] processes =
            {
                new[] { "adguard", "AdGuard" },
                new[] { "adguardSvc", "AdGuard" },
                new[] { "KillerNetworkService", "Killer" },
                new[] { "SmartByteNetworkService", "SmartByte" },
                new[] { "IntelConnectivity", "Intel Connectivity" },
                new[] { "CPDA", "Check Point" }
            };

            List<string> found = new List<string>();

            foreach (string[] pair in services)
            {
                string output = Cmd("sc query \"" + pair[0] + "\"");
                if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddUnique(found, pair[1]);
            }

            foreach (string[] pair in processes)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(pair[0]);
                    if (procs.Length > 0) AddUnique(found, pair[1]);
                    foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                }
                catch { }
            }

            if (found.Count == 0)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "не найдено";
                return r;
            }

            r.Status = CheckStatus.Warn;
            r.Detail = "запущен: " + string.Join(", ", found.ToArray());
            r.Advice = "Эти программы стоят между приложением и сетью и ломают обход. " +
                       "Если что-то не работает — временно отключите их и проверьте снова.";
            return r;
        }

        private static void AddUnique(List<string> list, string value)
        {
            foreach (string existing in list)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value);
        }

        private static CheckResult CheckAdmin()
        {
            CheckResult r = new CheckResult { Name = "Права администратора" };

            if (Core.AppInfo.IsAdmin)
            {
                r.Status = CheckStatus.Ok;
                r.Detail = "есть";
            }
            else
            {
                r.Status = CheckStatus.Warn;
                r.Detail = "нет";
                r.Advice = "Для запуска обхода права запрашиваются отдельно (UAC). " +
                           "Настройки и диагностика работают и без них.";
            }

            return r;
        }

        private static CheckResult CheckDiskSpace()
        {
            CheckResult r = new CheckResult { Name = "Свободное место" };

            try
            {
                string root = Path.GetPathRoot(Paths.BaseDir);
                DriveInfo drive = new DriveInfo(root);

                long freeMb = drive.AvailableFreeSpace / (1024 * 1024);

                if (freeMb < 100)
                {
                    r.Status = CheckStatus.Fail;
                    r.Detail = freeMb + " МБ";
                    r.Advice = "Мало места: обновления и списки не смогут распаковаться.";
                }
                else if (freeMb < 500)
                {
                    r.Status = CheckStatus.Warn;
                    r.Detail = freeMb + " МБ";
                }
                else
                {
                    r.Status = CheckStatus.Ok;
                    r.Detail = freeMb + " МБ";
                }
            }
            catch (Exception ex)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// Сбитое время ломает TLS: сертификаты выглядят недействительными,
        /// и «ничего не работает» без внятной причины.
        /// </summary>
        private static CheckResult CheckSystemTime()
        {
            CheckResult r = new CheckResult { Name = "Время системы" };

            try
            {
                DateTime now = DateTime.UtcNow;
                DateTime local = DateTime.Now;

                // проверяем, что год вменяемый и время не в будущем относительно сборки
                if (local.Year < 2024)
                {
                    r.Status = CheckStatus.Fail;
                    r.Detail = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    r.Advice = "Время в прошлом: TLS-сертификаты будут недействительны.";
                    return r;
                }

                if (local.Year > 2035)
                {
                    r.Status = CheckStatus.Fail;
                    r.Detail = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    r.Advice = "Время в будущем: TLS-сертификаты будут недействительны.";
                    return r;
                }

                // смещение от UTC должно быть в пределах часовых поясов
                double offsetHours = (local - now).TotalHours;
                if (Math.Abs(offsetHours) > 14)
                {
                    r.Status = CheckStatus.Warn;
                    r.Detail = "смещение от UTC: " + offsetHours.ToString("0.0", CultureInfo.InvariantCulture) + " ч";
                    r.Advice = "Странное смещение. Проверьте часовой пояс и синхронизацию времени.";
                    return r;
                }

                r.Status = CheckStatus.Ok;
                r.Detail = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                           " (UTC" + (offsetHours >= 0 ? "+" : "") +
                           offsetHours.ToString("0", CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception ex)
            {
                r.Status = CheckStatus.Warn;
                r.Detail = ex.Message;
            }

            return r;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>
        /// Запуск внешней команды с принудительной английской кодировкой.
        ///
        /// `chcp 437` — ключевой трюк: без него `netsh` и `sc` отвечают на языке
        /// системы, и любой парсинг ломается. CP437 понимает Encoding без
        /// CodePagesEncodingProvider, который в .NET Core требует NuGet.
        /// </summary>
        public static string Cmd(string command)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c chcp 437 >nul & " + command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "";

                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    return stdout + (stderr.Length > 0 ? "\n" + stderr : "");
                }
            }
            catch (Exception ex)
            {
                return "ошибка: " + ex.Message;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 100 ? t.Substring(0, 97) + "…" : t;
            }

            return "";
        }

        /// <summary>Обёртка: одна упавшая проверка не должна ломать весь список.</summary>
        private static CheckResult Safe(string name, Func<CheckResult> check, Action<string> progress)
        {
            if (progress != null) { try { progress(name); } catch { } }

            try
            {
                CheckResult result = check();
                if (result == null) return new CheckResult { Name = name, Status = CheckStatus.Skip };
                return result;
            }
            catch (Exception ex)
            {
                return new CheckResult
                {
                    Name = name,
                    Status = CheckStatus.Warn,
                    Detail = ex.GetType().Name + ": " + ex.Message
                };
            }
        }
    }
}