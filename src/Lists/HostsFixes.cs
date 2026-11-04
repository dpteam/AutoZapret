using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Lists
{
    /// <summary>Готовый набор записей для hosts.</summary>
    public sealed class HostsFix
    {
        public string Id = "";
        public string Name = "";
        public string Note = "";

        /// <summary>IP и имя — по одной паре на запись.</summary>
        public List<KeyValuePair<string, string>> Entries = new List<KeyValuePair<string, string>>();

        public int Count { get { return Entries.Count; } }
    }

    /// <summary>
    /// Удалённый источник hosts: скачивается при каждом применении.
    ///
    /// Отличие от HostsFix: фикс — это зашитые записи, а источник живёт в сети и
    /// обновляется сам (у Flowseal и malw.link списки меняются). Поэтому записи
    /// не хранятся в файле, а забираются в момент «Применить».
    /// </summary>
    public sealed class HostsSource
    {
        public string Id = "";
        public string Name = "";
        public string Url = "";
        public string Note = "";

        /// <summary>Что загружать при недоступности сети: файл в assets/hosts/cache.</summary>
        public string CacheFile = "";

        /// <summary>true — файл лежит рядом с программой и качать ничего не нужно.</summary>
        public bool Bundled;

        /// <summary>true — содержит блокировки (0.0.0.0), применять осознанно.</summary>
        public bool Careful;
    }

    /// <summary>
    /// Пользовательские фиксы для hosts: «Xbox», «Microsoft» и что угодно ещё.
    ///
    /// Зачем отдельная библиотека, а не просто текст в интерфейсе: фикс — это
    /// набор адресов, который проверяли люди, и его надо уметь применить одной
    /// кнопкой, обновить централизованно и не дублировать в hosts по десять раз.
    /// Файл `assets/hosts/fixes.json` можно править руками — формат простой.
    /// </summary>
    public static class HostsFixes
    {
        public static string Path
        {
            get { return System.IO.Path.Combine(Paths.BaseDir, "assets", "hosts", "fixes.json"); }
        }

        public static List<HostsFix> Load()
        {
            List<HostsFix> result = new List<HostsFix>();

            try
            {
                if (!File.Exists(Path)) return result;

                Dictionary<string, object> root = Json.ParseObjectSafe(File.ReadAllText(Path, Encoding.UTF8));
                if (root == null) return result;

                List<object> fixes = Json.GetArray(root, "fixes");
                if (fixes == null) return result;

                foreach (object item in fixes)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map == null) continue;

                    HostsFix fix = new HostsFix
                    {
                        Id = Json.GetString(map, "id", ""),
                        Name = Json.GetString(map, "name", ""),
                        Note = Json.GetString(map, "note", "")
                    };

                    if (fix.Id.Length == 0) continue;

                    List<object> entries = Json.GetArray(map, "entries");
                    if (entries != null)
                    {
                        foreach (object e in entries)
                        {
                            Dictionary<string, object> entry = e as Dictionary<string, object>;
                            if (entry == null) continue;

                            string ip = Json.GetString(entry, "ip", "");
                            string host = Json.GetString(entry, "host", "");

                            if (ip.Length == 0 || host.Length == 0) continue;

                            fix.Entries.Add(new KeyValuePair<string, string>(ip, host));
                        }
                    }

                    result.Add(fix);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("HOSTS", "чтение фиксов hosts", ex);
            }

            return result;
        }

        public static HostsFix Find(string id)
        {
            foreach (HostsFix fix in Load())
                if (string.Equals(fix.Id, id, StringComparison.OrdinalIgnoreCase)) return fix;

            return null;
        }

        /// <summary>Скачиваемые источники: Flowseal и malw.link по умолчанию.</summary>
        public static List<HostsSource> Sources()
        {
            return ReadList("sources", false);
        }

        /// <summary>
        /// Вшитые файлы из assets/hosts: работают без сети.
        ///
        /// Это запас на случай, когда GitHub недоступен: у человека должен
        /// оставаться рабочий hosts, а не пустота. «careful» помечает файлы,
        /// где есть блокировки через 0.0.0.0 — их применяют осознанно.
        /// </summary>
        public static List<HostsSource> Bundled()
        {
            return ReadList("bundled", true);
        }

        private static List<HostsSource> ReadList(string key, bool bundled)
        {
            List<HostsSource> result = new List<HostsSource>();

            try
            {
                if (!File.Exists(Path)) return result;

                Dictionary<string, object> root = Json.ParseObjectSafe(File.ReadAllText(Path, Encoding.UTF8));
                List<object> sources = root == null ? null : Json.GetArray(root, key);

                if (sources == null) return result;

                foreach (object item in sources)
                {
                    Dictionary<string, object> map = item as Dictionary<string, object>;
                    if (map == null) continue;

                    HostsSource source = new HostsSource
                    {
                        Id = Json.GetString(map, "id", ""),
                        Name = Json.GetString(map, "name", ""),
                        Url = Json.GetString(map, "url", ""),
                        Note = Json.GetString(map, "note", ""),
                        CacheFile = Json.GetString(map, "file", ""),
                        Bundled = bundled,
                        Careful = Json.GetBool(map, "careful", false)
                    };

                    if (source.Id.Length == 0) continue;

                    // у скачиваемого нужен URL, у вшитого — файл
                    if (bundled && source.CacheFile.Length == 0) continue;
                    if (!bundled && source.Url.Length == 0) continue;

                    result.Add(source);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("HOSTS", "чтение " + key + " hosts", ex);
            }

            return result;
        }

        public static HostsSource FindSource(string id)
        {
            foreach (HostsSource source in Sources())
                if (string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)) return source;

            foreach (HostsSource source in Bundled())
                if (string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase)) return source;

            return null;
        }

        /// <summary>Полный путь ко вшитому файлу hosts.</summary>
        public static string BundledPath(HostsSource source)
        {
            if (source == null || source.CacheFile.Length == 0) return "";

            return System.IO.Path.Combine(Paths.BaseDir, "assets", "hosts", source.CacheFile);
        }

        /// <summary>
        /// Скачать источник. null — не получилось.
        ///
        /// Проверки как у любых скачанных файлов (AGENTS.md): не HTML, размер в
        /// разумных пределах, не пустой. hosts-файл — это сотни строк вида
        /// «IP имя», и HTML-страница ошибки провайдера легко притворится им.
        /// </summary>
        public static string Download(HostsSource source, out string error)
        {
            error = "";

            try
            {
                string text = Fetch(source.Url);

                if (text == null)
                {
                    error = "нет ответа от " + source.Url;
                    return null;
                }

                if (LooksLikeHtml(text))
                {
                    error = "вместо hosts пришла HTML-страница (блокировка или ошибка сайта)";
                    return null;
                }

                if (text.Length < 32)
                {
                    error = "ответ слишком короткий (" + text.Length + " символов)";
                    return null;
                }

                if (text.Length > 8 * 1024 * 1024)
                {
                    error = "ответ слишком большой (" + text.Length + " символов)";
                    return null;
                }

                Log.Info("HOSTS", "источник " + source.Name + " скачан: " + text.Length + " символов");
                return text;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Log.Warn("HOSTS", "источник " + source.Name + ": " + error);
                return null;
            }
        }

        private static string Fetch(string url)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                System.Net.HttpWebRequest request =
                    (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);

                request.UserAgent = "AutoZapret/" + AppInfo.Version;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                                 System.Net.DecompressionMethods.Deflate;

                using (System.Net.HttpWebResponse response =
                       (System.Net.HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK) return null;

                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream == null) return null;

                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("HOSTS", "загрузка " + url + ": " + ex.Message);
                return null;
            }
        }

        private static bool LooksLikeHtml(string text)
        {
            string head = text.TrimStart();

            if (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) return true;
            if (head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return true;

            return head.IndexOf("<body", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}