using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Orchestrator
{
    /// <summary>
    /// Пресет по процессу: «запущен Discord → стратегия X», «запущена игра → фильтр портов».
    ///
    /// Зачем это вообще нужно: одна универсальная стратегия не бывает лучшей для всего.
    /// Discord душат по-своему, игры требуют широкий диапазон портов, а обычный браузинг
    /// от этого только страдает. Раньше человек переключал это руками.
    /// </summary>
    public sealed class ProcessPreset
    {
        public string Id = "";
        public string Name = "";

        /// <summary>Имена процессов без .exe, в нижнем регистре при сравнении.</summary>
        public List<string> Processes = new List<string>();

        /// <summary>Стратегия для этого пресета. Пусто — не менять.</summary>
        public string Strategy = "";

        /// <summary>Фильтр портов: off | tcp | udp | tcp+udp. Пусто — не менять.</summary>
        public string GameFilter = "";

        public bool Enabled = true;

        /// <summary>Есть ли вообще что применять. Пресет-пустышка только вводит в заблуждение.</summary>
        public bool ChangesSomething
        {
            get { return Strategy.Length > 0 || GameFilter.Length > 0; }
        }

        public string Describe()
        {
            List<string> parts = new List<string>();

            if (Strategy.Length > 0) parts.Add("стратегия " + Strategy);
            if (GameFilter.Length > 0) parts.Add("порты " + GameFilter);
            if (parts.Count == 0) parts.Add("ничего не меняет");

            return Name + " (" + string.Join(", ", Processes.ToArray()) + ") → " +
                   string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>
    /// Загрузка пресетов и выбор подходящего. Логика чистая, без таймеров и потоков,
    /// чтобы её можно было проверить в selftest без запуска процессов.
    /// </summary>
    public static class ProcessPresets
    {
        /// <summary>Путь к данным пресетов.</summary>
        public static string FilePath
        {
            get { return Path.Combine(Paths.BaseDir, "assets", "presets", "processes.json"); }
        }

        /// <summary>
        /// Читает пресеты. Битый файл не роняет приложение: возвращается пустой список,
        /// и вызывающий решает, использовать ли встроенный набор.
        /// </summary>
        public static List<ProcessPreset> Load(string path)
        {
            List<ProcessPreset> presets = new List<ProcessPreset>();

            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return presets;

            Dictionary<string, object> root = Json.ParseObjectSafe(text);
            if (root == null) return presets;

            List<object> items = Json.GetArray(root, "presets");
            if (items == null) return presets;

            foreach (object item in items)
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) continue;

                ProcessPreset preset = new ProcessPreset
                {
                    Id = Json.GetString(map, "id", ""),
                    Name = Json.GetString(map, "name", ""),
                    Strategy = Json.GetString(map, "strategy", ""),
                    GameFilter = NormalizeFilter(Json.GetString(map, "gameFilter", "")),
                    Enabled = Json.GetBool(map, "enabled", true)
                };

                List<object> processes = Json.GetArray(map, "processes");
                if (processes != null)
                {
                    foreach (object p in processes)
                    {
                        string name = p as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        preset.Processes.Add(name.Trim());
                    }
                }

                // без процессов и без имени пресет бесполезен
                if (preset.Processes.Count == 0) continue;
                if (preset.Name.Length == 0) preset.Name = preset.Processes[0];
                if (preset.Id.Length == 0) preset.Id = preset.Name.ToLowerInvariant();

                presets.Add(preset);
            }

            return presets;
        }

        /// <summary>
        /// Сохраняет пресеты обратно в файл. Нужно для переключателей в UI:
        /// заставлять человека править JSON руками — плохой UX для «галочки».
        /// </summary>
        public static bool Save(List<ProcessPreset> presets, string path)
        {
            if (presets == null || string.IsNullOrEmpty(path)) return false;

            try
            {
                List<object> items = new List<object>();

                foreach (ProcessPreset preset in presets)
                {
                    List<object> processes = new List<object>();
                    foreach (string p in preset.Processes) processes.Add(p);

                    items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "id", preset.Id },
                        { "name", preset.Name },
                        { "processes", processes },
                        { "strategy", preset.Strategy },
                        { "gameFilter", preset.GameFilter },
                        { "enabled", preset.Enabled }
                    });
                }

                Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "schema", 1L },
                    { "note", "Пресеты по процессам: когда запущено приложение, включается подходящая стратегия и фильтр портов. Порядок = приоритет, первый совпавший побеждает. Пустая strategy или gameFilter означает 'не менять'." },
                    { "presets", items }
                };

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                AtomicFile.WriteAllText(path, Json.Stringify(root));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("PRSET", "не удалось сохранить пресеты: " + ex.Message);
                return false;
            }
        }

        /// <summary>Встроенный набор на случай, если файла нет (чистый клон, повреждённый assets).</summary>
        public static List<ProcessPreset> Builtin()
        {
            List<ProcessPreset> presets = new List<ProcessPreset>();

            ProcessPreset discord = new ProcessPreset
            {
                Id = "discord",
                Name = "Discord",
                Strategy = "flowseal.general-alt12"
            };
            discord.Processes.AddRange(new[] { "Discord", "DiscordPTB", "DiscordCanary" });
            presets.Add(discord);

            ProcessPreset games = new ProcessPreset
            {
                Id = "games",
                Name = "Игры (CS2, Dota 2, Valorant)",
                GameFilter = "tcp+udp"
            };
            games.Processes.AddRange(new[] { "cs2", "dota2", "VALORANT", "VALORANT-Win64-Shipping", "r5apex" });
            presets.Add(games);

            return presets;
        }

        /// <summary>
        /// Первый включённый пресет, чей процесс запущен. Порядок в файле = приоритет.
        ///
        /// isRunning передаётся снаружи: так тест не зависит от реально запущенных
        /// программ, а сервис может кэшировать список процессов.
        /// </summary>
        public static ProcessPreset Match(List<ProcessPreset> presets, Func<string, bool> isRunning)
        {
            if (presets == null || isRunning == null) return null;

            foreach (ProcessPreset preset in presets)
            {
                if (!preset.Enabled) continue;
                if (!preset.ChangesSomething) continue;

                foreach (string process in preset.Processes)
                {
                    if (string.IsNullOrEmpty(process)) continue;

                    bool running;
                    try { running = isRunning(process); }
                    catch { running = false; }

                    if (running) return preset;
                }
            }

            return null;
        }

        /// <summary>Запущен ли процесс. Имя сравнивается без учёта регистра, .exe можно не указывать.</summary>
        public static bool IsRunning(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            string name = processName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            try
            {
                Process[] procs = Process.GetProcessesByName(name);
                bool any = procs.Length > 0;
                foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                return any;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Проверяет фильтр портов. Пустая строка допустима (означает «не менять»).</summary>
        public static bool IsValidFilter(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;

            string v = value.Trim().ToLowerInvariant();
            return v == "off" || v == "tcp" || v == "udp" || v == "tcp+udp";
        }

        private static string NormalizeFilter(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            string v = value.Trim().ToLowerInvariant();
            if (v == "none" || v == "disable") return "off";
            if (v == "both" || v == "tcp,udp" || v == "udp+tcp") return "tcp+udp";

            return IsValidFilter(v) ? v : "";
        }
    }

    /// <summary>
    /// Результат одного шага: что совпало и надо ли применять.
    ///
    /// Отдельный тип, а не `ProcessPreset` или `bool`: «пресета нет» и «ничего не делать»
    /// — разные вещи, и при сведении их к одному значению сброс пресета молча теряется.
    /// </summary>
    public sealed class PresetDecision
    {
        /// <summary>Совпавший пресет или null, если ни один не подходит.</summary>
        public ProcessPreset Preset;

        /// <summary>true — нужно перезапустить обход с новыми параметрами.</summary>
        public bool Apply;

        public string Reason = "";
    }

    /// <summary>
    /// Следит за процессами и переключает обход под запущенное приложение.
    ///
    /// Правила, без которых это принесло бы больше вреда, чем пользы:
    ///   * не трогаем выключенный обход — человек сам решает, когда он включён;
    ///   * не переключаем чаще, чем раз в CooldownSeconds: старт winws не бесплатный,
    ///     а приложения запускаются пачками (игра + лаунчер + оверлей);
    ///   * возвращаемся к стратегии из настроек, когда пресет перестал совпадать;
    ///   * любое переключение пишем в лог: молчаливая смена стратегии выглядит как баг.
    /// </summary>
    public sealed class ProcessPresetWatcher : IDisposable
    {
        private readonly ZapretService _zapret;
        private readonly object _gate = new object();

        private List<ProcessPreset> _presets = new List<ProcessPreset>();
        private ProcessPreset _active;
        private string _appliedId = "";
        private DateTime _lastSwitch = DateTime.MinValue;

        private Thread _thread;
        private volatile bool _stop;
        private bool _disposed;

        public ProcessPresetWatcher(ZapretService zapret)
        {
            _zapret = zapret;
        }

        /// <summary>Интервал опроса. 5 секунд — компромисс: не жжёт CPU и не опаздывает.</summary>
        public int PollSeconds = 5;

        /// <summary>Минимум между переключениями стратегии.</summary>
        public int CooldownSeconds = 20;

        /// <summary>Сработал другой пресет (или сброс). Вызывается из фонового потока.</summary>
        public event Action<ProcessPreset> Changed;

        public ProcessPreset Active
        {
            get { lock (_gate) return _active; }
        }

        public List<ProcessPreset> Presets
        {
            get { lock (_gate) return new List<ProcessPreset>(_presets); }
            set { lock (_gate) _presets = value ?? new List<ProcessPreset>(); }
        }

        /// <summary>
        /// Проверка «процесс запущен». Вынесена в поле, чтобы selftest мог
        /// прогнать сценарии без реально запущенных Discord и Counter-Strike.
        /// </summary>
        public Func<string, bool> ProcessChecker = ProcessPresets.IsRunning;

        /// <summary>Перечитывает пресеты с диска. Вызывается при старте и после правки файла.</summary>
        public void Reload()
        {
            List<ProcessPreset> presets = ProcessPresets.Load(ProcessPresets.FilePath);

            if (presets.Count == 0)
            {
                presets = ProcessPresets.Builtin();
                Log.Debug("PRSET", "файл пресетов не найден, взят встроенный набор");
            }

            lock (_gate) _presets = presets;
        }

        public void Start()
        {
            if (_thread != null) return;

            Reload();

            _stop = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "preset-watch"
            };
            _thread.Start();

            Log.Info("PRSET", "слежение за процессами запущено, пресетов: " + Presets.Count);
        }

        public void Stop()
        {
            _stop = true;

            Thread thread = _thread;
            _thread = null;

            if (thread != null)
            {
                try { thread.Join(2000); } catch { }
            }

            lock (_gate) { _active = null; _appliedId = ""; }
        }

        /// <summary>
        /// Один шаг: определить пресет и, если нужно, переключить обход.
        /// Отдельный метод — чтобы selftest мог вызвать его напрямую, без потока.
        /// </summary>
        public PresetDecision Tick(bool protectionRunning, string currentStrategy, string defaultStrategy,
                                   string defaultFilter)
        {
            ProcessPreset preset = ProcessPresets.Match(Presets, ProcessChecker);
            PresetDecision decision = new PresetDecision { Preset = preset };

            lock (_gate)
            {
                string presetId = preset == null ? "" : preset.Id;

                if (presetId == _appliedId)
                {
                    _active = preset;
                    decision.Reason = "без изменений";
                    return decision;
                }

                if (!protectionRunning)
                {
                    // обход выключен: запоминаем, но не включаем и не переключаем
                    _active = preset;
                    _appliedId = presetId;
                    decision.Reason = "обход выключен";
                    return decision;
                }

                if ((DateTime.UtcNow - _lastSwitch).TotalSeconds < CooldownSeconds)
                {
                    decision.Reason = "слишком часто, ждём";
                    return decision;
                }

                string strategy = preset != null && preset.Strategy.Length > 0
                    ? preset.Strategy
                    : defaultStrategy;

                string filter = preset != null && preset.GameFilter.Length > 0
                    ? preset.GameFilter
                    : defaultFilter;

                bool sameStrategy = string.Equals(strategy, currentStrategy, StringComparison.OrdinalIgnoreCase);
                bool sameFilter = string.Equals(filter, defaultFilter, StringComparison.OrdinalIgnoreCase);

                _active = preset;
                _appliedId = presetId;
                _lastSwitch = DateTime.UtcNow;

                if (sameStrategy && sameFilter)
                {
                    decision.Reason = "параметры уже совпадают";
                    return decision;
                }

                decision.Apply = true;
                decision.Reason = preset == null ? "сброс к стратегии из настроек" : "пресет " + preset.Name;
                return decision;
            }
        }

        /// <summary>Применяет пресет: останавливает winws и стартует с нужными параметрами.</summary>
        public string Apply(ProcessPreset preset, string defaultStrategy, string defaultFilter)
        {
            string strategy = preset != null && preset.Strategy.Length > 0
                ? preset.Strategy
                : defaultStrategy;

            string filter = preset != null && preset.GameFilter.Length > 0
                ? preset.GameFilter
                : defaultFilter;

            string label = preset == null ? "без пресета" : preset.Name;

            Log.Info("PRSET", "переключаю на " + label + ": стратегия " + strategy +
                              (filter.Length > 0 ? ", порты " + filter : ""));

            // Пресеты меняют аргументы winws (в том числе игровой фильтр),
            // а у своего движка аргументов нет — есть профили. Молча запустить
            // winws вместо выбранного движка значит подменить исполнителя,
            // поэтому отказываем явно.
            Settings current = Settings.Current;

            if (current != null && string.Equals(current.EngineKind, "own", StringComparison.OrdinalIgnoreCase))
            {
                const string message = "пресеты процессов работают только с zapret";

                Log.Warn("PRSET", message + ": движок «свой» не принимает аргументы winws");
                return message;
            }

            try
            {
                _zapret.Stop();

                // пауза на выгрузку драйвера: без неё старт уходит в пустоту
                Thread.Sleep(1200);

                string error = _zapret.Start(strategy, filter);
                if (error != null)
                {
                    Log.Warn("PRSET", "не удалось применить пресет '" + label + "': " + error);
                    return error;
                }

                Action<ProcessPreset> handler = Changed;
                if (handler != null) { try { handler(preset); } catch { } }

                return null;
            }
            catch (Exception ex)
            {
                Log.Exception("PRSET", "ошибка применения пресета", ex);
                return ex.Message;
            }
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    Settings settings = Settings.Current;
                    bool running = _zapret.State == ProtectionState.Running && settings.ProcessPresets;

                    PresetDecision decision = Tick(running, _zapret.ActiveStrategyId,
                                                   settings.StrategyId, settings.GameFilter);

                    if (decision.Apply)
                    {
                        Log.Info("PRSET", decision.Reason);
                        Apply(decision.Preset, settings.StrategyId, settings.GameFilter);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("PRSET", "опрос пресетов: " + ex.Message);
                }

                int step = Math.Max(1, PollSeconds);
                for (int i = 0; i < step * 4 && !_stop; i++)
                    Thread.Sleep(250);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
