using System;
using System.Collections.Generic;
using System.IO;
using AutoZapret.Core;
using AutoZapret.Lists;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Откуда брать цели для проверки стратегий.
    ///
    /// Зачем это вообще нужно. Встроенный набор — youtube, discord, google,
    /// twitch — это «типичные» цели. У человека может не быть проблем с
    /// youtube и быть проблема с одним сайтом, которого в наборе нет. Тогда
    /// перебор стратегий честно показывает «всё прошло» (потому что проверял
    /// не то), а нужный сайт не открывается — и выглядит это как «программа
    /// врёт».
    ///
    /// Поэтому цели берутся из СВОЕГО списка — того же `list-general-user.txt`,
    /// который движок и так обходит. Проверяем ровно то, что человек туда
    /// записал.
    /// </summary>
    public static class ProbeTargets
    {
        /// <summary>
        /// Сколько своих доменов проверять.
        ///
        /// Восемь — не «мало», а осознанный потолок. Одна проба — это до
        /// 8 секунд; перебор 20 стратегий × 4 пробы × N доменов растёт линейно
        /// по N. При 30 доменах человек будет ждать 40+ минут и закроет окно.
        /// Лучше проверить восемь самых нужных, чем тридцать и не дождаться.
        /// </summary>
        public const int MaxOwn = 8;

        /// <summary>
        /// Цели по настройке: свой список или встроенный набор.
        ///
        /// note — короткое объяснение для интерфейса, почему выбрано именно
        /// это. Молчаливая подмена целей — худший вариант: человек думает,
        /// что проверили его домены, а проверили youtube.
        /// </summary>
        public static List<ProbeTarget> ForSettings(out string note)
        {
            Settings settings = Settings.Current;

            if (settings != null && settings.ProbeOwnList)
            {
                string ownNote;
                List<ProbeTarget> own = Own(MaxOwn, out ownNote);

                if (own.Count > 0)
                {
                    note = "свои домены: " + own.Count + (ownNote.Length > 0 ? ", " + ownNote : "");
                    return own;
                }

                // Список пуст — не подменяем молча на встроенный: говорим, что
                // своих доменов нет, и проверяем встроенные. Иначе «проверял
                // свои» окажется неправдой.
                note = "свой список пуст — проверяю встроенные цели";
                return ConnectivityProbe.ZapretTargets();
            }

            note = "встроенные цели";
            return ConnectivityProbe.ZapretTargets();
        }

        public static List<ProbeTarget> ForSettings()
        {
            string ignored;
            return ForSettings(out ignored);
        }

        /// <summary>
        /// Цели из `list-general-user.txt`.
        ///
        /// Что отбрасываем и почему:
        ///   * выключенные записи (`# domain`) — человек их отключил;
        ///   * `!domain` — это ИСКЛЮЧЕНИЕ, «не обходить». Проверять его
        ///     стратегией бессмысленно: он и должен открываться без обхода;
        ///   * `*.domain` — по шаблону нельзя сделать запрос. Базовая часть
        ///     (`domain`) может не резолвиться, а может и резолвиться в
        ///     заглушку. Пробовать её — значит получить провал не по вине
        ///     стратегии;
        ///   * IP и CIDR — HTTPS по адресу без имени ломается на SNI и на
        ///     виртуальных хостах, и провал опять не про стратегию;
        ///   * повторы — иначе один домен займёт половину бюджета времени.
        /// </summary>
        public static List<ProbeTarget> Own(int max, out string note)
        {
            List<ProbeTarget> result = new List<ProbeTarget>();
            note = "";

            string path = HostlistManager.GeneralPath;

            List<ListEntry> entries;

            try { entries = HostlistManager.Read(path); }
            catch (Exception ex)
            {
                note = "список не прочитался: " + ex.Message;
                return result;
            }

            if (entries.Count == 0)
            {
                note = "список пуст";
                return result;
            }

            int skippedDisabled = 0;
            int skippedException = 0;
            int skippedPattern = 0;
            int skippedAddress = 0;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ListEntry entry in entries)
            {
                if (result.Count >= Math.Max(1, max)) break;

                if (!entry.Enabled) { skippedDisabled++; continue; }

                string value = (entry.Value ?? "").Trim();
                if (value.Length == 0) continue;

                // `!domain` — исключение из обхода, а не цель.
                if (value.StartsWith("!", StringComparison.Ordinal)) { skippedException++; continue; }

                // Шаблон: по нему нельзя обратиться.
                if (value.IndexOf('*') >= 0) { skippedPattern++; continue; }

                // Адрес или подсеть: HTTPS по IP проверяет не то.
                if (HostlistManager.IsValidIpOrCidr(value)) { skippedAddress++; continue; }

                // Домен с портом (`example.com:8080`) в hostlist допустим, но
                // в URL порт надо оставить, а имя цели — без него.
                string host = value;
                int colon = host.LastIndexOf(':');

                if (colon > 0 && host.IndexOf(']') < 0) host = host.Substring(0, colon);

                if (host.Length == 0 || host.IndexOf('.') < 0) { skippedPattern++; continue; }

                if (!seen.Add(host)) continue;

                result.Add(new ProbeTarget(host, "https://" + value, false));
            }

            List<string> skipped = new List<string>();

            if (skippedDisabled > 0) skipped.Add("выключенных " + skippedDisabled);
            if (skippedException > 0) skipped.Add("исключений " + skippedException);
            if (skippedPattern > 0) skipped.Add("шаблонов/служебных " + skippedPattern);
            if (skippedAddress > 0) skipped.Add("адресов " + skippedAddress);

            note = skipped.Count > 0 ? "пропущено: " + string.Join(", ", skipped.ToArray()) : "";

            return result;
        }

        /// <summary>
        /// Сколько доменов в своём списке вообще пригодны для проверки.
        ///
        /// Нужно интерфейсу: показать «своих доменов: 12, проверю первые 8»
        /// до запуска, а не после. Иначе человек с 200 доменами не понимает,
        /// почему проверилось восемь.
        /// </summary>
        public static int CountUsable()
        {
            string note;
            return Own(int.MaxValue, out note).Count;
        }
    }
}
