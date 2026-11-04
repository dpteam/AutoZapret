using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AutoZapret.Core;

namespace AutoZapret.Payloads
{
    public enum PayloadKind
    {
        Unknown,
        TlsClientHello,
        QuicInitial,
        Stun
    }

    public sealed class Payload
    {
        public string File = "";
        public string FullPath = "";
        public PayloadKind Kind = PayloadKind.Unknown;
        public string Sni = "";
        public int Size;
        public bool Valid;
        public string InvalidReason = "";

        /// <summary>Откуда взят: "flowseal", "tlspack", "own".</summary>
        public string Source = "";

        public string DisplayName
        {
            get
            {
                string kind = KindName(Kind);
                return string.IsNullOrEmpty(Sni) ? kind + " / " + File : kind + " / " + Sni;
            }
        }

        public static string KindName(PayloadKind kind)
        {
            switch (kind)
            {
                case PayloadKind.TlsClientHello: return "TLS";
                case PayloadKind.QuicInitial: return "QUIC";
                case PayloadKind.Stun: return "STUN";
                default: return "?";
            }
        }
    }

    /// <summary>
    /// Проверка .bin-фейков перед подстановкой в аргументы winws.
    ///
    /// Зачем: winws НЕ проверяет содержимое. Если подсунуть битый файл, он либо
    /// ругнётся невнятно, либо (хуже) будет молча слать мусор, и пользователь
    /// решит, что «стратегия не работает». Проверка дешёвая, ложных срабатываний
    /// на настоящих фейках не даёт.
    ///
    /// Форматы:
    ///   TLS ClientHello: 16 03 xx LL LL | 01 LL LL ...
    ///   QUIC Initial:    C0..CF 00000001 ... (long header, version 1, Initial)
    ///   STUN:            LL LL (кратно 4) 21 12 A4 42 ...
    /// </summary>
    public static class PayloadValidator
    {
        public static PayloadKind DetectKind(byte[] data, out string reason)
        {
            reason = "";

            if (data == null || data.Length < 5)
            {
                reason = "файл слишком мал";
                return PayloadKind.Unknown;
            }

            // TLS handshake record
            if (data[0] == 0x16)
            {
                if (data.Length < 43)
                {
                    reason = "TLS: меньше 43 байт";
                    return PayloadKind.Unknown;
                }
                if (data[5] != 0x01)
                {
                    reason = "TLS: не ClientHello (тип " + data[5] + ")";
                    return PayloadKind.Unknown;
                }

                int recordLen = (data[3] << 8) | data[4];
                if (recordLen + 5 != data.Length)
                {
                    reason = "TLS: длина записи " + recordLen + " не совпадает с файлом " + (data.Length - 5);
                    return PayloadKind.Unknown;
                }

                if (data[9] != 0x03)
                {
                    reason = "TLS: неверная версия";
                    return PayloadKind.Unknown;
                }

                return PayloadKind.TlsClientHello;
            }

            // QUIC long header
            if ((data[0] & 0x80) != 0)
            {
                // Первый байт long header:
                //   0x80 = Header Form (long)
                //   0x40 = Fixed Bit — ВСЕГДА 1, это не тип пакета
                //   0x30 = Long Packet Type: 0x00 Initial, 0x10 0-RTT, 0x20 Handshake, 0x30 Retry
                //
                // ВАЖНО: здесь НЕ проверяем тип пакета и версию жёстко.
                // В наборе Flowseal есть файлы с версией 2 (0x6b3343cf), с greased-версиями
                // и даже Handshake-пакеты, названные quic_initial_*. Все они рабочие:
                // winws просто отправляет эти байты как фейк. Строгая RFC-проверка
                // отбраковывала бы половину набора — это ложные срабатывания.
                //
                // Настоящая задача валидатора: поймать обрезанный, пустой или
                // принципиально не тот файл. Поэтому требуем только:
                //   * long header (это точно QUIC, а не TLS/STUN/мусор);
                //   * >= 1200 байт (требование RFC 9000 к клиентскому Initial).
                if (data.Length < 1200)
                {
                    reason = "QUIC: " + data.Length + " байт, нужно минимум 1200";
                    return PayloadKind.Unknown;
                }

                return PayloadKind.QuicInitial;
            }

            // STUN
            if (data.Length >= 20)
            {
                int msgType = (data[0] << 8) | data[1];
                int msgLen = (data[2] << 8) | data[3];

                if ((msgType & 0xC000) == 0 &&
                    msgLen % 4 == 0 &&
                    data[4] == 0x21 && data[5] == 0x12 && data[6] == 0xA4 && data[7] == 0x42)
                {
                    return PayloadKind.Stun;
                }
            }

            reason = "неизвестный формат (первый байт 0x" +
                     data[0].ToString("x2", CultureInfo.InvariantCulture) + ")";
            return PayloadKind.Unknown;
        }

        public static Payload Inspect(string path, string source = "")
        {
            Payload payload = new Payload
            {
                File = Path.GetFileName(path),
                FullPath = path,
                Source = source
            };

            try
            {
                if (!File.Exists(path))
                {
                    payload.InvalidReason = "файл не найден";
                    return payload;
                }

                byte[] data = File.ReadAllBytes(path);
                payload.Size = data.Length;

                string reason;
                payload.Kind = DetectKind(data, out reason);
                payload.Valid = payload.Kind != PayloadKind.Unknown;
                payload.InvalidReason = reason;
                payload.Sni = ExtractSni(data, payload.Kind);
                return payload;
            }
            catch (Exception ex)
            {
                payload.InvalidReason = ex.Message;
                return payload;
            }
        }

