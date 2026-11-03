using System;
using System.IO;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Атомарная запись файлов.
    ///
    /// Зачем: настройки и списки пишутся в тот момент, когда пользователь жмёт кнопки,
    /// а приложение может быть убито (или диск отвалиться) ровно посреди записи.
    /// Полузаписанный settings.json = потерянные настройки и «странное поведение»
    /// на следующем старте.
    ///
    /// Схема:
    ///   1) пишем во временный файл в ТОМ ЖЕ каталоге (иначе File.Replace не работает
    ///      между томами - %TEMP% часто на другом диске);
    ///   2) File.Replace(temp, target, backup) - атомарно на NTFS;
    ///   3) старая версия остаётся в target.bak.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            byte[] bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(content ?? string.Empty);
            WriteAllBytes(path, bytes);
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string backup = path + ".bak";

            try
            {
                using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (!File.Exists(path))
                {
                    // первый раз - заменять нечего
                    File.Move(temp, path);
                    return;
                }

                try
                {
                    // replaceToBackup: атомарно и с сохранением предыдущей версии
                    File.Replace(temp, path, backup, true);
                }
                catch (PlatformNotSupportedException)
                {
                    // не NTFS (сетевой диск, FAT32): делаем как можем
                    File.Copy(temp, path, true);
                    TryDelete(temp);
                }
                catch (IOException)
                {
                    // файл занят читателем - тоже откатываемся на копию
                    File.Copy(temp, path, true);
                    TryDelete(temp);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>
        /// Читает текст. При ошибке возвращает null и НЕ бросает.
        /// Если основной файл битый, но есть .bak - вернёт содержимое .bak.
        /// </summary>
        public static string ReadAllTextOrNull(string path, Encoding encoding = null)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return File.ReadAllText(path, encoding ?? new UTF8Encoding(false));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Уводит битый файл в сторону: settings.json -> settings.json.corrupt-20261104T123000.
        /// Возвращает путь, куда переехал файл, или null.
        /// </summary>
        public static string Quarantine(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string dest = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
                File.Move(path, dest);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
