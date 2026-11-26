using System;
using System.IO;

namespace AutoZapret.Core
{
    /// <summary>
    /// Все пути приложения в одном месте.
    ///
    /// Два режима:
    ///   * portable - данные рядом с exe (папка data\), включается файлом portable.txt
    ///     или автоматически, если exe лежит НЕ в Program Files и папка доступна на запись;
    ///   * installed - данные в %LOCALAPPDATA%\AutoZapret.
    ///
    /// Причина: портативная установка в Program Files не может писать рядом с exe
    /// (UAC-виртуализация), и настройки молча теряются. См. AGENTS.md.
    /// </summary>
    public static class Paths
    {
        private static readonly object Gate = new object();
        private static bool _resolved;
        private static bool _portable;
        private static string _baseDir;
        private static string _dataDir;

        public const string AppFolderName = "AutoZapret";

        /// <summary>Каталог, где лежит AutoZapret.exe (с завершающим слешем).</summary>
        public static string BaseDir
        {
            get { Resolve(); return _baseDir; }
        }

        public static bool IsPortable
        {
            get { Resolve(); return _portable; }
        }

        /// <summary>Корень данных: настройки, логи, состояние.</summary>
        public static string DataDir
        {
            get { Resolve(); return _dataDir; }
        }

        public static string StateDir { get { return Path.Combine(DataDir, "state"); } }

        public static string LogDir { get { return Path.Combine(DataDir, "logs"); } }

        public static string SettingsFile { get { return Path.Combine(StateDir, "settings.json"); } }

        /// <summary>Профиль WebView2. Отдельный от Edge, чтобы не воевать за кэш.</summary>
        public static string WebViewDataDir { get { return Path.Combine(DataDir, "webview2"); } }

        /// <summary>Движок zapret (winws.exe, WinDivert, lua). Скачивается tools\fetch-engine.ps1.</summary>
        public static string EngineDir { get { return Path.Combine(BaseDir, "engine"); } }

        public static string EngineBinDir { get { return Path.Combine(EngineDir, "bin"); } }

        public static string EngineListsDir { get { return Path.Combine(EngineDir, "lists"); } }

        /// <summary>Наши фейки (.bin) и манифест.</summary>
        public static string FakesDir { get { return Path.Combine(BaseDir, "assets", "fakes"); } }

        /// <summary>
        /// Свои наборы hosts — отдельно от поставки.
        ///
        /// В `assets/hosts/fixes.json` лежат фиксы, которые проверяли мы; файл
        /// едет с программой и обновляется вместе с ней. Всё, что добавил
        /// человек (свой набор адресов для hosts), живёт здесь и обновление
        /// не задевает — иначе правка поставки стирала бы его записи.
        /// </summary>
        public static string CustomHostsFixesFile
        {
            get { return Path.Combine(BaseDir, "assets", "hosts", "fixes.user.json"); }
        }

        public static string StrategiesDir { get { return Path.Combine(BaseDir, "assets", "strategies"); } }

        /// <summary>
        /// Свои стратегии — отдельно от кэша движка.
        ///
        /// В `assets/strategies/` лежат `strategies.v1.json` и `strategies.v2.json`,
        /// которые перезаписываются при обновлении движка. Всё, что добавил
        /// человек (свои .bat, победитель подбора фейка), живёт здесь и
        /// обновление не задевает.
        /// </summary>
        public static string CustomStrategiesFile
        {
            get { return Path.Combine(StrategiesDir, "strategies.custom.json"); }
        }

        /// <summary>Куда складывать .bat при экспорте.</summary>
        public static string ExportedStrategiesDir
        {
            get { return Path.Combine(BaseDir, "exported-strategies"); }
        }

        /// <summary>
        /// Куда складывать `.azconf.json` — файлы для обмена с другим человеком.
        ///
        /// Отдельно от `.bat`: там папка на каждый движок, здесь одна общая,
        /// потому что в самом файле уже написано, какой это движок.
        /// </summary>
        public static string SharedConfDir
        {
            get { return Path.Combine(ExportedStrategiesDir, "shared"); }
        }