        /// <summary>
        /// Достаёт SNI из ClientHello, если он там есть. Нужно для отображения
        /// и для выбора фейка «под домен».
        /// </summary>
        public static string ExtractSni(byte[] data, PayloadKind kind)
        {
            if (kind != PayloadKind.TlsClientHello || data == null || data.Length < 43) return "";

            try
            {
                int pos = 5 + 4;                      // record header + handshake header
                pos += 2 + 32;                        // version + random

                int sessionLen = data[pos];
                pos += 1 + sessionLen;

                int cipherLen = (data[pos] << 8) | data[pos + 1];
                pos += 2 + cipherLen;

                int compLen = data[pos];
                pos += 1 + compLen;

                if (pos + 2 > data.Length) return "";
                int extTotal = (data[pos] << 8) | data[pos + 1];
                pos += 2;

                int end = Math.Min(pos + extTotal, data.Length);

                while (pos + 4 <= end)
                {
                    int extType = (data[pos] << 8) | data[pos + 1];
                    int extLen = (data[pos + 2] << 8) | data[pos + 3];
                    pos += 4;

                    if (extType == 0x0000 && pos + extLen <= data.Length)
                    {
                        // server_name: list_len(2) type(1) name_len(2) name
                        int p = pos + 2;
                        if (p + 3 > data.Length) return "";
                        int nameLen = (data[p + 1] << 8) | data[p + 2];
                        p += 3;
                        if (p + nameLen > data.Length) return "";
                        return Encoding.ASCII.GetString(data, p, nameLen);
                    }

                    pos += extLen;
                }
            }
            catch { }

            return "";
        }
    }

    /// <summary>
    /// Библиотека фейков: индексирует .bin в engine\bin и assets\fakes,
    /// проверяет их и отдаёт для UI.
    ///
    /// Индекс кешируется: файлов стало 1671 (набор TLSpack), а пересканирование
    /// читает каждый файл с диска и проверяет формат — это секунды, которые
    /// нельзя тратить на каждое открытие экрана.
    /// </summary>
    public sealed class PayloadLibrary
    {
        private static readonly object CacheGate = new object();
        private static PayloadLibrary _cached;

        private readonly List<Payload> _payloads = new List<Payload>();
        private readonly object _gate = new object();

        /// <summary>Общий экземпляр с кешем. null -> пересканировать.</summary>
        public static PayloadLibrary Shared()
        {
            lock (CacheGate)
            {
                if (_cached == null)
                {
                    _cached = new PayloadLibrary();
                    _cached.Scan();
                }
                return _cached;
            }
        }

        /// <summary>Сбросить кеш (после импорта новых фейков).</summary>
        public static void Invalidate()
        {
            lock (CacheGate) _cached = null;
        }

        public IReadOnlyList<Payload> All
        {
            get { lock (_gate) return _payloads.ToArray(); }
        }

        public int Count { get { lock (_gate) return _payloads.Count; } }

        public int ValidCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (Payload p in _payloads) if (p.Valid) n++;
                    return n;
                }
            }
        }

        /// <summary>Сканирует каталоги и строит индекс. Возвращает число файлов.</summary>
        public int Scan()
        {
            List<Payload> found = new List<Payload>();

            ScanDirectory(Paths.EngineBinDir, "flowseal", found);
            ScanDirectory(Paths.FakesDir, "own", found);

            lock (_gate)
            {
                _payloads.Clear();
                _payloads.AddRange(found);
            }

            Log.Info("FAKE", "фейков найдено: " + found.Count + ", валидных: " + ValidCount);
            return found.Count;
        }

        /// <summary>Поиск по SNI или имени файла (для UI).</summary>
        public List<Payload> Search(string query, int limit)
        {
            List<Payload> result = new List<Payload>();
            string q = (query ?? "").Trim().ToLowerInvariant();

            lock (_gate)
            {
                foreach (Payload p in _payloads)
                {
                    if (!p.Valid) continue;

                    if (q.Length > 0)
                    {
                        bool match = (p.Sni ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     p.File.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!match) continue;
                    }

                    result.Add(p);
                    if (limit > 0 && result.Count >= limit) break;
                }
            }

            return result;
        }

        private static void ScanDirectory(string dir, string source, List<Payload> target)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try
            {
                foreach (string path in Directory.GetFiles(dir, "*.bin"))
                {
                    Payload payload = PayloadValidator.Inspect(path, source);
                    target.Add(payload);

                    if (!payload.Valid)
                        Log.Warn("FAKE", Path.GetFileName(path) + ": " + payload.InvalidReason);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FAKE", "сканирование " + dir + ": " + ex.Message);
            }
        }

        /// <summary>Валидные фейки заданного типа.</summary>
        public List<Payload> ByKind(PayloadKind kind)
        {
            List<Payload> result = new List<Payload>();
            lock (_gate)
            {
                foreach (Payload p in _payloads)
                    if (p.Valid && p.Kind == kind) result.Add(p);
            }
            return result;
        }

        public Payload Find(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            lock (_gate)
            {
                foreach (Payload p in _payloads)
                    if (string.Equals(p.File, fileName, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>
        /// Проверяет, что все .bin, на которые ссылаются аргументы стратегии, валидны.
        /// Возвращает список проблем (пусто = всё хорошо).
        /// </summary>
        public List<string> ValidateStrategyArguments(List<string> args)
        {
            List<string> problems = new List<string>();
            if (args == null) return problems;

            foreach (string arg in args)
            {
                int eq = arg.IndexOf('=');
                if (eq < 0) continue;

                string value = arg.Substring(eq + 1);
                if (!value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(value)) continue;   // отсутствие файла ловит отдельная проверка

                Payload payload = PayloadValidator.Inspect(value);
                if (!payload.Valid)
                {
                    problems.Add(Path.GetFileName(value) + ": " + payload.InvalidReason);
                }
            }

            return problems;
        }
    }
}
