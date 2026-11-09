using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>Один профиль из набора zapret, переведённый под Android.</summary>
    public sealed class ZapretPreset
    {
        public string Id = "";
        public string Name = "";
        public string Source = "";
        public string Note = "";

        /// <summary>Позиции разреза. Может быть пусто.</summary>
        public int[] Positions = new int[0];

        /// <summary>
        /// Динамическая позиция: "midsld" или "sniext+1".
        ///
        /// В конфигах winws позиция задаётся не числом, а смыслом — «середина
        /// домена второго уровня», «сразу после расширения SNI». Число зависит
        /// от конкретного ClientHello, поэтому считаем его на месте, когда
        /// пакет уже есть.
        /// </summary>
        public string PositionHint = "";

        public bool Disorder;
        public bool Oob;
        public bool Fake;
        public int FakeTtl = 8;

        /// <summary>Пусто — профиль работает. Иначе причина, почему нет.</summary>
        public string Unsupported = "";

        public bool Works { get { return Unsupported.Length == 0; } }

        public string Describe()
        {
            if (!Works) return Name + " — не поддерживается: " + Unsupported;

            StringBuilder text = new StringBuilder();

            if (Fake) text.Append("фейк");
            if (Fake && (Positions.Length > 0 || PositionHint.Length > 0)) text.Append(" + ");

            if (Positions.Length > 0 || PositionHint.Length > 0)
            {
                text.Append("разрез");

                if (PositionHint.Length > 0) text.Append(" (" + PositionHint + ")");
                else text.Append(" " + string.Join("+", Array.ConvertAll(Positions, delegate (int p) { return p.ToString(); })));
            }

            if (Disorder) text.Append(text.Length > 0 ? " + disorder" : "disorder");
            if (Oob) text.Append(text.Length > 0 ? " + oob" : "oob");

            if (text.Length == 0) return Name;

            return Name + ": " + text;
        }

        /// <summary>Перенести в рабочую конфигурацию прокси.</summary>
        public void ApplyTo(DesyncConfig config)
        {
            if (config == null || !Works) return;

            config.Enabled = true;
            config.Positions = Positions;
            config.PositionHint = PositionHint;
            config.Disorder = Disorder;
            config.Oob = Oob;
            config.Fake = Fake;
            config.FakeTtl = FakeTtl;
        }
    }

    /// <summary>
    /// Перевод стратегий zapret (winws) в техники Android.
    ///
    /// Зачем переводить, а не запускать. `winws` перехватывает трафик через
    /// WinDivert, `winws2` через NFQUEUE — обоим нужен доступ к сетевому стеку
    /// ядра, которого на Android нет. Запустить их нельзя ни при каких
    /// условиях. Но набор техник, который они описывают, частично выражается
    /// обычными сокетами, а значит переносится.
    ///
    /// Что переносится:
    ///   split / multisplit     -> разрез ClientHello в заданных позициях
    ///   disorder / multidisorder -> кусок с TTL=1 перед настоящим
    ///   fake                   -> подставной ClientHello с низким TTL
    ///   fakedsplit / hostfakesplit -> фейк + разрез
    ///
    /// Что НЕ переносится и честно помечается:
    ///   syndata        — ClientHello внутри SYN, нужен raw-сокет;
    ///   seqovl         — перекрытие seq, нужен доступ к очереди ядра;
    ///   --lua-desync   — произвольный скрипт zapret2;
    ///   подмена TTL у DPI-копий без фейка.
    ///
    /// Правило из AGENTS.md: «Стратегия, из которой молча выкинули половину
    /// опций, работает ровно настолько, чтобы виноватым оказался движок».
    /// Поэтому неподдерживаемое не выбрасывается молча, а показывается с
    /// причиной.
    /// </summary>
    public static class ZapretPresets
    {
        private static List<ZapretPreset> _cache;

        /// <summary>
        /// Загрузить и перевести наборы стратегий из ассетов приложения.
        ///
        /// Файлы — те же самые, что у десктопа (`assets/strategies/*.json`),
        /// подключены как ссылки в csproj. Один источник правды: правка
        /// стратегии на десктопе автоматически видна на Android.
        /// </summary>
        public static List<ZapretPreset> All(global::Android.Content.Context context)
        {
            if (_cache != null) return _cache;

            List<ZapretPreset> result = new List<ZapretPreset>();

            result.AddRange(Load(context, "strategies.v1.json"));

            // v2 целиком на lua-desync, то есть не переносится. Но показать
            // его с честной причиной полезнее, чем сделать вид, что набора нет.
            result.AddRange(Load(context, "strategies.v2.json"));

            _cache = result;

            return result;
        }

        public static List<ZapretPreset> Working(global::Android.Content.Context context)
        {
            List<ZapretPreset> result = new List<ZapretPreset>();

            foreach (ZapretPreset preset in All(context))
                if (preset.Works) result.Add(preset);

            return result;
        }

        public static ZapretPreset ById(global::Android.Content.Context context, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (ZapretPreset preset in All(context))
                if (preset.Id == id) return preset;

            return null;
        }

        // ------------------------------------------------------------ разбор

        private static List<ZapretPreset> Load(global::Android.Content.Context context, string assetName)
        {
            List<ZapretPreset> result = new List<ZapretPreset>();

            string json = ReadAsset(context, assetName);

            if (json == null) return result;

            try
            {
                using (Stream stream = context.Assets.Open(assetName))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch
            {
                return result;
            }
            // Свой минимальный разборщик JSON: сторонние библиотеки запрещены,
            // а структура здесь известна и простая — массив strategies, у
            // каждого массив profiles, у профиля массив args из строк.
            foreach (string strategyJson in SplitObjects(json, "\"strategies\""))
            {
                string id = Field(strategyJson, "id");
                string name = Field(strategyJson, "name");
                string note = Field(strategyJson, "note");
                string engine = Field(strategyJson, "engine");

                if (id.Length == 0) continue;

                ZapretPreset preset = Translate(id, name, note, engine, strategyJson);

                if (preset != null) result.Add(preset);
            }

            return result;
        }

        /// <summary>
        /// Прочитать ассет. Пробуем оба пути: сборка кладёт файлы в assets/ с
        /// префиксом Assets/, и точный путь зависит от того, как задан Link в
        /// csproj. Ошибка здесь стоит дорого — список профилей молча пустой.
        /// </summary>
        private static string ReadAsset(global::Android.Content.Context context, string assetName)
        {
            string[] candidates =
            {
                assetName,
                "Assets/" + assetName
            };

            foreach (string path in candidates)
            {
                try
                {
                    using (Stream stream = context.Assets.Open(path))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();

                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>Перевести одну стратегию: берём первый TCP-профиль, он и есть основной.</summary>
        private static ZapretPreset Translate(string id, string name, string note, string engine, string json)
        {
            ZapretPreset preset = new ZapretPreset
            {
                Id = "zapret." + id,
                Name = name,
                Note = note,
                Source = engine == "v2" ? "zapret2" : "zapret 1"
            };

            // ВАЖНО: профили разбираем по отдельности. Раньше аргументы
            // собирались со всех профилей сразу — и один UDP-профиль с seqovl
            // отбраковывал стратегию целиком, хотя её TCP-часть переносится
            // прекрасно. Именно поэтому «ни один профиль не переносится».
            List<List<string>> profiles = CollectProfiles(json);

            if (profiles.Count == 0)
            {
                preset.Unsupported = "нет аргументов";
                return preset;
            }

            // Ищем первый профиль, который вообще про TCP. UDP-профили нам не
            // годятся: desync у нас работает только на TCP, а UDP идёт как есть.
            List<string> args = null;
            bool sawSeqovl = false;
            bool sawLua = false;
            bool sawSyndata = false;

            foreach (List<string> candidate in profiles)
            {
                bool tcp = false;

                foreach (string arg in candidate)
                {
                    if (arg.StartsWith("--filter-tcp", StringComparison.Ordinal)) { tcp = true; break; }
                    if (arg.StartsWith("--filter-udp", StringComparison.Ordinal)) { tcp = false; break; }
                }

                if (!tcp) continue;

                // запоминаем, чего не хватает, чтобы потом честно сказать причину
                foreach (string arg in candidate)
                {
                    if (arg.StartsWith("--dpi-desync-split-seqovl", StringComparison.Ordinal)) sawSeqovl = true;
                    if (arg.StartsWith("--lua-desync", StringComparison.Ordinal)) sawLua = true;
                }

                args = candidate;
                break;
            }

            if (args == null)
            {
                preset.Unsupported = "нет TCP-профиля (только UDP)";
                return preset;
            }

            // ---- что просили
            string desync = "";
            List<int> positions = new List<int>();
            string hint = "";

            foreach (string arg in args)
            {
                if (arg.StartsWith("--dpi-desync=", StringComparison.Ordinal))
                {
                    desync = arg.Substring("--dpi-desync=".Length);
                }
                else if (arg.StartsWith("--dpi-desync-split-pos=", StringComparison.Ordinal))
                {
                    ParsePositions(arg.Substring("--dpi-desync-split-pos=".Length), positions, ref hint);
                }
                else if (arg.StartsWith("--dpi-desync-split-seqovl", StringComparison.Ordinal))
                {
                    sawSeqovl = true;
                }
                else if (arg.StartsWith("--lua-desync", StringComparison.Ordinal))
                {
                    sawLua = true;
                }
            }

            // ---- неподдерживаемое: говорим прямо, а не выкидываем молча
            if (sawLua)
            {
                preset.Unsupported = "zapret2 на lua-скриптах: нужен их движок, а не сокеты";
                return preset;
            }

            if (desync.Length == 0)
            {
                preset.Unsupported = "нет --dpi-desync в TCP-профиле";
                return preset;
            }

            // ---- перевод имён техник
            foreach (string kind in desync.Split(','))
            {
                string k = kind.Trim().ToLowerInvariant();

                if (k == "split" || k == "multisplit") continue;

                if (k == "disorder" || k == "multidisorder")
                {
                    preset.Disorder = true;
                    continue;
                }

                if (k == "fake")
                {
                    preset.Fake = true;
                    continue;
                }

                if (k == "fakedsplit" || k == "hostfakesplit")
                {
                    preset.Fake = true;
                    if (positions.Count == 0 && hint.Length == 0) positions.Add(1);
                    continue;
                }

                if (k == "oob" || k == "disoob")
                {
                    preset.Oob = true;
                    continue;
                }

                if (k == "syndata")
                {
                    sawSyndata = true;
                    continue;
                }

                if (k == "none" || k.Length == 0) continue;

                preset.Unsupported = "техника «" + k + "» не выражается через сокеты";
                return preset;
            }

            if (sawSyndata)
            {
                preset.Unsupported = "syndata: ClientHello внутри SYN, нужен raw-сокет";
                return preset;
            }

            // seqovl — это перекрытие seq, его без root не сделать. Но если
            // кроме него есть что перенести, переносим: урезанный профиль
            // лучше, чем ничего, и причина записывается в Note.
            if (sawSeqovl)
            {
                preset.Note = (preset.Note.Length > 0 ? preset.Note + " · " : "") +
                              "без seqovl (на Android недоступно)";
            }

            preset.Positions = positions.ToArray();
            preset.PositionHint = hint;

            // TTL фейка: в конфигах winws он чаще не задан (фейк убивается
            // fooling-ом). На Android без raw-сокетов единственный способ
            // убить фейк до сервера — низкий TTL, поэтому берём проверенное
            // на живой сети значение.
            preset.FakeTtl = 8;

            if (!preset.Fake && !preset.Disorder && !preset.Oob &&
                preset.Positions.Length == 0 && preset.PositionHint.Length == 0)
            {
                preset.Unsupported = "техника без действий, применимых на Android";
            }

            return preset;
        }

        /// <summary>
        /// Разобрать позиции разреза.
        ///
        /// winws понимает не только числа: `midsld` — середина домена второго
        /// уровня, `sniext+1` — сразу после расширения SNI. Это считается по
        /// конкретному ClientHello, поэтому запоминаем подсказку и решаем на
        /// месте.
        /// </summary>
        private static void ParsePositions(string value, List<int> positions, ref string hint)
        {
            foreach (string part in value.Split(','))
            {
                string p = part.Trim();

                if (p.Length == 0) continue;

                int number;

                if (int.TryParse(p, out number))
                {
                    if (number > 0) positions.Add(number);
                    continue;
                }

                if (p == "midsld" || p == "sniext+1" || p == "sniext" || p == "host")
                {
                    hint = p;
                }
            }
        }

        /// <summary>
        /// Собрать аргументы по профилям отдельно.
        ///
        /// Именно отдельно: `--filter-tcp` и `--filter-udp` описывают разные
        /// потоки, и смешивать их аргументы нельзя — UDP-профиль «заражает»
        /// TCP-профиль своими seqovl и fake-quic.
        /// </summary>
        private static List<List<string>> CollectProfiles(string json)
        {
            List<List<string>> result = new List<List<string>>();

            int index = 0;

            while (true)
            {
                int at = json.IndexOf("\"args\"", index, StringComparison.Ordinal);
                if (at < 0) break;

                int open = json.IndexOf('[', at);
                if (open < 0) break;

                int close = json.IndexOf(']', open);
                if (close < 0) break;

                string block = json.Substring(open, close - open);

                List<string> args = SplitQuoted(block);

                if (args.Count > 0) result.Add(args);

                index = close;
            }

            return result;
        }

        /// <summary>Разбить объекты массива strategies.</summary>
        private static List<string> SplitObjects(string json, string key)
        {
            List<string> result = new List<string>();

            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return result;

            int open = json.IndexOf('[', at);
            if (open < 0) return result;

            // Глубина считается от элементов массива, а не от самого массива:
            // на входе в первый объект стратегии она равна 0 и становится 1.
            // Раньше здесь было сравнение с 1 — из-за сдвига на единицу
            // собирались вложенные объекты профилей, и парсер не находил ни
            // одной стратегии.
            int depth = 0;
            int start = -1;

            for (int i = open + 1; i < json.Length; i++)
            {
                char c = json[i];

                if (c == '"')
                {
                    // пропускаем строку целиком, чтобы скобки внутри неё не считались
                    i++;

                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') break;

                        i++;
                    }

                    continue;
                }

                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;

                    if (depth == 0 && start >= 0)
                    {
                        result.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }

            return result;
        }

        private static List<string> SplitQuoted(string text)
        {
            List<string> result = new List<string>();

            int i = 0;

            while (i < text.Length)
            {
                int open = text.IndexOf('"', i);
                if (open < 0) break;

                StringBuilder value = new StringBuilder();
                i = open + 1;

                while (i < text.Length)
                {
                    char c = text[i];

                    if (c == '\\' && i + 1 < text.Length)
                    {
                        value.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (c == '"') { i++; break; }

                    value.Append(c);
                    i++;
                }

                result.Add(value.ToString());
            }

            return result;
        }

        private static string Field(string json, string name)
        {
            string key = "\"" + name + "\"";

            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";

            int colon = json.IndexOf(':', at + key.Length);
            if (colon < 0) return "";

            int quote = json.IndexOf('"', colon + 1);
            if (quote < 0) return "";

            StringBuilder value = new StringBuilder();
            int i = quote + 1;

            while (i < json.Length)
            {
                char c = json[i];

                if (c == '\\' && i + 1 < json.Length)
                {
                    value.Append(json[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"') break;

                value.Append(c);
                i++;
            }

            return value.ToString();
        }
    }
}
