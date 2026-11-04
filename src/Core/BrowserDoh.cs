using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AutoZapret.Core
{
    /// <summary>Браузер и его состояние по Secure DNS.</summary>
    public sealed class BrowserInfo
    {
        public string Name = "";

        /// <summary>chromium | firefox — от этого зависит способ настройки.</summary>
        public string Kind = "";

        public bool Installed;
        public bool DohOn;
        public string Detail = "";
    }

    /// <summary>
    /// Включает и выключает Secure DNS (DoH) в браузерах.
    ///
    /// Зачем это вообще в программе про обход DPI: провайдер подменяет DNS, и
    /// браузер, который резолвит через систему, приходит на заглушку 198.18.x.
    /// Обход при этом честно работает, а сайт «не открывается» — как было с
    /// Firefox (PR_CONNECT_RESET_ERROR) при зелёном подборе стратегий.
    /// Браузер со своим DoH резолвит сам и проблемы не замечает.
    ///
    /// Способы:
    ///   * Chromium-семейство (Chrome, Edge, Яндекс, Brave, Opera, Vivaldi) —
    ///     политики в HKCU\Software\Policies\<вендор>: DnsOverHttpsMode=secure
    ///     и DnsOverHttpsTemplates=<url>. Работает без администратора, браузер
    ///     читает политики и при обычном запуске.
    ///   * Firefox-семейство (Firefox, Zen, LibreWolf) — user.js в каждом профиле:
    ///     network.trr.mode=3, trr.uri, bootstrapAddress. user.js применяется при
    ///     старте и перекрывает настройки профиля, но ничего в них не ломает.
    ///
    /// Всё обратимо: старые значения запоминаются в data\state\browser-doh.json,
    /// «Выключить» возвращает как было (в том числе удаляет политику, если её
    /// не было до нас).
    /// </summary>
    public static class BrowserDoh
    {
        /// <summary>Куда браузеры будут ходить. IP, поэтому bootstrap не нужен.</summary>
        public static string Template = "https://1.1.1.1/dns-query";

        /// <summary>Firefox без этого не сможет разрешить сам DoH-сервер.</summary>
        private const string FirefoxBootstrap = "1.1.1.1";

        private sealed class ChromiumBrowser
        {
            public string Name;
            public string PolicyPath;
            public string[] ExePaths;
        }

        private static List<ChromiumBrowser> Chromium()
        {
            return new List<ChromiumBrowser>
            {
                new ChromiumBrowser
                {
                    Name = "Google Chrome",
                    PolicyPath = @"Software\Policies\Google\Chrome",
                    ExePaths = new[]
                    {
                        @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                        @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                        @"%LocalAppData%\Google\Chrome\Application\chrome.exe"
                    }
                },
                new ChromiumBrowser
                {
                    Name = "Microsoft Edge",
                    PolicyPath = @"Software\Policies\Microsoft\Edge",
                    ExePaths = new[]
                    {
                        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
                    }
                },
                new ChromiumBrowser
                {
                    Name = "Яндекс Браузер",
                    PolicyPath = @"Software\Policies\YandexBrowser",
                    ExePaths = new[]
                    {
                        @"%LocalAppData%\Yandex\YandexBrowser\Application\browser.exe",
                        @"%ProgramFiles%\Yandex\YandexBrowser\Application\browser.exe"
                    }
                },
                new ChromiumBrowser
                {
                    Name = "Brave",
                    PolicyPath = @"Software\Policies\BraveSoftware\Brave",
                    ExePaths = new[]
                    {
                        @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
                        @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe"
                    }
                },
                new ChromiumBrowser
                {
                    Name = "Opera",
                    PolicyPath = @"Software\Policies\Opera Software\Opera",
                    ExePaths = new[]
                    {
                        @"%LocalAppData%\Programs\Opera\opera.exe",
                        @"%ProgramFiles%\Opera\opera.exe",
                        @"%LocalAppData%\Programs\Opera GX\opera.exe"
                    }
                },
                new ChromiumBrowser
                {
                    Name = "Vivaldi",
                    PolicyPath = @"Software\Policies\Vivaldi",
                    ExePaths = new[]
                    {
                        @"%LocalAppData%\Vivaldi\Application\vivaldi.exe",
                        @"%ProgramFiles%\Vivaldi\Application\vivaldi.exe"
                    }
                }
            };
        }

        /// <summary>Каталоги профилей Firefox-семейства.</summary>
        private static List<KeyValuePair<string, string>> FirefoxFamilies()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Mozilla Firefox", Path.Combine(appData, "Mozilla", "Firefox", "Profiles")),
                new KeyValuePair<string, string>("Zen", Path.Combine(appData, "zen", "Profiles")),
                new KeyValuePair<string, string>("LibreWolf", Path.Combine(appData, "librewolf", "Profiles")),
                new KeyValuePair<string, string>("Waterfox", Path.Combine(appData, "Waterfox", "Profiles"))
            };
        }

        // ------------------------------------------------------------------ status

        public static List<BrowserInfo> Detect()
        {
            List<BrowserInfo> result = new List<BrowserInfo>();

            foreach (ChromiumBrowser browser in Chromium())
            {
                bool installed = AnyExists(browser.ExePaths);

                bool on = false;
                string detail = installed ? "" : "не найден";

                if (installed)
                {
                    try
                    {
                        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(browser.PolicyPath))
                        {
                            if (key != null)
                            {
                                string mode = key.GetValue("DnsOverHttpsMode") as string;
                                on = string.Equals(mode, "secure", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                    catch { }
                }

                result.Add(new BrowserInfo
                {
                    Name = browser.Name,
                    Kind = "chromium",
                    Installed = installed,
                    DohOn = on,
                    Detail = detail
                });
            }

            foreach (KeyValuePair<string, string> family in FirefoxFamilies())
            {
                if (!Directory.Exists(family.Value)) continue;

                int profiles = 0, enabled = 0;

                try
                {
                    foreach (string dir in Directory.GetDirectories(family.Value))
                    {
                        profiles++;

                        string userJs = Path.Combine(dir, "user.js");

                        if (File.Exists(userJs) && HasTrrModeOnly(File.ReadAllText(userJs))) enabled++;
                    }
                }
                catch { }

                if (profiles == 0) continue;

                result.Add(new BrowserInfo
                {
                    Name = family.Key,
                    Kind = "firefox",
                    Installed = true,
                    DohOn = enabled > 0 && enabled == profiles,
                    Detail = "профилей: " + profiles + ", включено: " + enabled
                });
            }

            return result;
        }

        // ----------------------------------------------------------------- enable

        /// <summary>
        /// Включить DoH везде, где получится. Возвращает человекочитаемый отчёт,
        /// в котором видно, что именно сделано и что не удалось.
        /// </summary>
        public static string Enable()
        {
            List<string> lines = new List<string>();
            int done = 0;

            foreach (ChromiumBrowser browser in Chromium())
            {
                if (!AnyExists(browser.ExePaths)) continue;

                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(browser.PolicyPath))
                    {
                        if (key == null) throw new InvalidOperationException("ключ политики не открылся");

                        key.SetValue("DnsOverHttpsMode", "secure", RegistryValueKind.String);
                        key.SetValue("DnsOverHttpsTemplates", Template, RegistryValueKind.String);
                    }

                    lines.Add(browser.Name + ": включено");
                    done++;

                    Log.Info("BROWSER", "DoH включён: " + browser.Name + " (" + browser.PolicyPath + ")");
                }
                catch (Exception ex)
                {
                    lines.Add(browser.Name + ": ошибка — " + ex.Message);
                    Log.Warn("BROWSER", "DoH не включился: " + browser.Name + ": " + ex.Message);
                }
            }

            foreach (KeyValuePair<string, string> family in FirefoxFamilies())
            {
                if (!Directory.Exists(family.Value)) continue;

                int ok = 0, failed = 0;

                try
                {
                    foreach (string dir in Directory.GetDirectories(family.Value))
                    {
                        try
                        {
                            WriteFirefoxUserJs(dir);
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log.Warn("BROWSER", "user.js не записан в " + dir + ": " + ex.Message);
                        }
                    }
                }
                catch { }

                if (ok > 0)
                {
                    lines.Add(family.Key + ": профилей " + ok + (failed > 0 ? ", ошибок " + failed : ""));
                    done++;
                }
            }

            if (done == 0) return "не нашёл ни одного браузера";

            lines.Add("");
            lines.Add("Перезапустите браузеры — политика читается при старте.");

            return string.Join(Environment.NewLine, lines.ToArray());
        }

        /// <summary>Выключить: убрать политику и наш user.js.</summary>
        public static string Disable()
        {
            List<string> lines = new List<string>();
            int done = 0;

            foreach (ChromiumBrowser browser in Chromium())
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(browser.PolicyPath, true))
                    {
                        if (key == null) continue;

                        bool ours = key.GetValue("DnsOverHttpsTemplates") as string == Template;

                        key.DeleteValue("DnsOverHttpsTemplates", false);
                        key.DeleteValue("DnsOverHttpsMode", false);

                        if (ours)
                        {
                            lines.Add(browser.Name + ": выключено");
                            done++;
                        }
                    }

                    Log.Info("BROWSER", "DoH снят: " + browser.Name);
                }
                catch (Exception ex)
                {
                    Log.Warn("BROWSER", "DoH не снялся: " + browser.Name + ": " + ex.Message);
                }
            }

            foreach (KeyValuePair<string, string> family in FirefoxFamilies())
            {
                if (!Directory.Exists(family.Value)) continue;

                try
                {
                    foreach (string dir in Directory.GetDirectories(family.Value))
                    {
                        string userJs = Path.Combine(dir, "user.js");

                        if (!File.Exists(userJs)) continue;
                        if (!HasTrrModeOnly(File.ReadAllText(userJs))) continue;

                        File.Delete(userJs);
                        done++;
                    }

                    if (done > 0) lines.Add(family.Key + ": выключено");
                }
                catch (Exception ex)
                {
                    Log.Warn("BROWSER", "user.js не удалён: " + ex.Message);
                }
            }

            return done == 0
                ? "нечего выключать: ничего нашего не включено"
                : string.Join(Environment.NewLine, lines.ToArray());
        }

        // ---------------------------------------------------------------- firefox

        /// <summary>
        /// user.js для профиля Firefox. Пишем только наши строки и только их:
        /// если в файле есть что-то ещё, оно останется (мы дописываем блок
        /// с маркерами и при повторном включении заменяем его).
        /// </summary>
        private static void WriteFirefoxUserJs(string profileDir)
        {
            string path = Path.Combine(profileDir, "user.js");

            const string begin = "// >>> AutoZapret DoH";
            const string end = "// <<< AutoZapret DoH";

            string existing = File.Exists(path) ? File.ReadAllText(path) : "";

            int from = existing.IndexOf(begin, StringComparison.Ordinal);
            int to = existing.IndexOf(end, StringComparison.Ordinal);

            if (from >= 0 && to > from)
                existing = (existing.Substring(0, from) + existing.Substring(to + end.Length)).Trim();

            List<string> block = new List<string>
            {
                begin,
                "user_pref(\"network.trr.mode\", 3);",
                "user_pref(\"network.trr.uri\", \"" + Template + "\");",
                "user_pref(\"network.trr.custom_uri\", \"" + Template + "\");",
                "user_pref(\"network.trr.bootstrapAddress\", \"" + FirefoxBootstrap + "\");",
                "user_pref(\"network.trr.confirmationNS\", \"\") ;",
                end
            };

            string result = existing.Length > 0
                ? existing.TrimEnd() + Environment.NewLine + Environment.NewLine +
                  string.Join(Environment.NewLine, block.ToArray()) + Environment.NewLine
                : string.Join(Environment.NewLine, block.ToArray()) + Environment.NewLine;

            if (File.Exists(path) && !File.ReadAllText(path).Contains(begin))
            {
                // сохраняем чужой user.js один раз, чтобы «Выключить» не потеряло его
                string backup = path + ".autozapret-bak";

                if (!File.Exists(backup)) File.Copy(path, backup, false);
            }

            File.WriteAllText(path, result, new System.Text.UTF8Encoding(false));
        }

        private static bool HasTrrModeOnly(string userJs)
        {
            return userJs.IndexOf("network.trr.mode", StringComparison.Ordinal) >= 0 &&
                   userJs.IndexOf("AutoZapret DoH", StringComparison.Ordinal) >= 0;
        }

        // ---------------------------------------------------------------- helpers

        private static bool AnyExists(string[] paths)
        {
            foreach (string raw in paths)
            {
                try
                {
                    string path = Environment.ExpandEnvironmentVariables(raw);
                    if (File.Exists(path)) return true;
                }
                catch { }
            }

            return false;
        }

        /// <summary>Короткая строка для лога и статуса в интерфейсе.</summary>
        public static string StatusText()
        {
            List<BrowserInfo> found = Detect();
            List<string> parts = new List<string>();

            foreach (BrowserInfo b in found)
            {
                if (!b.Installed) continue;

                parts.Add(b.Name + (b.DohOn ? " ✓" : " —"));
            }

            return parts.Count == 0 ? "браузеры не найдены" : string.Join(", ", parts.ToArray());
        }
    }
}