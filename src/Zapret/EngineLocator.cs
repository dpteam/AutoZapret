using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Где лежит движок и всё ли на месте.
    ///
    /// Проверяем то, без чего winws не запустится или запустится молча сломанным:
    ///   winws.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys.
    /// Отдельно проверяем разрядность: 32-битной сборки winws не существует.
    /// </summary>
    public sealed class EngineLocator
    {
        public const string WinwsExe = "winws.exe";
        public const string CygwinDll = "cygwin1.dll";
        public const string DivertDll = "WinDivert.dll";
        public const string DivertSys = "WinDivert64.sys";

        public string EngineDir { get; private set; }
        public string BinDir { get; private set; }
        public string ListsDir { get; private set; }
        public string UtilsDir { get; private set; }
        public string Version { get; private set; }

        public bool Ready { get; private set; }
        public string Problem { get; private set; }

        public EngineLocator()
        {
            EngineDir = Paths.EngineDir;
            BinDir = Paths.EngineBinDir;
            ListsDir = Paths.EngineListsDir;
            UtilsDir = Path.Combine(EngineDir, "utils");
            Version = "";
            Refresh();
        }

        /// <summary>Перепроверить состояние (после fetch-engine или обновления).</summary>
        public void Refresh()
        {
            Ready = false;
            Problem = null;

            if (!Directory.Exists(EngineDir))
            {
                Problem = "Движок не установлен: нет каталога engine.";
                return;
            }

            string winws = Path.Combine(BinDir, WinwsExe);
            if (!File.Exists(winws))
            {
                Problem = "Нет engine\\bin\\winws.exe. Запустите tools\\fetch-engine.ps1.";
                return;
            }

            string[] required = { CygwinDll, DivertDll, DivertSys };
            foreach (string name in required)
            {
                if (!File.Exists(Path.Combine(BinDir, name)))
                {
                    Problem = "Нет engine\\bin\\" + name + " — winws не запустится.";
                    return;
                }
            }

            if (!File.Exists(Path.Combine(ListsDir, "list-general.txt")))
            {
                Problem = "Нет списков (engine\\lists\\list-general.txt).";
                return;
            }

            Version = ReadVersion();
            Ready = true;
        }

        /// <summary>Путь к winws.exe или null.</summary>
        public string WinwsPath
        {
            get
            {
                string p = Path.Combine(BinDir, WinwsExe);
                return File.Exists(p) ? p : null;
            }
        }

        /// <summary>
        /// Плейсхолдеры для подстановки в аргументы стратегий.
        /// Пути всегда абсолютные и в том же стиле, что ждёт winws (обратные слеши).
        /// </summary>
        public Dictionary<string, string> BuildPlaceholders(string gameFilter)
        {
            string tcp, udp;
            MapGameFilter(gameFilter, out tcp, out udp);

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "BIN", BinDir.TrimEnd('\\') },
                { "LISTS", ListsDir.TrimEnd('\\') },
                { "FAKES", Paths.FakesDir.TrimEnd('\\') },
                { "GAME_TCP", tcp },
                { "GAME_UDP", udp },
                { "WF_TCP", "80,443,2053,2083,2087,2096,8443" },
                { "WF_UDP", "443,19294-19344,50000-50100" }
            };
            return map;
        }

        /// <summary>
        /// GameFilter из Flowseal: файл utils\game_filter.enabled содержит all|tcp|udp.
        /// Если файла нет — фильтр выключен (порт 12 = «ничего»).
        /// </summary>
        public void MapGameFilter(string mode, out string tcp, out string udp)
        {
            tcp = "12";
            udp = "12";

            string m = (mode ?? "off").Trim().ToLowerInvariant();
            if (m == "all" || m == "tcp+udp" || m == "both")
            {
                tcp = "1024-65535";
                udp = "1024-65535";
            }
            else if (m == "tcp")
            {
                tcp = "1024-65535";
            }
            else if (m == "udp")
            {
                udp = "1024-65535";
            }
        }

        public string ReadVersion()
        {
            try
            {
                string p = Path.Combine(EngineDir, "version.txt");
                if (File.Exists(p)) return File.ReadAllText(p).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>Короткое описание состояния для логов и UI.</summary>
        public string StatusLine()
        {
            if (!Ready) return Problem ?? "движок не готов";
            return "winws " + (Version.Length > 0 ? Version : "?") + " @ " + BinDir;
        }
    }
}