        /// <summary>
        /// Создаёт каталоги, без которых приложение не может работать.
        /// Вызывать один раз на старте, до первого Log.Write.
        /// </summary>
        public static void EnsureDirectories()
        {
            SafeCreate(DataDir);
            SafeCreate(StateDir);
            SafeCreate(LogDir);
        }

        /// <summary>Сбросить кэш (нужно тестам, которые подменяют окружение).</summary>
        public static void Reset()
        {
            lock (Gate) { _resolved = false; }
        }

        /// <summary>
        /// Задать каталог данных явно.
        ///
        /// Нужно там, где `AppDomain.BaseDirectory` и `LocalApplicationData`
        /// указывают не туда. На Android оба бесполезны: APK — это zip, писать
        /// в него нельзя, а «локальные данные приложения» приходится брать у
        /// `Context.FilesDir`, который знает только Activity.
        ///
        /// Поэтому приложение на старте говорит: «данные здесь», и весь
        /// переносимый код (прокси, кэш, настройки) работает без правок.
        /// </summary>
        public static void SetDataDir(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return;

            lock (Gate)
            {
                _baseDir = EnsureTrailingSlash(AppDomain.CurrentDomain.BaseDirectory);
                _dataDir = EnsureTrailingSlash(dataDir);
                _portable = false;
                _resolved = true;
            }
        }

        /// <summary>
        /// Заставить службу работать в portable-режиме: данные рядом с exe.
        ///
        /// Зачем: служба стартует от LocalSystem, и её %LocalAppData% — это
        /// C:\Windows\System32\config\systemprofile\AppData\Local. Если оставить
        /// обычное определение, служба читала бы ДРУГОЙ settings.json, чем
        /// интерфейс пользователя: человек выбрал стратегию у себя, а служба
        /// запускает стратегию по умолчанию. Выглядит как «служба игнорирует
        /// настройки».
        ///
        /// Portable-режим у нас и так основной способ поставки (zip рядом с exe),
        /// поэтому для службы он не исключение, а правило.
        /// </summary>
        public static void ForcePortable()
        {
            lock (Gate)
            {
                _baseDir = EnsureTrailingSlash(AppDomain.CurrentDomain.BaseDirectory);
                _portable = true;
                _dataDir = Path.Combine(_baseDir, "data");
                _resolved = true;
            }
        }

        /// <summary>Можно ли писать в каталог программы (нужно службе).</summary>
        public static bool BaseDirWritable()
        {
            try { return IsWritable(EnsureTrailingSlash(AppDomain.CurrentDomain.BaseDirectory)); }
            catch { return false; }
        }

        private static void Resolve()
        {
            lock (Gate)
            {
                if (_resolved) return;
                _baseDir = EnsureTrailingSlash(AppDomain.CurrentDomain.BaseDirectory);
                _portable = DetectPortable(_baseDir);
                _dataDir = _portable
                    ? Path.Combine(_baseDir, "data")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        AppFolderName);
                _resolved = true;
            }
        }

        private static bool DetectPortable(string baseDir)
        {
            try
            {
                if (File.Exists(Path.Combine(baseDir, "portable.txt"))) return true;
                if (IsUnderProgramFiles(baseDir)) return false;
                return IsWritable(baseDir);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnderProgramFiles(string dir)
        {
            try
            {
                string[] roots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };
                string normalized = Path.GetFullPath(dir).TrimEnd('\\');
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string r = Path.GetFullPath(root).TrimEnd('\\');
                    if (normalized.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsWritable(string dir)
        {
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    fs.WriteByte(0);
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            }
        }

        private static string EnsureTrailingSlash(string path)
        {
            // Разделитель зависит от системы. На Linux обратный слеш — обычный
            // символ имени файла, а не разделитель: путь "/opt/app\" указывает
            // на файл с именем «app\», и Path.Combine даёт "/opt/app\engine",
            // то есть каталог с именем «app\engine». Всё, что ищется по такому
            // пути, «не находится» — и это выглядит как отсутствие файлов.
            char separator = System.IO.Path.DirectorySeparatorChar;

            if (string.IsNullOrEmpty(path)) return "." + separator;

            return path[path.Length - 1] == separator ? path : path + separator;
        }

        private static void SafeCreate(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }
    }
}
