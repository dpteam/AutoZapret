using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Update
{
    /// <summary>
    /// Самообновление приложения.
    ///
    /// Заменять запущенный exe нельзя — Windows не даст. Поэтому схема такая:
    ///   1) скачать новый архив во временный каталог;
    ///   2) сгенерировать .bat, который ждёт завершения нашего PID,
    ///      копирует файлы и запускает новую версию;
    ///   3) запустить .bat отдельно (без окна) и выйти.
    ///
    /// GitHub API без токена даёт 60 запросов в час на IP, поэтому при 403
    /// переключаемся на Atom-ленту релизов — она без лимитов.
    /// </summary>
    public sealed class SelfUpdater
    {
        public const string Owner = "DPT";
        public const string Repo = "AutoZapret";

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        private static DateTime _lastCheck = DateTime.MinValue;
        private static UpdateCheck _lastResult;

        public event Action<string> Progress;

        /// <summary>Проверяет наличие новой версии (с кэшем на час).</summary>
        public UpdateCheck Check(bool force = false)
        {
            if (!force && _lastResult != null && DateTime.UtcNow - _lastCheck < CacheTtl)
                return _lastResult;

            UpdateCheck check = new UpdateCheck { CurrentVersion = AppInfo.Version };

            Report("проверяю обновления приложения…");

            string tag = null;

            // 1) GitHub API
            tag = TryApi();

            // 2) Atom-лента как резерв (API отдаёт 403 при исчерпании лимита)
            if (tag == null)
            {
                Report("API недоступен, пробую ленту релизов…");
                tag = TryAtom();
            }

            if (tag == null)
            {
                check.Message = "не удалось проверить (нет сети или репозиторий недоступен)";
                _lastResult = check;
                _lastCheck = DateTime.UtcNow;
                return check;
            }

            check.Ok = true;
            check.LatestVersion = tag;
            check.Url = "https://github.com/" + Owner + "/" + Repo + "/releases/latest";
            check.Message = check.HasUpdate
                ? "доступна версия " + tag + " (сейчас " + AppInfo.Version + ")"
                : "установлена последняя версия";

            _lastResult = check;
            _lastCheck = DateTime.UtcNow;

            Log.Info("UPD", "приложение: " + check.Message);
            return check;
        }

        private static string TryApi()
        {
            try
            {
                string url = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Accept = "application/vnd.github+json";
                request.Timeout = 10000;

                string token = Environment.GetEnvironmentVariable("GH_TOKEN");
                if (string.IsNullOrEmpty(token)) token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(token)) request.Headers.Add("Authorization", "Bearer " + token);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    Dictionary<string, object> root = Json.ParseObjectSafe(json);
                    if (root == null) return null;

                    string tag = Json.GetString(root, "tag_name", "");
                    return SanitizeTag(tag);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    Log.Debug("UPD", "API вернул " + (int)response.StatusCode);
                    response.Dispose();
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Debug("UPD", "API: " + ex.Message);
                return null;
            }
        }

        private static string TryAtom()
        {
            try
            {
                string url = "https://github.com/" + Owner + "/" + Repo + "/releases.atom";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 10000;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string xml = reader.ReadToEnd();

                    // первый <entry><id>tag:github.com,2008:Repository/123/v1.4.1</id>
                    Match match = Regex.Match(xml, @"<id>tag:github\.com[^<]*?/(v?[\d][^<]*)</id>");
                    if (!match.Success) return null;

                    return SanitizeTag(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("UPD", "atom: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Отбраковывает форки, пре-релизы и мусорные теги.
        /// Иначе «v1.2.3-custom-fork» станет «обновлением».
        /// </summary>
        private static string SanitizeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;

            string value = tag.Trim();

            // пре-релизы и форки
            if (value.Contains("-")) return null;
            if (value.IndexOf("fork", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (value.IndexOf("custom", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (value.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            string digits = value.TrimStart('v', 'V');
            if (digits.Length == 0) return null;
            if (!char.IsDigit(digits[0])) return null;

            return digits;
        }

        // ---------------------------------------------------------------- install

        /// <summary>
        /// Скачивает и применяет обновление. Возвращает null при успехе.
        /// После успеха приложение должно выйти — заменщик сам запустит новую версию.
        /// </summary>
        public string Install(string version, string zipUrl)
        {
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "az-app-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(temp);

                string archive = Path.Combine(temp, "update.zip");

                Report("скачиваю " + version + "…");
                string downloadError = DownloadFile(zipUrl, archive);
                if (downloadError != null) return downloadError;

                long size = new FileInfo(archive).Length;
                if (size < 100 * 1024)
                    return "архив подозрительно мал (" + size + " байт)";

                // ---- проверка целостности. ОБЯЗАТЕЛЬНА, без «нет хеша — пропускаем»:
                //      иначе достаточно не отдать checksums.txt, и защиты нет.
                Report("проверяю SHA-256…");
                string integrityError = VerifyArchive(archive, version);
                if (integrityError != null) return integrityError;

                Report("распаковываю…");
                string extract = Path.Combine(temp, "new");
                Directory.CreateDirectory(extract);

                string unzipError = ExtractZip(archive, extract);
                if (unzipError != null) return unzipError;

                if (!File.Exists(Path.Combine(extract, "AutoZapret.exe")))
                    return "в архиве нет AutoZapret.exe";

                Report("готовлю замену…");
                string batPath = WriteReplacer(temp, extract);
                if (batPath == null) return "не удалось создать скрипт замены";

                Report("перезапускаюсь…");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + batPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                Log.Info("UPD", "обновление до " + version + " запущено, выходим");
                return null;
            }
            catch (Exception ex)
            {
                Log.Exception("UPD", "установка приложения", ex);
                return ex.Message;
            }
        }

        /// <summary>
        /// Скрипт-заменщик. Ждёт нашего PID, потом копирует.
        /// Без ожидания Windows не даст перезаписать запущенный exe.
        /// </summary>
        private static string WriteReplacer(string tempDir, string extractDir)
        {
            try
            {
                string targetDir = Paths.BaseDir.TrimEnd('\\');
                int pid = Process.GetCurrentProcess().Id;
                string exePath = Application_ExecutablePath();

                string batPath = Path.Combine(tempDir, "replace.bat");

                StringBuilder bat = new StringBuilder();
                bat.AppendLine("@echo off");
                bat.AppendLine("chcp 65001 >nul");
                bat.AppendLine("setlocal");
                bat.AppendLine("title AutoZapret update");
                bat.AppendLine();
                bat.AppendLine("rem ждём, пока наш процесс завершится");
                bat.AppendLine(":wait");
                bat.AppendLine("tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul");
                bat.AppendLine("if not errorlevel 1 (");
                bat.AppendLine("  ping -n 2 127.0.0.1 >nul");
                bat.AppendLine("  goto wait");
                bat.AppendLine(")");
                bat.AppendLine();
                bat.AppendLine("rem на всякий случай гасим движок");
                bat.AppendLine("taskkill /IM winws.exe /F >nul 2>&1");
                bat.AppendLine("ping -n 2 127.0.0.1 >nul");
                bat.AppendLine();
                bat.AppendLine("rem копируем поверх");
                bat.AppendLine("xcopy \"" + extractDir + "\\*\" \"" + targetDir + "\\\" /E /Y /I /Q >nul");
                bat.AppendLine();
                bat.AppendLine("rem запускаем новую версию");
                bat.AppendLine("start \"\" \"" + exePath + "\"");
                bat.AppendLine();
                bat.AppendLine("rem убираем за собой");
                bat.AppendLine("del \"%~f0\"");

                File.WriteAllText(batPath, bat.ToString(), new UTF8Encoding(false));
                return batPath;
            }
            catch (Exception ex)
            {
                Log.Error("UPD", "скрипт замены: " + ex.Message);
                return null;
            }
        }

        private static string Application_ExecutablePath()
        {
            try { return System.Windows.Forms.Application.ExecutablePath; }
            catch { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        /// <summary>
        /// Скачивает checksums.txt релиза и сверяет хеш архива.
        /// Возвращает null при успехе или текстовую причину отказа.
        /// </summary>
        private string VerifyArchive(string archive, string version)
        {
            string checksumsPath = Path.Combine(Path.GetDirectoryName(archive), "checksums.txt");

            string downloadError = DownloadFile(ChecksumsUrl(version), checksumsPath);
            if (downloadError != null)
            {
                Log.Warn("UPD", "checksums.txt недоступен: " + downloadError);
                return "не удалось скачать контрольные суммы релиза — обновление отменено";
            }

            Dictionary<string, string> map;

            try
            {
                map = Checksums.Parse(File.ReadAllText(checksumsPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Warn("UPD", "checksums.txt не разобран: " + ex.Message);
                return "файл контрольных сумм повреждён — обновление отменено";
            }

            if (map.Count == 0)
                return "в контрольных суммах нет ни одной записи — обновление отменено";

            ChecksumResult result = Checksums.Verify(archive, map, PortableZipName);

            if (!result.Ok)
            {
                Log.Error("UPD", "проверка SHA-256 не прошла: " + result.Text());
                return "проверка целостности не прошла: " + result.Text();
            }

            Log.Info("UPD", "SHA-256 архива совпал: " + result.Actual.Substring(0, 16) + "…");
            return null;
        }

        private static string DownloadFile(string url, string target)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "AutoZapret/" + AppInfo.Version);
                    client.DownloadFile(url, target);
                }
                return null;
            }
            catch (Exception ex)
            {
                return "скачивание: " + ex.Message;
            }
        }

        private static string ExtractZip(string archive, string target)
        {
            try
            {
                using (System.IO.Compression.ZipArchive zip =
                       System.IO.Compression.ZipFile.OpenRead(archive))
                {
                    foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // это каталог

                        string dest = Path.Combine(target, entry.FullName.Replace('/', '\\'));
                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                        try { entry.ExtractToFile(dest, true); }
                        catch (Exception ex) { Log.Warn("UPD", "распаковка " + entry.Name + ": " + ex.Message); }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return "распаковка: " + ex.Message;
            }
        }

        /// <summary>
        /// Ссылка на portable-архив релиза. Имя фиксировано в release.yml.
        /// </summary>
        public static string PortableZipUrl(string version)
        {
            return "https://github.com/" + Owner + "/" + Repo + "/releases/download/v" + version +
                   "/AutoZapret-portable.zip";
        }

        /// <summary>Имя архива в релизе. Нужно, чтобы найти его хеш в checksums.txt.</summary>
        public const string PortableZipName = "AutoZapret-portable.zip";

        /// <summary>Ссылка на файл контрольных сумм релиза.</summary>
        public static string ChecksumsUrl(string version)
        {
            return "https://github.com/" + Owner + "/" + Repo + "/releases/download/v" + version +
                   "/checksums.txt";
        }

        private void Report(string message)
        {
            Action<string> handler = Progress;
            if (handler != null) { try { handler(message); } catch { } }
        }
    }
}
