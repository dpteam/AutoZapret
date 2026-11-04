using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Payloads
{
    public sealed class PayloadImportResult
    {
        public bool Ok;
        public string Message = "";

        public int Scanned;
        public int Valid;
        public int SkippedInvalid;
        public int Imported;
        public int Duplicates;
        public int Domains;

        public List<string> ImportedNames = new List<string>();
        public List<string> Problems = new List<string>();
    }

    /// <summary>
    /// Импорт библиотеки фейков из набора zapret-custom-binaries (TLSpack, MIT).
    ///
    /// Зачем импортёр, а не «скопировать всё»: набор содержит 1672 файла, но это
    /// множественные СНИМКИ одних и тех же доменов (по 10-20 на домен, разные
    /// соединения). Для GUI нужен один хороший фейк на домен, иначе список
    /// в интерфейсе превращается в помойку из `_00_`, `_01_`, `_sun1-13_`.
    ///
    /// Правила отбора:
    ///   * файл обязан пройти PayloadValidator (иначе не попадёт в аргументы winws);
    ///   * группировка по (тип, SNI); SNI берём из самого ClientHello, а не из имени
    ///     файла — имя врёт (например `tls_clienthello_max_ru.bin` содержит onetrust.com);
    ///   * из группы берём TLS 1.3 (предпочтительнее), затем самый крупный файл:
    ///     крупный hello содержит больше расширений и правдоподобнее;
    ///   * имена выходных файлов чистые: `tls_<sni>.bin` / `quic_<domain>.bin`.
    /// </summary>
    public static class PayloadImporter
    {
        public const string ManifestName = "fakes.json";
        public const int ManifestSchema = 1;

        private static readonly Regex TimestampRegex =
            new Regex(@"_(\d{8}T\d{6})$", RegexOptions.Compiled);

        /// <summary>Каталоги набора, которые имеет смысл импортировать.</summary>
        public static string[] SourceSubdirs()
        {
            return new[]
            {
                Path.Combine("whitelist", "tls1.3", "tls_clienthello"),
                Path.Combine("whitelist", "tls1.3", "quic"),
                Path.Combine("whitelist", "tls1.2", "tls_clienthello"),
                Path.Combine("whitelist", "tls1.2", "quic_inital"),
                "epicgames",
                "microsoft",
                "steam",
                "random"
            };
        }

        /// <summary>Где искать набор. В dev-раскладке это refs\TLSpack.</summary>
        public static string FindSource()
        {
            string[] candidates =
            {
                Path.Combine(Paths.BaseDir, "fakes-source"),
                Path.Combine(Paths.BaseDir, "..", "refs", "TLSpack"),
                Path.Combine(Paths.BaseDir, "..", "..", "refs", "TLSpack"),
                Path.Combine(Paths.BaseDir, "..", "..", "..", "refs", "TLSpack")
            };

            foreach (string c in candidates)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(c, "whitelist"))) return Path.GetFullPath(c);
                }
                catch { }
            }

            return null;
        }

        // ----------------------------------------------------------------- import

        /// <summary>
        /// Импортирует набор в assets\fakes. targetDir = null -> Paths.FakesDir.
        /// </summary>
        public static PayloadImportResult Import(string sourceDir, string targetDir, Action<string> progress)
        {
            PayloadImportResult result = new PayloadImportResult();

            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                result.Message = "каталог набора не найден";
                return result;
            }

            if (string.IsNullOrEmpty(targetDir)) targetDir = Paths.FakesDir;

            try
            {
                Directory.CreateDirectory(targetDir);

                // группа -> лучший кандидат
                Dictionary<string, Candidate> best = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

                foreach (string sub in SourceSubdirs())
                {
                    string dir = Path.Combine(sourceDir, sub);
                    if (!Directory.Exists(dir)) continue;

                    Report(progress, "читаю " + sub + "…");

                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.bin"); }
                    catch (Exception ex)
                    {
                        result.Problems.Add(sub + ": " + ex.Message);
                        continue;
                    }

                    foreach (string file in files)
                    {
                        result.Scanned++;

                        Payload payload = PayloadValidator.Inspect(file, "tlspack");
                        if (!payload.Valid)
                        {
                            result.SkippedInvalid++;
                            continue;
                        }
                        result.Valid++;

                        string sni = payload.Sni;
                        if (string.IsNullOrEmpty(sni)) sni = DomainFromFileName(Path.GetFileNameWithoutExtension(file));
                        if (string.IsNullOrEmpty(sni)) sni = "unknown";

                        bool isTls13 = sub.IndexOf("tls1.3", StringComparison.OrdinalIgnoreCase) >= 0;
                        string key = Payload.KindName(payload.Kind) + "|" + sni.ToLowerInvariant();

                        Candidate candidate = new Candidate
                        {
                            Path = file,
                            Kind = payload.Kind,
                            Sni = sni,
                            Size = payload.Size,
                            IsTls13 = isTls13
                        };

                        Candidate existing;
                        if (best.TryGetValue(key, out existing))
                        {
                            result.Duplicates++;
                            if (IsBetter(candidate, existing)) best[key] = candidate;
                        }
                        else
                        {
                            best[key] = candidate;
                        }
                    }
                }

                Report(progress, "копирую " + best.Count + " фейков…");

                List<object> manifestEntries = new List<object>();
                HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Candidate candidate in best.Values)
                {
                    string name = BuildName(candidate);
                    if (!usedNames.Add(name))
                    {
                        // коллизия имён (например два разных домена дали одно имя)
                        name = Path.GetFileNameWithoutExtension(name) + "-" +
                               usedNames.Count.ToString(CultureInfo.InvariantCulture) + ".bin";
                        usedNames.Add(name);
                    }

                    string dest = Path.Combine(targetDir, name);

                    try
                    {
                        File.Copy(candidate.Path, dest, true);
                        result.Imported++;
                        result.ImportedNames.Add(name);

                        manifestEntries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "file", name },
                            { "kind", Payload.KindName(candidate.Kind) },
                            { "sni", candidate.Sni },
                            { "size", (long)candidate.Size },
                            { "tls13", candidate.IsTls13 },
                            { "source", "zapret-custom-binaries" },
                            { "sha256", Hash(dest) }
                        });
                    }
                    catch (Exception ex)
                    {
                        result.Problems.Add(name + ": " + ex.Message);
                    }
                }

                result.Domains = best.Count;
                WriteManifest(targetDir, manifestEntries);

                result.Ok = true;
                result.Message = "импортировано " + result.Imported + " фейков из " + result.Scanned +
                                 " файлов (дубликатов " + result.Duplicates +
                                 ", отбраковано " + result.SkippedInvalid + ")";

                Log.Info("FAKE", result.Message);
                return result;
            }
            catch (Exception ex)
            {
                Log.Exception("FAKE", "импорт", ex);
                result.Message = ex.Message;
                return result;
            }
        }

        /// <summary>Что лучше: TLS 1.3 предпочтительнее, затем больший размер.</summary>
        private static bool IsBetter(Candidate candidate, Candidate existing)
        {
            if (candidate.IsTls13 != existing.IsTls13) return candidate.IsTls13;
            return candidate.Size > existing.Size;
        }

        private static string BuildName(Candidate candidate)
        {
            string prefix = candidate.Kind == PayloadKind.QuicInitial ? "quic" : "tls";
            string sni = candidate.Sni.ToLowerInvariant();

            StringBuilder sb = new StringBuilder(prefix.Length + sni.Length + 5);
            sb.Append(prefix).Append('_');

            foreach (char c in sni)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }

            // _tls13 только для TLS: у QUIC это сбивает с толку (там свой handshake)
            if (candidate.IsTls13 && candidate.Kind == PayloadKind.TlsClientHello) sb.Append("_tls13");
            sb.Append(".bin");

            return sb.ToString();
        }

        /// <summary>
        /// Достаёт домен из имени файла: `tls_clienthello_www_google_com_20260430T080152`
        /// -> `www.google.com`. Нужно для QUIC, где SNI в байтах не прочитать
        /// (Initial зашифрован ключами из DCID).
        ///
        /// ВАЖНО: числовые префиксы НЕ отбрасываем. `00_img_avito_st` — это реальный
        /// домен `00.img.avito.st` (avito использует нумерованные CDN-хосты),
        /// `sun1-13_userapi_com` — реальный `sun1-13.userapi.com` (Яндекс).
        /// Отбрасывание префикса превратило бы их в несуществующие домены.
        /// </summary>
        public static string DomainFromFileName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return "";

            string s = TimestampRegex.Replace(baseName, "");
            s = s.Replace("tls_clienthello_", "").Replace("quic_initial_", "");
            s = s.Trim('_');

            // `www_google_com` -> `www.google.com`
            string[] parts = s.Split('_');
            if (parts.Length >= 2) s = string.Join(".", parts);

            return s;
        }

        private static void WriteManifest(string dir, List<object> entries)
        {
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "schema", ManifestSchema },
                { "generatedAt", DateTime.Now.ToString("o") },
                { "source", "zapret-custom-binaries (MIT)" },
                { "count", (long)entries.Count },
                { "entries", entries }
            };

            AtomicFile.WriteAllText(Path.Combine(dir, ManifestName), Json.Stringify(root));
        }

        /// <summary>Читает манифест, если он есть. Возвращает file -> sni.</summary>
        public static Dictionary<string, string> ReadManifest(string dir)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = Path.Combine(dir, ManifestName);
                string text = AtomicFile.ReadAllTextOrNull(path);
                if (text == null) return map;

                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                if (root == null) return map;

                List<object> entries = Json.GetArray(root, "entries");
                if (entries == null) return map;

                foreach (object item in entries)
                {
                    Dictionary<string, object> m = item as Dictionary<string, object>;
                    if (m == null) continue;

                    string file = Json.GetString(m, "file", "");
                    string sni = Json.GetString(m, "sni", "");
                    if (file.Length > 0) map[file] = sni;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FAKE", "манифест: " + ex.Message);
            }

            return map;
        }

        private static string Hash(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(64);
                    foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress == null) return;
            try { progress(message); } catch { }
        }

        private sealed class Candidate
        {
            public string Path = "";
            public PayloadKind Kind;
            public string Sni = "";
            public int Size;
            public bool IsTls13;
        }
    }
}