using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;
using AutoZapret.Payloads;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Один слот подстановки фейка: опция стратегии и файл, на который она
    /// сейчас указывает.
    /// </summary>
    public sealed class FakeSlot
    {
        /// <summary>Имя опции без `=` — например `--dpi-desync-fake-tls`.</summary>
        public string Option = "";

        /// <summary>Текущее имя файла — только имя, без каталога.</summary>
        public string File = "";

        /// <summary>Какой это вид фейка: TLS / QUIC / STUN.</summary>
        public PayloadKind Kind = PayloadKind.Unknown;

        public string Describe()
        {
            return Option.Replace("--dpi-desync-", "") + " = " + File;
        }
    }

    /// <summary>
    /// Подбор фейка (.bin) для стратегии.
    ///
    /// Зачем. У нас 1700+ файлов фейков, и в стратегии используется ровно тот,
    /// что выбрал автор набора — на ЕГО провайдере. На другом провайдере тот же
    /// DPI реагирует на другой домен, и правильный фейк другой. Подбирать это
    /// руками невозможно: переписать аргумент, перезапустить движок, проверить,
    /// повторить сотни раз.
    ///
    /// Что делает: берёт стратегию, находит в ней опции, указывающие на .bin,
    /// подставляет по очереди каждый файл ТОГО ЖЕ вида (TLS к TLS, QUIC к QUIC)
    /// и проверяет обычным перебором стратегий. Побеждает тот, что дал
    /// наибольший счёт.
    ///
    /// Чего НЕ делает: не смешивает виды. Подставить TLS-фейк в
    /// `--dpi-desync-fake-quic` — значит отправить в QUIC мусор, и winws
    /// честно ничего не сделает, а провал спишется на стратегию.
    /// </summary>
    public static class FakeSweeper
    {
        /// <summary>Опции, значение которых — файл фейка.</summary>
        private static readonly string[] FakeOptions =
        {
            "--dpi-desync-fake-tls",
            "--dpi-desync-fake-tls-mod",     // не файл: значение вида rnd,dupsid — отсеется проверкой
            "--dpi-desync-fake-http",
            "--dpi-desync-fake-quic",
            "--dpi-desync-fake-stun",
            "--dpi-desync-fake-discord",
            "--dpi-desync-fake-dht",
            "--dpi-desync-fake-syndata",
            "--dpi-desync-fake-unknown",
            "--dpi-desync-fake-unknown-udp",
            "--dpi-desync-fakedsplit-pattern",
            "--dpi-desync-split-seqovl-pattern",
            "--dpi-desync-hostfakesplit-pattern"
        };

        /// <summary>
        /// Найти в стратегии все слоты, где указан файл фейка.
        ///
        /// Возвращает и слоты, и предупреждения о том, что не удалось
        /// определить: молча пропущенный слот означает «подбор прошёл, а фейк
        /// не поменялся», и это выглядит как «подбор не помогает».
        /// </summary>
        public static List<FakeSlot> Slots(Strategy strategy, List<string> warnings)
        {
            List<FakeSlot> slots = new List<FakeSlot>();

            if (strategy == null) return slots;

            foreach (string arg in AllArgs(strategy))
            {
                if (string.IsNullOrEmpty(arg)) continue;

                int eq = arg.IndexOf('=');
                if (eq <= 0) continue;

                string option = arg.Substring(0, eq);
                string value = arg.Substring(eq + 1);

                if (!IsFakeOption(option)) continue;

                // Значение-файл всегда оканчивается на .bin. Всё остальное
                // (rnd,dupsid,sni=..., 0x00000000, !) — это не путь.
                if (!value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;

                string file = Path.GetFileName(value.Replace('/', '\\'));

                FakeSlot slot = new FakeSlot { Option = option, File = file };

                string full = FindPayload(file, value);

                if (full == null)
                {
                    if (warnings != null)
                        warnings.Add("не нашёл файл фейка " + file + " — слот пропущен");
                    continue;
                }

                Payload payload = PayloadValidator.Inspect(full);

                if (!payload.Valid)
                {
                    if (warnings != null)
                        warnings.Add(file + " не фейк: " + payload.InvalidReason);
                    continue;
                }

                slot.Kind = payload.Kind;
                slots.Add(slot);
            }

            return slots;
        }

        /// <summary>
        /// Кандидаты того же вида, что и слот. Только валидные файлы: битый
        /// .bin winws отправит как есть, и провал будет выглядеть как вина
        /// стратегии.
        /// </summary>
        public static List<Payload> Candidates(PayloadKind kind, int limit)
        {
            List<Payload> result = new List<Payload>();

            if (kind == PayloadKind.Unknown) return result;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in PayloadDirs())
            {
                if (!Directory.Exists(dir)) continue;

                string[] files;

                try { files = Directory.GetFiles(dir, "*.bin"); }
                catch { continue; }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string path in files)
                {
                    string name = Path.GetFileName(path);

                    if (!seen.Add(name)) continue;

                    Payload payload;

                    try { payload = PayloadValidator.Inspect(path); }
                    catch { continue; }

                    if (!payload.Valid || payload.Kind != kind) continue;

                    result.Add(payload);

                    if (limit > 0 && result.Count >= limit) return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Сделать копию стратегии с одним заменённым фейком.
        ///
        /// Копия, а не правка на месте: оригинал должен остаться в каталоге
        /// нетронутым, иначе после отмены подбора человек получит стратегию с
        /// чужим фейком и не поймёт, откуда он.
        /// </summary>
        public static Strategy Variant(Strategy source, FakeSlot slot, string file, string id)
        {
            if (source == null || slot == null) return null;

            Strategy copy = new Strategy
            {
                Id = id,
                Name = source.Name + " · " + file,
                Engine = source.Engine,
                Note = "подбор фейка: " + slot.Option.Replace("--dpi-desync-", "") + " = " + file,
                Recommended = false,
                Source = source.Source,
                Targets = new List<string>(source.Targets),
                GlobalArgs = ReplaceAll(source.GlobalArgs, slot.Option, file)
            };

            foreach (StrategyProfile profile in source.Profiles)
            {
                copy.Profiles.Add(new StrategyProfile
                {
                    Name = profile.Name,
                    Label = profile.Label,
                    Args = ReplaceAll(profile.Args, slot.Option, file)
                });
            }

            return copy;
        }

        // ------------------------------------------------------------- helpers

        private static List<string> ReplaceAll(List<string> args, string option, string file)
        {
            List<string> result = new List<string>(args.Count);

            foreach (string arg in args)
            {
                if (arg != null && arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(option + "=${BIN}/" + file);
                    continue;
                }

                result.Add(arg);
            }

            return result;
        }

        private static IEnumerable<string> AllArgs(Strategy strategy)
        {
            foreach (string a in strategy.GlobalArgs) yield return a;

            foreach (StrategyProfile profile in strategy.Profiles)
                foreach (string a in profile.Args) yield return a;
        }

        private static bool IsFakeOption(string option)
        {
            foreach (string known in FakeOptions)
                if (string.Equals(option, known, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Найти файл по имени: сначала там, куда указывает аргумент, потом в
        /// известных каталогах. Аргумент может быть уже развёрнут в абсолютный
        /// путь, а может быть `${BIN}/x.bin` — оба случая нормальны.
        /// </summary>
        private static string FindPayload(string fileName, string valueFromArg)
        {
            if (!string.IsNullOrEmpty(valueFromArg))
            {
                string candidate = valueFromArg.Replace("${BIN}", Paths.EngineBinDir)
                                               .Replace("${FAKES}", Paths.FakesDir)
                                               .Replace('/', '\\');

                if (File.Exists(candidate)) return candidate;
            }

            foreach (string dir in PayloadDirs())
            {
                string path = Path.Combine(dir, fileName);

                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static IEnumerable<string> PayloadDirs()
        {
            yield return Paths.EngineBinDir;
            yield return Paths.FakesDir;
        }
    }
}
