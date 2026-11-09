using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Генератор подставного TLS ClientHello.
    ///
    /// Зачем он нужен. На живой сети (МТС, Android 16) из всех техник прошла
    /// ровно одна: `fake` с TTL 8. Разрез ClientHello, disorder, oob и tlsrec
    /// давали таймаут — то есть DPI собирал поток и блокировал его целиком.
    /// Фейк работает потому, что DPI видит ЗАВЕРШЁННЫЙ ClientHello с безобидным
    /// именем, помечает соединение как проверенное, а настоящий ClientHello
    /// следом проходит без разбора. Низкий TTL нужен, чтобы фейк умер на пути
    /// к серверу: DPI стоит ближе (в пределах 8 хопов), сервер — дальше.
    ///
    /// Порт с десктопа (`src/Payloads/FakeGenerator.cs`), где эта же логика
    /// проверена. Отличия: нет зависимости от библиотеки payload-файлов и
    /// нет самопроверки через валидатор — проверяем формат на месте.
    /// </summary>
    public static class FakeTls
    {
        /// <summary>Размер, который занимает Chrome-подобный ClientHello.</summary>
        public const int DefaultSize = 517;

        private const int MaxSize = 4096;

        // GREASE по RFC 8701: 0x0A0A, 0x1A1A, ... 0xFAFA
        private static readonly ushort[] Grease =
        {
            0x0A0A, 0x1A1A, 0x2A2A, 0x3A3A, 0x4A4A, 0x5A5A, 0x6A6A, 0x7A7A,
            0x8A8A, 0x9A9A, 0xAAAA, 0xBABA, 0xCACA, 0xDADA, 0xEAEA, 0xFAFA
        };

        /// <summary>
        /// Собрать ClientHello с указанным SNI.
        ///
        /// targetSize — размер, который должен получиться. Он важен: фейк
        /// уходит в тот же поток, и сильно меньший размер сам по себе заметен.
        /// </summary>
        public static byte[] ClientHello(string sni, int targetSize)
        {
            string host = NormalizeHost(sni);

            if (host.Length == 0) host = "www.wikipedia.org";

            RandomNumberGenerator rng = RandomNumberGenerator.Create();

            try
            {
                ushort grease = Grease[NextByte(rng) % Grease.Length];

                using (MemoryStream handshake = new MemoryStream(512))
                {
                    WriteU16(handshake, 0x0303);                    // legacy_version
                    WriteBytes(handshake, RandomBytes(rng, 32));    // random

                    WriteByte(handshake, 32);
                    WriteBytes(handshake, RandomBytes(rng, 32));    // session_id

                    ushort[] ciphers =
                    {
                        grease,
                        0x1301, 0x1302, 0x1303,      // TLS 1.3
                        0xC02B, 0xC02F, 0xC02C, 0xC030
                    };

                    WriteU16(handshake, (ushort)(ciphers.Length * 2));

                    foreach (ushort cipher in ciphers) WriteU16(handshake, cipher);

                    WriteByte(handshake, 1);    // compression methods length
                    WriteByte(handshake, 0);    // null compression

                    using (MemoryStream extensions = new MemoryStream(512))
                    {
                        WriteExtension(extensions, grease, new byte[0]);
                        WriteExtension(extensions, 0x0000, ServerName(host));
                        WriteExtension(extensions, 0x0017, new byte[0]);
                        WriteExtension(extensions, 0xff01, new byte[] { 0x00 });
                        WriteExtension(extensions, 0x000a, SupportedGroups(grease));
                        WriteExtension(extensions, 0x000b, new byte[] { 0x01, 0x00 });
                        WriteExtension(extensions, 0x0023, new byte[0]);
                        WriteExtension(extensions, 0x0010, Alpn());
                        WriteExtension(extensions, 0x0005, new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00 });
                        WriteExtension(extensions, 0x000d, SignatureAlgorithms());
                        WriteExtension(extensions, 0x0012, new byte[0]);
                        WriteExtension(extensions, 0x0033, KeyShare(rng));
                        WriteExtension(extensions, 0x002d, new byte[] { 0x01, 0x01 });
                        WriteExtension(extensions, 0x002b, SupportedVersions(grease));
                        WriteExtension(extensions, 0x001b, new byte[] { 0x02, 0x00, 0x02 });

                        // 5 (запись) + 4 (handshake) + тело + 2 (длина расширений)
                        int baseSize = 5 + 4 + (int)handshake.Length + 2 + (int)extensions.Length;

                        int target = targetSize > 0 ? Math.Min(targetSize, MaxSize) : DefaultSize;

                        if (target > baseSize)
                        {
                            int pad = target - baseSize - 4;

                            if (pad > 0) WriteExtension(extensions, 0x0015, new byte[pad]);
                        }

                        byte[] extBytes = extensions.ToArray();

                        WriteU16(handshake, (ushort)extBytes.Length);
                        WriteBytes(handshake, extBytes);

                        byte[] body = handshake.ToArray();

                        using (MemoryStream record = new MemoryStream(target + 8))
                        {
                            WriteByte(record, 0x16);                            // handshake record
                            WriteU16(record, 0x0301);                           // legacy record version
                            WriteU16(record, (ushort)(body.Length + 4));
                            WriteByte(record, 0x01);                            // ClientHello
                            WriteU24(record, body.Length);
                            WriteBytes(record, body);

                            return record.ToArray();
                        }
                    }
                }
            }
            finally
            {
                rng.Dispose();
            }
        }

        /// <summary>Похоже ли на TLS ClientHello (для самопроверки генератора).</summary>
        public static bool LooksLikeClientHello(byte[] data)
        {
            if (data == null || data.Length < 6) return false;

            return data[0] == 0x16 && data[1] == 0x03 && data[5] == 0x01;
        }

        // ------------------------------------------------------------ домен

        public static string NormalizeHost(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            string value = input.Trim().ToLowerInvariant();

            int scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) value = value.Substring(scheme + 3);

            int slash = value.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) value = value.Substring(0, slash);

            int colon = value.IndexOf(':');
            if (colon > 0) value = value.Substring(0, colon);

            value = value.Trim().Trim('.');

            StringBuilder safe = new StringBuilder(value.Length);

            foreach (char c in value)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
                if (ok) safe.Append(c);
            }

            string host = safe.ToString();

            if (host.Length == 0) return "";
            if (host.IndexOf('.') < 0) return "";

            return host;
        }

        // ------------------------------------------------------- расширения

        private static byte[] ServerName(string host)
        {
            byte[] name = Encoding.ASCII.GetBytes(host);

            using (MemoryStream ms = new MemoryStream(name.Length + 5))
            {
                WriteU16(ms, (ushort)(name.Length + 3));
                WriteByte(ms, 0);
                WriteU16(ms, (ushort)name.Length);
                WriteBytes(ms, name);

                return ms.ToArray();
            }
        }

        private static byte[] SupportedGroups(ushort grease)
        {
            ushort[] groups = { grease, 0x001d, 0x0017, 0x0018 };

            using (MemoryStream ms = new MemoryStream(groups.Length * 2 + 2))
            {
                WriteU16(ms, (ushort)(groups.Length * 2));

                foreach (ushort g in groups) WriteU16(ms, g);

                return ms.ToArray();
            }
        }

        private static byte[] Alpn()
        {
            byte[] h2 = Encoding.ASCII.GetBytes("h2");
            byte[] http = Encoding.ASCII.GetBytes("http/1.1");

            using (MemoryStream ms = new MemoryStream(16))
            {
                WriteU16(ms, (ushort)(h2.Length + http.Length + 2));
                WriteByte(ms, (byte)h2.Length);
                WriteBytes(ms, h2);
                WriteByte(ms, (byte)http.Length);
                WriteBytes(ms, http);

                return ms.ToArray();
            }
        }

        private static byte[] SignatureAlgorithms()
        {
            ushort[] algs = { 0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601 };

            using (MemoryStream ms = new MemoryStream(algs.Length * 2 + 2))
            {
                WriteU16(ms, (ushort)(algs.Length * 2));

                foreach (ushort a in algs) WriteU16(ms, a);

                return ms.ToArray();
            }
        }

        private static byte[] SupportedVersions(ushort grease)
        {
            using (MemoryStream ms = new MemoryStream(7))
            {
                WriteByte(ms, 6);
                WriteU16(ms, grease);
                WriteU16(ms, 0x0304);   // TLS 1.3
                WriteU16(ms, 0x0303);   // TLS 1.2

                return ms.ToArray();
            }
        }

        /// <summary>
        /// key_share с настоящим открытым ключом.
        ///
        /// Случайные 32 байта не подходят: это заведомо невалидная точка, и
        /// такой hello отличается от настоящего сильнее, чем другой, но
        /// корректный. Если ни один ECDH недоступен — честно откатываемся на
        /// случайные байты, но это последний резерв.
        /// </summary>
        private static byte[] KeyShare(RandomNumberGenerator rng)
        {
            byte[] x25519 = TryX25519();

            if (x25519 != null && x25519.Length == 32)
            {
                using (MemoryStream ms = new MemoryStream(40))
                {
                    WriteU16(ms, 36);
                    WriteU16(ms, 0x001d);
                    WriteU16(ms, 32);
                    WriteBytes(ms, x25519);

                    return ms.ToArray();
                }
            }

            byte[] p256 = TryP256();

            if (p256 != null)
            {
                using (MemoryStream ms = new MemoryStream(72))
                {
                    WriteU16(ms, (ushort)(4 + p256.Length));
                    WriteU16(ms, 0x0017);
                    WriteU16(ms, (ushort)p256.Length);
                    WriteBytes(ms, p256);

                    return ms.ToArray();
                }
            }

            byte[] fallback = RandomBytes(rng, 32);

            using (MemoryStream fb = new MemoryStream(40))
            {
                WriteU16(fb, 36);
                WriteU16(fb, 0x001d);
                WriteU16(fb, 32);
                WriteBytes(fb, fallback);

                return fb.ToArray();
            }
        }

        private static byte[] TryX25519()
        {
            try
            {
                ECCurve curve = ECCurve.CreateFromValue("1.3.101.110");

                using (ECDiffieHellman ecdh = ECDiffieHellman.Create(curve))
                {
                    byte[] spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

                    if (spki.Length < 32) return null;

                    byte[] key = new byte[32];
                    Buffer.BlockCopy(spki, spki.Length - 32, key, 0, 32);

                    return key;
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] TryP256()
        {
            try
            {
                using (ECDiffieHellman ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    ECParameters parameters = ecdh.ExportParameters(false);

                    if (parameters.Q.X == null || parameters.Q.Y == null) return null;

                    byte[] point = new byte[1 + parameters.Q.X.Length + parameters.Q.Y.Length];

                    point[0] = 0x04;

                    Buffer.BlockCopy(parameters.Q.X, 0, point, 1, parameters.Q.X.Length);
                    Buffer.BlockCopy(parameters.Q.Y, 0, point, 1 + parameters.Q.X.Length, parameters.Q.Y.Length);

                    return point;
                }
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------- примитивы

        private static void WriteExtension(MemoryStream ms, ushort type, byte[] data)
        {
            WriteU16(ms, type);
            WriteU16(ms, (ushort)data.Length);
            WriteBytes(ms, data);
        }

        private static byte[] RandomBytes(RandomNumberGenerator rng, int count)
        {
            byte[] buffer = new byte[Math.Max(0, count)];

            if (buffer.Length > 0) rng.GetBytes(buffer);

            return buffer;
        }

        private static int NextByte(RandomNumberGenerator rng)
        {
            byte[] one = new byte[1];
            rng.GetBytes(one);

            return one[0];
        }

        private static void WriteByte(MemoryStream ms, byte value)
        {
            ms.WriteByte(value);
        }

        private static void WriteU16(MemoryStream ms, ushort value)
        {
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteU24(MemoryStream ms, int value)
        {
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteBytes(MemoryStream ms, byte[] data)
        {
            ms.Write(data, 0, data.Length);
        }
    }
}
