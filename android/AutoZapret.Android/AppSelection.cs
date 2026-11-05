using System;
using System.Collections.Generic;
using Android.Content;
using Android.Content.PM;

namespace AutoZapret.Android
{
    /// <summary>
    /// Какие приложения заворачивать в туннель.
    ///
    /// Три режима, как у ByeByeDPI и как принято в Android-обходчиках:
    ///   * all    — весь телефон (по умолчанию);
    ///   * only   — только выбранные (белый список);
    ///   * except — все, кроме выбранных (чёрный список).
    ///
    /// Зачем это вообще: банковские приложения и госуслуги часто ругаются на
    /// активный VPN и на «подозрительный» маршрут. Человек должен иметь
    /// возможность пустить через обход только то, что действительно блокируют.
    /// </summary>
    public static class AppSelection
    {
        public const string ModeAll = "all";
        public const string ModeOnly = "only";
        public const string ModeExcept = "except";

        public static string LoadMode(Context context)
        {
            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);
            string mode = prefs.GetString("app_mode", ModeAll);

            if (mode != ModeOnly && mode != ModeExcept) mode = ModeAll;

            return mode;
        }

        public static HashSet<string> LoadApps(Context context)
        {
            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);
            return Parse(prefs.GetString("app_list", ""));
        }

        public static void Save(Context context, string mode, IEnumerable<string> apps)
        {
            if (mode != ModeOnly && mode != ModeExcept) mode = ModeAll;

            List<string> list = new List<string>(apps ?? new string[0]);
            list.Sort(StringComparer.Ordinal);

            ISharedPreferences prefs = context.GetSharedPreferences("autozapret", FileCreationMode.Private);

            using (ISharedPreferencesEditor editor = prefs.Edit())
            {
                editor.PutString("app_mode", mode);
                editor.PutString("app_list", string.Join(",", list.ToArray()));
                editor.Apply();
            }
        }

        public static HashSet<string> Parse(string value)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(value)) return result;

            foreach (string part in value.Split(','))
            {
                string item = part.Trim();
                if (item.Length > 0) result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Человеческое имя приложения. LoadLabel сам достаёт строку из ресурсов
        /// пакета; GetApplicationLabel на некоторых прошивках возвращает пустоту
        /// для приложений, установленных в другом профиле.
        /// </summary>
        private static string LabelOf(PackageManager manager, ApplicationInfo info)
        {
            try
            {
                string label = info.LoadLabel(manager);

                if (!string.IsNullOrEmpty(label)) return label;
            }
            catch
            {
                // ниже — запасной вариант
            }

            try
            {
                string label = manager.GetApplicationLabel(info);

                if (!string.IsNullOrEmpty(label)) return label;
            }
            catch
            {
            }

            return info.PackageName;
        }

        /// <summary>Установленные приложения с человеческими названиями.</summary>
        public static List<AppEntry> Installed(Context context)
        {
            List<AppEntry> result = new List<AppEntry>();

            try
            {
                PackageManager manager = context.PackageManager;

                // Не GetInstalledApplications, а явный запрос MAIN/LAUNCHER:
                // так в списке только то, что человек видит в лончере, а не
                // сотня служебных пакетов. Заодно это работает и без
                // QUERY_ALL_PACKAGES (см. комментарий в манифесте).
                Intent launcher = new Intent(Intent.ActionMain);
                launcher.AddCategory(Intent.CategoryLauncher);

                IList<ResolveInfo> activities = manager.QueryIntentActivities(launcher, PackageInfoFlags.MetaData);

                if (activities != null)
                {
                    foreach (ResolveInfo resolve in activities)
                    {
                        ActivityInfo activity = resolve == null ? null : resolve.ActivityInfo;
                        ApplicationInfo info = activity == null ? null : activity.ApplicationInfo;

                        if (info == null || string.IsNullOrEmpty(info.PackageName)) continue;
                        if (info.PackageName == context.PackageName) continue;

                        result.Add(new AppEntry(info.PackageName, LabelOf(manager, info)));
                    }
                }
            }
            catch
            {
                // без QUERY_ALL_PACKAGES система покажет только «свои» — это не
                // повод падать, просто список будет короче
            }

            result.Sort(delegate (AppEntry a, AppEntry b)
            {
                return string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase);
            });

            return result;
        }
    }

    public sealed class AppEntry
    {
        public readonly string Package;
        public readonly string Label;

        public AppEntry(string package, string label)
        {
            Package = package;
            Label = label;
        }
    }
}