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
    }
}