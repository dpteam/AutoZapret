using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AutoZapret.Proxy
{
    /// <summary>
    /// Проверка, что мы говорим именно с Telegram, а не с заглушкой.
    ///
    /// ЗАЧЕМ
    ///   Раньше оба TLS в туннеле открывались с `delegate { return true; }`,
    ///   то есть принимали ЛЮБОЙ сертификат. Комментарий рядом при этом обещал
    ///   «если релей подменяет соединение, сертификат не сойдётся» — обещание
    ///   было ложным: сходилось всё что угодно.
    ///
    ///   Последствия видны только снаружи: при подмене DNS, чужом локальном
    ///   прокси или релее, который открыл TCP не туда, мы получаем «успешный»
    ///   TLS, а потом «неизвестный proto tag» или тишину. Человек ищет причину
    ///   в стратегии, хотя соединение ушло не в Telegram.
    ///
    ///   Проверка превращает это в понятную строку: «сертификат не от Telegram
    ///   (есть: example.com)».
    ///
    /// Идея взята из refs/telegram_core.py (`_tg_cert_ok_ws`, `_tg_cert_names`),
    /// где она стоит на пробе TG-WS. Реализация своя: там certifi и словарь
    /// сертификата Python, здесь X509Certificate2.
    /// </summary>
    public static class GatewayCheck
    {
        /// <summary>Домен, к которому должен вести сертификат шлюза Telegram.</summary>
        public const string TelegramDomain = "telegram.org";

        /// <summary>
        /// Имена из SAN сертификата, в нижнем регистре, без точки на конце.
        /// </summary>
        public static List<string> Names(X509Certificate certificate)
        {
            List<string> result = new List<string>();

            X509Certificate2 cert = certificate as X509Certificate2;

            if (cert == null && certificate != null)
            {
                try { cert = new X509Certificate2(certificate); }
                catch { return result; }
            }

            if (cert == null) return result;

            try
            {
                foreach (X509Extension extension in cert.Extensions)
                {
                    X509SubjectAlternativeNameExtension san =
                        extension as X509SubjectAlternativeNameExtension;

                    if (san == null) continue;

                    foreach (string dns in san.EnumerateDnsNames())
                    {
                        string name = (dns ?? "").Trim().TrimEnd('.').ToLowerInvariant();
                        if (name.Length > 0 && !result.Contains(name)) result.Add(name);
                    }
                }
            }
            catch
            {
                // Расширение не разобралось — работаем с тем, что есть.
            }

            return result;
        }

        /// <summary>
        /// Принадлежит ли сертификат Telegram: `telegram.org` или любое имя
        /// под ним (`kws2.web.telegram.org`, `*.web.telegram.org`).
        ///
        /// Именно «заканчивается на .telegram.org», а не «содержит»: иначе
        /// `telegram.org.evil.com` и `eviltelegram.org` проходили бы проверку,
        /// и подмена называлась бы Telegram'ом.
        /// </summary>
        public static bool IsTelegram(X509Certificate certificate)
        {
            foreach (string name in Names(certificate))
            {
                if (name == TelegramDomain) return true;
                if (name.EndsWith("." + TelegramDomain, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Строка для ошибки: что в сертификате на самом деле.
        ///
        /// Без неё сообщение «сертификат не тот» заставляет гадать, к кому мы
        /// вообще попали. Первые три имени обычно отвечают на этот вопрос.
        /// </summary>
        public static string Describe(X509Certificate certificate)
        {
            List<string> names = Names(certificate);

            if (names.Count == 0) return "имён в сертификате нет";

            int take = names.Count > 3 ? 3 : names.Count;

            List<string> parts = new List<string>();

            for (int i = 0; i < take; i++) parts.Add(names[i]);

            string text = string.Join(", ", parts.ToArray());

            if (names.Count > take) text += " и ещё " + (names.Count - take);

            return text;
        }

        /// <summary>
        /// Колбэк для SslStream, который требует сертификат Telegram.
        ///
        /// Проверка цепочки тоже остаётся включённой: самоподписанный
        /// сертификат с именем `telegram.org` — это ровно то, чем выглядит
        /// подмена, и принимать его нельзя.
        /// </summary>
        public static bool ValidateTelegram(object sender, X509Certificate certificate,
                                             X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null) return false;

            if (errors != SslPolicyErrors.None) return false;

            return IsTelegram(certificate);
        }

        /// <summary>
        /// Колбэк для внешнего TLS к релею.
        ///
        /// Здесь требование мягче и это осознанно: релей — это Cloudflare
        /// Worker (сертификат Cloudflare) или свой сервер в локальной сети,
        /// у которого может быть самоподписанный. Требовать `telegram.org`
        /// от релея нельзя — он и не Telegram.
        ///
        /// Но цепочку проверяем: просроченный или поддельный сертификат
        /// релея — это тоже подмена, и молчать о ней незачем.
        /// </summary>
        public static bool ValidateRelay(object sender, X509Certificate certificate,
                                         X509Chain chain, SslPolicyErrors errors)
        {
            if (certificate == null) return false;

            return errors == SslPolicyErrors.None;
        }
    }
}
