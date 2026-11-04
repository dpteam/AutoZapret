using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoZapret.Core;

namespace AutoZapret.Update
{
    public sealed class ChecksumResult
    {
        public bool Ok;
        public string Expected = "";
        public string Actual = "";
        public string Error = "";

        public string Text()
        {
            if (Ok) return "SHA-256 совпал";
            if (Error.Length > 0) return Error;
            return "несовпадение: ожидался " + Short(Expected) + ", получен " + Short(Actual);
        }

        private static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "?";
            return hash.Length > 16 ? hash.Substring(0, 16) + "…" : hash;
        }
    }

    /// <summary>
    /// Проверка целостности скачанных файлов по SHA-256.
    ///
    /// Зачем это обязательно (AGENTS.md, «Безопасность»): обновление
    /// приложения — это исполняемый файл, который мы сами потом запускаем
    /// через скрипт-заменщик. Без сверки хеша один подменённый ответ
    /// от сети превращается в чужой код с нашими правами.
    ///
    /// Поддерживаются оба ходовых формата:
    ///   * `sha256sum`:  `<64 hex>  <имя файла>`  (два пробела; `*` = бинарный режим)
    ///   * BSD:          `SHA256 (<имя файла>) = <64 hex>`
    ///
    /// Поведение при отсутствии ожидаемого хеша — ОТКАЗ, а не «ну и ладно».
    /// Мягкий режим («нет хеша — пропускаем») обнуляет всю защиту: атакующему
    /// достаточно не отдать `checksums.txt`.
    /// </summary>
    public static class Checksums
    {
        /// <summary>Размер файла, который ещё можно хешировать в память построчно.</summary>
        private const int BufferSize = 81920;

        /// <summary>SHA-256 файла в нижнем регистре hex.</summary>
        public static string Sha256File(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);

                    StringBuilder hex = new StringBuilder(64);
                    foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return hex.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("UPD", "не удалось посчитать SHA-256: " + ex.Message);
                return "";
            }
        }

        /// <summary>SHA-256 строки. Нужен только тестам и отпечаткам.</summary>
        public static string Sha256Text(string text)
        {
            if (text == null) text = "";

            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));

                StringBuilder hex = new StringBuilder(64);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        /// <summary>
        /// Разбирает файл контрольных сумм. Ключ — имя файла без каталога,
        /// в нижнем регистре: в релизах путь бывает и `.\AutoZapret.zip`, и `AutoZapret.zip`.
        /// </summary>
        public static Dictionary<string, string> Parse(string text)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(text)) return map;

            // HTML вместо файла — типичный признак редиректа на страницу логина
            string head = text.TrimStart();
            if (head.StartsWith("<", StringComparison.Ordinal))
                throw new FormatException("вместо контрольных сумм пришёл HTML");

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                string hash = null;
                string name = null;

                // ---- BSD: SHA256 (name) = hash
                Match bsd = Regex.Match(line, @"^SHA256\s*\((?<name>.+?)\)\s*=\s*(?<hash>[0-9a-fA-F]{64})$",
                    RegexOptions.IgnoreCase);
                if (bsd.Success)
                {
                    name = bsd.Groups["name"].Value;
                    hash = bsd.Groups["hash"].Value;
                }
                else
                {
                    // ---- sha256sum: hash<пробелы>[ *]name
                    Match sum = Regex.Match(line, @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$");
                    if (sum.Success)
                    {
                        hash = sum.Groups["hash"].Value;
                        name = sum.Groups["name"].Value;
                    }
                }

                if (hash == null || string.IsNullOrEmpty(name)) continue;

                string key = NormalizeName(name);
                if (key.Length == 0) continue;

                // первая запись побеждает: дубликат в файле — уже подозрительно,
                // но перебивать первое значение вторым было бы хуже
                if (!map.ContainsKey(key)) map[key] = hash.ToLowerInvariant();
            }

            return map;
        }

        /// <summary>Ищет хеш для файла. Пустая строка, если записи нет.</summary>
        public static string Find(Dictionary<string, string> map, string fileName)
        {
            if (map == null || string.IsNullOrEmpty(fileName)) return "";

            string key = NormalizeName(fileName);
            string value;
            return map.TryGetValue(key, out value) ? value : "";
        }

        /// <summary>
        /// Сверяет файл с ожидаемым хешем из словаря.
        /// Возвращает результат; при отсутствии записи — ошибка, а не «пропущено».
        /// </summary>
        public static ChecksumResult Verify(string path, Dictionary<string, string> map, string fileName = null)
        {
            ChecksumResult result = new ChecksumResult();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                result.Error = "файл не найден";
                return result;
            }

            string name = string.IsNullOrEmpty(fileName) ? Path.GetFileName(path) : fileName;

            string expected = Find(map, name);
            if (expected.Length == 0)
            {
                result.Error = "в файле контрольных сумм нет записи для " + name;
                return result;
            }

            string actual = Sha256File(path);
            if (actual.Length == 0)
            {
                result.Error = "не удалось посчитать SHA-256";
                return result;
            }

            result.Expected = expected;
            result.Actual = actual;
            result.Ok = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

            if (!result.Ok) result.Error = "";   // текстом займётся Text()

            return result;
        }

        /// <summary>Похоже ли значение на SHA-256. Нужно для валидации ввода.</summary>
        public static bool LooksLikeHash(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }

        /// <summary>Имя без каталога и без ведущих `.\`, в нижнем регистре.</summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            string value = name.Trim().Trim('"');

            // `.\AutoZapret.zip` и `dist/AutoZapret.zip` -> `AutoZapret.zip`
            int slash = value.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) value = value.Substring(slash + 1);

            value = value.TrimStart('.', '\\', '/');

            return value.ToLowerInvariant();
        }
    }
}