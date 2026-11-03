using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    public sealed class HostsUpdateResult
    {
        public bool Ok;
        public string Message = "";
        public HostsAnalysis Analysis;
        public string Preview = "";
    }

    /// <summary>
    /// Сборка hosts из нескольких источников.
    ///
    /// Источники (в порядке приоритета — кто первый, того и IP при конфликте):
    ///   1. Flowseal `.service/hosts` — выверен под их же стратегии
    ///      (github, raw.githubusercontent и т.д., чтобы zapret мог обновляться)
    ///   2. dns.malw.link — большой список заблокированных ресурсов
    ///   3. локальные копии из refs (если сеть недоступна и файлы есть рядом)
    ///
    /// Конфликты не решаются молча: если один домен в двух источниках указывает
    /// на разные IP, это показывается пользователю. Молчаливый выбор ломает сайт
    /// непредсказуемо, и потом невозможно понять, почему «не работает».
    /// </summary>
    public static class HostsUpdater
    {
        /// <summary>Flowseal держит свой hosts в .service/hosts.</summary>
        public const string FlowsealUrl =
            "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/hosts";

        public sealed class Source
        {
            public string Name = "";
            public string Url = "";
            public string[] LocalPaths = new string[0];
        }

        public static List<Source> Sources()
        {
            return new List<Source>
            {
                new Source
                {
                    Name = "flowseal",
                    Url = FlowsealUrl,
                    LocalPaths = new[]
                    {
                        System.IO.Path.Combine(Paths.BaseDir, "..", "refs", "zapret-discord-youtube", ".service", "hosts"),
                        System.IO.Path.Combine(Paths.BaseDir, "..", "..", "refs", "zapret-discord-youtube", ".service", "hosts")
                    }
                },
                new Source
                {
                    Name = "malw.link",
                    Url = HostsFile.MalwLinkUrl,
                    LocalPaths = new[]
                    {
                        System.IO.Path.Combine(Paths.BaseDir, "..", "refs", "Zapret-Manager", "files", "hosts_malw.link.txt"),
                        System.IO.Path.Combine(Paths.BaseDir, "..", "..", "refs", "Zapret-Manager", "files", "hosts_malw.link.txt")
                    }
                }
            };
        }

        /// <summary>
        /// Скачивает источники, сливает и возвращает предпросмотр (ничего не пишет).
        ///
        /// Если verify=true — дополнительно проверяет по TCP адреса из конфликтов
        /// и выбирает живые. Проверка идёт вторым проходом: сначала нужно знать,
        /// какие IP вообще конфликтуют, чтобы не дёргать сеть зря.
        /// </summary>
        public static HostsUpdateResult Build(bool includeFlowseal = true, bool includeMalw = true,
                                              bool verify = false, Action<string> progress = null)
        {
            HostsUpdateResult result = new HostsUpdateResult();

            string existing = HostsFile.ReadSystem();
            if (existing == null)
            {
                result.Message = "Не удалось прочитать системный hosts (нужны права администратора?)";
                return result;
            }

            List<KeyValuePair<string, string>> collected = new List<KeyValuePair<string, string>>();
            List<string> usedSources = new List<string>();

            foreach (Source source in Sources())
            {
                if (source.Name == "flowseal" && !includeFlowseal) continue;
                if (source.Name == "malw.link" && !includeMalw) continue;

                Report(progress, "получаю " + source.Name + "…");

                string text = Download(source.Url);
                string from = "сеть";

                if (text == null)
                {
                    text = ReadLocal(source);
                    from = "локальная копия";
                }

                if (text == null)
                {
                    Log.Warn("HOSTS", source.Name + ": источник недоступен");
                    continue;
                }

                collected.Add(new KeyValuePair<string, string>(source.Name, text));
                usedSources.Add(source.Name + " (" + from + ")");
                Log.Info("HOSTS", source.Name + ": получен, " + text.Length + " байт, " + from);
            }

            if (collected.Count == 0)
            {
                result.Message = "Ни один источник не доступен (нет сети и нет локальных копий)";
                return result;
            }

            Report(progress, "ищу дубликаты и конфликты…");
            HostsAnalysis first = HostsFile.Analyze(collected, existing);

            HostsAnalysis final = first;
            Dictionary<string, HostsVerifier.Result> checks = null;

            if (verify && first.Conflicts.Count > 0)
            {
                // собираем все адреса из конфликтов и проверяем их одним заходом
                List<string> ips = new List<string>();
                foreach (HostsConflict conflict in first.Conflicts)
                    foreach (string ip in conflict.Ips)
                        if (!ips.Contains(ip)) ips.Add(ip);

                Report(progress, "проверяю " + ips.Count + " адресов из конфликтов…");
                checks = HostsVerifier.Check(ips, 1500, 8);

                int alive = 0;
                foreach (HostsVerifier.Result r in checks.Values) if (r.Ok) alive++;
                Report(progress, "ответили " + alive + " из " + checks.Count);

                // имя -> лучший живой адрес
                Dictionary<string, string> preferred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (HostsConflict conflict in first.Conflicts)
                {
                    string picked = HostsVerifier.PickBest(conflict.Ips, checks, null);
                    if (picked != null) preferred[conflict.Hostname] = picked;
                }

                final = HostsFile.Analyze(collected, existing, preferred);

                // прикладываем к конфликтам, чем закончилась проверка
                foreach (HostsConflict conflict in final.Conflicts)
                {
                    HostsVerifier.Result r;
                    if (checks.TryGetValue(conflict.Chosen, out r))
                    {
                        conflict.Verified = true;
                        conflict.VerifiedMs = r.Ok ? r.Ms : -1;
                        conflict.VerifiedNote = r.Ok ? "" : (r.Error.Length > 0 ? r.Error : "нет ответа");
                    }
                }

                Log.Info("HOSTS", "проверка: " + alive + "/" + checks.Count +
                                  " адресов ответили, конфликтов " + final.Conflicts.Count);
            }

            result.Ok = true;
            result.Analysis = final;
            result.Preview = HostsFile.Build(existing, final,
                "источники: " + string.Join(", ", usedSources.ToArray()) +
                (verify ? "; IP проверены по TCP" : ""));
            result.Message = final.Summary();

            Log.Info("HOSTS", "анализ: " + final.Summary());
            return result;
        }

        /// <summary>Собирает и сразу записывает. Возвращает null при успехе.</summary>
        public static string Apply(bool includeFlowseal, bool includeMalw, bool verify = false,
                                   Action<string> progress = null)
        {
            HostsUpdateResult built = Build(includeFlowseal, includeMalw, verify, progress);
            if (!built.Ok) return built.Message;

            if (built.Analysis.UniqueHosts == 0)
                return "Источники пусты — записывать нечего.";

            return HostsFile.Apply(built.Preview);
        }

        /// <summary>
        /// Проверяет уже записанный блок: какие адреса отвечают, какие мертвы.
        /// Ничего не меняет — только отчёт.
        /// </summary>
        public static HostsUpdateResult VerifyCurrent(Action<string> progress = null)
        {
            HostsUpdateResult result = new HostsUpdateResult();

            string existing = HostsFile.ReadSystem();
            if (existing == null)
            {
                result.Message = "Не удалось прочитать hosts";
                return result;
            }

            int lines;
            if (!HostsFile.HasBlock(existing, out lines))
            {
                result.Message = "Блока AutoZapret в hosts нет — проверять нечего.";
                return result;
            }

            HostsAnalysis analysis = HostsFile.Analyze(
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("текущий блок", existing)
                },
                existing);

            List<string> ips = new List<string>();
            foreach (HostsEntry e in analysis.Entries)
                if (!ips.Contains(e.Ip)) ips.Add(e.Ip);

            Report(progress, "проверяю " + ips.Count + " адресов…");
            Dictionary<string, HostsVerifier.Result> checks = HostsVerifier.Check(ips, 1500, 8);

            int alive = 0;
            List<string> dead = new List<string>();

            foreach (KeyValuePair<string, HostsVerifier.Result> kv in checks)
            {
                if (kv.Value.Ok) alive++;
                else dead.Add(kv.Key + " (" + kv.Value.Error + ")");
            }

            result.Ok = true;
            result.Analysis = analysis;

            StringBuilder sb = new StringBuilder();
            sb.Append("адресов ").Append(checks.Count)
              .Append(", отвечают ").Append(alive);
            if (dead.Count > 0) sb.Append(", не отвечают: ").Append(string.Join(", ", dead.ToArray()));

            result.Message = sb.ToString();
            return result;
        }

        /// <summary>Только анализ текущего состояния без сети (для диагностики).</summary>
        public static HostsUpdateResult Inspect()
        {
            HostsUpdateResult result = new HostsUpdateResult();

            string existing = HostsFile.ReadSystem();
            if (existing == null)
            {
                result.Message = "Не удалось прочитать hosts";
                return result;
            }

            int lines;
            bool hasBlock = HostsFile.HasBlock(existing, out lines);

            // конфликты внутри уже записанного блока
            HostsAnalysis analysis = HostsFile.Analyze(
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("текущий блок", existing)
                },
                existing);

            result.Ok = true;
            result.Analysis = analysis;
            result.Message = hasBlock
                ? "блок есть, строк: " + lines + "; " + analysis.Summary()
                : "блока AutoZapret нет; " + analysis.Summary();

            return result;
        }

        private static string Download(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                                 System.Net.DecompressionMethods.Deflate;

                using (System.Net.HttpWebResponse response = (System.Net.HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK) return null;

                    using (System.IO.Stream stream = response.GetResponseStream())
                    {
                        if (stream == null) return null;
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8, true))
                        {
                            string text = reader.ReadToEnd();
                            if (text.Length < 200) return null;

                            string head = text.Length > 200 ? text.Substring(0, 200) : text;
                            if (head.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                head.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0)
                                return null;

                            return text;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("HOSTS", "скачивание " + url + ": " + ex.Message);
                return null;
            }
        }

        private static string ReadLocal(Source source)
        {
            foreach (string path in source.LocalPaths)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        string text = System.IO.File.ReadAllText(path, Encoding.UTF8);
                        if (text.Length > 200) return text;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress == null) return;
            try { progress(message); } catch { }
        }
    }
}