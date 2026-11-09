using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Сборка логов в ZIP «для техподдержки».
    ///
    /// Зачем отдельная функция, а не «пришлите скриншот». Скриншот не содержит
    /// ни версии, ни настроек, ни того, что происходило до поломки, — а
    /// разбирать приходится именно это. Здесь всё сразу и в одном файле,
    /// который друг может отправить одним касанием.
    ///
    /// Что внутри:
    ///   * версия приложения и Android, модель устройства;
    ///   * все настройки (без секретов — их у нас нет);
    ///   * лог самого приложения (то, что видно на экране);
    ///   * logcat с тегом AutoZapret и падениями;
    ///   * состояние сети: активная сеть, есть ли IPv6, подлог DNS;
    ///   * последний результат проверки блокировки.
    ///
    /// Чего внутри НЕТ и не будет: пароли, ключи, содержимое трафика,
    /// персональные данные. Только то, что нужно для разбора поломки.
    /// </summary>
    public static class SupportBundle
    {
        /// <summary>Сколько строк лога приложения сохраняем.</summary>
        private const int MaxLogLines = 500;

        /// <summary>
        /// Собрать архив. Возвращает путь к файлу или null с текстом ошибки.
        /// </summary>
        public static string Build(string directory, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(directory);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = Path.Combine(directory, "autozapret-log-" + stamp + ".zip");

                // Пишем во временный файл рядом с целевым: %TEMP% может быть на
                // другом диске, и File.Move между дисками падает.
                string temp = path + ".tmp";

                if (File.Exists(temp)) File.Delete(temp);

                using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    AddText(zip, "info.txt", BuildInfo());

                    string logText = BuildAppLog();

                    if (logText.Length > 0) AddText(zip, "app.log", logText);

                    string system = BuildSystemState();

                    if (system.Length > 0) AddText(zip, "system.txt", system);

                    // logcat отдаёт сам Android: приложение не имеет права
                    // читать чужие логи, но свои — через Process в этом же
                    // процессе доступны не всегда. Поэтому основной источник —
                    // свой лог выше, а logcat добавляем, если получится.
                    string cat = TryLogcat();

                    if (cat.Length > 0) AddText(zip, "logcat.txt", cat);
                }

                if (File.Exists(path)) File.Delete(path);

                File.Move(temp, path);

                return path;
            }
            catch (Exception ex)
            {
                if (log != null) log("ZIP: " + ex.GetType().Name + ": " + ex.Message);

                return null;
            }
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);

            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
            }
        }

        private static string BuildInfo()
        {
            StringBuilder text = new StringBuilder();

            text.AppendLine("AutoZapret — отчёт для техподдержки");
            text.AppendLine("собран: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine();

            text.AppendLine("приложение: " + AppInfo.Version);
            text.AppendLine("android: " + AppInfo.AndroidVersion + " (API " + AppInfo.Sdk + ")");
            text.AppendLine("устройство: " + AppInfo.Device + " / " + AppInfo.Model);
            text.AppendLine("abi: " + AppInfo.Abi);
            text.AppendLine();

            text.AppendLine("--- настройки ---");
            text.AppendLine(AppInfo.Settings());

            return text.ToString();
        }

        private static string BuildAppLog()
        {
            List<string> lines = AppLog.Snapshot(MaxLogLines);

            if (lines.Count == 0) return "";

            StringBuilder text = new StringBuilder();

            foreach (string line in lines) text.AppendLine(line);

            return text.ToString();
        }

        private static string BuildSystemState()
        {
            StringBuilder text = new StringBuilder();

            text.AppendLine("--- сеть ---");
            text.AppendLine(AppInfo.Network());

            text.AppendLine();
            text.AppendLine("--- DNS ---");
            text.AppendLine(AppInfo.DnsState());

            return text.ToString();
        }

        private static string TryLogcat()
        {
            try
            {
                // Свой процесс может читать logcat только если у него есть
                // разрешение READ_LOGS (его нет у обычных приложений). Поэтому
                // пробуем и молча соглашаемся с отказом.
                using (Java.Lang.Process process = Java.Lang.Runtime.GetRuntime().Exec(
                    new[] { "logcat", "-d", "-t", "300", "-s", "AutoZapret:V", "AndroidRuntime:E" }))
                {
                    using (Stream stream = process.InputStream)
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string text = reader.ReadToEnd();

                        return text ?? "";
                    }
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
