using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AutoZapret.Core;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Что получилось из разбора стратегии winws.
    ///
    /// Неподдержанное собирается списком, а не глотается молча. Это главное
    /// решение всего файла: стратегия, из которой тихо выкинули половину
    /// опций, «работает» ровно настолько, чтобы человек решил, что виноват
    /// движок. Лучше честно сказать «вот это я не умею».
    /// </summary>
    public sealed class CompatResult
    {
        public OwnProfileSet Profiles = new OwnProfileSet();

        /// <summary>Опции, которые наш движок не умеет. С причиной.</summary>
        public List<string> Unsupported = new List<string>();

        /// <summary>Профили, которые пришлось выбросить целиком.</summary>
        public List<string> Dropped = new List<string>();

        public int ProfilesTotal;
        public int ProfilesUsable;

        /// <summary>
        /// Стратегия написана на Lua (zapret 2) — исполнять её нашим движком
        /// нельзя в принципе.
        ///
        /// Отдельный флаг, а не «просто ноль профилей»: у v2-стратегии есть
        /// `--filter-tcp` и списки, поэтому она РАЗБИРАЕТСЯ в набор профилей —
        /// только техники в них нет, и получилось бы «работает, но ничего не
        /// делает». Это худший из возможных исходов.
        /// </summary>
        public bool Lua;

        /// <summary>
        /// Файлы списков не найдены (движок не установлен, каталога lists нет).
        ///
        /// Отдельный флаг, потому что иначе это выглядит как «стратегия плохая»:
        /// профили выбрасываются один за другим с причиной «нет списков целей»,
        /// и человек решает, что наш движок не умеет исполнять стратегии.
        /// На самом деле не хватает данных, а не возможностей.
        /// </summary>
        public bool ListsMissing;

        /// <summary>Сколько файлов списков не нашлось.</summary>
        public int MissingFiles;


        /// <summary>Порты для WinDivert из `--wf-tcp` / `--wf-udp`.</summary>
        public string WfTcp = "";
        public string WfUdp = "";

        public bool Ok { get { return ProfilesUsable > 0 && !Lua && !ListsMissing; } }

        public string Summary()
        {
            if (Lua) return "zapret 2 (Lua) — своим движком не исполнить";

            if (ListsMissing)
                return "нет файлов списков (" + MissingFiles + " шт.) — движок не установлен, " +
                       "исполнять нечего: без списков обход трогал бы весь трафик";

            if (ProfilesTotal == 0) return "в стратегии нет профилей";

            string text = "профилей " + ProfilesUsable + " из " + ProfilesTotal;

            if (Unsupported.Count > 0) text += ", не поддержано опций: " + Unsupported.Count;
            if (Dropped.Count > 0) text += ", выброшено: " + Dropped.Count;

            return text;
        }

        /// <summary>Строка для лога: неподдержанное — по одному в строке.</summary>
        public string Detail()
        {
            List<string> lines = new List<string>();

            lines.Add(Summary());

            foreach (string d in Dropped) lines.Add("выброшен профиль: " + d);
            foreach (string u in Unsupported) lines.Add("не поддержано: " + u);

            return string.Join("\n", lines.ToArray());
        }
    }

    /// <summary>
    /// Разбор аргументов `winws` в нашу внутреннюю модель (ROADMAP P10.5).
    ///
    /// Зачем это вообще: у нас 22 стратегии Flowseal уже разобраны в
    /// `strategies.json` — то есть работа проделана, и она проверена на живых
    /// провайдерах. Если наш движок умеет исполнять те же описания, человек
    /// переключает «движок: встроенный / zapret» и видит ТЕ ЖЕ стратегии.
    /// Иначе выбор движка означал бы «другие стратегии», и сравнивать нечего.
    ///
    /// Что разбирается: порты (`--filter-tcp` / `--filter-udp`), списки
    /// (`--hostlist`, `--ipset` и их исключения), техника (`--dpi-desync` с
    /// параметрами), fooling, повторы, файлы фейков.
    ///
    /// Что не разбирается и почему — см. Unsupported. Ключевое:
    ///   * `--filter-l7` (discord/stun/quic) требует распознавания протокола
    ///     по содержимому: у нас его нет, и притворяться нельзя;
    ///   * `--lua-desync` (весь zapret2) — это исполняемый Lua, а не опции;
    ///   * `ts` в fooling — нужны TCP timestamps, которых мы не ставим.
    /// </summary>
    public static class WinwsCompat
    {
        /// <summary>Разобрать стратегию. placeholders — ${BIN}, ${LISTS} и прочее.</summary>
        public static CompatResult Parse(Strategy strategy, Dictionary<string, string> placeholders)
        {
            CompatResult result = new CompatResult();

            if (strategy == null) return result;

            // ---- глобальные опции: они про перехват, а не про технику
            foreach (string raw in strategy.GlobalArgs)
            {
                string arg = Substitute(raw, placeholders);

                string name, value;
                SplitOption(arg, out name, out value);

                switch (name)
                {
                    case "--wf-tcp":
                    case "--wf-tcp-out":
                        result.WfTcp = value;
                        break;

                    case "--wf-udp":
                    case "--wf-udp-out":
                        result.WfUdp = value;
                        break;

                    case "--debug":
                    case "--lua-init":
                    case "--blob":
                    case "--payload":
                        break;      // к обходу не относится

                    default:
                        Note(result, name + " (глобальная)");
                        break;
                }
            }

            // ---- профили: каждый — свой набор опций, как `--new` у winws
            result.ProfilesTotal = strategy.Profiles.Count;

            foreach (StrategyProfile source in strategy.Profiles)
            {
                string reason;
                OwnProfile profile = ParseProfile(source, placeholders, result, out reason);

                if (profile == null)
                {
                    string name = source.Name.Length > 0 ? source.Name : source.Label;

                    result.Dropped.Add(name + (reason.Length > 0 ? " (" + reason + ")" : ""));
                    continue;
                }

                result.Profiles.Add(profile);
                result.ProfilesUsable++;
            }

            if (result.Lua)
            {
                // Профили уже разобраны, но техники в них нет: у v2 всё
                // поведение живёт в Lua-скрипте. Оставлять набор в таком виде
                // нельзя — он бы «работал» и не делал ничего.
                result.Profiles = new OwnProfileSet();
                result.ProfilesUsable = 0;
            }

            // Списки не найдены — это не «стратегия плохая», а «движок не
            // установлен». Различать обязательно: на чистой машине и в CI
            // иначе получается «профилей 1 из 9» и виноват наш движок.
            //
            // Условие «хоть один файл не найден», а не «ни одного профиля»:
            // часть профилей живёт на inline-доменах (--hostlist-domains) и
            // выживает без файлов, из-за чего «1 из 9» выглядело как успех.
            if (result.MissingFiles > 0) result.ListsMissing = true;

            return result;
        }

        private static OwnProfile ParseProfile(StrategyProfile source,
                                               Dictionary<string, string> placeholders,
                                               CompatResult result,
                                               out string reason)
        {
            reason = "";

            OwnProfile profile = new OwnProfile
            {
                Name = source.Name.Length > 0 ? source.Name : source.Label,
                TcpPorts = new List<int>(),
                UdpPorts = new List<int>(),
                Technique = new TechniqueOptions()
            };

            OwnFilter filter = new OwnFilter();

            bool anyProtocol = false;
            bool udpProfile = false;
            string fakeTls = "", fakeQuic = "", fakeHttp = "", fakeStun = "", fakeDiscord = "";

            foreach (string raw in source.Args)
            {
                string arg = Substitute(raw, placeholders);

                string name, value;
                SplitOption(arg, out name, out value);

                switch (name)
                {
                    // ---- порты
                    case "--filter-tcp":
                        AddPorts(profile.TcpPorts, value, result, name);
                        break;

                    case "--filter-udp":
                        AddPorts(profile.UdpPorts, value, result, name);
                        udpProfile = true;
                        break;

                    case "--filter-l3":
                        // ipv4/ipv6: наш движок разбирает только IPv4
                        if (value.IndexOf("ipv6", StringComparison.OrdinalIgnoreCase) >= 0)
                            Note(result, name + "=" + value + " (только IPv4)");
                        break;

                    case "--filter-l7":
                        // Распознавание протокола по содержимому — единственное,
                        // чего у нас нет принципиально. Профиль выбросить целиком
                        // нельзя: у него бывают и порты. Оставляем, но говорим.
                        Note(result, name + "=" + value +
                                     " (нет распознавания протокола — профиль работает по портам)");
                        break;

                    // ---- списки
                    case "--hostlist":
                        LoadList(filter, value, false, result);
                        break;

                    case "--hostlist-exclude":
                        LoadList(filter, value, true, result);
                        break;

                    case "--ipset":
                        LoadIpSet(filter, value, false, result);
                        break;

                    case "--ipset-exclude":
                        LoadIpSet(filter, value, true, result);
                        break;

                    case "--hostlist-domains":
                        foreach (string d in value.Split(','))
                            filter.AddHost(d.Trim());
                        break;

                    case "--hostlist-exclude-domains":
                        foreach (string d in value.Split(','))
                            filter.AddExcludeHost(d.Trim());
                        break;

                    // ---- техника
                    case "--dpi-desync":
                        ParseDesync(value, profile.Technique, result);
                        break;

                    case "--dpi-desync-split-pos":
                        AddPositions(profile.Technique, value);
                        break;

                    case "--dpi-desync-repeats":
                        profile.Technique.Repeats = Clamp(Int(value, profile.Technique.Repeats), 1, 16);
                        break;

                    case "--dpi-desync-split-seqovl":
                        profile.Technique.Overlap = Clamp(Int(value, 0), 0, 1400);
                        break;

                    case "--dpi-desync-fooling":
                        ParseFooling(value, profile.Technique, result);
                        break;

                    case "--dpi-desync-badseq-increment":
                        // шаг для badseq у нас равен длине payload; своё значение
                        // поддержим, только если оно осмысленное
                        Note(result, name + "=" + value + " (шаг берётся из длины payload)");
                        break;

                    case "--dpi-desync-any-protocol":
                        anyProtocol = true;
                        break;

                    case "--dpi-desync-cutoff":
                        // «перестать после N срабатываний в соединении»: у нас
                        // нет учёта соединений, только пакетов
                        Note(result, name + "=" + value + " (нет учёта соединений)");
                        break;

                    // ---- файлы фейков
                    case "--dpi-desync-fake-tls":
                        fakeTls = value;
                        break;

                    case "--dpi-desync-fake-quic":
                    case "--dpi-desync-fake-unknown-udp":
                        fakeQuic = value;
                        break;

                    case "--dpi-desync-fake-http":
                        fakeHttp = value;
                        break;

                    case "--dpi-desync-fake-stun":
                        fakeStun = value;
                        break;

                    // Discord-голос: у Flowseal это отдельная опция, но payload
                    // тот же STUN-совместимый. Игнорировать её значит потерять
                    // именно тот файл, которым обходится голос.
                    case "--dpi-desync-fake-discord":
                        fakeDiscord = value;
                        break;

                    case "--dpi-desync-fake-tls-mod":
                    case "--dpi-desync-hostfakesplit-mod":
                    case "--dpi-desync-split-seqovl-pattern":
                    case "--dpi-desync-fakedsplit-pattern":
                    case "--dpi-desync-fake-unknown":
                    case "--ip-id":
                        Note(result, name);
                        break;

                    // ---- zapret2
                    case "--lua-desync":
                        result.Lua = true;
                        Note(result, "--lua-desync (это Lua, а не опции)");
                        break;

                    case "--new":
                        break;

                    default:
                        if (name.StartsWith("--", StringComparison.Ordinal))
                            Note(result, name);
                        break;
                }
            }

            // ---- собираем профиль
            if (profile.TcpPorts.Count == 0 && profile.UdpPorts.Count == 0)
            {
                // Профиль без портов у winws значит «все порты из --wf-*».
                // У нас «все» — это весь TCP, то есть банк и госуслуги тоже.
                // Такой профиль честнее выбросить, чем включить вслепую.
                reason = "нет портов";
                return null;
            }

            profile.Technique.AnyProtocol = anyProtocol;

            // UDP: «fake» здесь — самостоятельная техника, а не префикс
            // к TCP-разрезанию. В TCP-разборе `fake` идёт перед основной
            // техникой, и парсер ставит Kind=Off + префикс. Для UDP основной
            // техники нет вовсе, значит было бы «ничего не делать».
            if (udpProfile &&
                profile.Technique.Kind == Technique.Off &&
                profile.Technique.Prefix != null &&
                profile.Technique.Prefix.Contains(Technique.Fake))
            {
                profile.Technique.Kind = Technique.Fake;
                profile.Technique.Prefix.Clear();
            }

            // Файл фейка. У UDP приоритет у того, что относится к голосу:
            // STUN и discord — это и есть RTC, ради которого всё делается.
            string fakeFile = udpProfile
                ? FirstFake(fakeStun, fakeDiscord, fakeQuic)
                : fakeTls;

            if (fakeFile.Length == 0) fakeFile = udpProfile ? "" : fakeHttp;
            if (fakeFile.Length == 0 && fakeStun.Length > 0) fakeFile = fakeStun;

            if (fakeFile.Length > 0)
            {
                if (fakeFile == "!")
                {
                    // `!` в конфигах zapret — «фейк не из файла, а свой».
                    // Для нас это ровно то, что мы и так делаем генератором.
                }
                else if (File.Exists(fakeFile))
                {
                    profile.Technique.FakeFile = fakeFile;
                }
                else
                {
                    // Не ошибка: фейк сгенерируем сами, и это лучше, чем
                    // отправить мусор из несуществующего файла. Полный путь
                    // в отчёте — чтобы было видно, чего именно не хватает.
                    Note(result, "файл фейка не найден: " + fakeFile + " (сгенерируем сами)");
                }
            }

            if (filter.HasLimits)
            {
                profile.Filter = filter;
            }
            else if (IsPortOnlyUdp(profile))
            {
                // Профиль по UDP-портам без списков — это норма, а не ошибка.
                //
                // Голосовой Discord/STUN у Flowseal задан именно так:
                // `--filter-udp=19294-19344,50000-50100` и `--filter-l7=...`.
                // Списков доменов там нет и быть не может: у STUN нет SNI,
                // а цели определяются портами. Если такой профиль выбрасывать,
                // голос не обходится вообще — что и наблюдалось.
                //
                // Безопасность сохраняется: порты взяты из конфига стратегии,
                // а не «весь UDP».
            }
            else
            {
                // Без списков TCP-профиль трогал бы весь трафик на своих
                // портах, то есть весь интернет. Не то, чего ждёт человек,
                // включивший стратегию.
                reason = "нет списков целей — работал бы на весь трафик";
                return null;
            }

            return profile;
        }

        /// <summary>
        /// UDP-профиль, у которого нет TCP-портов, а техника работает по UDP.
        ///
        /// Только такие профили можно оставить без списков целей: их границы —
        /// это порты из конфига. Всё остальное без списков трогало бы лишнее.
        /// </summary>
        private static bool IsPortOnlyUdp(OwnProfile profile)
        {
            if (profile == null) return false;
            if (profile.TcpPorts != null && profile.TcpPorts.Count > 0) return false;
            if (profile.UdpPorts == null || profile.UdpPorts.Count == 0) return false;

            TechniqueOptions technique = profile.Technique;

            return technique != null &&
                   (technique.Kind == Technique.Fake || technique.Kind == Technique.UdpLen);
        }

        /// <summary>Первый непустой путь — приоритет у STUN/Discord.</summary>
        private static string FirstFake(params string[] candidates)
        {
            foreach (string value in candidates)
                if (!string.IsNullOrEmpty(value)) return value;

            return "";
        }

        // ------------------------------------------------------------------ desync

        private static void ParseDesync(string value, TechniqueOptions options, CompatResult result)
        {
            // `fake,multisplit` — цепочка: сначала фейк, потом основная техника.
            // `syndata,multidisorder` — то же с SYN.
            string main = "";

            foreach (string part in value.Split(','))
            {
                string name = part.Trim().ToLowerInvariant();
                if (name.Length == 0) continue;

                switch (name)
                {
                    case "fake":
                        options.Prefix.Add(Technique.Fake);
                        break;

                    case "syndata":
                        options.Prefix.Add(Technique.SynData);
                        break;

                    case "split":
                    case "multisplit":
                    case "multidisorder":
                    case "hostfakesplit":
                    case "fakedsplit":
                        if (main.Length > 0)
                        {
                            // Две основные техники в одной строке наш движок не
                            // умеет: они по-разному меняют seq, и совместить их
                            // без полноценного конвейера winws нельзя.
                            Note(result, "--dpi-desync: вторая техника " + name + " пропущена");
                            break;
                        }

                        main = name;
                        break;

                    case "none":
                    case "off":
                        break;

                    default:
                        Note(result, "--dpi-desync=" + name);
                        break;
                }
            }

            switch (main)
            {
                case "split": options.Kind = Technique.Split; break;
                case "multisplit": options.Kind = Technique.MultiSplit; break;
                case "multidisorder": options.Kind = Technique.MultiDisorder; break;
                case "hostfakesplit": options.Kind = Technique.HostFakeSplit; break;
                case "fakedsplit": options.Kind = Technique.FakeSplit; break;
                default:
                    // только префикс (например, один fake) — основная техника
                    // не нужна, оригинал уходит после фейка
                    if (options.Prefix.Count > 0) options.Kind = Technique.Off;
                    break;
            }
        }

        private static void ParseFooling(string value, TechniqueOptions options, CompatResult result)
        {
            foreach (string part in value.Split(','))
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "badsum": options.BadSum = true; break;
                    case "badseq": options.BadSeq = true; break;
                    case "md5sig": options.Md5Sig = true; break;
                    case "datanoack": options.DataNoAck = true; break;
                    case "": break;

                    case "ts":
                        Note(result, "fooling ts (нужны TCP timestamps, мы их не ставим)");
                        break;

                    case "hopbyhop":
                        Note(result, "fooling hopbyhop (только IPv6)");
                        break;

                    default:
                        Note(result, "fooling " + part.Trim());
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Разобрать только строку `--dpi-desync=...` в набор техник.
        ///
        /// Отдельный вход для тестов: цепочки вида `fake,multisplit` — это
        /// свойство парсера, а не конкретной стратегии из assets. Проверять их
        /// на живом наборе значит зависеть от того, что сегодня лежит в данных.
        /// </summary>
        public static TechniqueOptions ParseTechnique(string desync, out List<string> unsupported)
        {
            CompatResult scratch = new CompatResult();
            TechniqueOptions options = new TechniqueOptions();

            if (!string.IsNullOrEmpty(desync)) ParseDesync(desync, options, scratch);

            unsupported = scratch.Unsupported;

            return options;
        }

        private static void AddPorts(List<int> ports, string value, CompatResult result, string option)
        {
            foreach (string part in value.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;

                if (p == "*")
                {
                    // «все порты» — нам нечего с этим сделать безопасно
                    Note(result, option + "=* (все порты не поддерживаются)");
                    continue;
                }

                int dash = p.IndexOf('-');

                if (dash > 0)
                {
                    int from = Int(p.Substring(0, dash), 0);
                    int to = Int(p.Substring(dash + 1), 0);

                    // диапазон разворачиваем в список: у нас порты — список,
                    // а не интервалы, и придумывать интервалы ради 50000-50100
                    // значит усложнить горячий путь ради одного конфига
                    if (from <= 0 || to < from || to - from > 512)
                    {
                        Note(result, option + "=" + p + " (диапазон слишком широкий)");
                        continue;
                    }

                    for (int port = from; port <= to; port++)
                        if (!ports.Contains(port)) ports.Add(port);

                    continue;
                }

                int single = Int(p, 0);
                if (single > 0 && single < 65536 && !ports.Contains(single)) ports.Add(single);
            }
        }

        private static void AddPositions(TechniqueOptions options, string value)
        {
            options.Positions.Clear();

            foreach (string part in value.Split(','))
            {
                int p = Int(part.Trim(), 0);
                if (p > 0 && !options.Positions.Contains(p)) options.Positions.Add(p);
            }

            if (options.Positions.Count > 0) options.Position = options.Positions[0];
        }

        private static void LoadList(OwnFilter filter, string path, bool exclude, CompatResult result)
        {
            if (!File.Exists(path))
            {
                // `*-user.txt` — личные дополнения, их отсутствие это норма:
                // поставка может их не содержать, а zapret работает и без них.
                // Считать их пропажей значит объявить «движок не установлен»
                // там, где всё в порядке.
                if (IsOptionalList(Path.GetFileName(path))) return;

                result.MissingFiles++;
                Note(result, "список не найден: " + Path.GetFileName(path));
                return;
            }

            filter.LoadHosts(path, exclude);
        }

        private static void LoadIpSet(OwnFilter filter, string path, bool exclude, CompatResult result)
        {
            if (!File.Exists(path))
            {
                if (IsOptionalList(Path.GetFileName(path))) return;

                result.MissingFiles++;
                Note(result, "ipset не найден: " + Path.GetFileName(path));
                return;
            }

            filter.LoadIpSet(path, exclude);
        }

        /// <summary>Файл, которого может не быть, и это не поломка.</summary>
        private static bool IsOptionalList(string fileName)
        {
            string name = (fileName ?? "").ToLowerInvariant();

            return name.EndsWith("-user.txt", StringComparison.Ordinal);
        }

        private static void SplitOption(string arg, out string name, out string value)
        {
            int eq = arg.IndexOf('=');

            if (eq < 0)
            {
                name = arg.Trim();
                value = "";
                return;
            }

            name = arg.Substring(0, eq).Trim();
            value = arg.Substring(eq + 1).Trim();
        }

        private static string Substitute(string arg, Dictionary<string, string> placeholders)
        {
            return Strategy.Substitute(arg, placeholders);
        }

        private static int Int(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// Запомнить неподдержанное. Дубликаты не плодим: одна и та же опция
        /// в девяти профилях — это одна строка в отчёте, а не девять.
        /// </summary>
        private static void Note(CompatResult result, string what)
        {
            if (what.Length == 0) return;
            if (result.Unsupported.Contains(what)) return;

            result.Unsupported.Add(what);
        }
    }
}
