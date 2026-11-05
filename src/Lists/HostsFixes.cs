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

        /// <summary>Каталог кэша скачанных наборов (рядом с данными, не в поставке).</summary>
        public static string CacheDir
        {
            get { return System.IO.Path.Combine(Paths.DataDir, "hosts-cache"); }
        }

        /// <summary>Полный путь к кэшу набора. Пусто — у набора нет файла.</summary>
        public static string CachePath(HostsSource source)
        {
            if (source == null || source.CacheFile.Length == 0) return "";

            return System.IO.Path.Combine(CacheDir, source.CacheFile);
        }

        /// <summary>
        /// Взять набор без сети: кэш → поставка.
        ///
        /// Почему без сети: применение фиксов должно быть мгновенным и
        /// работать при заблокированном GitHub — ровно в той ситуации, когда
        /// hosts и нужен. Свежесть обновляется отдельной кнопкой, осознанно.
        ///
        /// Порядок именно такой: кэш — это то, что человек скачал сам, и он
        /// новее поставки. Поставка — запас на первый запуск и на случай,
        /// когда скачать нечего.
        /// </summary>
        public static string Read(HostsSource source, out string origin)
        {
            origin = "";

            if (source == null) return null;

            // Вшитый набор (оптимизированный hosts) — только из поставки:
            // у него нет сети, из которой он мог бы обновиться.
            if (source.Bundled)
            {
                string bundledText = ReadFile(BundledPath(source));

                if (bundledText == null)
                {
                    origin = "нет в поставке";
                    return null;
                }

                origin = "поставка " + Stamp(BundledPath(source));
                return bundledText;
            }

            string cached = ReadFile(CachePath(source));

            if (cached != null)
            {
                origin = "кэш " + Stamp(CachePath(source));
                return cached;
            }

            string shipped = ReadFile(BundledPath(source));

            if (shipped != null)
            {
                origin = "поставка " + Stamp(BundledPath(source));
                return shipped;
            }

            origin = "не скачан";
            return null;
        }

        /// <summary>
        /// Сколько записей в наборе сейчас. 0 — данных нет.
        ///
        /// Нужно интерфейсу: «Discord — голос (RTC), 200 записей» говорит
        /// человеку, что он получит, до нажатия. Пустой набор выглядит как
        /// сломанный, и это правильное впечатление.
        /// </summary>
        public static int CountEntries(HostsSource source)
        {
            string origin;
            string text = Read(source, out origin);

            if (text == null) return 0;

            HostsReport report = new HostsReport();
            List<HostsRow> rows = HostsEditor.Parse(text, report);

            int count = 0;
            foreach (HostsRow row in rows) count += row.Hosts.Count;

            return count;
        }

        /// <summary>Читает файл набора. null — нет файла или он пустой/битый.</summary>
        private static string ReadFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                string text = File.ReadAllText(path, Encoding.UTF8);

                if (text.Length < 32) return null;
                if (LooksLikeHtml(text)) return null;

                return text;
            }
            catch (Exception ex)
            {
                Log.Warn("HOSTS", "чтение " + System.IO.Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Дата файла как «05.11.2026» — человеку, не машине.</summary>
        private static string Stamp(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "?";

                return File.GetLastWriteTime(path).ToString("dd.MM.yyyy");
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// Откуда набор возьмётся и насколько он свежий — строка для интерфейса.
        ///
        /// Без этого список наборов выглядит одинаково всегда: «Flowseal» и
        /// «dns.malw.link» без единого признака, скачаны они когда-нибудь или
        /// нет. Человек нажимает «применить» и не знает, что применит.
        /// </summary>
        public static string Describe(HostsSource source, out bool ready)
        {
            ready = false;

            if (source == null) return "";

            if (source.Bundled)
            {
                string path = BundledPath(source);
                ready = File.Exists(path);

                return ready ? "в поставке, " + Stamp(path) : "нет в поставке";
            }

            string cache = CachePath(source);

            if (File.Exists(cache))
            {
                ready = true;
                return "скачан " + Stamp(cache);
            }

            string shipped = BundledPath(source);

            if (File.Exists(shipped))
            {
                ready = true;
                return "из поставки " + Stamp(shipped) + " — можно обновить";
            }

            return "не скачан";
        }

        /// <summary>
        /// Скачать набор и положить в кэш. Возвращает текст или null.
        ///
        /// Кэш пишется только после проверок: иначе HTML-страница провайдера
        /// сохранится как «hosts», и в следующий раз человек применит её вместо
        /// набора — при этом без сети и без единого намёка на причину.
        /// </summary>
        public static string Download(HostsSource source, out string error)
        {
            error = "";

            if (source == null) { error = "нет источника"; return null; }
            if (source.Bundled) { error = "вшитый набор не скачивается"; return null; }

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

                // запись в кэш — атомарно: битый кэш хуже отсутствующего,
                // потому что «есть файл» выглядит как «всё в порядке»
                SaveCache(source, text);

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

        /// <summary>
        /// Положить набор в кэш атомарно: temp → File.Replace.
        ///
        /// Не File.WriteAllText: если процесс убьют посередине (выход из
        /// приложения, обновление), останется половина файла, которая пройдёт
        /// проверку «больше 32 символов» и будет применена как набор.
        /// </summary>
        private static void SaveCache(HostsSource source, string text)
        {
            string path = CachePath(source);
            if (path.Length == 0) return;

            try
            {
                Directory.CreateDirectory(CacheDir);

                string temp = path + ".tmp";

                File.WriteAllText(temp, text, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    // File.Replace требует, чтобы оба файла были на одном томе —
                    // поэтому temp лежит рядом с целевым, а не в %TEMP%
                    File.Replace(temp, path, null, true);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("HOSTS", "кэш " + System.IO.Path.GetFileName(path) + ": " + ex.Message);

                try { File.Delete(path + ".tmp"); } catch { }
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

        // --------------------------------------------------------------- update all

        /// <summary>Итог обновления одного набора — для отчёта в интерфейсе.</summary>
        public sealed class UpdateResult
        {
            public string Id = "";
            public string Name = "";
            public bool Ok;
            public int Lines;
            public string Origin = "";
            public string Error = "";

            public string Text()
            {
                if (!Ok) return Name + ": " + Error;

                return Name + ": " + Lines + " записей (" + Origin + ")";
            }
        }

        /// <summary>
        /// Обновить все известные наборы из сети.
        ///
        /// Возвращает по строке на набор: удачные и неудачные. Не бросаем
        /// исключение на первом провале — если один источник лежит, второй
        /// всё равно надо скачать, и человеку нужен полный отчёт, а не «не
        /// вышло» без деталей.
        ///
        /// Уже имеющийся кэш не трогаем при неудаче: старый набор лучше
        /// пустоты, и об этом прямо сказано в ошибке.
        /// </summary>
        public static List<UpdateResult> UpdateAll()
        {
            List<UpdateResult> results = new List<UpdateResult>();

            foreach (HostsSource source in Sources())
            {
                UpdateResult result = new UpdateResult
                {
                    Id = source.Id,
                    Name = source.Name
                };

                string error;
                string text = Download(source, out error);

                if (text == null)
                {
                    result.Ok = false;
                    result.Error = error;

                    // честно: остался предыдущий набор, если он был
                    string origin;
                    string previous = Read(source, out origin);

                    if (previous != null) result.Error += " (остался " + origin + ")";

                    results.Add(result);
                    continue;
                }

                HostsReport report = new HostsReport();
                List<HostsRow> rows = HostsEditor.Parse(text, report);

                result.Ok = true;
                result.Lines = rows.Count;
                result.Origin = "сеть, " + text.Length + " символов";

                if (rows.Count == 0)
                {
                    // скачали, но распарсить нечего — это не успех
                    result.Ok = false;
                    result.Error = "скачано, но записей не найдено";
                }

                results.Add(result);
            }

            Log.Info("HOSTS", "обновление наборов: " + string.Join(" | ", DescribeAll(results)));

            return results;
        }

        private static string[] DescribeAll(List<UpdateResult> results)
        {
            List<string> lines = new List<string>();

            foreach (UpdateResult r in results) lines.Add(r.Text());

            return lines.ToArray();
        }
    }
}