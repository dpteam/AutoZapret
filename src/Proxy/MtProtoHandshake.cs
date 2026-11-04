using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AutoZapret.Proxy
{
    public enum MtProtoTag
    {
        Abridged,       // EF EF EF EF
        Intermediate,   // EE EE EE EE
        Padded          // DD DD DD DD
    }

    public sealed class HandshakeResult
    {
        public bool Ok;
        public string Error = "";

        public int Dc;
        public bool IsMedia;
        public bool IsTestDc;
        public MtProtoTag Tag;

        /// <summary>Ключ и IV для направления «клиент -> прокси» (и обратно после реверса).</summary>
        public byte[] ClientKey;
        public byte[] ClientIv;

        /// <summary>Исходные 64 байта от клиента — нужны для вычисления обратного направления.</summary>
        public byte[] ClientInit;
    }

    /// <summary>
    /// MTProto obfuscated2 handshake.
    ///
    /// Раскладка входных 64 байт (из refs/tg-ws-proxy, proxy/utils.py):
    ///   [0..8)   random, пропускается
    ///   [8..40)  prekey (32)
    ///   [40..56) iv (16)
    ///   [56..60) proto tag
    ///   [60..62) dc_idx, int16 LE, знак = media
    ///   [62..64) random
    ///
    /// Ключ: SHA256(prekey || secret), расшифровка AES-256-CTR с iv из [40..56).
    ///
    /// КРИТИЧНО (самая частая ошибка при порте): направления асимметричны.
    ///   клиент -> прокси : key = SHA256(prekey || secret),           iv = init[40..56)
    ///   прокси -> клиент : key = SHA256(reverse(init[8..56])[0..32] || secret)
    ///                      iv  = reverse(init[8..56])[32..48)
    /// То есть обратный ключ выводится из ПЕРЕВЁРНУТОГО блока prekey||iv.
    /// Для направления в Telegram secret НЕ используется вовсе.
    /// </summary>
    public static class MtProtoHandshake
    {
        public const int HandshakeLen = 64;
        public const int SkipLen = 8;
        public const int PrekeyLen = 32;
        public const int KeyLen = 32;
        public const int IvLen = 16;
        public const int TagPos = 56;
        public const int DcPos = 60;

        public static readonly byte[] TagAbridged = { 0xEF, 0xEF, 0xEF, 0xEF };
        public static readonly byte[] TagIntermediate = { 0xEE, 0xEE, 0xEE, 0xEE };
        public static readonly byte[] TagPadded = { 0xDD, 0xDD, 0xDD, 0xDD };

        /// <summary>Разбирает 64-байтовый init от клиента.</summary>
        public static HandshakeResult Decode(byte[] init, byte[] secret)
        {
            HandshakeResult result = new HandshakeResult();

            if (init == null || init.Length < HandshakeLen)
            {
                result.Error = "init короче 64 байт";
                return result;
            }
            if (secret == null || secret.Length != 16)
            {
                result.Error = "секрет должен быть 16 байт";
                return result;
            }

            byte[] prekey = new byte[PrekeyLen];
            Buffer.BlockCopy(init, SkipLen, prekey, 0, PrekeyLen);

            byte[] iv = new byte[IvLen];
            Buffer.BlockCopy(init, SkipLen + PrekeyLen, iv, 0, IvLen);

            byte[] key = DeriveKey(prekey, secret);

            byte[] plain = new byte[HandshakeLen];
            Buffer.BlockCopy(init, 0, plain, 0, HandshakeLen);

            using (AesCtr ctr = new AesCtr(key, iv))
                ctr.Transform(plain, 0, HandshakeLen);

            // тег протокола
            if (Match(plain, TagPos, TagAbridged)) result.Tag = MtProtoTag.Abridged;
            else if (Match(plain, TagPos, TagIntermediate)) result.Tag = MtProtoTag.Intermediate;
            else if (Match(plain, TagPos, TagPadded)) result.Tag = MtProtoTag.Padded;
            else
            {
                result.Error = "неизвестный proto tag " + Hex(plain, TagPos, 4) +
                               " (неверный секрет или не MTProto-клиент)";
                return result;
            }

            // dc_idx: int16 LE, знак означает media
            int rawDc = plain[DcPos] | (plain[DcPos + 1] << 8);
            if (rawDc > 32767) rawDc -= 65536;

            int dc = Math.Abs(rawDc);
            result.IsMedia = rawDc < 0;

            if (dc >= 10000)
            {
                dc -= 10000;
                result.IsTestDc = true;
            }

            if (!((dc >= 1 && dc <= 5) || dc == 203))
            {
                result.Error = "неверный DC: " + dc;
                return result;
            }

            result.Dc = dc;
            result.ClientKey = key;
            result.ClientIv = iv;
            result.ClientInit = init;
            result.Ok = true;
            return result;
        }

        /// <summary>Ключ направления «прокси -> клиент»: из перевёрнутого prekey||iv.</summary>
        public static void DeriveReverse(byte[] init, byte[] secret, out byte[] key, out byte[] iv)
        {
            // rev = reverse(init[8..56]) — 48 байт
            byte[] rev = new byte[48];
            for (int i = 0; i < 48; i++)
                rev[i] = init[SkipLen + 48 - 1 - i];

            byte[] prekey = new byte[PrekeyLen];
            Buffer.BlockCopy(rev, 0, prekey, 0, PrekeyLen);

            iv = new byte[IvLen];
            Buffer.BlockCopy(rev, PrekeyLen, iv, 0, IvLen);

            key = DeriveKey(prekey, secret);
        }

        /// <summary>Ключ направления «прокси -> Telegram»: без secret, прямо из init.</summary>
        public static void DeriveRelay(byte[] relayInit, out byte[] key, out byte[] iv)
        {
            key = new byte[KeyLen];
            Buffer.BlockCopy(relayInit, SkipLen, key, 0, KeyLen);

            iv = new byte[IvLen];
            Buffer.BlockCopy(relayInit, SkipLen + PrekeyLen, iv, 0, IvLen);
        }

        public static byte[] DeriveKey(byte[] prekey, byte[] secret)
        {
            byte[] input = new byte[prekey.Length + secret.Length];
            Buffer.BlockCopy(prekey, 0, input, 0, prekey.Length);
            Buffer.BlockCopy(secret, 0, input, prekey.Length, secret.Length);

            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(input);
        }

        /// <summary>
        /// Генерирует 64-байтовый init для подключения к Telegram.
        ///
        /// Первые байты не должны выглядеть как HTTP-запрос или TLS-запись:
        /// это проверяет DPI. Запрещённые начала — из refs/tg-ws-proxy.
        /// </summary>
        public static byte[] GenerateRelayInit(int dc, bool isMedia)
        {
            byte[] init = new byte[HandshakeLen];
            RandomNumberGenerator rng = RandomNumberGenerator.Create();

            try
            {
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    rng.GetBytes(init);
                    if (!LooksReserved(init)) break;
                }

                // tail: tag(4) || dc(2 LE, знак = media) || random(2), зашифрованные
                byte[] key = new byte[KeyLen];
                Buffer.BlockCopy(init, SkipLen, key, 0, KeyLen);

                byte[] iv = new byte[IvLen];
                Buffer.BlockCopy(init, SkipLen + PrekeyLen, iv, 0, IvLen);

                byte[] tail = new byte[8];
                Buffer.BlockCopy(TagIntermediate, 0, tail, 0, 4);

                int dcValue = isMedia ? -dc : dc;
                tail[4] = (byte)(dcValue & 0xFF);
                tail[5] = (byte)((dcValue >> 8) & 0xFF);
                // tail[6..8] остаются случайными из init

                using (AesCtr ctr = new AesCtr(key, iv))
                {
                    // keystream для позиции 56: прокручиваем 56 байт
                    ctr.Skip(TagPos);
                    ctr.Transform(tail, 0, 8);
                }

                Buffer.BlockCopy(tail, 0, init, TagPos, 8);
                return init;
            }
            finally
            {
                rng.Dispose();
            }
        }

        private static bool LooksReserved(byte[] init)
        {
            // 0xEF первым байтом — похоже на abridged-тег
            if (init[0] == 0xEF) return true;

            // 4..8 должны быть не нулевые
            if (init[4] == 0 && init[5] == 0 && init[6] == 0 && init[7] == 0) return true;

            // Начала, похожие на HTTP или TLS. Проверяем и полные четырёхбайтовые
            // шаблоны, и двухбайтовые префиксы: DPI смотрит на первые байты, и
            // «GE…» для него уже начало «GET». Отбрасываем оба варианта — стоит
            // это 2 попытки на 65536, а не «раз в тысячу рукопожатий не повезло».
            if (init[0] == (byte)'G' && init[1] == (byte)'E') return true;
            if (init[0] == 0x16 && init[1] == 0x03) return true;

            if (init[0] == (byte)'H' && init[1] == (byte)'E' && init[2] == (byte)'A' && init[3] == (byte)'D') return true;
            if (init[0] == (byte)'P' && init[1] == (byte)'O' && init[2] == (byte)'S' && init[3] == (byte)'T') return true;
            if (init[0] == (byte)'G' && init[1] == (byte)'E' && init[2] == (byte)'T' && init[3] == (byte)' ') return true;
            if (init[0] == (byte)'O' && init[1] == (byte)'P' && init[2] == (byte)'T' && init[3] == (byte)'I') return true;
            if (init[0] == 0x16 && init[1] == 0x03 && init[2] == 0x01 && init[3] == 0x02) return true;

            return false;
        }

        private static bool Match(byte[] data, int offset, byte[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
                if (data[offset + i] != pattern[i]) return false;
            return true;
        }

        public static string Hex(byte[] data, int offset, int count)
        {
            StringBuilder sb = new StringBuilder(count * 2);
            for (int i = 0; i < count && offset + i < data.Length; i++)
                sb.Append(data[offset + i].ToString("x2"));
            return sb.ToString();
        }

        public static string TagName(MtProtoTag tag)
        {
            switch (tag)
            {
                case MtProtoTag.Abridged: return "abridged";
                case MtProtoTag.Intermediate: return "intermediate";
                case MtProtoTag.Padded: return "padded";
                default: return "?";
            }
        }
    }
}
