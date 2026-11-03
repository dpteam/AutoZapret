using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.App
{
    /// <summary>
    /// Мост между страницей в WebView2 и ядром.
    ///
    /// Правила (AGENTS.md):
    ///   * единственный вход - Handle(json) из OnWebMessage;
    ///   * единственный выход - Send(dict) -> chrome.webview.postMessage;
    ///   * никакой бизнес-логики здесь: только маршрутизация и сериализация.
    ///
    /// Формат сообщений (общий для обеих сторон):
    ///   { "action": "имя", ...параметры }            страница -> хост
    ///   { "type":   "имя", ...данные }               хост -> страница
    /// </summary>
    public sealed class WebBridge
    {
        private readonly Action<Dictionary<string, object>> _post;
        private readonly Dictionary<string, Action<Dictionary<string, object>>> _handlers;
        private readonly object _gate = new object();

        /// <summary>Подписи экранов для навигации - отдаются странице при ready.</summary>
        public WebBridge(Action<Dictionary<string, object>> post)
        {
            _post = post;
            _handlers = new Dictionary<string, Action<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
        }

        public void Register(string action, Action<Dictionary<string, object>> handler)
        {
            if (string.IsNullOrEmpty(action) || handler == null) return;
            lock (_gate) _handlers[action] = handler;
        }

        /// <summary>Вызывается из OnWebMessage. Никогда не бросает.</summary>
        public void Handle(string json)
        {
            Dictionary<string, object> message = Json.ParseObjectSafe(json);
            if (message == null)
            {
                Log.Warn("UI", "нераспознанное сообщение из страницы (" + (json == null ? 0 : json.Length) + " символов)");
                SendError("bad-message");
                return;
            }

            string action = Json.GetString(message, "action", "");
            if (string.IsNullOrEmpty(action))
            {
                SendError("no-action");
                return;
            }

            Action<Dictionary<string, object>> handler;
            lock (_gate) _handlers.TryGetValue(action, out handler);

            if (handler == null)
            {
                Log.Warn("UI", "неизвестное действие: " + action);
                Send(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "type", "error" },
                    { "message", "unknown action: " + action }
                });
                return;
            }

            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                Log.Exception("UI", "обработчик '" + action + "'", ex);
                Send(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "type", "error" },
                    { "message", ex.Message },
                    { "action", action }
                });
            }
        }

        /// <summary>Отправить сообщение в страницу. Можно из любого потока.</summary>
        public void Send(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            try { _post(payload); }
            catch (Exception ex) { Log.Warn("UI", "post не удался: " + ex.Message); }
        }

        public void SendError(string code)
        {
            Send(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "type", "error" },
                { "message", code }
            });
        }

        // ------------------------------------------------------------ помощники
        // Короткие обёртки, чтобы не собирать словари руками в каждом обработчике.

        public static Dictionary<string, object> Msg(string type, params object[] pairs)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            if (type != null) map["type"] = type;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[Convert.ToString(pairs[i], CultureInfo.InvariantCulture)] = pairs[i + 1];
            return map;
        }
    }
}
