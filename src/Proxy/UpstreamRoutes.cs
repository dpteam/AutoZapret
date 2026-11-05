using System;
using System.Collections.Generic;
using AutoZapret.Core;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Память о том, какой WS-шлюз Telegram работает, а какой — нет.
    ///
    /// Зачем отдельный класс, если доменов всего два и они перебираются в цикле:
    /// без памяти каждое НОВОЕ соединение (а их за минуту бывает десятки)
    /// начинает с первого домена. Если первый мёртв, каждое соединение платит
    /// таймаут 8 секунд, и Telegram показывает «соединение…» вместо сообщений.
    ///
    /// Идея взята из refs/_Add15/telegram_access.py (слой «Доступ»), где маршрут
    /// запоминается на 120 секунд, а упавший уходит в карантин на 30. Перенесено
    /// только это: остальное там про Bot API и MTProxy, к нашему WS-прокси
    /// отношения не имеет.
    ///
    /// Три правила, и все три важны:
    ///   * рабочий маршрут помним (иначе платим таймаут каждый раз);
    ///   * упавший — в карантин, но НЕ навсегда (сеть меняется, блокировки снимают);
    ///   * неудачи считаем по хосту: если хост отказал на TCP, второй его
    ///     вариант (тот же хост с «-1») проверять бессмысленно.
    /// </summary>
    public static class UpstreamRoutes
    {
        /// <summary>Сколько живёт память о рабочем маршруте.</summary>
        public const int MaxAgeSec = 120;

        /// <summary>Карантин упавшего маршрута.</summary>
        public const int CooldownSec = 30;

        /// <summary>Первая пауза, когда не работает ВООБЩЕ ничего.</summary>
        public const int RecheckPauseSec = 2;

        /// <summary>Потолок экспоненциальной паузы.</summary>
        public const int RecheckMaxSec = 30;

        private sealed class RouteState
        {
            public string Domain = "";
            public DateTime SeenAt;
        }

        private static readonly object Gate = new object();

        // ключ — «dc:media»: у обычного и медиа-подключения разные домены
        private static readonly Dictionary<string, RouteState> Good = new Dictionary<string, RouteState>();
        private static readonly Dictionary<string, DateTime> Failed = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, int> Strikes = new Dictionary<string, int>();

        // «Всё лежит» — отдельное состояние от «этот домен упал».
        //
        // Если не работает НИ ОДИН маршрут, то пробовать снова на каждом
        // соединении бессмысленно: клиент всё равно ждёт, пока мы переберём
        // всё с таймаутами, и видит «соединение…». Пауза растёт вдвое за
        // каждую неудачу (2 → 4 → 8 → 16 → 30 и дальше по 30), а ручная
        // проверка её сбрасывает — человек не должен ждать из-за того, что
        // мы сами себя затормозили.
        private static DateTime _downUntil = DateTime.MinValue;
        private static string _downReason = "";
        private static int _downFails;

        private static string Key(int dc, bool isMedia)
        {
            return dc + ":" + (isMedia ? "m" : "n");
        }

        /// <summary>
        /// Порядок, в котором пробовать домены: рабочий первым, карантинные —
        /// в конце, с учётом «ходов» по хосту.
        ///
        /// Порядок детерминированный: сначала запомненный, потом остальные в
        /// исходном порядке. Перемешивать не нужно — домены у Telegram не
        /// равнозначны по скорости, и случайный выбор только мешает.
        /// </summary>
        public static List<string> Order(int dc, bool isMedia, List<string> domains)
        {
            if (domains == null || domains.Count == 0) return new List<string>();

            string key = Key(dc, isMedia);
            DateTime now = DateTime.UtcNow;

            List<string> fresh = new List<string>();
            List<string> quarantined = new List<string>();

            lock (Gate)
            {
                Prune(now);

                RouteState good;

                if (Good.TryGetValue(key, out good) &&
                    (now - good.SeenAt).TotalSeconds < MaxAgeSec &&
                    domains.Contains(good.Domain) &&
                    !IsFailed(good.Domain, now))
                {
                    fresh.Add(good.Domain);
                }

                foreach (string domain in domains)
                {
                    if (fresh.Contains(domain)) continue;

                    // Хост уже отказал в этом проходе дважды — его другие
                    // варианты тоже не проверяем: это тот же сервер.
                    int strikes;
                    if (Strikes.TryGetValue(domain, out strikes) && strikes >= 2) continue;

                    if (IsFailed(domain, now)) quarantined.Add(domain);
                    else fresh.Add(domain);
                }
            }

            // Карантинные — в конце, а не выброшены: если свежих не осталось,
            // лучше попробовать «плохой», чем не подключиться вовсе.
            fresh.AddRange(quarantined);

            // Пустым список быть не должен НИКОГДА.
            //
            // Регресс, найденный на живой проверке: два отказа TCP подряд дают
            // домену два удара, а `Strikes >= 2` выкидывал его из перебора.
            // Когда так выбыли все домены, `Order` возвращал пустой список —
            // и прокси отвечал «нет доступных маршрутов» на каждое соединение,
            // даже после паузы. То есть после пары плохих минут он не
            // восстанавливался до перезапуска.
            //
            // Удары — это подсказка «пробуй позже», а не запрет: пробовать
            // плохой домен всегда лучше, чем не пробовать никакой.
            if (fresh.Count == 0) return new List<string>(domains);

            return fresh;
        }

        /// <summary>Маршрут сработал — запоминаем и снимаем карантин.</summary>
        public static void Remember(int dc, bool isMedia, string domain)
        {
            if (string.IsNullOrEmpty(domain)) return;

            string key = Key(dc, isMedia);

            lock (Gate)
            {
                Good[key] = new RouteState { Domain = domain, SeenAt = DateTime.UtcNow };

                Failed.Remove(domain);
                Strikes.Remove(domain);

                // заработало — значит «всё лежит» больше не правда
                _downUntil = DateTime.MinValue;
                _downReason = "";
                _downFails = 0;
            }
        }

        /// <summary>
        /// Не работает НИ ОДИН маршрут. Считаем паузу и запоминаем причину.
        ///
        /// Возвращает паузу в секундах: 2, 4, 8, 16, 30, 30… — вдвое за каждую
        /// неудачу подряд, но не больше 30. Дальше по 30, а не «навсегда»:
        /// сеть может восстановиться сама, и мы обязаны это заметить.
        /// </summary>
        public static int MarkDown(string reason)
        {
            lock (Gate)
            {
                _downFails++;

                int delay = RecheckPauseSec * (1 << Math.Min(_downFails - 1, 10));

                if (delay > RecheckMaxSec) delay = RecheckMaxSec;

                _downUntil = DateTime.UtcNow.AddSeconds(delay);
                _downReason = reason ?? "";

                return delay;
            }
        }

        /// <summary>
        /// Лежим ли мы сейчас в паузе. Если да — зачем и сколько осталось.
        ///
        /// Вызывается в начале подключения: во время паузы пробовать нечего,
        /// и лучше честно сказать «повтор через N с», чем заставить клиент
        /// ждать полный перебор.
        /// </summary>
        public static bool IsDown(out string reason, out int secondsLeft)
        {
            lock (Gate)
            {
                int left = (int)Math.Ceiling((_downUntil - DateTime.UtcNow).TotalSeconds);

                if (left <= 0)
                {
                    reason = "";
                    secondsLeft = 0;
                    return false;
                }

                reason = _downReason;
                secondsLeft = left;
                return true;
            }
        }

        /// <summary>
        /// Сбросить паузу. Вызывается при ручной проверке и при старте прокси:
        /// человек нажал кнопку — он не должен ждать из-за нашего backoff.
        /// </summary>
        public static void Wake()
        {
            lock (Gate)
            {
                _downUntil = DateTime.MinValue;
                _downReason = "";
                _downFails = 0;
            }
        }

        /// <summary>
        /// Маршрут упал — в карантин на 30 секунд.
        ///
        /// Удары считаем по ДОМЕНУ, а не по «хвосту» имени. Здесь легко
        /// ошибиться: kws2.web.telegram.org и kws2-1.web.telegram.org выглядят
        /// как один хост, но это разные адреса, и смысл второго варианта
        /// именно в том, что первый мог не отвечать. Группировка по хосту
        /// убивала оба варианта одним отказом TCP — то есть отключала ровно
        /// тот резерв, ради которого он существует.
        ///
        /// connect — сразу два удара (до домена не достучались, второй раз
        /// в этом проходе пробовать нечего), остальное — один (ошибка TLS или
        /// WS может быть случайной).
        /// </summary>
        public static void Fail(string domain, bool connectStage)
        {
            if (string.IsNullOrEmpty(domain)) return;

            DateTime now = DateTime.UtcNow;

            lock (Gate)
            {
                Failed[domain] = now.AddSeconds(CooldownSec);

                int strikes;
                Strikes.TryGetValue(domain, out strikes);

                Strikes[domain] = strikes + (connectStage ? 2 : 1);

                // Запомненный маршрут упал — забываем, чтобы не пробовать его
                // первым в следующий раз
                List<string> keys = new List<string>(Good.Keys);

                foreach (string key in keys)
                    if (Good[key].Domain == domain) Good.Remove(key);
            }
        }

        /// <summary>Забыть всё: вызывается при остановке прокси и смене сети.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Good.Clear();
                Failed.Clear();
                Strikes.Clear();

                _downUntil = DateTime.MinValue;
                _downReason = "";
                _downFails = 0;
            }
        }

        /// <summary>Строка для лога и проверки: что помним прямо сейчас.</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                List<string> parts = new List<string>();

                if (_downUntil > DateTime.UtcNow)
                {
                    int left = (int)Math.Ceiling((_downUntil - DateTime.UtcNow).TotalSeconds);
                    parts.Add("пауза ещё " + left + " с (" + _downReason + ")");
                }

                if (Good.Count == 0) parts.Add("маршрут не запомнен");

                foreach (KeyValuePair<string, RouteState> pair in Good)
                    parts.Add(pair.Key + " -> " + pair.Value.Domain);

                return string.Join(", ", parts.ToArray());
            }
        }

        private static bool IsFailed(string domain, DateTime now)
        {
            DateTime until;

            return Failed.TryGetValue(domain, out until) && until > now;
        }

        private static void Prune(DateTime now)
        {
            List<string> dead = new List<string>();

            foreach (KeyValuePair<string, DateTime> pair in Failed)
                if (pair.Value <= now) dead.Add(pair.Key);

            foreach (string domain in dead)
            {
                Failed.Remove(domain);

                // Удары живут ровно столько, сколько карантин.
                //
                // Иначе счётчик копился бы вечно: домен отвалился утром —
                // и до вечера исключён из перебора (так и выходило «нет
                // доступных маршрутов»). Карантин кончился — начинаем с чистого
                // листа, но помним, что в этом проходе уже был отказ.
                Strikes.Remove(domain);
            }

            if (Strikes.Count > 64) Strikes.Clear();
        }

        /// <summary>
        /// «kws2-1.web.telegram.org» -> «kws2» — по хосту, без варианта.
        ///
        /// Сейчас НЕ используется: удары считаем по домену, потому что
        /// kws2 и kws2-1 — разные адреса, и склеивать их значит отключать
        /// резерв одним отказом TCP. Оставлено как справочник на случай,
        /// если понадобится группировать по серверу.
        /// </summary>
        private static string HostOf(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return "";

            int dot = domain.IndexOf('.');
            string head = dot > 0 ? domain.Substring(0, dot) : domain;

            int dash = head.IndexOf('-');
            if (dash > 0) head = head.Substring(0, dash);

            return head.ToLowerInvariant();
        }
    }
}
