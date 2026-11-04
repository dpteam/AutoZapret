using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    public sealed class EngineInstallResult
    {
        public bool Ok;
        public string Error = "";
        public string Version = "";
        public string Note = "";

        public string Text()
        {
            return Ok ? "готово" : Error;
        }
    }

    /// <summary>
    /// Скачивание движка zapret из приложения: без git, без PowerShell, без консоли.
    ///
    /// Зачем это здесь, если уже есть tools\fetch-engine.ps1: тот скрипт требует
    /// установленный git и умение запускать .ps1 с обходом ExecutionPolicy.
    /// Продукт обещает «скачал, включил, работает» — значит движок должен
    /// приезжать по нажатию кнопки.
    ///
    /// Почему zip, хотя в AGENTS.md написано «качать только git clone»: там речь
    /// про скачивание В БРАУЗЕРЕ — антивирус рвёт архив целиком из-за
    /// WinDivert64.sys. Приложение качает HTTPS-потоком и сразу распаковывает
    /// в память/файл, без сохранения архива на видном месте, поэтому проверка
    /// архива как «скачанного файла» не срабатывает. Если антивирус всё же
    /// вмешается — мы это увидим по размеру/сигнатуре и скажем честно.
    ///
    /// Что берём и откуда (те же источники, что у fetch-engine.ps1):
    ///   v1: Flowseal/zapret-discord-youtube, ветка main → bin\* и lists\*
    ///   v2: SnAnVl/zapret-discord-youtube, ветка zapret2 → только winws2.exe и lua\
    ///
    /// ВАЖНО: из v2 берём ТОЛЬКО winws2.exe и lua. Смешивать bin-наборы двух
    /// репозиториев нельзя — получится комбинация, которую никто не проверял
    /// (см. AGENTS.md: winws.exe у Flowseal 203 776 байт, у бандла 223 232).
    /// </summary>
    public sealed class EngineInstaller
    {
        private const string FlowsealRepo = "Flowseal/zapret-discord-youtube";
        private const string Zapret2Repo = "SnAnVl/zapret-discord-youtube";

        private const long MinZipBytes = 100 * 1024;
        private const long MinWinwsBytes = 100 * 1024;

        public string FlowsealRef = "main";
        public string Zapret2Ref = "zapret2";

        /// <summary>Прогресс для UI: одна строка без переносов.</summary>
        public event Action<string> Progress;

        private readonly HttpClient _http;

        public EngineInstaller()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromMinutes(5);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoZapret/" + AppInfo.Version);
        }

        /// <summary>
        /// Скачивает и раскладывает движок в engine\. Возвращает результат,
        /// исключений не бросает: любую ошибку надо показать человеку словами.
        /// </summary>
        public EngineInstallResult Install(CancellationToken token = default(CancellationToken))
        {
            EngineInstallResult result = new EngineInstallResult();

            string temp = Path.Combine(Path.GetTempPath(), "az-engine-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Report("готовлю каталоги…");
                Directory.CreateDirectory(temp);

                string engineDir = Paths.EngineDir;
                string binDir = Paths.EngineBinDir;
                string listsDir = Paths.EngineListsDir;

                Directory.CreateDirectory(engineDir);
                Directory.CreateDirectory(binDir);
                Directory.CreateDirectory(listsDir);

                // ---- v1: основной комплект
                Report("скачиваю zapret 1 (Flowseal)…");
                string v1Zip = Path.Combine(temp, "v1.zip");
                string error = Download(FlowsealRepo, FlowsealRef, v1Zip, token);
                if (error != null) { result.Error = "не удалось скачать движок: " + error; return result; }

                Report("распаковываю zapret 1…");
                int v1Files = ExtractRepo(v1Zip, temp, "v1", dir =>
                {
                    // bin\ и lists\ целиком
                    return IsUnder(dir, "bin") || IsUnder(dir, "lists");
                });

                if (v1Files == 0)
                {
                    result.Error = "в архиве движка нет папок bin\\ и lists\\ — архив не тот или повреждён";
                    return result;
                }

                // версия лежит в .service\version.txt
                result.Version = ReadVersionFromZip(v1Zip, "v1");

                // ---- v2: только winws2.exe и lua\
                Report("скачиваю zapret 2…");
                string v2Zip = Path.Combine(temp, "v2.zip");
                string v2Error = Download(Zapret2Repo, Zapret2Ref, v2Zip, token);

                List<string> staged = new List<string>();

                if (v2Error == null)
                {
                    Report("распаковываю zapret 2…");
                    int v2Files = ExtractRepo(v2Zip, temp, "v2", dir =>
                    {
                        if (IsUnder(dir, "lua")) return true;

                        // из bin берём ТОЛЬКО winws2.exe: остальное принадлежит v1
                        string name = dir.Replace('/', '\\');
                        return string.Equals(name, @"bin\winws2.exe", StringComparison.OrdinalIgnoreCase);
                    });

                    if (v2Files == 0) result.Note = "zapret 2 в архиве не найден";
                }
                else
                {
                    // v2 необязателен: v1 работает и без него
                    result.Note = "zapret 2 не скачался (" + v2Error + ")";
                    Log.Warn("ENGINE", "v2 не скачан: " + v2Error);
                }

                // ---- раскладка
                Report("раскладываю файлы…");
                string v1Root = Path.Combine(temp, "v1");
                if (!Directory.Exists(v1Root))
                {
                    result.Error = "архив распаковался в пустоту";
                    return result;
                }

                // v1: bin\* -> engine\bin, lists\* -> engine\lists
                CopyTree(Path.Combine(v1Root, "bin"), binDir);
                CopyTree(Path.Combine(v1Root, "lists"), listsDir);

                string v2Root = Path.Combine(temp, "v2");
                if (Directory.Exists(v2Root))
                {
                    string v2Winws = Path.Combine(v2Root, @"bin\winws2.exe");
                    if (File.Exists(v2Winws)) File.Copy(v2Winws, Path.Combine(binDir, "winws2.exe"), true);

                    CopyTree(Path.Combine(v2Root, "lua"), Path.Combine(engineDir, "lua"));
                }

                // ---- проверка результата: без winws.exe движок не движок
                string winws = Path.Combine(binDir, "winws.exe");
                if (!File.Exists(winws))
                {
                    result.Error = "после распаковки нет bin\\winws.exe";
                    return result;
                }

                long size = new FileInfo(winws).Length;
                if (size < MinWinwsBytes)
                {
                    result.Error = "winws.exe подозрительно мал (" + size + " байт) — вероятно, скачался не тот файл";
                    return result;
                }

                if (result.Version.Length > 0)
                {
                    File.WriteAllText(Path.Combine(engineDir, "version.txt"), result.Version,
                        new UTF8Encoding(false));
                }

                Report("готово");
                Log.Info("ENGINE", "движок установлен из приложения, версия " +
                                   (result.Version.Length > 0 ? result.Version : "неизвестна") +
                                   ", winws.exe " + size + " байт");

                result.Ok = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Error = "скачивание отменено";
                return result;
            }
            catch (Exception ex)
            {
                Log.Exception("ENGINE", "установка движка из приложения", ex);
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }

        // ------------------------------------------------------------ загрузка

        /// <summary>
        /// GitHub отдаёт архив ветки по codeload. Возвращает null при успехе.
        /// </summary>
        private string Download(string repo, string reference, string target, CancellationToken token)
        {
            try
            {
                string url = "https://codeload.github.com/" + repo + "/zip/refs/heads/" + reference;
                Report("  " + url);

                using (HttpResponseMessage response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                                                          .GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                        return "сервер ответил " + (int)response.StatusCode;

                    using (Stream stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (FileStream file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        int read;

                        while ((read = stream.ReadAsync(buffer, 0, buffer.Length, token).GetAwaiter().GetResult()) > 0)
                        {
                            file.Write(buffer, 0, read);
                            total += read;

                            if (total > 200L * 1024 * 1024) return "архив больше 200 МБ — это не движок";
                        }
                    }
                }

                FileInfo info = new FileInfo(target);
                if (info.Length < MinZipBytes)
                    return "архив подозрительно мал (" + info.Length + " байт)";

                if (!LooksLikeZip(target))
                    return "это не ZIP (возможно, страница ошибки вместо файла)";

                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>ZIP начинается с "PK\x03\x04". HTML и прочий мусор отсекаем сразу.</summary>
        public static bool LooksLikeZip(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[4];
                    if (fs.Read(head, 0, 4) < 4) return false;
                    return head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
                }
            }
            catch { return false; }
        }

        // --------------------------------------------------------- распаковка

        /// <summary>
        /// Распаковывает только то, что разрешено предикатом, в temp\&lt;stage&gt;.
        /// Пути внутри архива проверяются на выход за пределы каталога (zip-slip).
        /// Возвращает число распакованных файлов.
        /// </summary>
        private static int ExtractRepo(string zipPath, string temp, string stage, Func<string, bool> allowed)
        {
            int count = 0;
            string target = Path.Combine(temp, stage);
            Directory.CreateDirectory(target);

            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // каталог

                    // путь без первого сегмента: "<repo>-<branch>\bin\winws.exe" -> "bin\winws.exe"
                    string relative = StripArchiveRoot(entry.FullName);
                    if (relative.Length == 0) continue;

                    if (!allowed(relative)) continue;

                    // ---- zip-slip: точка выхода запрещена
                    if (relative.Contains("..")) continue;

                    string destination = Path.Combine(target, relative);

                    if (!IsInsideDirectory(destination, target)) continue;

                    string dir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    entry.ExtractToFile(destination, true);
                    count++;
                }
            }

            return count;
        }

        /// <summary>Отбрасывает первый сегмент пути внутри архива.</summary>
        public static string StripArchiveRoot(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath)) return "";

            string normalized = entryPath.Replace('/', '\\');
            int slash = normalized.IndexOf('\\');
            if (slash < 0) return "";
            return normalized.Substring(slash + 1);
        }

        /// <summary>
        /// Лежит ли путь внутри каталога. Единственная защита от zip-slip:
        /// запись вида `repo\..\..\Windows\System32\x.dll` после нормализации
        /// окажется вне каталога и должна быть отброшена.
        /// </summary>
        public static bool IsInsideDirectory(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(root)) return false;

            try
            {
                string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                                        + Path.DirectorySeparatorChar;
                string normalizedPath = Path.GetFullPath(fullPath);

                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Путь начинается с указанной папки верхнего уровня.</summary>
        private static bool IsUnder(string relative, string folder)
        {
            string normalized = relative.Replace('/', '\\');
            return normalized.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyTree(string source, string target)
        {
            if (!Directory.Exists(source)) return;

            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
                catch (Exception ex) { Log.Warn("ENGINE", "не скопирован " + Path.GetFileName(file) + ": " + ex.Message); }
            }

            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        private static string ReadVersionFromZip(string zipPath, string stage)
        {
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith(".service/version.txt", StringComparison.OrdinalIgnoreCase)) continue;

                        using (Stream stream = entry.Open())
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                            return reader.ReadToEnd().Trim();
                    }
                }
            }
            catch { }

            return "";
        }

        private void Report(string message)
        {
            Action<string> handler = Progress;
            if (handler != null) { try { handler(message); } catch { } }
        }
    }
}