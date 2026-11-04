using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AutoZapret.Payloads
{
    public sealed class GeneratedFake
    {
        public byte[] Data;
        public PayloadKind Kind = PayloadKind.Unknown;
        public string Sni = "";
        public bool Valid;
        public string Error = "";

        /// <summary>Какой ключ получилось положить в key_share.</summary>
        public string KeyShare = "";

        public int Size { get { return Data == null ? 0 : Data.Length; } }

        public string Describe()
        {
            if (!Valid) return "не создан: " + Error;

            return (Kind == PayloadKind.QuicInitial ? "QUIC Initial" : "TLS ClientHello") +
                   ", " + Size + " Б" +
                   (Sni.Length > 0 ? ", SNI " + Sni : "") +
                   (KeyShare.Length > 0 ? ", ключ " + KeyShare : "");
        }
    }

    /// <summary>
    /// Свой генератор фейк-payload'ов.
    ///
    /// Зачем свой, если есть 1694 готовых из TLSpack (MIT): библиотека снята
    /// с конкретных браузеров в конкретный день. Нужен свой домен — в готовом
    /// наборе его нет, а подставлять чужой SNI это лотерея.
    ///
    /// Честно о том, что здесь получается:
    ///   * это СИНТЕТИЧЕСКИЙ ClientHello, а не слепок Chrome. Порядок расширений
    ///     и набор шифров выглядят правдоподобно, но отпечаток браузера не
    ///     воспроизводится и не будет;
    ///   * key_share содержит НАСТОЯЩИЙ открытый ключ ECDH (X25519, а если
    ///     система его не умеет — P-256), а не случайные 32 байта. Разница
    ///     важна: случайный мусор в key_share — это заведомо битый hello,
    ///     и любой сервер отбросит его иначе, чем настоящий;
    ///   * фейк никогда не проверяется сервером по назначению: winws отправляет
    ///     его как приманку, и настоящая сторона его отбрасывает. Поэтому
    ///     «идеальная криптография» тут не нужна, а вот «похоже на TLS для DPI» —
    ///     нужно.
    ///
    /// Исходник `payloadGen` (StressOzz) НЕ используется: у него не указана
    /// лицензия. Здесь только идея снятия фейков и открытые форматы RFC 8446 / 9000.
    /// </summary>
    public static class FakeGenerator
    {
        /// <summary>Размер Chrome-подобного ClientHello в наборе TLSpack.</summary>
        public const int DefaultTlsSize = 517;

        /// <summary>RFC 9000: клиентский Initial обязан быть не меньше 1200 байт.</summary>
        public const int QuicSize = 1200;

        private const int MaxTlsSize = 4096;

        // GREASE-значения по RFC 8701: 0x0A0A, 0x1A1A, ... 0xFAFA
        private static readonly ushort[] GreaseValues =
        {
            0x0A0A, 0x1A1A, 0x2A2A, 0x3A3A, 0x4A4A, 0x5A5A, 0x6A6A, 0x7A7A,
            0x8A8A, 0x9A9A, 0xAAAA, 0xBABA, 0xCACA, 0xDADA, 0xEAEA, 0xFAFA
        };

        /// <summary>
        /// Собирает TLS ClientHello с указанным SNI.
        ///
        /// targetSize — желаемый размер файла. Именно размер важен: он попадает
        /// в первый пакет, и несовпадение с реальным hello заметно. Если
        /// естественный размер больше цели, padding не добавляется.
        /// </summary>
        public static GeneratedFake TlsClientHello(string sni, int targetSize = DefaultTlsSize)
        {
            GeneratedFake result = new GeneratedFake { Sni = sni ?? "" };

            try
            {
                string host = NormalizeHost(sni);
                if (host.Length == 0)
                {
                    result.Error = "нужен домен для SNI";
                    return result;
                }

                if (host.Length > 200)
                {
                    result.Error = "домен слишком длинный";
                    return result;
                }

                result.Sni = host;

                RandomNumberGenerator rng = RandomNumberGenerator.Create();
                ushort grease = GreaseValues[NextByte(rng) % GreaseValues.Length];

                // ---- тело ClientHello (без заголовка записи)
                MemoryStream handshake = new MemoryStream(512);

                WriteU16(handshake, 0x0303);                    // legacy_version
                WriteBytes(handshake, RandomBytes(rng, 32));    // random

                WriteByte(handshake, 32);                       // session_id length
                WriteBytes(handshake, RandomBytes(rng, 32));    // session_id

                // ---- cipher suites
                List<ushort> ciphers = new List<ushort>
                {
                    grease,
                    0x1301,   // TLS_AES_128_GCM_SHA256
                    0x1302,   // TLS_AES_256_GCM_SHA384
                    0x1303,   // TLS_CHACHA20_POLY1305_SHA256
                    0xC02B,   // ECDHE_ECDSA_AES128_GCM_SHA256
                    0xC02F,   // ECDHE_RSA_AES128_GCM_SHA256
                    0xC02C,   // ECDHE_ECDSA_AES256_GCM_SHA384
                    0xC030    // ECDHE_RSA_AES256_GCM_SHA384
                };

                WriteU16(handshake, (ushort)(ciphers.Count * 2));
                foreach (ushort cipher in ciphers) WriteU16(handshake, cipher);

                WriteByte(handshake, 1);    // compression methods length
                WriteByte(handshake, 0);    // null compression

                // ---- расширения
                MemoryStream extensions = new MemoryStream(512);

                WriteExtension(extensions, grease, new byte[0]);

                WriteExtension(extensions, 0x0000, ServerName(host));
                WriteExtension(extensions, 0x0017, new byte[0]);                    // extended_master_secret
                WriteExtension(extensions, 0xff01, new byte[] { 0x00 });            // renegotiation_info
                WriteExtension(extensions, 0x000a, SupportedGroups(grease, rng));
                WriteExtension(extensions, 0x000b, new byte[] { 0x01, 0x00 });      // ec_point_formats
                WriteExtension(extensions, 0x0023, new byte[0]);                    // session_ticket
                WriteExtension(extensions, 0x0010, Alpn());
                WriteExtension(extensions, 0x0005, new byte[] { 0x01, 0x00, 0x00, 0x01, 0x00 }); // status_request
                WriteExtension(extensions, 0x000d, SignatureAlgorithms(grease));
                WriteExtension(extensions, 0x0012, new byte[0]);                    // signed_certificate_timestamp

                byte[] keyShareExtension;
                string keyName;
                keyShareExtension = KeyShare(rng, out keyName);
                WriteExtension(extensions, 0x0033, keyShareExtension);
                result.KeyShare = keyName;

                WriteExtension(extensions, 0x002d, new byte[] { 0x01, 0x01 });      // psk_key_exchange_modes
                WriteExtension(extensions, 0x002b, SupportedVersions(grease));      // supported_versions
                WriteExtension(extensions, 0x001b, new byte[] { 0x02, 0x00, 0x02 }); // compress_certificate

                // ---- padding до целевого размера
                // Считаем итоговый размер без padding: 5 (запись) + 4 (handshake)
                // + тело + 2 (длина расширений) + сами расширения.
                int baseSize = 5 + 4 + (int)handshake.Length + 2 + (int)extensions.Length;

                int target = targetSize > 0 ? Math.Min(targetSize, MaxTlsSize) : DefaultTlsSize;

                if (target > baseSize)
                {
                    // 4 байта уйдут на заголовок самого расширения padding
                    int padLength = target - baseSize - 4;
                    if (padLength > 0)
                        WriteExtension(extensions, 0x0015, new byte[padLength]);
                }
                else
                {
                    target = baseSize;
                }

                byte[] extBytes = extensions.ToArray();

                WriteU16(handshake, (ushort)extBytes.Length);
                WriteBytes(handshake, extBytes);

                byte[] body = handshake.ToArray();

                // ---- заголовок записи
                MemoryStream record = new MemoryStream(target + 8);
                WriteByte(record, 0x16);            // handshake record
                WriteU16(record, 0x0301);           // legacy record version
                WriteU16(record, (ushort)(body.Length + 4));
                WriteByte(record, 0x01);            // ClientHello
                WriteU24(record, body.Length);
                WriteBytes(record, body);

                result.Data = record.ToArray();
                result.Kind = PayloadKind.TlsClientHello;

                // ---- самопроверка: результат обязан проходить наш же валидатор
                string reason;
                PayloadKind detected = PayloadValidator.DetectKind(result.Data, out reason);

                result.Valid = detected == PayloadKind.TlsClientHello;
                if (!result.Valid) result.Error = "самопроверка не прошла: " + reason;

                rng.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Собирает QUIC Initial ровно на 1200 байт.
        ///
        /// Полезная нагрузка внутри — случайные байты, и это не халтура:
        /// содержимое Initial зашифровано ключами, выведенными из публичного DCID,
        /// поэтому DPI видит только заголовок и длину. Настоящий CRYPTO-фрейм
        /// дал бы ровно то же самое снаружи, но потребовал бы AES-GCM и HKDF
        /// ради нулевого эффекта.
        /// </summary>
        public static GeneratedFake QuicInitial(int size = QuicSize, string sni = "")
        {
            GeneratedFake result = new GeneratedFake { Sni = sni ?? "" };

            try
            {
                // для QUIC SNI остаётся справочным: он нужен только для имени файла
                string host = FakeGenerator.NormalizeHost(sni);
                if (host.Length > 0) result.Sni = host;

                if (size < 64)
                {
                    result.Error = "слишком маленький Initial";
                    return result;
                }

                const int dcidLength = 8;

                // 1 (первый байт) + 4 (версия) + 1 (DCID len) + 8 (DCID)
                // + 1 (SCID len) + 1 (token len) + 2 (length varint) = 18
                const int headerLength = 18;

                int remaining = size - headerLength;
                if (remaining < 5)
                {
                    result.Error = "размер не помещается в заголовок";
                    return result;
                }

                if (remaining > 16383)
                {
                    result.Error = "слишком большой Initial (максимум 16401)";
                    return result;
                }

                RandomNumberGenerator rng = RandomNumberGenerator.Create();

                MemoryStream packet = new MemoryStream(size);

                WriteByte(packet, 0xC0);                // long header + fixed bit, тип 00 = Initial
                WriteU32(packet, 0x00000001);           // QUIC v1
                WriteByte(packet, dcidLength);
                WriteBytes(packet, RandomBytes(rng, dcidLength));
                WriteByte(packet, 0);                   // SCID length
                WriteByte(packet, 0);                   // token length

                // length покрывает PN(4) + payload, двухбайтовый varint
                WriteByte(packet, (byte)(0x40 | (remaining >> 8)));
                WriteByte(packet, (byte)(remaining & 0xFF));

                WriteBytes(packet, RandomBytes(rng, 4));            // packet number
                WriteBytes(packet, RandomBytes(rng, remaining - 4)); // payload

                result.Data = packet.ToArray();
                result.Kind = PayloadKind.QuicInitial;

                string reason;
                PayloadKind detected = PayloadValidator.DetectKind(result.Data, out reason);

                result.Valid = detected == PayloadKind.QuicInitial;
                if (!result.Valid) result.Error = "самопроверка не прошла: " + reason;

                rng.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Имя файла в стиле набора: `tls_clienthello_&lt;домен&gt;.bin`, точки — в подчёркивания.
        /// Никаких пробелов и слешей: имя потом подставляется в аргументы winws.
        /// </summary>
        public static string FileNameFor(string sni, PayloadKind kind)
        {
            string host = NormalizeHost(sni);
            if (host.Length == 0) host = "custom";

            StringBuilder safe = new StringBuilder(host.Length);
            foreach (char c in host)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '-' || c == '_';
                safe.Append(ok ? c : '_');
            }

            string prefix = kind == PayloadKind.QuicInitial ? "quic_initial_" : "tls_clienthello_";
            return prefix + safe + ".bin";
        }

        /// <summary>
        /// Сохраняет фейк в каталог. Возвращает путь или null с текстом ошибки.
        /// </summary>
        public static string Save(GeneratedFake fake, string directory, out string error)
        {
            error = "";

            if (fake == null || !fake.Valid || fake.Data == null)
            {
                error = fake == null ? "нечего сохранять" : fake.Error;
                return null;
            }

            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, FileNameFor(fake.Sni, fake.Kind));
                File.WriteAllBytes(path, fake.Data);
                return path;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Приводит домен к виду, пригодному для SNI: без схемы, пути и порта.
        /// Пользователь вставит «https://www.youtube.com/watch?v=...» — это нормально.
        /// </summary>
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

            // только буквы, цифры, дефис и точка
            StringBuilder safe = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
                if (ok) safe.Append(c);
            }

            string host = safe.ToString();

            if (host.Length == 0) return "";
            if (host.IndexOf('.') < 0) return "";    // «localhost» и одно слово не годятся
            if (host.StartsWith(".", StringComparison.Ordinal)) return "";

            return host;
        }

        // ------------------------------------------------------- построение

        private static byte[] ServerName(string host)
        {
            byte[] name = Encoding.ASCII.GetBytes(host);

            using (MemoryStream ms = new MemoryStream(name.Length + 5))
            {
                WriteU16(ms, (ushort)(name.Length + 3));   // длина списка
                WriteByte(ms, 0);                          // host_name
                WriteU16(ms, (ushort)name.Length);
                WriteBytes(ms, name);
                return ms.ToArray();
            }
        }

        private static byte[] SupportedGroups(ushort grease, RandomNumberGenerator rng)
        {
            ushort[] groups = { grease, 0x001d, 0x0017, 0x0018 };   // x25519, P-256, P-384

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
            byte[] http11 = Encoding.ASCII.GetBytes("http/1.1");

            using (MemoryStream ms = new MemoryStream(16))
            {
                WriteU16(ms, (ushort)(h2.Length + http11.Length + 2));
                WriteByte(ms, (byte)h2.Length);
                WriteBytes(ms, h2);
                WriteByte(ms, (byte)http11.Length);
                WriteBytes(ms, http11);
                return ms.ToArray();
            }
        }

        private static byte[] SignatureAlgorithms(ushort grease)
        {
            ushort[] algs =
            {
                0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601
            };

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
                WriteByte(ms, 6);       // длина списка
                WriteU16(ms, grease);
                WriteU16(ms, 0x0304);   // TLS 1.3
                WriteU16(ms, 0x0303);   // TLS 1.2
                return ms.ToArray();
            }
        }

        /// <summary>
        /// key_share. Сначала пробуем настоящий X25519 (Windows 10 1607+ умеет),
        /// иначе честно откатываемся на P-256 — тоже настоящий ключ, только
        /// группа другая. Случайные 32 байта не используем: это заведомо
        /// невалидная точка, и такой hello отличается от настоящего сильнее,
        /// чем другой, но корректный.
        /// </summary>
        private static byte[] KeyShare(RandomNumberGenerator rng, out string keyName)
        {
            keyName = "";

            byte[] x25519 = TryX25519(rng);
            if (x25519 != null && x25519.Length == 32)
            {
                keyName = "X25519";

                using (MemoryStream ms = new MemoryStream(40))
                {
                    WriteU16(ms, 36);          // длина списка key_share
                    WriteU16(ms, 0x001d);      // x25519
                    WriteU16(ms, 32);
                    WriteBytes(ms, x25519);
                    return ms.ToArray();
                }
            }

            byte[] p256 = TryP256();
            if (p256 != null)
            {
                keyName = "P-256";

                using (MemoryStream ms = new MemoryStream(72))
                {
                    WriteU16(ms, (ushort)(4 + p256.Length));
                    WriteU16(ms, 0x0017);      // secp256r1
                    WriteU16(ms, (ushort)p256.Length);
                    WriteBytes(ms, p256);
                    return ms.ToArray();
                }
            }

            // последний резерв: случайные байты. Валидатор их пропустит
            // (ему важен формат записи), но в отчёте это будет видно.
            keyName = "случайные байты (ни один ECDH недоступен)";
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

        private static byte[] TryX25519(RandomNumberGenerator rng)
        {
            try
            {
                // CNG на Windows 10 1607+ поддерживает curve25519; .NET просто проксирует
                ECCurve curve = ECCurve.CreateFromValue("1.3.101.110");

                using (ECDiffieHellman ecdh = ECDiffieHellman.Create(curve))
                {
                    byte[] spki = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

                    // в SPKI последние 32 байта — сам открытый ключ
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
                    point[0] = 0x04;   // несжатая точка
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
            ms.WriteByte((byte)((value >> 16) & 0xFF));
            ms.WriteByte((byte)((value >> 8) & 0xFF));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteU32(MemoryStream ms, uint value)
        {
            ms.WriteByte((byte)((value >> 24) & 0xFF));
            ms.WriteByte((byte)((value >> 16) & 0xFF));
            ms.WriteByte((byte)((value >> 8) & 0xFF));
            ms.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteBytes(MemoryStream ms, byte[] data)
        {
            if (data != null && data.Length > 0) ms.Write(data, 0, data.Length);
        }
    }
}