using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Настройки приложения.
    ///
    /// Правила:
    ///   * запись только через AtomicFile (temp -> File.Replace -> .bak);
    ///   * битый файл уезжает в *.corrupt-<ts>, приложение стартует с дефолтами
    ///     и НЕ падает - пользователь не должен остаться без окна из-за одного байта;
    ///   * Normalize() приводит любые значения в допустимые границы (порт, режимы),
    ///     чтобы в UI не приезжал «порт 999999» из старого файла;
    ///   * секрет прокси не пишется в лог (только через Log.Secret).
    /// </summary>
    public sealed class Settings
    {
        public const int CurrentSchema = 1;

        // ---- engine
        public string EngineKind = "v1";           // v1 | v2
        public string EngineVersion = "";          // заполняется после fetch-engine

        // ---- zapret
        public string StrategyId = "flowseal.general";
        public string GameFilter = "tcp+udp";      // off | tcp | udp | tcp+udp
        public string IpsetMode = "loaded";        // off | loaded | any
        public bool AutoStart = true;
        public bool ServiceMode = false;           // запускать как службу, а не как процесс
        public bool AutoPilot = false;             // автоподбор стратегии в фоне
        public bool ProcessPresets = false;        // переключать стратегию под запущенное приложение

        // ---- proxy
        public bool ProxyEnabled = false;
        public string ProxyHost = "127.0.0.1";
        public int ProxyPort = 1443;
        public string ProxySecret = "";            // 32 hex-символа, без префикса dd
        public bool ProxyCfProxy = true;

        // ---- ui
        public string Theme = "dark";
        public bool SimpleMode = true;
        public bool MinimizeToTray = true;
        public bool StartMinimized = false;
        public bool CloseToTrayHintShown = false;

        // ---- updates
        public bool UpdateEngine = true;
        public bool UpdateApp = true;
        public bool UpdateLists = true;
        public string UpdateChannel = "stable";    // stable | beta

        // ---- misc
        public string LastRunVersion = "";
        public string LastRunAt = "";

        private static readonly object Gate = new object();
        private static Settings _current;

        /// <summary>Текущие настройки. Никогда не null.</summary>
        public static Settings Current
        {
            get
            {
                lock (Gate)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        /// <summary>Подменить текущие (после загрузки из другого места, в тестах).</summary>
        public static void SetCurrent(Settings settings)
        {
            lock (Gate) _current = settings ?? new Settings();
        }

        // ------------------------------------------------------------------ load

        public static Settings Load()
        {
            string path = Paths.SettingsFile;
            string text = AtomicFile.ReadAllTextOrNull(path);

            if (text == null)
            {
                // может быть, есть бэкап от прошлой атомарной записи
                text = AtomicFile.ReadAllTextOrNull(path + ".bak");
                if (text != null) Log.Warn("APP", "settings.json отсутствует, читаю .bak");
            }

            if (text == null)
            {
                Settings fresh = new Settings();
                fresh.Normalize();
                return fresh;
            }

            Dictionary<string, object> map = Json.ParseObjectSafe(text);
            if (map == null)
            {
                string moved = AtomicFile.Quarantine(path);
                Log.Error("APP", "settings.json не разобрался" + (moved != null ? ", перемещён в " + moved : ""));
                Settings fallback = new Settings();
                fallback.Normalize();
                return fallback;
            }

            Settings s = FromMap(map);
            s.Normalize();
            return s;
        }

        private static Settings FromMap(Dictionary<string, object> map)
        {
            Settings s = new Settings();

            Dictionary<string, object> engine = Json.GetObject(map, "engine");
            if (engine != null)
            {
                s.EngineKind = Json.GetString(engine, "kind", s.EngineKind);
                s.EngineVersion = Json.GetString(engine, "version", s.EngineVersion);
            }

            Dictionary<string, object> zapret = Json.GetObject(map, "zapret");
            if (zapret != null)
            {
                s.StrategyId = Json.GetString(zapret, "strategyId", s.StrategyId);
                s.GameFilter = Json.GetString(zapret, "gameFilter", s.GameFilter);
                s.IpsetMode = Json.GetString(zapret, "ipsetMode", s.IpsetMode);
                s.AutoStart = Json.GetBool(zapret, "autoStart", s.AutoStart);
                s.ServiceMode = Json.GetBool(zapret, "serviceMode", s.ServiceMode);
                s.AutoPilot = Json.GetBool(zapret, "autoPilot", s.AutoPilot);
                s.ProcessPresets = Json.GetBool(zapret, "processPresets", s.ProcessPresets);
            }

            Dictionary<string, object> proxy = Json.GetObject(map, "proxy");
            if (proxy != null)
            {
                s.ProxyEnabled = Json.GetBool(proxy, "enabled", s.ProxyEnabled);
                s.ProxyHost = Json.GetString(proxy, "host", s.ProxyHost);
                s.ProxyPort = Json.GetInt(proxy, "port", s.ProxyPort);
                s.ProxySecret = Json.GetString(proxy, "secret", s.ProxySecret);
                s.ProxyCfProxy = Json.GetBool(proxy, "cfProxy", s.ProxyCfProxy);
            }

            Dictionary<string, object> ui = Json.GetObject(map, "ui");
            if (ui != null)
            {
                s.Theme = Json.GetString(ui, "theme", s.Theme);
                s.SimpleMode = Json.GetBool(ui, "simpleMode", s.SimpleMode);
                s.MinimizeToTray = Json.GetBool(ui, "minimizeToTray", s.MinimizeToTray);
                s.StartMinimized = Json.GetBool(ui, "startMinimized", s.StartMinimized);
                s.CloseToTrayHintShown = Json.GetBool(ui, "closeToTrayHintShown", s.CloseToTrayHintShown);
            }

            Dictionary<string, object> updates = Json.GetObject(map, "updates");
            if (updates != null)
            {
                s.UpdateEngine = Json.GetBool(updates, "engine", s.UpdateEngine);
                s.UpdateApp = Json.GetBool(updates, "app", s.UpdateApp);
                s.UpdateLists = Json.GetBool(updates, "lists", s.UpdateLists);
                s.UpdateChannel = Json.GetString(updates, "channel", s.UpdateChannel);
            }

            Dictionary<string, object> misc = Json.GetObject(map, "misc");
            if (misc != null)
            {
                s.LastRunVersion = Json.GetString(misc, "lastRunVersion", s.LastRunVersion);
                s.LastRunAt = Json.GetString(misc, "lastRunAt", s.LastRunAt);
            }

            return s;
        }

        // ------------------------------------------------------------------ save

        public void Save()
        {
            Normalize();
            string json = ToJson();
            AtomicFile.WriteAllText(Paths.SettingsFile, json);
            lock (Gate) _current = this;
        }

        public string ToJson()
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal);
            root["schema"] = CurrentSchema;

            root["engine"] = Map(
                "kind", EngineKind,
                "version", EngineVersion);

            root["zapret"] = Map(
                "strategyId", StrategyId,
                "gameFilter", GameFilter,
                "ipsetMode", IpsetMode,
                "autoStart", AutoStart,
                "serviceMode", ServiceMode,
                "autoPilot", AutoPilot,
                "processPresets", ProcessPresets);

            root["proxy"] = Map(
                "enabled", ProxyEnabled,
                "host", ProxyHost,
                "port", (long)ProxyPort,
                "secret", ProxySecret,
                "cfProxy", ProxyCfProxy);

            root["ui"] = Map(
                "theme", Theme,
                "simpleMode", SimpleMode,
                "minimizeToTray", MinimizeToTray,
                "startMinimized", StartMinimized,
                "closeToTrayHintShown", CloseToTrayHintShown);

            root["updates"] = Map(
                "engine", UpdateEngine,
                "app", UpdateApp,
                "lists", UpdateLists,
                "channel", UpdateChannel);

            root["misc"] = Map(
                "lastRunVersion", LastRunVersion,
                "lastRunAt", LastRunAt);

            return Json.Stringify(root);
        }

        private static Dictionary<string, object> Map(params object[] pairs)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[Convert.ToString(pairs[i], CultureInfo.InvariantCulture)] = pairs[i + 1];
            return map;
        }

        // --------------------------------------------------------------- normalize

        /// <summary>Приводит всё в допустимые границы. Идемпотентно.</summary>
        public void Normalize()
        {
            EngineKind = OneOf(EngineKind, "v1", "v2");
            GameFilter = OneOf(GameFilter, "off", "tcp", "udp", "tcp+udp");
            IpsetMode = OneOf(IpsetMode, "off", "loaded", "any");
            Theme = OneOf(Theme, "dark", "light");
            UpdateChannel = OneOf(UpdateChannel, "stable", "beta");

            if (string.IsNullOrWhiteSpace(StrategyId)) StrategyId = "flowseal.general";

            if (string.IsNullOrWhiteSpace(ProxyHost)) ProxyHost = "127.0.0.1";
            if (ProxyPort < 1 || ProxyPort > 65535) ProxyPort = 1443;
            ProxySecret = NormalizeSecret(ProxySecret);
            if (ProxySecret.Length == 0) ProxySecret = NewSecret();

            if (string.IsNullOrEmpty(EngineVersion)) EngineVersion = "";
        }

        /// <summary>
        /// 16 случайных байт в hex = 32 символа. Ровно столько требует MTProto-прокси.
        /// Префикс dd хранится отдельно, в ссылке tg://proxy.
        /// </summary>
        public static string NewSecret()
        {
            byte[] bytes = new byte[16];
            using (System.Security.Cryptography.RandomNumberGenerator rng =
                   System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder(32);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Оставляет только hex, приводит к нижнему регистру, обрезает/дополняет до 32.</summary>
        public static string NormalizeSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string v = value.Trim();
            if (v.StartsWith("dd", StringComparison.OrdinalIgnoreCase) && v.Length > 32)
                v = v.Substring(2);

            StringBuilder sb = new StringBuilder(32);
            foreach (char c in v)
            {
                if (sb.Length >= 32) break;
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) sb.Append(c);
                else if (c >= 'A' && c <= 'F') sb.Append((char)(c + 32));
            }
            if (sb.Length != 32) return "";
            return sb.ToString();
        }

        /// <summary>Ссылка для Telegram. Всегда dd-префикс: обычная обфускация без Fake-TLS.</summary>
        public string BuildProxyLink()
        {
            string host = ProxyHost == "0.0.0.0" ? "127.0.0.1" : ProxyHost;
            return "tg://proxy?server=" + host + "&port=" + ProxyPort.ToString(CultureInfo.InvariantCulture) +
                   "&secret=dd" + ProxySecret;
        }

        private static string OneOf(string value, params string[] allowed)
        {
            if (!string.IsNullOrEmpty(value))
            {
                string v = value.Trim().ToLowerInvariant();
                foreach (string a in allowed)
                    if (string.Equals(a, v, StringComparison.Ordinal)) return a;
            }
            return allowed.Length > 0 ? allowed[0] : "";
        }
    }
}
