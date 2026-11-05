using System;
using System.Collections.Generic;
using System.Net;
using AutoZapret.Core;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Один профиль движка: техника плюс свой набор целей.
    ///
    /// Зачем это, если уже есть одна техника и один фильтр: у winws профили —
    /// основа. Отдельно youtube (split), отдельно голосовой Discord (fake по
    /// UDP), отдельно игры (другой порт). Одна техника на всё — это компромисс,
    /// который на практике проигрывает: то, что пробивает Google, не обязано
    /// пробивать Discord.
    ///
    /// Порядок важен: первый подходящий профиль выигрывает. Поэтому узкие
    /// профили идут раньше широких — ровно как в конфигах zapret.
    /// </summary>
    public sealed class OwnProfile
    {
        public string Name = "";
        public TechniqueOptions Technique = new TechniqueOptions();

        /// <summary>Свои цели. null — брать общий фильтр движка.</summary>
        public OwnFilter Filter;

        /// <summary>Порты TCP, которые ловит профиль.</summary>
        public List<int> TcpPorts = new List<int> { 443 };

        /// <summary>Порты UDP (QUIC). Пусто — профиль UDP не трогает.</summary>
        public List<int> UdpPorts = new List<int>();

        /// <summary>Сколько раз профиль сработал — для отчёта.</summary>
        public int Applied;

        public string Describe()
        {
            List<string> parts = new List<string>();

            parts.Add(Name);
            parts.Add(Technique.Describe());

            if (TcpPorts.Count > 0) parts.Add("tcp:" + string.Join(",", TcpPorts.ToArray()));
            if (UdpPorts.Count > 0) parts.Add("udp:" + string.Join(",", UdpPorts.ToArray()));
            if (Filter != null && Filter.HasLimits) parts.Add(Filter.Describe());

            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>Ловит ли профиль этот порт.</summary>
        public bool MatchesTcp(int port)
        {
            return TcpPorts != null && TcpPorts.Contains(port);
        }

        public bool MatchesUdp(int port)
        {
            return UdpPorts != null && UdpPorts.Contains(port);
        }
    }

    /// <summary>
    /// Набор профилей: выбор подходящего по порту и цели.
    ///
    /// Отдельный класс, потому что логика выбора — единственное место, где
    /// можно ошибиться так, что «движок работает, а обход нет»: не тот профиль
    /// выиграл, и техника применилась не к тому трафику.
    /// </summary>
    public sealed class OwnProfileSet
    {
        private readonly List<OwnProfile> _profiles = new List<OwnProfile>();

        public int Count { get { return _profiles.Count; } }
        public List<OwnProfile> All { get { return _profiles; } }

        /// <summary>
        /// Все TCP-порты, которые ловит хоть один профиль. Нужны для фильтра
        /// WinDivert: ядро не отдаст пакет, который не попал в фильтр.
        /// </summary>
        public List<int> AllTcpPorts()
        {
            List<int> ports = new List<int>();

            foreach (OwnProfile profile in _profiles)
            {
                if (profile.TcpPorts == null) continue;

                foreach (int port in profile.TcpPorts)
                    if (!ports.Contains(port)) ports.Add(port);
            }

            return ports;
        }

        /// <summary>То же для UDP (QUIC).</summary>
        public List<int> AllUdpPorts()
        {
            List<int> ports = new List<int>();

            foreach (OwnProfile profile in _profiles)
            {
                if (profile.UdpPorts == null) continue;

                foreach (int port in profile.UdpPorts)
                    if (!ports.Contains(port)) ports.Add(port);
            }

            return ports;
        }

        /// <summary>
        /// Выражение WinDivert по портам всех профилей (без `outbound`).
        ///
        /// Живёт здесь, а не в движке, потому что это свойство НАБОРА профилей:
        /// какие порты мы вообще собираемся ловить. Ошибка тут не видна в
        /// логах — ядро просто не отдаёт пакеты, и движок «работает», ничего
        /// не делая.
        /// </summary>
        public string FilterExpression()
        {
            List<string> parts = new List<string>();

            string tcp = PortExpression("tcp", AllTcpPorts());
            if (tcp.Length > 0) parts.Add("(" + tcp + ") and tcp.PayloadLength > 40");

            string udp = PortExpression("udp", AllUdpPorts());
            if (udp.Length > 0) parts.Add("(" + udp + ") and udp.PayloadLength > 20");

            return string.Join(" or ", parts.ToArray());
        }

        /// <summary>`tcp.DstPort == 443 or tcp.DstPort == 2053` без повторов.</summary>
        private static string PortExpression(string protocol, List<int> ports)
        {
            if (ports == null || ports.Count == 0) return "";

            List<string> parts = new List<string>();

            foreach (int port in ports)
                parts.Add(protocol + ".DstPort == " + port);

            return string.Join(" or ", parts.ToArray());
        }

        /// <summary>Есть ли хоть один профиль, который трогает UDP (QUIC).</summary>
        public bool HasUdp
        {
            get
            {
                foreach (OwnProfile profile in _profiles)
                    if (profile.UdpPorts != null && profile.UdpPorts.Count > 0) return true;

                return false;
            }
        }

        public void Add(OwnProfile profile)
        {
            if (profile == null) return;
            _profiles.Add(profile);
        }

        /// <summary>
        /// Найти профиль для TCP-соединения. Порядок добавления = приоритет.
        ///
        /// Если ни один не подошёл, возвращаем null: значит, трафик не наш и
        /// его надо пропустить как есть.
        /// </summary>
        public OwnProfile MatchTcp(int port, string sni, IPAddress destination)
        {
            foreach (OwnProfile profile in _profiles)
            {
                if (!profile.MatchesTcp(port)) continue;

                // профиль без своих целей наследует общий фильтр
                OwnFilter filter = profile.Filter;

                if (filter == null || !filter.HasLimits) return profile;
                if (filter.ShouldDesync(sni, destination)) return profile;
            }

            return null;
        }

        /// <summary>То же для UDP. SNI у QUIC зашифрован, решение по адресу.</summary>
        public OwnProfile MatchUdp(int port, IPAddress destination)
        {
            foreach (OwnProfile profile in _profiles)
            {
                if (!profile.MatchesUdp(port)) continue;

                OwnFilter filter = profile.Filter;

                if (filter == null || !filter.HasLimits) return profile;
                if (filter.ShouldDesync("", destination)) return profile;
            }

            return null;
        }

        public string Describe()
        {
            if (_profiles.Count == 0) return "профилей нет";

            List<string> parts = new List<string>();
            foreach (OwnProfile profile in _profiles) parts.Add(profile.Describe());

            return string.Join(" | ", parts.ToArray());
        }

        /// <summary>
        /// Профили по умолчанию: одна техника на весь TCP/443.
        ///
        /// Это то, что было раньше, но оформленное как профиль — так у движка
        /// один путь исполнения, а не два (особый случай «без профилей» и
        /// общий случай «с профилями»). Два пути = два поведения = баги,
        /// которые видны только у половины людей.
        /// </summary>
        public static OwnProfileSet Single(TechniqueOptions technique, OwnFilter filter)
        {
            OwnProfileSet set = new OwnProfileSet();

            set.Add(new OwnProfile
            {
                Name = "всё TCP/443",
                Technique = technique,
                Filter = filter,
                TcpPorts = new List<int> { 443 },
                UdpPorts = technique != null && technique.Kind == Technique.UdpLen
                    ? new List<int> { 443 }
                    : new List<int>()
            });

            return set;
        }

        /// <summary>
        /// Набор «как у zapret general»: разные техники для разных целей.
        ///
        /// Идея взята из конфигов zapret, где профили разделены по смыслу:
        /// youtube, discord, всё остальное. Порядок здесь не случайный —
        /// узкие цели идут первыми, иначе широкий профиль их перехватит.
        /// </summary>
        public static OwnProfileSet ZapretLike(TechniqueOptions fallback, OwnFilter general)
        {
            // Без фильтра профили бессмысленны: все три претендуют на один и
            // тот же TCP/443, и «google» выигрывал бы весь трафик. В режиме
            // «трогать всё» поведение должно остаться прежним — одна техника.
            if (general == null || !general.HasLimits) return Single(fallback, general);

            OwnProfileSet set = new OwnProfileSet();

            // 1) Google/YouTube: split по 443 — то, что реально работает
            //    на большинстве провайдеров и не ломает остальное
            OwnProfile google = new OwnProfile
            {
                Name = "google",
                Technique = new TechniqueOptions
                {
                    Kind = Technique.MultiSplit,
                    Positions = new List<int> { 1, 120 },
                    Repeats = 6
                },
                TcpPorts = new List<int> { 443 }
            };

            google.Filter = SubsetFilter(general, new[]
            {
                "google.com", "youtube.com", "ytimg.com", "googlevideo.com",
                "gstatic.com", "googleapis.com", "ggpht.com", "youtu.be"
            });

            set.Add(google);

            // 2) Discord: fake — у него своя манера проверки TLS
            OwnProfile discord = new OwnProfile
            {
                Name = "discord",
                Technique = new TechniqueOptions
                {
                    Kind = Technique.Fake,
                    Repeats = 6,
                    FakeSeqOffset = -66000
                },
                TcpPorts = new List<int> { 443, 2053, 2083, 2087, 2096, 8443 }
            };

            discord.Filter = SubsetFilter(general, new[]
            {
                "discord.com", "discordapp.com", "discordapp.net", "discord.media",
                "discord.gg", "discordcdn.com"
            });

            set.Add(discord);

            // 3) всё остальное — техника по умолчанию и общий фильтр
            set.Add(new OwnProfile
            {
                Name = "остальное",
                Technique = fallback,
                Filter = general,
                TcpPorts = new List<int> { 443 },
                UdpPorts = fallback != null && fallback.Kind == Technique.UdpLen
                    ? new List<int> { 443 }
                    : new List<int>()
            });

            return set;
        }

        /// <summary>
        /// Профиль с ограниченным набором доменов.
        ///
        /// Если общий фильтр вообще без ограничений (режим «весь трафик»),
        /// оставляем так же: иначе профиль начнёт отсекать то, что человек
        /// явно разрешил.
        /// </summary>
        private static OwnFilter SubsetFilter(OwnFilter general, string[] domains)
        {
            if (general == null || !general.HasLimits) return null;

            OwnFilter subset = new OwnFilter();

            foreach (string domain in domains) subset.AddHost(domain);

            // исключения наследуем: банк не должен обходиться даже внутри
            // профиля для google
            subset.CopyExcludesFrom(general);

            return subset;
        }
    }
}