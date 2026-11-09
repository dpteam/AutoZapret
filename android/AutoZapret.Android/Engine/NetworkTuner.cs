using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Найденный рабочий профиль: техника + TTL.
    ///
    /// Хранится строкой вида `fake:12` или `split1`, чтобы лежать в настройках
    /// без сериализатора.
    /// </summary>
    public sealed class TuneProfile
    {
        public string PresetId = DesyncPreset.Split1.Id;

        /// <summary>TTL фейка. 0 — не используется этой техникой.</summary>
        public int Ttl;

        public static TuneProfile Parse(string text)
        {
            TuneProfile profile = new TuneProfile();

            if (string.IsNullOrEmpty(text)) return profile;

            int colon = text.IndexOf(':');

            if (colon < 0)
            {
                profile.PresetId = DesyncPreset.ById(text).Id;
                return profile;
            }

            profile.PresetId = DesyncPreset.ById(text.Substring(0, colon)).Id;

            int ttl;

            if (int.TryParse(text.Substring(colon + 1), out ttl)) profile.Ttl = ttl;

            return profile;
        }

        public string Format()
        {
            if (Ttl > 0) return PresetId + ":" + Ttl;

            return PresetId;
        }

        /// <summary>Человеческое описание для интерфейса.</summary>
        public string Describe()
        {
            DesyncPreset preset = DesyncPreset.ById(PresetId);

            if (preset.Id == DesyncPreset.Off.Id) return "обход не нужен";

            if (Ttl > 0) return preset.Name + " (TTL " + Ttl + ")";

            return preset.Name;
        }

        /// <summary>Перенести профиль в конфигурацию прокси.</summary>
        public void ApplyTo(DesyncConfig config)
        {
            if (config == null) return;

            DesyncPreset preset = DesyncPreset.ById(PresetId);

            preset.ApplyTo(config);

            if (preset.Fake && Ttl > 0) config.FakeTtl = Ttl;
        }
    }

    public sealed class TuneOutcome
    {
        public string NetworkKey = "";
        public string NetworkName = "";

        public TuneProfile Winner;

        /// <summary>Что проверяли и чем закончилось — для отчёта в интерфейсе.</summary>
        public List<string> Rows = new List<string>();

        public int Tested;
        public bool Ok;
        public string Detail = "";
    }

    /// <summary>
    /// Автоподбор техники под конкретную сеть.
    ///
    /// Зачем. TTL фейка — не константа: он равен числу хопов до DPI. В одной
    /// сети это 8, в другой 5 или 12. Подобранный на МТС профиль на домашнем
    /// Wi-Fi не работает, и наоборот. Плюс сами сети блокируют по-разному: где-то
    /// хватает разреза, где-то нужен фейк.
    ///
    /// Что делает: перебирает техники и TTL, каждую проверяет НАСТОЯЩИМ
    /// соединением через наш же прокси (SOCKS5 + TLS-рукопожатие), запоминает
    /// победителя отдельно для каждой сети.
    ///
    /// Почему проверка идёт через прокси, а не «в лоб»: нам нужен именно тот
    /// путь, которым ходит трафик. Проверка в обход прокси показала бы
    /// работоспособность сети, а не обхода.
    ///
    /// Почему не переключаем сети сами: обычное приложение на Android не имеет
    /// права включать и выключать Wi-Fi (нужны привилегии системы). Поэтому
    /// «подобрать для всех сетей» запоминает, что вторая сеть ещё не подобрана,
    /// и добирает её при следующем запуске, когда она станет активной.
    /// </summary>
    public static class NetworkTuner
    {
        /// <summary>Хост для проверки. YouTube — самый показательный на этих сетях.</summary>
        private const string TargetHost = "www.youtube.com";

        /// <summary>Запасной хост: если первый недоступен сам по себе, не считаем это провалом обхода.</summary>
        private const string BackupHost = "twitch.tv";

        private const int TrialTimeoutMs = 3500;

        /// <summary>Порядок перебора: от самого мягкого вмешательства к самому шумному.</summary>
        private static readonly string[] Candidates =
        {
            DesyncPreset.Off.Id,
            "split1",
            "split2",
            "multisplit",
            "disorder",
            "oob",
            "fake:8",
            "fake:6",
            "fake:10",
            "fake:5",
            "fake:12",
            "fake:4",
            "fake:16",
            "fake:20"
        };

        // ------------------------------------------------------------ сети

        /// <summary>Ключ текущей сети: wifi / mobile / ethernet / other.</summary>
        public static string CurrentKey(global::Android.Content.Context context)
        {
            try
            {
                global::Android.Net.ConnectivityManager manager =
                    context.GetSystemService(global::Android.Content.Context.ConnectivityService)
                    as global::Android.Net.ConnectivityManager;

                if (manager == null) return "other";

                global::Android.Net.Network network = manager.ActiveNetwork;

                if (network == null) return "none";

                global::Android.Net.NetworkCapabilities caps = manager.GetNetworkCapabilities(network);

                if (caps == null) return "other";

                if (caps.HasTransport(global::Android.Net.TransportType.Wifi)) return "wifi";
                if (caps.HasTransport(global::Android.Net.TransportType.Cellular)) return "mobile";
                if (caps.HasTransport(global::Android.Net.TransportType.Ethernet)) return "ethernet";

                return "other";
            }
            catch
            {
                return "other";
            }
        }

        public static string NameOf(string key)
        {
            if (key == "wifi") return "Wi-Fi";
            if (key == "mobile") return "мобильная сеть";
            if (key == "ethernet") return "Ethernet";
            if (key == "none") return "нет сети";

            return "другая сеть";
        }

        // ------------------------------------------------------- хранилище

        private static global::Android.Content.ISharedPreferences Prefs(global::Android.Content.Context context)
        {
            return context.GetSharedPreferences("autozapret", global::Android.Content.FileCreationMode.Private);
        }

        public static TuneProfile Load(global::Android.Content.Context context, string key)
        {
            string text = Prefs(context).GetString("tune_" + key, "");

            if (string.IsNullOrEmpty(text)) return null;

            return TuneProfile.Parse(text);
        }

        public static string LoadedAt(global::Android.Content.Context context, string key)
        {
            long stamp = Prefs(context).GetLong("tune_" + key + "_at", 0);

            if (stamp <= 0) return "";

            try
            {
                return new DateTime(stamp).ToString("dd.MM HH:mm");
            }
            catch
            {
                return "";
            }
        }

        public static void Save(global::Android.Content.Context context, string key, TuneProfile profile)
        {
            using (global::Android.Content.ISharedPreferencesEditor editor = Prefs(context).Edit())
            {
                editor.PutString("tune_" + key, profile.Format());
                editor.PutLong("tune_" + key + "_at", DateTime.Now.Ticks);
                editor.Apply();
            }
        }

        /// <summary>Профиль для текущей сети, если он есть и авто-применение включено.</summary>
        public static TuneProfile ForCurrent(global::Android.Content.Context context)
        {
            if (!Prefs(context).GetBoolean("auto_profile", true)) return null;

            return Load(context, CurrentKey(context));
        }

        public static bool AutoApplyEnabled(global::Android.Content.Context context)
        {
            return Prefs(context).GetBoolean("auto_profile", true);
        }

        public static void SetAutoApply(global::Android.Content.Context context, bool enabled)
        {
            using (global::Android.Content.ISharedPreferencesEditor editor = Prefs(context).Edit())
            {
                editor.PutBoolean("auto_profile", enabled);
                editor.Apply();
            }
        }

        // ---------------------------------------------------------- подбор

        /// <summary>
        /// Перебрать техники для текущей сети. Блокирует поток — вызывать из
        /// фонового.
        /// </summary>
        public static TuneOutcome Tune(global::Android.Content.Context context, Action<string> progress)
        {
            TuneOutcome outcome = new TuneOutcome();

            outcome.NetworkKey = CurrentKey(context);
            outcome.NetworkName = NameOf(outcome.NetworkKey);

            if (outcome.NetworkKey == "none")
            {
                outcome.Detail = "нет активной сети";
                return outcome;
            }

            // Имя разбираем один раз: если DoH не работает, дальше проверять
            // нечего — любая техника «провалится» по чужой причине.
            List<IPAddress> addresses = Doh.Resolve(TargetHost);

            if (addresses.Count == 0)
            {
                outcome.Detail = "не удалось разрешить " + TargetHost + " — сеть недоступна";
                return outcome;
            }

            Report(progress, "сеть: " + outcome.NetworkName + ", цель " + TargetHost + " -> " + addresses[0]);

            List<TuneProfile> tried = new List<TuneProfile>();
            List<bool> passed = new List<bool>();

            foreach (string candidate in Candidates)
            {
                TuneProfile profile = TuneProfile.Parse(candidate);

                bool ok = Verify(profile, TargetHost, addresses[0]);

                tried.Add(profile);
                passed.Add(ok);

                outcome.Tested++;

                outcome.Rows.Add((ok ? "+ " : "  ") + Describe(profile) + (ok ? "  прошло" : "  нет"));

                Report(progress, (ok ? "[ok] " : "[--] ") + Describe(profile));
            }

            // Победитель — первый прошедший в порядке мягкости. Перебор уже
            // идёт от «ничего не делать» к «фейк с большим TTL», поэтому
            // достаточно взять первый успех.
            for (int i = 0; i < tried.Count; i++)
            {
                if (!passed[i]) continue;

                outcome.Winner = tried[i];
                break;
            }

            if (outcome.Winner == null)
            {
                outcome.Detail = "ни одна техника не прошла: похоже, блокировка не по SNI " +
                                 "(нужен внешний релей)";
                return outcome;
            }

            // Победителя перепроверяем на втором хосте: одна удача могла быть
            // случайной, и сохранять её в настройки нельзя.
            if (outcome.Winner.PresetId != DesyncPreset.Off.Id)
            {
                List<IPAddress> backup = Doh.Resolve(BackupHost);

                if (backup.Count > 0 && !Verify(outcome.Winner, BackupHost, backup[0]))
                {
                    outcome.Detail = "прошло на " + TargetHost + ", но не на " + BackupHost +
                                     " — профиль не сохранён";
                    outcome.Winner = null;
                    return outcome;
                }
            }

            Save(context, outcome.NetworkKey, outcome.Winner);

            outcome.Ok = true;
            outcome.Detail = "сохранено для «" + outcome.NetworkName + "»: " + outcome.Winner.Describe();

            return outcome;
        }

        private static string Describe(TuneProfile profile)
        {
            DesyncPreset preset = DesyncPreset.ById(profile.PresetId);

            if (preset.Id == DesyncPreset.Off.Id) return "без обхода";
            if (profile.Ttl > 0) return preset.Name + " TTL " + profile.Ttl;

            return preset.Name;
        }

        /// <summary>
        /// Проверить профиль настоящим соединением.
        ///
        /// Поднимаем временный SOCKS5 на свободном порту с этим профилем,
        /// проходим SOCKS5-рукопожатие, затем TLS до целевого хоста. Успешное
        /// рукопожатие = обход сработал: сертификат получен, значит поток дошёл
        /// до настоящего сервера и не был разорван по пути.
        /// </summary>
        private static bool Verify(TuneProfile profile, string host, IPAddress address)
        {
            Socks5Proxy proxy = new Socks5Proxy();

            try
            {
                proxy.Config.Port = 0;          // свободный порт
                proxy.Config.BlockQuic = false; // UDP тут не участвует
                proxy.Log = null;               // не засоряем журнал приложения

                profile.ApplyTo(proxy.Config);

                string error = proxy.Start();

                if (error != null) return false;

                return Trial(proxy.ActualPort, host, address);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { proxy.Stop(); } catch { }
            }
        }

        /// <summary>Один проход: SOCKS5 CONNECT + TLS.</summary>
        private static bool Trial(int port, string host, IPAddress address)
        {
            TcpClient client = null;

            try
            {
                client = new TcpClient();
                client.NoDelay = true;

                IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(TrialTimeoutMs)) return false;

                client.EndConnect(ar);

                client.ReceiveTimeout = TrialTimeoutMs;
                client.SendTimeout = TrialTimeoutMs;

                NetworkStream stream = client.GetStream();

                // приветствие SOCKS5 без аутентификации
                stream.Write(new byte[] { 5, 1, 0 }, 0, 3);

                byte[] hello = ReadExact(stream, 2);

                if (hello == null || hello[0] != 5 || hello[1] != 0) return false;

                // CONNECT на домен: прокси сам разберёт его через DoH
                byte[] name = Encoding.ASCII.GetBytes(host);
                byte[] request = new byte[7 + name.Length];

                request[0] = 5;
                request[1] = 1;
                request[2] = 0;
                request[3] = 3;
                request[4] = (byte)name.Length;

                Buffer.BlockCopy(name, 0, request, 5, name.Length);

                request[5 + name.Length] = 0x01;   // порт 443
                request[6 + name.Length] = 0xBB;

                stream.Write(request, 0, request.Length);

                byte[] reply = ReadExact(stream, 10);

                if (reply == null || reply[0] != 5 || reply[1] != 0) return false;

                // TLS: сертификат не проверяем — нам важен сам факт рукопожатия,
                // а не доверие к нему (в тесте может быть и подменённый серт).
                using (SslStream ssl = new SslStream(stream, false, delegate { return true; }))
                {
                    ssl.AuthenticateAsClient(host);

                    return ssl.IsAuthenticated;
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
            }
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;

            try
            {
                while (read < count)
                {
                    int n = stream.Read(buffer, read, count - read);
                    if (n <= 0) return null;

                    read += n;
                }

                return buffer;
            }
            catch
            {
                return null;
            }
        }

        private static void Report(Action<string> progress, string line)
        {
            if (progress == null) return;

            try { progress(line); } catch { }
        }
    }
}
