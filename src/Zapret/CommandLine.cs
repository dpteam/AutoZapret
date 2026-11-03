using System;
using System.Collections.Generic;
using System.Text;

namespace AutoZapret.Zapret
{
    /// <summary>
    /// Сборка командной строки для Process.Start в .NET Framework.
    ///
    /// ПОЧЕМУ ЭТО НУЖНО. В .NET Core 2.1+ есть ProcessStartInfo.ArgumentList,
    /// который сам корректно квотирует аргументы. В .NET Framework 4.8 его НЕТ —
    /// есть только строка Arguments. Значит, квотирование придётся делать самим,
    /// и делать строго по правилам разбора командной строки Windows
    /// (CommandLineToArgvW / MS C runtime):
    ///
    ///   * аргумент без пробелов, табов и кавычек передаётся как есть;
    ///   * иначе он заключается в двойные кавычки;
    ///   * последовательность обратных слешей перед кавычкой удваивается;
    ///   * обратные слеши в конце аргумента удваиваются (иначе съедят закрывающую кавычку);
    ///   * кавычка внутри аргумента экранируется как \" .
    ///
    /// Если делать это «на глаз» (например, просто обернуть в кавычки), ломаются пути
    /// с пробелами и стратегии с кавычками внутри — а таких в конфигах zapret хватает.
    /// </summary>
    public static class CommandLine
    {
        /// <summary>Собирает строку Arguments из массива токенов.</summary>
        public static string Build(IEnumerable<string> args)
        {
            if (args == null) return string.Empty;

            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (string arg in args)
            {
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(Quote(arg));
            }
            return sb.ToString();
        }

        /// <summary>Квотирует один аргумент по правилам Windows.</summary>
        public static string Quote(string arg)
        {
            if (arg == null) return "\"\"";

            // пустая строка обязана быть в кавычках, иначе исчезнет
            if (arg.Length == 0) return "\"\"";

            bool needsQuotes = false;
            foreach (char c in arg)
            {
                if (c == ' ' || c == '\t' || c == '"' || c == '\n' || c == '\v')
                {
                    needsQuotes = true;
                    break;
                }
            }
            if (!needsQuotes) return arg;

            StringBuilder sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');

            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    // удваиваем все накопленные слеши и добавляем экранированную кавычку
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                // обычный символ: сначала отдаём накопленные слеши как есть
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }

            // хвостовые слеши удваиваются, чтобы не экранировать закрывающую кавычку
            if (backslashes > 0) sb.Append('\\', backslashes * 2);

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Разбирает строку на аргументы (обратная операция, для тестов).</summary>
        public static List<string> Split(string commandLine)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return result;

            int i = 0;
            while (i < commandLine.Length)
            {
                while (i < commandLine.Length && (commandLine[i] == ' ' || commandLine[i] == '\t')) i++;
                if (i >= commandLine.Length) break;

                StringBuilder current = new StringBuilder();
                bool inQuotes = false;

                while (i < commandLine.Length)
                {
                    char c = commandLine[i];

                    if (!inQuotes && (c == ' ' || c == '\t')) break;

                    if (c == '\\')
                    {
                        int count = 0;
                        while (i < commandLine.Length && commandLine[i] == '\\') { count++; i++; }

                        if (i < commandLine.Length && commandLine[i] == '"')
                        {
                            current.Append('\\', count / 2);
                            if (count % 2 == 1) { current.Append('"'); i++; }
                        }
                        else
                        {
                            current.Append('\\', count);
                        }
                        continue;
                    }

                    if (c == '"')
                    {
                        if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                        {
                            current.Append('"');
                            i += 2;
                            continue;
                        }
                        inQuotes = !inQuotes;
                        i++;
                        continue;
                    }

                    current.Append(c);
                    i++;
                }

                result.Add(current.ToString());
            }

            return result;
        }
    }
}
