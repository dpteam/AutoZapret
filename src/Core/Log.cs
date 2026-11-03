using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoZapret.Core
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    public sealed class LogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Category;
        public string Message;

        public string ToLine()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff} {1,-5} {2,-6} {3}",
                Time, LevelTag(Level), Category, Message);
        }

        public static string LevelTag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DEBUG";
                case LogLevel.Info: return "INFO";
                case LogLevel.Warn: return "WARN";
                case LogLevel.Error: return "ERROR";
                default: return "?";
            }
        }
    }

    /// <summary>
    /// Логи: файл + кольцевой буфер для UI.
    ///
    /// Правила (AGENTS.md):
    ///   * категория 3-6 символов: APP, WINWS, PROXY, UPD, LIST, TRAY, DIAG;
    ///   * сообщение всегда в одну строку - переносы склеиваются;
    ///   * никаких секретов: Secret() вырезает длинные hex/base64-подобные строки.
    ///
    /// Ни один метод не бросает: логирование не должно ронять приложение.
    /// </summary>
    public static class Log
    {
        public const int RingCapacity = 1500;

        private static readonly object Gate = new object();
        private static readonly Queue<LogEntry> Ring = new Queue<LogEntry>(RingCapacity);
        private static readonly StringBuilder Pending = new StringBuilder();

        private static LogLevel _minLevel = LogLevel.Info;
        private static string _filePath;
        private static DateTime _fileDate;
        private static int _writeFailures;
        private static bool _disabled;

        /// <summary>Новая строка (для живого обновления в UI). Вызывается из любого потока.</summary>
        public static event Action<LogEntry> NewEntry;

        public static LogLevel MinLevel
        {
            get { lock (Gate) return _minLevel; }
            set { lock (Gate) _minLevel = value; }
        }

        public static void Debug(string category, string message) { Write(LogLevel.Debug, category, message); }
        public static void Info(string category, string message) { Write(LogLevel.Info, category, message); }
        public static void Warn(string category, string message) { Write(LogLevel.Warn, category, message); }
        public static void Error(string category, string message) { Write(LogLevel.Error, category, message); }

        public static void Exception(string category, string context, Exception ex)
        {
            if (ex == null) { Write(LogLevel.Error, category, context); return; }
            Write(LogLevel.Error, category, context + ": " + ex.GetType().Name + ": " + ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                foreach (string line in ex.StackTrace.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length > 0) Write(LogLevel.Debug, category, "  at " + t);
                }
            }
        }

        public static void Write(LogLevel level, string category, string message)
        {
            if (_disabled) return;
            try
            {
                LogLevel min;
                lock (Gate) min = _minLevel;
                if (level < min) return;

                LogEntry entry = new LogEntry
                {
                    Time = DateTime.Now,
                    Level = level,
                    Category = NormalizeCategory(category),
                    Message = OneLine(message)
                };

                lock (Gate)
                {
                    Ring.Enqueue(entry);
                    while (Ring.Count > RingCapacity) Ring.Dequeue();
                    AppendToFileLocked(entry);
                }

                Action<LogEntry> handler = NewEntry;
                if (handler != null)
                {
                    try { handler(entry); } catch { }
                }
            }
            catch
            {
                // логирование не имеет права бросать
            }
        }

        /// <summary>Последние записи (копия). Для первичной отрисовки логов в UI.</summary>
        public static List<LogEntry> Snapshot()
        {
            lock (Gate) return new List<LogEntry>(Ring);
        }

        public static void Clear()
        {
            lock (Gate) Ring.Clear();
        }

        /// <summary>Полный путь к текущему файлу лога (может быть null, если писать некуда).</summary>
        public static string CurrentFile
        {
            get { lock (Gate) return _filePath; }
        }

        /// <summary>
        /// Скрывает то, что не должно попасть в лог: секрет прокси, токены.
        /// Оставляем первые 4 символа, чтобы можно было сопоставить «тот же секрет или нет».
        /// </summary>
        public static string Secret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            if (value.Length <= 8) return "***";
            return value.Substring(0, 4) + "***(" + value.Length + ")";
        }

        // ------------------------------------------------------------- internals

        private static void AppendToFileLocked(LogEntry entry)
        {
            if (_writeFailures > 20) { _disabled = true; return; }
            try
            {
                string dir = Paths.LogDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                DateTime today = entry.Time.Date;
                if (_filePath == null || _fileDate != today)
                {
                    _fileDate = today;
                    _filePath = Path.Combine(dir, "autozapret-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                }

                Pending.Length = 0;
                Pending.Append(entry.ToLine()).Append("\r\n");
                File.AppendAllText(_filePath, Pending.ToString(), new UTF8Encoding(false));
                _writeFailures = 0;
            }
            catch
            {
                _writeFailures++;
            }
        }

        private static string NormalizeCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return "APP";
            string c = category.Trim().ToUpperInvariant();
            if (c.Length > 6) c = c.Substring(0, 6);
            return c;
        }

        private static string OneLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            if (message.IndexOf('\n') < 0 && message.IndexOf('\r') < 0) return message;

            StringBuilder sb = new StringBuilder(message.Length);
            bool lastWasSpace = false;
            foreach (char ch in message)
            {
                char c = (ch == '\r' || ch == '\n' || ch == '\t') ? ' ' : ch;
                if (c == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else lastWasSpace = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
