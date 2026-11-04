using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Что произошло с одним списком при восстановлении из бандла.</summary>
    public sealed class RestoreEntry
    {
        public string File = "";
        public bool Restored;
        public bool Kept;
        public string Reason = "";
    }

    /// <summary>
    /// Списки, вшитые в поставку, как аварийный запас.
    ///
    /// Зачем: списки лежат на GitHub, а GitHub — первое, что может стать
    /// недоступным (или просто пропадёт сеть при первом запуске). Без списков
    /// движок стартует и ничего не обходит: ровно та ситуация «движок жив,
    /// эффекта нет», которую мы разбирали неделю.
    ///
    /// Поэтому `assets\lists` содержит копию всех списков на момент сборки.
    /// При старте они копируются в `engine\lists`, если файла нет или он
    /// подозрительно мал. Сеть при этом не нужна вообще.
    ///
    /// Обновление отдельно: если сеть есть, качаем свежую версию и заменяем.
    /// Кэш — это пол, а не потолок: он гарантирует, что обход работает, но
    /// никогда не мешает получить новые данные.
    ///
    /// ВАЖНО: личные списки (`*-user.txt`) из бандла копируются только если
    /// файла нет вовсе — пустая заготовка. Затирать ими то, что человек уже
    /// написал, нельзя ни при каких условиях.
    /// </summary>
    public static class BundledLists
    {
        public static string SourceDir
        {
            get { return Path.Combine(Paths.BaseDir, "assets", "lists"); }
        }

        /// <summary>Есть ли вшитые списки (в сборке для разработки их может не быть).</summary>
        public static bool Available
        {
            get
            {
                try { return Directory.Exists(SourceDir) && Directory.GetFiles(SourceDir, "*.txt").Length > 0; }
                catch { return false; }
            }
        }

        /// <summary>Минимальный разумный размер: меньше — значит заглушка, а не список.</summary>
        private static long MinSize(string file)
        {
            if (file.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase)) return 0;

            if (file.StartsWith("ipset-all", StringComparison.OrdinalIgnoreCase)) return 100 * 1024;
            if (file.StartsWith("ipset-exclude", StringComparison.OrdinalIgnoreCase)) return 32;

            return 256;
        }

        /// <summary>
        /// Разложить вшитые списки в engine\lists там, где их нет или они пустые.
        /// Возвращает отчёт: что восстановлено, что оставлено и почему.
        /// </summary>
        public static List<RestoreEntry> Restore()
        {
            List<RestoreEntry> result = new List<RestoreEntry>();

            if (!Available)
            {
                Log.Info("LIST", "вшитых списков нет (сборка без assets\\lists)");
                return result;
            }

            try { Directory.CreateDirectory(Paths.EngineListsDir); }
            catch (Exception ex)
            {
                Log.Warn("LIST", "не создал каталог списков: " + ex.Message);
                return result;
            }

            foreach (string source in Directory.GetFiles(SourceDir, "*.txt"))
            {
                string file = Path.GetFileName(source);
                string target = Path.Combine(Paths.EngineListsDir, file);

                RestoreEntry entry = new RestoreEntry { File = file };
                result.Add(entry);

                long sourceSize = 0;
                try { sourceSize = new FileInfo(source).Length; } catch { }

                bool exists = File.Exists(target);
                long targetSize = 0;

                if (exists)
                {
                    try { targetSize = new FileInfo(target).Length; } catch { }
                }

                // Личный список не трогаем никогда, если он существует: даже
                // пустой — это осознанный выбор человека.
                bool isUser = file.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase);

                if (exists && (isUser || targetSize >= MinSize(file)))
                {
                    entry.Kept = true;
                    entry.Reason = isUser ? "личный список" : "уже есть";
                    continue;
                }

                if (sourceSize <= 2)
                {
                    // в бандле тоже пустышка — копировать нечего
                    entry.Kept = true;
                    entry.Reason = "в бандле пусто";
                    continue;
                }

                try
                {
                    File.Copy(source, target, true);

                    entry.Restored = true;
                    entry.Reason = exists ? "был почти пуст" : "не было";

                    Log.Info("LIST", "из бандла восстановлен " + file + " (" + sourceSize + " байт)");
                }
                catch (Exception ex)
                {
                    entry.Kept = true;
                    entry.Reason = "ошибка: " + ex.Message;

                    Log.Warn("LIST", "не восстановил " + file + ": " + ex.Message);
                }
            }

            int restored = 0;
            foreach (RestoreEntry entry in result) if (entry.Restored) restored++;

            if (restored > 0)
                Log.Info("LIST", "из бандла восстановлено списков: " + restored);

            return result;
        }

        /// <summary>
        /// Размер вшитой копии — чтобы интерфейс мог показать, что запас есть.
        /// </summary>
        public static string Describe()
        {
            if (!Available) return "нет";

            int files = 0;
            long bytes = 0;

            try
            {
                foreach (string file in Directory.GetFiles(SourceDir, "*.txt"))
                {
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            }
            catch { }

            return files + " файлов, " + (bytes / 1024) + " КБ";
        }
    }
}