using System;
using System.Collections.Generic;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Кольцевой буфер лога для экрана и для архива поддержки.
    ///
    /// Почему отдельный класс, а не «просто писать в logcat». logcat читают
    /// только отладочные сборки и то не всегда, а нам нужно отдать человеку
    /// архив, где видно, что происходило ДО поломки. Держим последние строки
    /// в памяти — их и отдаём в ZIP.
    /// </summary>
    public static class AppLog
    {
        private const int Capacity = 800;

        private static readonly List<string> Lines = new List<string>(Capacity);
        private static readonly object Gate = new object();

        public static void Add(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            string stamp = DateTime.Now.ToString("HH:mm:ss");

            lock (Gate)
            {
                Lines.Add(stamp + "  " + line);

                if (Lines.Count > Capacity) Lines.RemoveRange(0, Lines.Count - Capacity);
            }
        }

        public static List<string> Snapshot(int max)
        {
            lock (Gate)
            {
                int from = Lines.Count > max ? Lines.Count - max : 0;

                return new List<string>(Lines.GetRange(from, Lines.Count - from));
            }
        }

        public static void Clear()
        {
            lock (Gate) { Lines.Clear(); }
        }
    }

    /// <summary>
    /// Сведения об устройстве и сети — ровно то, что нужно в отчёте.
    /// </summary>
    public static class AppInfo
    {
        public static string Version = "?";
        public static string AndroidVersion = "?";
        public static int Sdk;
        public static string Device = "?";
        public static string Model = "?";
        public static string Abi = "?";

        /// <summary>Снимок настроек. Заполняется из активности при старте.</summary>
        public static Func<string> Settings = delegate { return "(нет данных)"; };

        /// <summary>Снимок сети. Заполняется из активности.</summary>
        public static Func<string> Network = delegate { return "(нет данных)"; };

        /// <summary>Снимок DNS. Заполняется из активности.</summary>
        public static Func<string> DnsState = delegate { return "(нет данных)"; };

        public static string Describe()
        {
            StringBuilder text = new StringBuilder();

            text.AppendLine("настройки:");
            text.AppendLine("  " + Settings().Replace("\n", "\n  "));

            text.AppendLine();
            text.AppendLine("сеть:");
            text.AppendLine("  " + Network().Replace("\n", "\n  "));

            text.AppendLine();
            text.AppendLine("dns:");
            text.AppendLine("  " + DnsState().Replace("\n", "\n  "));

            return text.ToString();
        }
    }
}
