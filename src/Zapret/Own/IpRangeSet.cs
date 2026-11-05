using System;
using System.Collections.Generic;
using System.Net;

namespace AutoZapret.Zapret.Own
{
    /// <summary>
    /// Набор IP-подсетей с быстрой проверкой «входит ли адрес».
    ///
    /// Почему не «перебрать 33 000 CIDR на каждый пакет»: движок работает в
    /// горячем пути, и линейный поиск по ipset-all.txt убил бы всю скорость —
    /// это десятки тысяч сравнений на каждый пакет.
    ///
    /// Почему не дерево (trie): 26 000 подсетей дают сотни тысяч узлов, это
    /// десятки мегабайт. Вместо этого подсети нормализуются в НЕПЕРЕСЕКАЮЩИЕСЯ
    /// интервалы и сливаются — после слияния их обычно втрое меньше, и поиск
    /// идёт двоичным поиском по отсортированному массиву.
    ///
    /// IPv4 и IPv6 хранятся отдельно: у них разная длина адреса, и смешивать
    /// их в одном массиве значит сравнивать несравнимое.
    /// </summary>
    public sealed class IpRangeSet
    {
        private struct Range
        {
            public uint Start;
            public uint End;
        }

        private struct Range6
        {
            public byte[] Start;
            public byte[] End;
        }

        private readonly List<Range> _v4 = new List<Range>();
        private readonly List<Range6> _v6 = new List<Range6>();

        private Range[] _v4Sorted = new Range[0];
        private Range6[] _v6Sorted = new Range6[0];
        private bool _built;

        public int Count { get { return _v4.Count + _v6.Count; } }
        public int Count4 { get { return _v4.Count; } }
        public int Count6 { get { return _v6.Count; } }

        /// <summary>Добавить «1.2.3.0/24» или «1.2.3.4». Невалидное — пропускаем.</summary>
        public bool Add(string cidr)
        {
            if (string.IsNullOrWhiteSpace(cidr)) return false;

            string value = cidr.Trim();
            _built = false;

            int slash = value.IndexOf('/');
            string addressPart = slash < 0 ? value : value.Substring(0, slash);

            int prefix;

            if (slash < 0)
            {
                prefix = -1;
            }
            else if (!int.TryParse(value.Substring(slash + 1), out prefix))
            {
                return false;
            }

            IPAddress address;

            if (!IPAddress.TryParse(addressPart, out address)) return false;

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                uint ip = ToUInt32(address.GetAddressBytes());
                int bits = prefix < 0 ? 32 : Math.Max(0, Math.Min(32, prefix));
                uint mask = bits == 0 ? 0 : 0xFFFFFFFFu << (32 - bits);

                _v4.Add(new Range { Start = ip & mask, End = (ip & mask) | ~mask });
                return true;
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                byte[] ip = address.GetAddressBytes();
                int bits = prefix < 0 ? 128 : Math.Max(0, Math.Min(128, prefix));

                byte[] start = (byte[])ip.Clone();
                byte[] end = (byte[])ip.Clone();

                ApplyMask(start, end, bits);

                _v6.Add(new Range6 { Start = start, End = end });
                return true;
            }

            return false;
        }

        /// <summary>Слить пересечения и отсортировать. Без этого поиск не работает.</summary>
        public void Build()
        {
            _v4.Sort((a, b) => a.Start.CompareTo(b.Start));

            List<Range> merged = new List<Range>(_v4.Count);

            foreach (Range r in _v4)
            {
                if (merged.Count == 0) { merged.Add(r); continue; }

                Range last = merged[merged.Count - 1];

                // перекрываются или стыкаются — объединяем
                if (r.Start <= last.End || (last.End != uint.MaxValue && r.Start == last.End + 1))
                {
                    if (r.End > last.End) last.End = r.End;
                    merged[merged.Count - 1] = last;
                    continue;
                }

                merged.Add(r);
            }

            _v4Sorted = merged.ToArray();

            _v6.Sort((a, b) => CompareBytes(a.Start, b.Start));

            List<Range6> merged6 = new List<Range6>(_v6.Count);

            foreach (Range6 r in _v6)
            {
                if (merged6.Count == 0) { merged6.Add(r); continue; }

                Range6 last = merged6[merged6.Count - 1];

                if (CompareBytes(r.Start, last.End) <= 0)
                {
                    if (CompareBytes(r.End, last.End) > 0) last.End = r.End;
                    merged6[merged6.Count - 1] = last;
                    continue;
                }

                merged6.Add(r);
            }

            _v6Sorted = merged6.ToArray();
            _built = true;
        }

        /// <summary>Входит ли адрес в набор. Один двоичный поиск.</summary>
        public bool Contains(IPAddress address)
        {
            if (address == null) return false;
            if (!_built) Build();

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return Contains4(ToUInt32(address.GetAddressBytes()));

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return Contains6(address.GetAddressBytes());

            return false;
        }

        public bool Contains4(uint ip)
        {
            if (!_built) Build();

            int lo = 0, hi = _v4Sorted.Length - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                Range r = _v4Sorted[mid];

                if (ip < r.Start) hi = mid - 1;
                else if (ip > r.End) lo = mid + 1;
                else return true;
            }

            return false;
        }

        private bool Contains6(byte[] ip)
        {
            if (!_built) Build();

            int lo = 0, hi = _v6Sorted.Length - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                Range6 r = _v6Sorted[mid];

                if (CompareBytes(ip, r.Start) < 0) hi = mid - 1;
                else if (CompareBytes(ip, r.End) > 0) lo = mid + 1;
                else return true;
            }

            return false;
        }

        private static void ApplyMask(byte[] start, byte[] end, int bits)
        {
            for (int i = 0; i < 16; i++)
            {
                int bitFrom = i * 8;
                int keep = Math.Max(0, Math.Min(8, bits - bitFrom));

                if (keep == 8) continue;

                byte mask = keep == 0 ? (byte)0 : (byte)(0xFF << (8 - keep));

                start[i] = (byte)(start[i] & mask);
                end[i] = (byte)(start[i] | (byte)~mask);
            }
        }

        private static uint ToUInt32(byte[] b)
        {
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int len = Math.Min(a.Length, b.Length);

            for (int i = 0; i < len; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return 1;
            }

            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// Скопировать диапазоны из другого набора.
        ///
        /// Нужно профилям: у них свои цели, но подсети-исключения общие.
        /// Копируем уже сведённые интервалы, а не исходные CIDR — их у нас
        /// в этой структуре и нет, и это правильно: после Build() важны
        /// именно интервалы.
        /// </summary>
        public void CopyFrom(IpRangeSet other)
        {
            if (other == null) return;

            other.Build();

            _v4.AddRange(other._v4Sorted);
            _v6.AddRange(other._v6Sorted);

            _built = false;
            Build();
        }

        /// <summary>Загрузить набор из файла ipset (по строке на запись).</summary>
        public static IpRangeSet Load(string path, out int invalid)
        {
            invalid = 0;

            IpRangeSet set = new IpRangeSet();

            try
            {
                if (!System.IO.File.Exists(path)) return set;

                foreach (string raw in System.IO.File.ReadLines(path))
                {
                    string line = raw.Trim();

                    if (line.Length == 0) continue;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith(";", StringComparison.Ordinal)) continue;

                    if (!set.Add(line)) invalid++;
                }

                set.Build();
            }
            catch (Exception ex)
            {
                Core.Log.Warn("OWN", "ipset " + System.IO.Path.GetFileName(path) + ": " + ex.Message);
            }

            return set;
        }
    }
}