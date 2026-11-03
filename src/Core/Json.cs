using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Минимальный JSON: парсер + сериализатор.
    ///
    /// Зачем свой, а не System.Text.Json: его нет в .NET Framework 4.8 без NuGet,
    /// а JavaScriptSerializer тянет System.Web.Extensions и ведёт себя странно
    /// с long/DateTime. Нам нужно ровно две вещи: разобрать сообщение из WebView2
    /// и сохранить настройки. Модель данных:
    ///
    ///   object  -> Dictionary&lt;string, object&gt;
    ///   array   -> List&lt;object&gt;
    ///   string  -> string
    ///   number  -> double (целые, влезающие в long, дополнительно помечены ниже)
    ///   bool    -> bool
    ///   null    -> null
    ///
    /// Правила: никаких исключений на «плохом» входе из UI - ParseSafe вернёт null.
    /// </summary>
    public static class Json
    {
        // ---------------------------------------------------------------- parse

        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int pos = 0;
            object value = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos < text.Length)
                throw new FormatException("trailing characters at " + pos);
            return value;
        }

        /// <summary>Не бросает: при любой ошибке возвращает null.</summary>
        public static object ParseSafe(string text)
        {
            try { return string.IsNullOrEmpty(text) ? null : Parse(text); }
            catch { return null; }
        }

        public static Dictionary<string, object> ParseObjectSafe(string text)
        {
            return ParseSafe(text) as Dictionary<string, object>;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                map[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + e + "'");
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            if (i == start) throw new FormatException("expected value at " + start);
            string raw = s.Substring(start, i - start);

            long l;
            if (!raw.Contains(".") && !raw.Contains("e") && !raw.Contains("E") &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                return l;

            double d;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("bad number '" + raw + "'");
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("expected '" + word + "' at " + i);
            i += word.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        // ------------------------------------------------------------- stringify

        public static string Stringify(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }

            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }

            if (value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is float || value is double || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("0"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is Enum) { WriteString(sb, value.ToString()); return; }

            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            IDictionary generic = value as IDictionary;
            if (generic != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in generic)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }

            IEnumerable seq = value as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------- accessors
        // Все безопасные: отсутствие ключа или неверный тип -> defaultValue.

        public static string GetString(Dictionary<string, object> map, string key, string def)
        {
            if (map == null) return def;
            object v;
            if (!map.TryGetValue(key, out v) || v == null) return def;
            string s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool def)
        {
            if (map == null) return def;
            object v;
            if (!map.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = v as string;
            if (s != null)
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            }
            return def;
        }

        public static int GetInt(Dictionary<string, object> map, string key, int def)
        {
            if (map == null) return def;
            object v;
            if (!map.TryGetValue(key, out v) || v == null) return def;
            if (v is long) return ClampToInt((long)v, def);
            if (v is double) return ClampToInt((double)v, def);
            int parsed;
            if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                             NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return def;
        }

        public static long GetLong(Dictionary<string, object> map, string key, long def)
        {
            if (map == null) return def;
            object v;
            if (!map.TryGetValue(key, out v) || v == null) return def;
            if (v is long) return (long)v;
            if (v is double) { double d = (double)v; return (d >= long.MinValue && d <= long.MaxValue) ? (long)d : def; }
            long parsed;
            if (long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                              NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return def;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            if (map == null) return null;
            object v;
            return map.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> GetArray(Dictionary<string, object> map, string key)
        {
            if (map == null) return null;
            object v;
            return map.TryGetValue(key, out v) ? v as List<object> : null;
        }

        private static int ClampToInt(long v, int def)
        {
            if (v < int.MinValue || v > int.MaxValue) return def;
            return (int)v;
        }

        private static int ClampToInt(double v, int def)
        {
            if (double.IsNaN(v) || v < int.MinValue || v > int.MaxValue) return def;
            return (int)v;
        }
    }
}
