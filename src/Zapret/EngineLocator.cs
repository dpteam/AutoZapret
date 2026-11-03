using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>Какой движок используем.</summary>
    public enum EngineKind
    {
        V1,   // winws.exe  — zapret 1 (Flowseal)
        V2    // winws2.exe — zapret 2 (snanvl), требует lua/
    }

    /// <summary>
    /// Где лежит движок и всё ли на месте.
    ///
    /// Два движка живут в одном каталоге engine\ и используют общие
    /// WinDivert и фейки. Отличаются только исполняемым файлом
    /// и наличием lua\ (нужен для v2).
    /// </summary>
    public sealed class EngineLocator
    {
        public const string WinwsV1 = "winws.exe";
        public const string WinwsV2 = "winws2.exe";
        public const string CygwinDll = "cygwin1.dll";
        public const string DivertDll = "WinDivert.dll";
        public const string DivertSys = "WinDivert64.sys";

        public string EngineDir { get; private set; }
        public string BinDir { get; private set; }
        public string ListsDir { get; private set; }
        public string UtilsDir { get; private set; }
        public string LuaDir { get; private set; }

        /// <summary>Версия установленного v1 (из version.txt).</summary>
        public string Version { get; private set; }

        /// <summary>Версия установленного v2 (из version2.txt).</summary>
        public string VersionV2 { get; private set; }

        /// <summary>Общие файлы (WinDivert, cygwin, фейки) на месте.</summary>
        public bool Ready { get; private set; }

        public string Problem { get; private set; }

        /// <summary>Доступен ли v1.</summary>
        public bool HasV1 { get; private set; }

        /// <summary>Доступен ли v2 (есть exe и все lua).</summary>
        public bool HasV2 { get; private set; }

        public EngineLocator()
        {
            EngineDir = Paths.EngineDir;
            BinDir = Paths.EngineBinDir;
            ListsDir = Paths.EngineListsDir;
            UtilsDir = Path.Combine(EngineDir, "utils");
            LuaDir = Path.Combine(EngineDir, "lua");
            Version = "";
            VersionV2 = "";
            Refresh();
        }

        /// <summary>Перепроверить состояние (после fetch-engine или обновления).</summary>
        public void Refresh()
        {
            Ready = false;
            HasV1 = false;
            HasV2 = false;
            Problem = null;

            if (!Directory.Exists(EngineDir))
            {
                Problem = "Движок не установлен: нет каталога engine.";
                return;
            }

            // общие файлы
            string[] required = { CygwinDll, DivertDll, DivertSys };
            foreach (string name in required)
            {
                if (!File.Exists(Path.Combine(BinDir, name)))
                {
                    Problem = "Нет engine\\bin\\" + name + " — движок не запустится.";
                    return;
                }
            }

            if (!File.Exists(Path.Combine(ListsDir, "list-general.txt")))
            {
                Problem = "Нет списков (engine\\lists\\list-general.txt).";
                return;
            }

            HasV1 = File.Exists(Path.Combine(BinDir, WinwsV1));
            Version = ReadVersionFile("version.txt");

            // v2 требует не только exe, но и lua
            bool hasV2Exe = File.Exists(Path.Combine(BinDir, WinwsV2));
            bool hasLua = Directory.Exists(LuaDir) && File.Exists(Path.Combine(LuaDir, "blobs.lua")) &&
                          File.Exists(Path.Combine(LuaDir, "zapret-lib.lua")) &&
                          File.Exists(Path.Combine(LuaDir, "zapret-antidpi.lua"));

            HasV2 = hasV2Exe && hasLua;
            VersionV2 = ReadVersionFile("version2.txt");

            if (!HasV1 && !HasV2)
            {
                Problem = "Нет ни winws.exe, ни winws2.exe. Запустите tools\\fetch-engine.ps1.";
                return;
            }

            Ready = true;
        }

        /// <summary>Путь к исполняемому файлу нужного движка или null.</summary>
        public string ExePath(EngineKind kind)
        {
            string name = kind == EngineKind.V2 ? WinwsV2 : WinwsV1;
            string path = Path.Combine(BinDir, name);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Совместимость: путь к v1.</summary>
        public string WinwsPath { get { return ExePath(EngineKind.V1); } }

        /// <summary>Доступен ли движок.</summary>
        public bool IsAvailable(EngineKind kind)
        {
            return kind == EngineKind.V2 ? HasV2 : HasV1;
        }

        /// <summary>Понятная причина, почему движок недоступен.</summary>
        public string UnavailableReason(EngineKind kind)
        {
            if (kind == EngineKind.V2)
            {
                if (!File.Exists(Path.Combine(BinDir, WinwsV2)))
                    return "zapret2 не установлен (нет winws2.exe). Запустите tools\\fetch-engine.ps1.";
                if (!Directory.Exists(LuaDir))
                    return "zapret2 не установлен (нет каталога lua). Запустите tools\\fetch-engine.ps1.";
                return "zapret2 не установлен (не хватает lua-скриптов). Запустите tools\\fetch-engine.ps1.";
            }

            if (!File.Exists(Path.Combine(BinDir, WinwsV1)))
                return "zapret 1 не установлен (нет winws.exe). Запустите tools\\fetch-engine.ps1.";

            return Problem ?? "движок недоступен";
        }

        /// <summary>Версия конкретного движка.</summary>
        public string VersionOf(EngineKind kind)
        {
            string v = kind == EngineKind.V2 ? VersionV2 : Version;
            return v.Length > 0 ? v : "?";
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
                { "LUA", LuaDir.TrimEnd('\\') },
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

        private string ReadVersionFile(string name)
        {
            try
            {
                string p = Path.Combine(EngineDir, name);
                if (File.Exists(p)) return File.ReadAllText(p).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>Короткое описание состояния для логов и UI.</summary>
        public string StatusLine()
        {
            if (!Ready) return Problem ?? "движок не готов";

            List<string> parts = new List<string>(2);
            if (HasV1) parts.Add("v1 " + VersionOf(EngineKind.V1));
            if (HasV2) parts.Add("v2 " + VersionOf(EngineKind.V2));

            return string.Join(", ", parts.ToArray()) + " @ " + BinDir;
        }
    }
}
