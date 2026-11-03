using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Update
{
    public sealed class UpdateCheck
    {
        public bool Ok;
        public string CurrentVersion = "";
        public string LatestVersion = "";
        public string Message = "";
        public string Url = "";

        public bool HasUpdate
        {
            get
            {
                if (string.IsNullOrEmpty(LatestVersion) || string.IsNullOrEmpty(CurrentVersion)) return false;
                return CompareVersions(LatestVersion, CurrentVersion) > 0;
            }
        }

        /// <summary>Сравнение версий вида «1.10.4» / «1.9» / «v1.2.3».</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] pa = ParseParts(a);
            int[] pb = ParseParts(b);

            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va > vb ? 1 : -1;
            }
            return 0;
        }

        private static int[] ParseParts(string version)
        {
            if (string.IsNullOrEmpty(version)) return new int[0];

            string v = version.Trim().TrimStart('v', 'V');

            // отрезаем суффикс после дефиса/плюса: 1.2.3-beta -> 1.2.3
            int cut = v.IndexOfAny(new[] { '-', '+' });
            if (cut > 0) v = v.Substring(0, cut);

            string[] chunks = v.Split('.');
            List<int> parts = new List<int>(chunks.Length);

            foreach (string chunk in chunks)
            {
                // «10b» -> 10
                int end = 0;
                while (end < chunk.Length && char.IsDigit(chunk[end])) end++;

                int value;
                if (end > 0 && int.TryParse(chunk.Substring(0, end), NumberStyles.Integer,
                                            CultureInfo.InvariantCulture, out value))
                    parts.Add(value);
                else
                    parts.Add(0);
            }

            return parts.ToArray();
        }
    }

    /// <summary>
    /// Обновление движка zapret (winws + списки + фейки).
    ///
    /// Порядок важен и выстрадан (см. AGENTS.md):
    ///   1) скачать во временный каталог и проверить, что это действительно архив;
    ///   2) остановить обход и ДОЖДАТЬСЯ выгрузки WinDivert (иначе файлы заняты);
    ///   3) сохранить резервную копию;
    ///   4) применить, НЕ перезаписывая пользовательские списки;
    ///   5) при любой ошибке — откатить.
    ///
    /// Источник — git clone, а не zip: антивирус рвёт скачивание архива целиком,
    /// потому что внутри WinDivert64.sys.
    /// </summary>
    public sealed class EngineUpdater
    {
        public const string VersionUrl =
            "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt";

        private readonly string _engineDir;
        private readonly string _backupDir;

        public EngineUpdater()
        {
            _engineDir = Paths.EngineDir;
            _backupDir = Path.Combine(Paths.DataDir, "engine-backup");
        }

        public event Action<string> Progress;

        /// <summary>Узнаёт последнюю версию в апстриме. Ничего не меняет.</summary>
        public UpdateCheck Check()
        {
            UpdateCheck check = new UpdateCheck();

            string current = ReadLocalVersion();
            check.CurrentVersion = current;

            Report("проверяю версию в апстриме…");

            string latest = Download(VersionUrl);
            if (latest == null)
            {
                check.Message = "не удалось получить версию (нет сети?)";
                Log.Warn("UPD", check.Message);
                return check;
            }

            latest = latest.Trim();
            if (latest.Length == 0 || latest.Length > 40)
            {
                check.Message = "апстрим вернул странную версию: " + latest;
                Log.Warn("UPD", check.Message);
                return check;
            }

            check.Ok = true;
            check.LatestVersion = latest;
            check.Url = "https://github.com/Flowseal/zapret-discord-youtube";

            check.Message = check.HasUpdate
                ? "доступна версия " + latest + " (сейчас " + (current.Length > 0 ? current : "нет") + ")"
                : "установлена последняя версия";

            Log.Info("UPD", "движок: " + check.Message);
            return check;
        }

        /// <summary>
        /// Скачивает и устанавливает движок. Возвращает null при успехе.
        /// stopEngine — колбэк, который останавливает обход и ждёт освобождения файлов.
        /// </summary>
        public string Install(Func<bool> stopEngine)
        {
            string temp = Path.Combine(Path.GetTempPath(), "az-engine-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Directory.CreateDirectory(temp);

                // 1) скачать
                Report("скачиваю движок…");
                string repoDir = Path.Combine(temp, "src");
                string cloneError = GitClone("https://github.com/Flowseal/zapret-discord-youtube.git",
                                             repoDir, "main");
                if (cloneError != null)
                {
                    return "не удалось скачать: " + cloneError;
                }

                // 2) проверить, что скачалось то, что нужно
                if (!File.Exists(Path.Combine(repoDir, "service.bat")))
                {
                    return "скачанное не похоже на zapret-discord-youtube";
                }

                string newVersion = ReadVersionFrom(repoDir);
                Report("получена версия " + (newVersion.Length > 0 ? newVersion : "?"));

                // 3) остановить обход
                if (stopEngine != null)
                {
                    Report("останавливаю обход…");
                    try { stopEngine(); }
                    catch (Exception ex) { Log.Warn("UPD", "остановка: " + ex.Message); }

                    if (!WaitForWinwsGone(15000))
                        return "winws не завершился — закройте его и повторите";
                }

                // 4) резервная копия
                Report("сохраняю резервную копию…");
                string backupError = Backup();
                if (backupError != null) Log.Warn("UPD", "резервная копия: " + backupError);

                // 5) применить
                Report("применяю обновление…");
                string applyError = Apply(repoDir);
                if (applyError != null)
                {
                    Report("ошибка, откатываю…");
                    Restore();
                    return applyError;
                }

                // 6) записать версию
                if (newVersion.Length > 0)
                {
                    try { File.WriteAllText(Path.Combine(_engineDir, "version.txt"), newVersion); }
                    catch { }
                }

                Report("готово: версия " + (newVersion.Length > 0 ? newVersion : "?"));
                Log.Info("UPD", "движок обновлён до " + newVersion);
                return null;
            }
            catch (Exception ex)
            {
                Log.Exception("UPD", "обновление движка", ex);
                return ex.Message;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Копирует bin/ и lists/ в engine. Пользовательские списки НЕ перезаписываются —
        /// иначе обновление стирает то, что человек настроил руками.
        /// </summary>
        private string Apply(string sourceDir)
        {
            string srcBin = Path.Combine(sourceDir, "bin");
            string srcLists = Path.Combine(sourceDir, "lists");
            string srcUtils = Path.Combine(sourceDir, "utils");

            string dstBin = Path.Combine(_engineDir, "bin");
            string dstLists = Path.Combine(_engineDir, "lists");
            string dstUtils = Path.Combine(_engineDir, "utils");

            Directory.CreateDirectory(dstBin);
            Directory.CreateDirectory(dstLists);
            Directory.CreateDirectory(dstUtils);

            string error = CopyDirectory(srcBin, dstBin, null);
            if (error != null) return "bin: " + error;

            // пользовательские файлы, которые нельзя трогать
            HashSet<string> protectedLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "list-general-user.txt",
                "list-exclude-user.txt",
                "ipset-exclude-user.txt",
                "ipset-all.txt"          // обновляется отдельно, из другого источника
            };

            if (Directory.Exists(srcLists))
            {
                error = CopyDirectory(srcLists, dstLists, protectedLists);
                if (error != null) return "lists: " + error;
            }

            if (Directory.Exists(srcUtils))
            {
                error = CopyDirectory(srcUtils, dstUtils, null);
                if (error != null) return "utils: " + error;
            }

            return null;
        }

        private static string CopyDirectory(string source, string target, HashSet<string> skip)
        {
            if (!Directory.Exists(source)) return null;

            try
            {
                foreach (string file in Directory.GetFiles(source))
                {
                    string name = Path.GetFileName(file);
                    if (skip != null && skip.Contains(name)) continue;

                    string dest = Path.Combine(target, name);
                    if (!CopyWithRetry(file, dest))
                        return "не удалось скопировать " + name;
                }

                foreach (string dir in Directory.GetDirectories(source))
                {
                    string dest = Path.Combine(target, Path.GetFileName(dir));
                    Directory.CreateDirectory(dest);

                    string error = CopyDirectory(dir, dest, null);
                    if (error != null) return error;
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Копирование с ретраями: файлы движка могут быть заняты ещё секунду-две
        /// после остановки процесса (антивирус, индексатор, сам Windows).
        /// </summary>
        private static bool CopyWithRetry(string source, string target, int attempts = 12)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    File.Copy(source, target, true);
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(250);
                }
            }

            // последняя попытка через временный файл: иногда помогает, когда
            // целевой файл заблокирован, а переименование разрешено
            try
            {
                string temp = target + ".upd";
                File.Copy(source, temp, true);
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // --------------------------------------------------------------- backup

        private string Backup()
        {
            try
            {
                if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true);
                Directory.CreateDirectory(_backupDir);

                foreach (string sub in new[] { "bin", "lists", "utils" })
                {
                    string src = Path.Combine(_engineDir, sub);
                    if (!Directory.Exists(src)) continue;

                    string dst = Path.Combine(_backupDir, sub);
                    Directory.CreateDirectory(dst);

                    foreach (string file in Directory.GetFiles(src))
                    {
                        try { File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true); }
                        catch { }
                    }
                }

                string versionFile = Path.Combine(_engineDir, "version.txt");
                if (File.Exists(versionFile))
                    File.Copy(versionFile, Path.Combine(_backupDir, "version.txt"), true);

                Log.Info("UPD", "резервная копия: " + _backupDir);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void Restore()
        {
            try
            {
                if (!Directory.Exists(_backupDir)) return;

                foreach (string sub in new[] { "bin", "lists", "utils" })
                {
                    string src = Path.Combine(_backupDir, sub);
                    if (!Directory.Exists(src)) continue;

                    string dst = Path.Combine(_engineDir, sub);
                    Directory.CreateDirectory(dst);

                    foreach (string file in Directory.GetFiles(src))
                    {
                        try { File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true); }
                        catch { }
                    }
                }

                Log.Info("UPD", "откат выполнен");
            }
            catch (Exception ex)
            {
                Log.Error("UPD", "откат не удался: " + ex.Message);
            }
        }

        // -------------------------------------------------------------- helpers

        private string ReadLocalVersion()
        {
            try
            {
                string path = Path.Combine(_engineDir, "version.txt");
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch { }
            return "";
        }

        private static string ReadVersionFrom(string repoDir)
        {
            try
            {
                string path = Path.Combine(repoDir, ".service", "version.txt");
                if (File.Exists(path)) return File.ReadAllText(path).Trim();

                string service = Path.Combine(repoDir, "service.bat");
                if (File.Exists(service))
                {
                    foreach (string line in File.ReadAllLines(service))
                    {
                        int idx = line.IndexOf("LOCAL_VERSION=", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;

                        string rest = line.Substring(idx + "LOCAL_VERSION=".Length).Trim().Trim('"');
                        int end = rest.IndexOfAny(new[] { ' ', '"' });
                        return end < 0 ? rest : rest.Substring(0, end);
                    }
                }
            }
            catch { }
            return "";
        }

        private static bool WaitForWinwsGone(int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < until)
            {
                if (!WinwsSupervisor.AnyWinwsRunning()) return true;
                Thread.Sleep(300);
            }

            return false;
        }

        private static string GitClone(string url, string target, string branch)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "clone --depth 1 --single-branch --branch " + branch +
                                " --quiet \"" + url + "\" \"" + target + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();

                    if (!p.WaitForExit(180000))
                    {
                        try { p.Kill(); } catch { }
                        return "таймаут git clone";
                    }

                    if (p.ExitCode != 0) return "git clone завершился с кодом " + p.ExitCode;
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string Download(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return null;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("UPD", "скачивание " + url + ": " + ex.Message);
                return null;
            }
        }

        private void Report(string message)
        {
            Action<string> handler = Progress;
            if (handler != null) { try { handler(message); } catch { } }
        }
    }
}
