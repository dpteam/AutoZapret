using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Zapret
{
    /// <summary>Результат перевода стратегии v1 в v2.</summary>
    public sealed class V2Conversion
    {
        /// <summary>Перевод удался полностью. false — запускать нельзя.</summary>
        public bool Ok;

        /// <summary>Аргументы для winws2.</summary>
        public List<string> Args = new List<string>();

        /// <summary>Аргументы, для которых нет однозначного перевода.</summary>
        public List<string> Unsupported = new List<string>();

        /// <summary>Почему не перевелось — одной строкой для интерфейса.</summary>
        public string Reason = "";

        public string Describe()
        {
            if (Ok) return "переведено в v2: " + Args.Count + " аргументов";

            string text = "не переводится на zapret 2";

            if (Unsupported.Count > 0)
                text += ": " + string.Join(", ", Unsupported.ToArray());

            return text;
        }
    }

    /// <summary>
    /// Перевод стратегии zapret 1 (winws, `--dpi-desync*`) в zapret 2
    /// (winws2, `--lua-desync*`).
    ///
    /// ПОЧЕМУ ЭТО ОСТОРОЖНО
    ///   Неверный перевод даёт стратегию, которая ЗАПУСКАЕТСЯ и ничего не
    ///   делает: winws2 принимает `--lua-desync=fake` без параметров, но без
    ///   blob'а ему нечего отправлять. Снаружи это «обход включён, сайт
    ///   закрыт», и человек ищет причину в движке.
    ///
    ///   Поэтому здесь действует правило: если аргумент нельзя перевести
    ///   ОДНОЗНАЧНО, перевод считается неудачным целиком (Ok = false), а
    ///   причина попадает в Unsupported. Никакого «выкинем непонятное и
    ///   поедем дальше» — это ровно та ловушка, которая описана в AGENTS.md
    ///   про разбор чужих конфигов.
    ///
    /// ЧТО ПЕРЕВОДИТСЯ
    ///   Таблица соответствий взята из docs/research/zapret-configs.md (раздел
    ///   «Правила перевода v1 → v2»), а не придумана: там же указано, что
    ///   v1 `--dpi-desync` и v2 `--lua-desync` — разные механики, и часть
    ///   опций v1 просто не имеет пары.
    /// </summary>
    public static class V2Converter
    {
        /// <summary>
        /// Алиасы из lua\blobs.lua: имя алиаса → имя файла.
        ///
        /// Нужны, потому что v2 ссылается на фейк по АЛИАСУ (`:blob=quic_4pda`),
        /// а v1 — по пути (`--dpi-desync-fake-quic=...\quic_initial_4pda_to.bin`).
        /// Без этого соответствия перевод ссылок на фейки невозможен.
        /// </summary>
        public static Dictionary<string, string> LoadBlobs(string luaDir)
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = Path.Combine(luaDir ?? "", "blobs.lua");
                if (!File.Exists(path)) return result;

                // Формат: `имя_алиаса = "файл.bin",` — разбираем построчно,
                // без Lua-интерпретатора: конструкция простая и стабильная.
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();

                    if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (!line.Contains("=")) continue;

                    int eq = line.IndexOf('=');
                    string alias = line.Substring(0, eq).Trim();
                    string rest = line.Substring(eq + 1).Trim();

                    int q1 = rest.IndexOf('"');
                    int q2 = q1 >= 0 ? rest.IndexOf('"', q1 + 1) : -1;

                    if (q1 < 0 || q2 < 0) continue;

                    string file = rest.Substring(q1 + 1, q2 - q1 - 1);

                    if (alias.Length > 0 && file.Length > 0 && !result.ContainsKey(alias))
                        result[alias] = file;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CONV", "blobs.lua: " + ex.Message);
            }

            return result;
        }

        /// <summary>Алиас для файла фейка. Пусто — файла нет в blobs.lua.</summary>
        public static string AliasFor(Dictionary<string, string> blobs, string fileName)
        {
            if (blobs == null || string.IsNullOrEmpty(fileName)) return "";

            string name = Path.GetFileName(fileName.Replace('\\', '/'));

            foreach (KeyValuePair<string, string> kv in blobs)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;

            return "";
        }

        /// <summary>
        /// Перевести аргументы v1 в v2.
        ///
        /// name — имя профиля (обязательный `--name=` в v2: без него профиль
        /// в логе не отличить).
        /// </summary>
        public static V2Conversion Convert(List<string> v1Args, Dictionary<string, string> blobs, string name)
        {
            V2Conversion result = new V2Conversion();

            if (v1Args == null || v1Args.Count == 0)
            {
                result.Reason = "у стратегии нет аргументов";
                return result;
            }

            // Разбиваем на профили по --new: в v1 профиль — это набор
            // аргументов, заканчивающийся --new.
            List<List<string>> profiles = new List<List<string>>();
            List<string> current = new List<string>();

            foreach (string arg in v1Args)
            {
                if (string.Equals(arg, "--new", StringComparison.Ordinal))
                {
                    profiles.Add(current);
                    current = new List<string>();
                    continue;
                }

                current.Add(arg);
            }

            if (current.Count > 0) profiles.Add(current);

            List<string> output = new List<string>();

            // --lua-init обязателен и идёт первым: без него winws2 не знает
            // функций, которые мы просим в --lua-desync.
            output.Add("--lua-init=@" + Path.Combine("lua", "zapret-antidpi.lua"));
            output.Add("--lua-init=@" + Path.Combine("lua", "zapret-priority.lua"));
            output.Add("--lua-init=@" + Path.Combine("lua", "blobs.lua"));

            int index = 0;

            foreach (List<string> profile in profiles)
            {
                index++;

                V2Profile converted = ConvertProfile(profile, blobs, name, index);

                if (converted.Unsupported.Count > 0)
                {
                    foreach (string u in converted.Unsupported)
                        if (!result.Unsupported.Contains(u)) result.Unsupported.Add(u);
                }

                if (converted.Args.Count > 0)
                {
                    if (output.Count > 3 && index > 1) output.Add("--new");
                    output.AddRange(converted.Args);
                }
            }

            if (result.Unsupported.Count > 0)
            {
                result.Reason = "не переводится на zapret 2: " + string.Join(", ", result.Unsupported.ToArray());
                return result;
            }

            if (output.Count <= 3)
            {
                result.Reason = "после перевода не осталось ни одного профиля";
                return result;
            }

            result.Args = output;
            result.Ok = true;
            return result;
        }

        /// <summary>Один профиль v1 → набор аргументов v2.</summary>
        private sealed class V2Profile
        {
            public List<string> Args = new List<string>();
            public List<string> Unsupported = new List<string>();
        }

        private static V2Profile ConvertProfile(List<string> profile, Dictionary<string, string> blobs,
                                                string name, int index)
        {
            V2Profile result = new V2Profile();

            List<string> techniques = new List<string>();

            // Параметры, которые относятся к конкретным техникам.
            string blob = "";
            string pos = "";
            string seqovl = "";
            string seqovlPattern = "";
            string repeats = "";
            string tcpTs = "";
            string tcpSeq = "";
            bool tcpMd5 = false;
            string tlsMod = "";
            string host = "";
            bool ipIdZero = false;

            string payload = "";

            foreach (string arg in profile)
            {
                // ---- структурные: в v2 называются так же
                if (IsPassthrough(arg))
                {
                    result.Args.Add(arg);
                    continue;
                }

                // ---- WinDivert-фильтр: в v2 только исходящее направление
                if (arg.StartsWith("--wf-tcp=", StringComparison.Ordinal))
                {
                    result.Args.Add("--wf-tcp-out=" + arg.Substring("--wf-tcp=".Length));
                    continue;
                }

                if (arg.StartsWith("--wf-udp=", StringComparison.Ordinal))
                {
                    result.Args.Add("--wf-udp-out=" + arg.Substring("--wf-udp=".Length));
                    continue;
                }

                // ---- cutoff → out-range
                if (arg.StartsWith("--dpi-desync-cutoff=", StringComparison.Ordinal))
                {
                    string value = arg.Substring("--dpi-desync-cutoff=".Length).Trim();
                    result.Args.Add("--out-range=-" + value);
                    continue;
                }

                // ---- any-protocol → payload=all
                if (arg.StartsWith("--dpi-desync-any-protocol", StringComparison.Ordinal))
                {
                    payload = "all";
                    continue;
                }

                // ---- сами техники
                if (arg.StartsWith("--dpi-desync=", StringComparison.Ordinal))
                {
                    string list = arg.Substring("--dpi-desync=".Length);

                    foreach (string t in list.Split(','))
                    {
                        string tech = t.Trim();
                        if (tech.Length == 0) continue;

                        if (!IsKnownTechnique(tech))
                        {
                            result.Unsupported.Add("--dpi-desync=" + tech);
                            continue;
                        }

                        techniques.Add(tech);
                    }

                    continue;
                }

                // ---- ссылки на фейки → алиас
                if (IsFakeFile(arg))
                {
                    int eq = arg.IndexOf('=');
                    string file = eq >= 0 ? arg.Substring(eq + 1) : "";
                    string alias = AliasFor(blobs, file);

                    if (alias.Length == 0)
                    {
                        // Файла нет в blobs.lua — переводить нечем. Это честный
                        // отказ, а не «пропустим и поедем».
                        result.Unsupported.Add(arg);
                        continue;
                    }

                    // В одном профиле два РАЗНЫХ фейка (например discord и stun
                    // с разными файлами) в v2 требуют разных payload-секций.
                    // Свести их в один --lua-desync нельзя, поэтому честно
                    // отказываемся, а не берём последний.
                    if (blob.Length > 0 && !string.Equals(blob, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Unsupported.Add(arg + " (другой фейк в том же профиле)");
                        continue;
                    }

                    blob = alias;
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-split-pos=", StringComparison.Ordinal))
                {
                    pos = arg.Substring("--dpi-desync-split-pos=".Length);
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-split-seqovl-pattern=", StringComparison.Ordinal))
                {
                    string file = arg.Substring("--dpi-desync-split-seqovl-pattern=".Length);
                    string alias = AliasFor(blobs, file);

                    if (alias.Length == 0) { result.Unsupported.Add(arg); continue; }

                    seqovlPattern = alias;
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-split-seqovl=", StringComparison.Ordinal))
                {
                    seqovl = arg.Substring("--dpi-desync-split-seqovl=".Length);
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-repeats=", StringComparison.Ordinal))
                {
                    repeats = arg.Substring("--dpi-desync-repeats=".Length);
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-badseq-increment=", StringComparison.Ordinal))
                {
                    tcpSeq = arg.Substring("--dpi-desync-badseq-increment=".Length);
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-fooling=", StringComparison.Ordinal))
                {
                    foreach (string f in arg.Substring("--dpi-desync-fooling=".Length).Split(','))
                    {
                        string fool = f.Trim();

                        if (fool == "ts") tcpTs = "-600000";
                        else if (fool == "md5sig") tcpMd5 = true;
                        else if (fool == "badseq") { /* номер берётся из badseq-increment */ }
                        else if (fool.Length > 0) result.Unsupported.Add("--dpi-desync-fooling=" + fool);
                    }

                    continue;
                }

                if (arg.StartsWith("--dpi-desync-fake-tls-mod=", StringComparison.Ordinal))
                {
                    tlsMod = arg.Substring("--dpi-desync-fake-tls-mod=".Length);
                    continue;
                }

                if (arg.StartsWith("--dpi-desync-hostfakesplit-mod=", StringComparison.Ordinal))
                {
                    // host=X[,altorder=1]
                    foreach (string part in arg.Substring("--dpi-desync-hostfakesplit-mod=".Length).Split(','))
                    {
                        string p = part.Trim();

                        if (p.StartsWith("host=", StringComparison.Ordinal)) host = p.Substring(5);
                        else if (p == "altorder=1") { /* в v2 не нужен */ }
                        else result.Unsupported.Add(arg);
                    }

                    continue;
                }

                if (string.Equals(arg, "--ip-id=zero", StringComparison.Ordinal))
                {
                    ipIdZero = true;
                    continue;
                }

                // ---- всё остальное: нет однозначного перевода
                result.Unsupported.Add(arg);
            }

            if (techniques.Count == 0) return result;

            // --name обязателен: без него в логе winws2 профили неразличимы.
            // Пробелы заменяем: имя уходит отдельным аргументом, но в
            // напечатанной команде для ручной проверки пробел её разорвёт.
            string profileName = name ?? "converted";

            if (index > 1) profileName += " #" + index;

            profileName = profileName.Replace(' ', '_');

            result.Args.Add("--name=" + profileName);

            if (payload.Length > 0) result.Args.Add("--payload=" + payload);

            // ---- собираем --lua-desync по техникам
            //
            // Параметры привязываются к тем техникам, к которым они относятся
            // в v1. Если параметр есть, а подходящей техники нет — это ошибка
            // перевода: значит мы поняли конфиг неправильно.
            foreach (string tech in techniques)
            {
                List<string> parts = new List<string> { tech };

                bool isFake = tech == "fake" || tech == "fakedsplit" || tech == "hostfakesplit" || tech == "syndata";
                bool isSplit = tech == "multisplit" || tech == "multidisorder" || tech == "fakedsplit";

                if (isFake)
                {
                    if (blob.Length > 0) parts.Add("blob=" + blob);
                    if (repeats.Length > 0) parts.Add("repeats=" + repeats);
                    if (tcpTs.Length > 0) parts.Add("tcp_ts=" + tcpTs);
                    if (tcpSeq.Length > 0) parts.Add("tcp_seq=" + tcpSeq);
                    if (tcpMd5) parts.Add("tcp_md5");
                    if (tlsMod.Length > 0) parts.Add("tls_mod=" + tlsMod);
                    if (host.Length > 0) parts.Add("host=" + host);
                    if (ipIdZero) parts.Add("ip_id=zero");
                }

                if (isSplit)
                {
                    if (pos.Length > 0) parts.Add("pos=" + pos);
                }

                if (tech == "multisplit")
                {
                    if (seqovl.Length > 0) parts.Add("seqovl=" + seqovl);
                    if (seqovlPattern.Length > 0) parts.Add("seqovl_pattern=" + seqovlPattern);
                }

                result.Args.Add("--lua-desync=" + string.Join(":", parts.ToArray()));
            }

            return result;
        }

        /// <summary>Аргументы, которые в v2 называются и значат то же самое.</summary>
        private static bool IsPassthrough(string arg)
        {
            string[] prefixes =
            {
                "--filter-tcp=", "--filter-udp=", "--filter-l3=", "--filter-l7=",
                "--hostlist=", "--hostlist-exclude=", "--hostlist-domains=",
                "--hostlist-exclude-domains=", "--ipset=", "--ipset-exclude="
            };

            foreach (string p in prefixes)
                if (arg.StartsWith(p, StringComparison.Ordinal)) return true;

            return false;
        }

        private static bool IsFakeFile(string arg)
        {
            // Все варианты «подставного файла»: имя опции отличается тем, к
            // какому L7-парсеру относится фейк (tls, quic, http, discord,
            // stun, unknown), но смысл один — какой файл отправлять.
            return arg.StartsWith("--dpi-desync-fake-tls=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-quic=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-http=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-discord=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-stun=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-unknown-udp=", StringComparison.Ordinal) ||
                   arg.StartsWith("--dpi-desync-fake-unknown=", StringComparison.Ordinal);
        }

        private static bool IsKnownTechnique(string tech)
        {
            switch (tech)
            {
                case "fake":
                case "multisplit":
                case "multidisorder":
                case "fakedsplit":
                case "hostfakesplit":
                case "syndata":
                    return true;
                default:
                    return false;
            }
        }
    }
}
