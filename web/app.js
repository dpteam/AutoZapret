/* ==========================================================================
   AutoZapret — клиентская часть интерфейса.

   Правила:
     * общение с ядром ТОЛЬКО через chrome.webview.postMessage;
     * наружу уходит { action: "...", ... }, внутрь приходит { type: "...", ... };
     * никакой бизнес-логики: только отрисовка состояния и сбор пользовательского ввода;
     * тяжёлые перерисовки — с дебаунсом, иначе UI умирает на потоке логов.
   ========================================================================== */

(function () {
    "use strict";

    // ------------------------------------------------------------ транспорт

    var host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

    function send(action, payload) {
        if (!host) {
            console.warn("chrome.webview недоступен — сообщение не отправлено:", action);
            return;
        }
        var message = payload || {};
        message.action = action;
        host.postMessage(message);
    }

    // ------------------------------------------------------------ состояние

    var state = {};
    var logLines = [];
    var logRenderTimer = null;
    var saveTimer = null;
    var pendingSettings = {};
    var netSpeedTimer = null;
    var engineDownloadBound = false;

    // ------------------------------------------------------------- локализация
    //
    // Ключ словаря — русский исходный текст (подход gettext: msgid = исходная
    // строка). Почему так, а не data-i18n на каждом узле: разметка уже написана
    // по-русски, и расставлять ключи по 650 строкам — это сотни правок, каждая
    // из которых может сломать вёрстку. Здесь же ноль изменений в HTML.
    //
    // Что переводится: интерфейс — подписи, кнопки, подсказки, заголовки.
    // Что НЕ переводится: тексты, которые присылает движок (диагностика, логи,
    // названия проверок). Они рождаются в C# и живут в русском.
    var i18nDict = null;          // null = русский, перевод не нужен
    var i18nLang = "ru";
    var lastPreflight = [];       // последняя проверка готовности
    var elevationHintShown = false;

    function t(source) {
        if (!i18nDict || !source) return source;

        var value = i18nDict[source];
        return typeof value === "string" ? value : source;
    }

    /// <summary>Шаблон с подстановками: tf("{0} из {1}", a, b).</summary>
    function tf(source) {
        var text = t(source);
        for (var i = 1; i < arguments.length; i++) {
            text = text.split("{" + (i - 1) + "}").join(String(arguments[i]));
        }
        return text;
    }

    function applyLanguage(lang, json) {
        i18nLang = lang === "en" ? "en" : "ru";

        if (i18nLang === "en" && json) {
            try {
                var parsed = JSON.parse(json);
                i18nDict = parsed && typeof parsed === "object" ? parsed : null;
            } catch (e) {
                console.warn("словарь перевода не разобран:", e);
                i18nDict = null;
            }
        } else {
            i18nDict = null;
        }

        document.documentElement.lang = i18nLang;
        translateDom();

        // динамические подписи (статус, подсказки движка) уже отрисованы
        // по-русски — перерисуем из последнего состояния
        if (state && state.version) renderState(state);
    }

    /// <summary>
    /// Переводит статический текст в разметке: текстовые узлы, placeholder и title.
    /// Пробелы по краям сохраняются, чтобы не поехала вёрстка.
    /// </summary>
    function translateDom() {
        if (!i18nDict) return;

        var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
        var nodes = [];

        while (walker.nextNode()) {
            var node = walker.currentNode;
            var parent = node.parentNode;

            // внутри тега script лежит весь app.js — его переводить нельзя
            if (parent && (parent.nodeName === "SCRIPT" || parent.nodeName === "STYLE")) continue;
            nodes.push(node);
        }

        for (var i = 0; i < nodes.length; i++) {
            var value = nodes[i].nodeValue;
            if (!value || !/[А-Яа-яЁё]/.test(value)) continue;

            var key = value.replace(/\s+/g, " ").trim();
            var translated = i18nDict[key];
            if (typeof translated !== "string") continue;

            var lead = value.match(/^\s*/)[0];
            var trail = value.match(/\s*$/)[0];
            nodes[i].nodeValue = lead + translated + trail;
        }

        var withPlaceholder = document.querySelectorAll("[placeholder]");
        for (var p = 0; p < withPlaceholder.length; p++) {
            var ph = withPlaceholder[p].getAttribute("placeholder");
            var phKey = ph.replace(/\s+/g, " ").trim();
            if (i18nDict[phKey]) withPlaceholder[p].setAttribute("placeholder", i18nDict[phKey]);
        }

        var withTitle = document.querySelectorAll("[title]");
        for (var k = 0; k < withTitle.length; k++) {
            var ti = withTitle[k].getAttribute("title");
            var tiKey = ti.replace(/\s+/g, " ").trim();
            if (i18nDict[tiKey]) withTitle[k].setAttribute("title", i18nDict[tiKey]);
        }
    }

    // ------------------------------------------------------------ навигация

    function showScreen(name) {
        var screens = document.querySelectorAll(".screen");
        for (var i = 0; i < screens.length; i++) {
            screens[i].classList.toggle("active", screens[i].id === "screen-" + name);
        }
        var navs = document.querySelectorAll(".nav-item");
        for (var j = 0; j < navs.length; j++) {
            navs[j].classList.toggle("active", navs[j].getAttribute("data-screen") === name);
        }
        document.querySelector(".content").scrollTop = 0;

        // экраны с данными подтягивают их при открытии
        if (name === "lists") {
            send("lists.get", { kind: "general" });
            send("lists.get", { kind: "exclude" });
            send("fakes.scan");
        }
        if (name === "proxy") {
            send("proxy.state");
        }
        if (name === "logs") {
            // логи запрашиваем при входе: экран не должен открываться пустым
            // и ждать, пока человек догадается нажать «Обновить»
            send("logs.get");
        }

        // скорость сети нужна только на главной: опрос раз в секунду
        // на других экранах — лишний трафик моста впустую
        updateNetSpeedPolling(name === "home");
    }

    /// <summary>
    /// Опрос скорости сети раз в секунду, только пока открыта главная.
    /// Первый замер всегда нулевой (разница с прошлым), поэтому подпись
    /// показываем после второго ответа — иначе «0 Б/с» выглядит как поломка.
    /// </summary>
    function updateNetSpeedPolling(active) {
        if (netSpeedTimer) { clearInterval(netSpeedTimer); netSpeedTimer = null; }

        var el = document.getElementById("net-speed");
        if (!active) { if (el) el.textContent = "—"; return; }

        send("net.speed");
        netSpeedTimer = setInterval(function () { send("net.speed"); }, 1000);
    }

    function renderNetSpeed(msg) {
        var el = document.getElementById("net-speed");
        if (!el) return;

        if (!msg.ok) { el.textContent = "нет данных"; return; }

        el.textContent = "↓ " + msg.downText + "   ↑ " + msg.upText;
    }

    function initNav() {
        var navs = document.querySelectorAll(".nav-item");
        for (var i = 0; i < navs.length; i++) {
            (function (item) {
                item.addEventListener("click", function () {
                    showScreen(item.getAttribute("data-screen"));
                });
            })(navs[i]);
        }
        var gotos = document.querySelectorAll("[data-goto]");
        for (var j = 0; j < gotos.length; j++) {
            (function (item) {
                item.addEventListener("click", function () {
                    showScreen(item.getAttribute("data-goto"));
                });
            })(gotos[j]);
        }
    }

    // ------------------------------------------------------------ отрисовка

    function text(id, value) {
        var el = document.getElementById(id);
        if (el) el.textContent = (value === undefined || value === null || value === "") ? "—" : value;
    }

    function applySimpleMode(on) {
        // Простой режим: только то, что нужно каждый день. Всё остальное помечено
        // data-advanced в разметке и скрывается через CSS — так список скрытого
        // живёт рядом с самим элементом, а не в двух местах.
        document.body.classList.toggle("simple", !!on);

        var hint = document.getElementById("simple-hint");
        if (hint) {
            hint.textContent = on
                ? "Скрыты: движок, режим списков, канал обновлений, пресеты, hosts и фейки"
                : "Показаны все настройки";
        }
    }

    /// <summary>
    /// Верхний блок главного экрана: «нет прав администратора» и результаты
    /// проверки готовности. Один контейнер на два случая — человеку всё равно,
    /// из какого места пришла проблема, важно что делать дальше.
    /// </summary>
    function renderAlerts(s) {
        var box = document.getElementById("p0-alerts");
        if (!box) return;

        var parts = [];

        // ---- нет прав администратора
        if (s && s.admin === false) {
            parts.push(
                '<div class="notice warn" style="margin-bottom:10px">' +
                '<span>!</span><div style="flex:1">' +
                '<div>' + t("Запущено без прав администратора — обход не включится.") + '</div>' +
                '<div class="row-hint" style="margin-top:4px">' +
                t("Драйвер WinDivert загружается только от администратора.") + '</div>' +
                '<button class="btn primary" id="btn-restart-admin" style="margin-top:8px">' +
                t("Перезапустить от админа") + '</button>' +
                '</div></div>');
        }

        // ---- проблемы готовности, если они есть
        if (lastPreflight.length > 0) {
            var rows = lastPreflight.map(function (p) {
                var cls = p.level === "blocker" ? "diag-bad" : "diag-warn";
                return '<div class="diag-row">' +
                    '<span class="badge ' + (p.level === "blocker" ? "bad" : "warn") + '">' +
                    (p.level === "blocker" ? "!" : "?") + '</span>' +
                    '<span class="name ' + cls + '">' + p.title + '</span>' +
                    '<span class="verdict">' + (p.hint || "") + '</span>' +
                    '</div>';
            }).join("");

            parts.push(
                '<div class="notice warn" style="margin-bottom:10px">' +
                '<span>!</span><div style="flex:1">' +
                '<div>' + t("Проверка готовности нашла проблемы:") + '</div>' +
                '<div class="diag-list" style="margin-top:8px">' + rows + '</div>' +
                '</div></div>');
        }

        box.innerHTML = parts.join("");
        box.style.display = parts.length > 0 ? "" : "none";
    }

    function setSwitch(id, on) {
        var el = document.getElementById(id);
        if (el) el.classList.toggle("on", !!on);
    }

    function isSwitchOn(id) {
        var el = document.getElementById(id);
        return el ? el.classList.contains("on") : false;
    }

    function setSelect(id, value) {
        var el = document.getElementById(id);
        if (el && value !== undefined && value !== null) el.value = value;
    }

    var GAME_FILTER_LABEL = {
        "off": "Выключен",
        "tcp": "TCP",
        "udp": "UDP",
        "tcp+udp": "TCP и UDP"
    };

    var PROTECTION_LABEL = {
        "off": "Обход выключен",
        "starting": "Запуск…",
        "running": "Обход включён",
        "stopping": "Остановка…",
        "error": "Ошибка"
    };

    var i18nRequested = "";

    function requestLanguage(lang) {
        if (i18nRequested === lang) return;
        i18nRequested = lang;
        send("i18n.get", { lang: lang });
    }

    function renderState(s) {
        state = s || {};

        // язык пришёл в настройках — подтягиваем словарь один раз на смену
        if (s.language && s.language !== i18nLang) requestLanguage(s.language);

        text("env-mode", s.portable ? "portable" : "installed");
        text("env-admin", t(s.admin ? "администратор" : "без прав админа"));

        // главная
        var protection = s.protection || "off";
        text("status-text", t(PROTECTION_LABEL[protection] || protection));

        var detail;
        if (!s.engineReady) detail = s.engineProblem || t("Движок не установлен");
        else if (protection === "running") detail = tf("Стратегия: {0} · {1}", s.activeStrategy || "—", fmtUptime(s.uptimeSec));
        else if (protection === "error") detail = s.protectionError || t("Ошибка");
        else detail = tf("Движок {0} готов", s.engineVersion || "?");
        text("status-detail", detail);

        // предупреждение о проблеме с движком
        var notice = document.getElementById("p0-notice");
        if (notice) {
            var show = !s.engineReady;
            notice.style.display = show ? "" : "none";
            if (show) {
                // Главный сценарий для нового человека: движка нет.
                // Раньше здесь была команда для консоли — это ровно тот
                // «пердолинг», от которого GUI и должен избавлять.
                document.getElementById("p0-notice-text").textContent =
                    t("Движок не установлен. Его можно скачать прямо здесь — без консоли и без git.");

                var block = document.getElementById("p0-engine-download");
                if (block) block.style.display = "flex";
            }
        }

        var dot = document.getElementById("status-dot");
        if (dot) {
            dot.className = "status-dot" +
                (protection === "running" ? " on" :
                 protection === "starting" || protection === "stopping" ? " busy" :
                 protection === "error" ? " err" : "");
        }

        var power = document.getElementById("power");
        if (power) {
            power.classList.toggle("on", protection === "running");
            power.classList.toggle("busy", protection === "starting" || protection === "stopping");
            power.disabled = !s.engineReady;
            var svg = power.querySelector("svg");
            if (svg) {
                svg.innerHTML = protection === "running"
                    ? '<rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" stroke="none"/>'
                    : '<path d="M12 3.5v8" /><path d="M6.2 6.9a8 8 0 1 0 11.6 0" />';
            }
        }

        // стратегия
        var sel = document.getElementById("strategy-select");
        if (sel && s.strategyId && sel.value !== s.strategyId) sel.value = s.strategyId;

        text("game-filter", GAME_FILTER_LABEL[s.gameFilter] || s.gameFilter);
        text("proxy-state", t(s.proxyEnabled ? "включён" : "выключен"));

        // настройки
        setSelect("set-engine", s.engineKind);
        var engHint = document.getElementById("engine-hint");
        if (engHint) {
            if (s.engineKind === "v2") {
                engHint.textContent = s.engineV2
                    ? t("zapret 2 — новее, но lua-скрипты чувствительны к версии")
                    : (s.engineV2Reason || t("zapret 2 не установлен"));
                engHint.className = "row-hint " + (s.engineV2 ? "" : "diag-bad");
            } else {
                engHint.textContent = t("zapret 1 — проверенный, совместимый");
                engHint.className = "row-hint";
            }
        }
        setSelect("set-game-filter", s.gameFilter);
        setSelect("set-ipset-mode", s.ipsetMode);
        var portEl = document.getElementById("set-proxy-port");
        if (portEl && document.activeElement !== portEl) portEl.value = s.proxyPort;
        text("set-proxy-secret", s.proxySecret || "—");
        setSwitch("set-simple-mode", s.simpleMode);
        applySimpleMode(s.simpleMode);
        setSwitch("set-minimize-to-tray", s.minimizeToTray);
        setSwitch("set-start-minimized", s.startMinimized);
        setSwitch("set-update-engine", s.updateEngine);
        setSwitch("set-update-app", s.updateApp);
        setSwitch("set-update-lists", s.updateLists);

        var channel = document.getElementById("set-update-channel");
        if (channel && s.updateChannel) channel.value = s.updateChannel;

        // о программе
        text("about-version", s.version);
        text("about-os", s.os);
        text("about-admin", t(s.admin ? "администратор" : "обычный пользователь"));
        text("about-portable", s.portable ? "portable (рядом с exe)" : "installed (LocalAppData)");
        text("about-datadir", s.dataDir);
        text("about-webview2", s.webview2);

        var langSelect = document.getElementById("set-language");
        if (langSelect && s.language) langSelect.value = s.language;

        renderAlerts(s);
    }

    function fmtUptime(sec) {
        sec = Math.max(0, sec | 0);
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = sec % 60;
        if (h > 0) return tf("{0} ч {1} мин", h, m);
        if (m > 0) return tf("{0} мин {1} с", m, s);
        return tf("{0} с", s);
    }

    function renderStrategies(items) {
        var sel = document.getElementById("strategy-select");
        if (!sel) return;

        var current = sel.value || state.strategyId;
        sel.innerHTML = "";

        if (!items || items.length === 0) {
            var opt = document.createElement("option");
            opt.textContent = "нет доступных стратегий";
            opt.value = "";
            sel.appendChild(opt);
            return;
        }

        items.forEach(function (s) {
            var opt = document.createElement("option");
            opt.value = s.id;
            var label = s.name;
            if (s.recommended) label = "★ " + label;
            if (s.note) label += "  —  " + s.note;
            opt.textContent = label;
            sel.appendChild(opt);
        });

        if (current) sel.value = current;
        else if (state.strategyId) sel.value = state.strategyId;
        updateStrategyNote(items);
    }

    function updateStrategyNote(items) {
        var sel = document.getElementById("strategy-select");
        var row = document.getElementById("row-strategy-note");
        if (!sel || !row || !items) return;
        var found = null;
        for (var i = 0; i < items.length; i++) if (items[i].id === sel.value) { found = items[i]; break; }
        if (found) {
            text("strategy-note", (found.note || "") + " · профилей: " + found.profiles);
            row.style.display = "";
        } else {
            row.style.display = "none";
        }
    }

    // ------------------------------------------------------------ логи

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }

    function renderLogs() {
        logRenderTimer = null;
        var view = document.getElementById("log-view");
        if (!view) return;

        var html = [];
        for (var i = 0; i < logLines.length; i++) {
            var raw = logLines[i];
            // формат: "HH:mm:ss.fff LEVEL  CAT    message"
            var m = /^(\d{2}:\d{2}:\d{2}\.\d{3})\s+(\S+)\s+(\S+)\s?(.*)$/.exec(raw);
            if (!m) {
                html.push('<div class="log-line">' + escapeHtml(raw) + '</div>');
                continue;
            }
            var cls = "log-line";
            if (m[2] === "WARN") cls += " warn";
            else if (m[2] === "ERROR") cls += " error";
            else if (m[2] === "DEBUG") cls += " debug";
            else cls += " info";

            html.push('<div class="' + cls + '">' +
                '<span class="t">' + escapeHtml(m[1]) + '</span> ' +
                '<span class="c">' + escapeHtml(m[3]) + '</span> ' +
                '<span class="m">' + escapeHtml(m[4]) + '</span>' +
                '</div>');
        }

        var atBottom = view.scrollHeight - view.scrollTop - view.clientHeight < 60;
        view.innerHTML = html.join("");
        if (atBottom) view.scrollTop = view.scrollHeight;
    }

    function scheduleLogRender() {
        if (logRenderTimer) return;
        // дебаунс: поток логов не должен перерисовывать DOM на каждую строку
        logRenderTimer = setTimeout(renderLogs, 250);
    }

    function onLogEntry(entry) {
        logLines.push(entry);
        if (logLines.length > 1500) logLines.splice(0, logLines.length - 1500);
        scheduleLogRender();
    }

    // ------------------------------------------------------------ настройки

    function queueSave(patch) {
        for (var k in patch) {
            if (Object.prototype.hasOwnProperty.call(patch, k)) pendingSettings[k] = patch[k];
        }
        if (saveTimer) clearTimeout(saveTimer);
        saveTimer = setTimeout(flushSave, 300);
    }

    function flushSave() {
        saveTimer = null;
        var payload = pendingSettings;
        pendingSettings = {};
        send("settings.set", payload);
    }

    var strategyItems = [];

    function initSettings() {
        var gf = document.getElementById("set-game-filter");
        if (gf) gf.addEventListener("change", function () { queueSave({ gameFilter: gf.value }); });

        var eng = document.getElementById("set-engine");
        if (eng) eng.addEventListener("change", function () {
            send("engine.select", { kind: eng.value });
        });

        var auto = document.getElementById("auto-toggle");
        if (auto) auto.addEventListener("click", function () {
            var next = !auto.classList.contains("on");
            auto.classList.toggle("on", next);
            send("auto.toggle", { enabled: next });
        });

        var presets = document.getElementById("presets-toggle");
        if (presets) presets.addEventListener("click", function () {
            var next = !presets.classList.contains("on");
            presets.classList.toggle("on", next);
            send("presets.enable", { enabled: next });
        });

        var autostart = document.getElementById("autostart-toggle");
        if (autostart) autostart.addEventListener("click", function () {
            var next = !autostart.classList.contains("on");
            autostart.classList.toggle("on", next);
            autostart.disabled = true;
            send("autostart.set", { enabled: next });
        });

        var presetsReload = document.getElementById("btn-presets-reload");
        if (presetsReload) presetsReload.addEventListener("click", function () { send("presets.reload"); });

        var presetsFile = document.getElementById("btn-presets-file");
        if (presetsFile) presetsFile.addEventListener("click", function () { send("presets.openFile"); });

        var im = document.getElementById("set-ipset-mode");
        if (im) im.addEventListener("change", function () { queueSave({ ipsetMode: im.value }); });

        var strategy = document.getElementById("strategy-select");
        if (strategy) {
            strategy.addEventListener("change", function () {
                send("strategy.select", { id: strategy.value });
                updateStrategyNote(strategyItems);
            });
        }

        var port = document.getElementById("set-proxy-port");
        if (port) {
            port.addEventListener("change", function () {
                var v = parseInt(port.value, 10);
                if (!isNaN(v) && v >= 1 && v <= 65535) queueSave({ proxyPort: v });
                else port.value = state.proxyPort || 1443;
            });
        }

        bindSwitch("set-simple-mode", "simpleMode");

        // простой режим должен переключаться сразу, а не после round-trip
        var simple = document.getElementById("set-simple-mode");
        if (simple) simple.addEventListener("click", function () {
            applySimpleMode(!simple.classList.contains("on"));
        });
        bindSwitch("set-auto-start", "autoStart");
        bindSwitch("set-minimize-to-tray", "minimizeToTray");
        bindSwitch("set-start-minimized", "startMinimized");
        bindSwitch("set-update-engine", "updateEngine");
        bindSwitch("set-update-app", "updateApp");
        bindSwitch("set-update-lists", "updateLists");

        var regen = document.getElementById("btn-secret-regen");
        if (regen) regen.addEventListener("click", function () {
            // секрет — это часть ссылки прокси: после смены все старые ссылки мертвы
            if (!window.confirm(
                "Секрет — часть ссылки подключения. После смены все уже розданные " +
                "ссылки перестанут работать, и устройства нужно будет подключить заново.\r\n\r\n" +
                "Сгенерировать новый секрет?")) return;

            send("secret.regenerate");
        });
    }

    function bindSwitch(id, field) {
        var el = document.getElementById(id);
        if (!el) return;
        el.addEventListener("click", function () {
            var next = !el.classList.contains("on");
            el.classList.toggle("on", next);
            var patch = {};
            patch[field] = next;
            queueSave(patch);
        });
    }

    // ------------------------------------------------------------ кнопки

    function initDiag() {
        var sys = document.getElementById("btn-diag-sys");
        if (sys) sys.addEventListener("click", function () {
            sys.disabled = true;
            sys.textContent = "Проверяю…";
            document.getElementById("diag-sys-result").innerHTML = "";
            send("diag.system");
        });

        var bundle = document.getElementById("btn-diag-bundle");
        if (bundle) bundle.addEventListener("click", function () {
            bundle.disabled = true;
            bundle.textContent = "Собираю…";
            send("diag.bundle");
        });

        var bundleFolder = document.getElementById("btn-diag-bundle-folder");
        if (bundleFolder) bundleFolder.addEventListener("click", function () { send("diag.bundleFolder"); });

        var probe = document.getElementById("btn-diag-probe");
        if (probe) probe.addEventListener("click", function () {
            probe.disabled = true;
            probe.textContent = "Проверяю…";
            send("diag.probe");
        });

        var deep = document.getElementById("btn-diag-deep");
        if (deep) deep.addEventListener("click", function () {
            deep.disabled = true;
            deep.textContent = "Определяю…";
            send("diag.deep");
        });

        var tcp16 = document.getElementById("btn-diag-tcp16");
        if (tcp16) tcp16.addEventListener("click", function () {
            var count = parseInt(document.getElementById("diag-tcp16-count").value, 10) || 8;
            tcp16.disabled = true;
            tcp16.textContent = "Проверяю…";
            document.getElementById("diag-tcp16-result").style.display = "none";
            send("diag.tcp16", { count: count });
        });

        var test = document.getElementById("btn-diag-test");
        if (test) test.addEventListener("click", function () {
            var count = parseInt(document.getElementById("diag-test-count").value, 10) || 5;
            test.disabled = true;
            test.textContent = "Идёт перебор…";
            document.getElementById("diag-test-progress").style.display = "";
            document.getElementById("diag-test-result").style.display = "none";
            send("diag.test", { count: count });
        });

        var apply = document.getElementById("btn-diag-apply");
        if (apply) apply.addEventListener("click", function () {
            if (!lastBestStrategy) return;
            send("strategy.select", { id: lastBestStrategy });
            apply.textContent = "Применено";
            apply.disabled = true;
        });
    }

    var lastBestStrategy = null;

    function renderBundle(msg) {
        var btn = document.getElementById("btn-diag-bundle");
        if (btn) { btn.disabled = false; btn.textContent = "Собрать архив"; }

        var box = document.getElementById("diag-bundle-result");
        if (!box) return;

        if (!msg.ok) {
            box.innerHTML = '<div class="advice">Не удалось собрать архив: ' + (msg.error || "?") + '</div>';
            box.style.display = "";
            return;
        }

        box.innerHTML =
            '<div class="row-hint diag-ok" style="margin-bottom:6px">' +
            'Готово: ' + msg.size + ', файлов ' + (msg.entries || []).length +
            '</div>' +
            '<div class="row-hint" style="word-break:break-all">' + msg.path + '</div>';
        box.style.display = "";
    }

    function renderAutoStart(msg) {
        var toggle = document.getElementById("autostart-toggle");
        if (toggle) {
            toggle.classList.toggle("on", !!msg.enabled);
            toggle.disabled = false;
        }

        var hint = document.getElementById("autostart-hint");
        if (hint) hint.textContent = msg.text || (msg.enabled ? "Включён" : "Выключен");

        var note = document.getElementById("autostart-note");
        if (note) {
            if (msg.stale) {
                note.style.display = "";
                note.innerHTML =
                    "<b>Автозапуск указывает не на этот файл.</b><br>" +
                    "Прописан: " + (msg.target || "?") + "<br>" +
                    "Сейчас: " + (msg.exe || "?") + "<br>" +
                    "Выключите и включите автозапуск снова, чтобы исправить.";
            } else if (msg.enabled && msg.method === "RunKey") {
                note.style.display = "";
                note.innerHTML =
                    "Автозапуск через реестр: Windows будет спрашивать UAC при каждом " +
                    "включении обхода. Через планировщик заданий запроса нет.";
            } else {
                note.style.display = "none";
            }
        }

        setSwitch("set-auto-start", !!msg.protectAtStart);
    }

    function renderPresets(msg) {
        setSwitch("presets-toggle", !!msg.enabled);

        var box = document.getElementById("presets-list");
        if (!box) return;

        var items = msg.items || [];
        box.innerHTML = "";

        if (items.length === 0) {
            box.innerHTML = '<div class="row-hint">Пресетов нет — файл пуст или не найден</div>';
            return;
        }

        items.forEach(function (p) {
            var div = document.createElement("div");
            div.className = "diag-row";

            var state = p.active ? "активен" : (p.running ? "процесс запущен" : "");
            var changes = [];
            if (p.strategy) changes.push("стратегия " + p.strategy);
            if (p.gameFilter) changes.push("порты " + p.gameFilter);

            div.innerHTML =
                '<span class="badge ' + (p.active ? "ok" : (p.enabled ? "" : "warn")) + '">' +
                (p.active ? "ON" : (p.enabled ? "·" : "—")) + '</span>' +
                '<span class="name">' + p.name + '</span>' +
                '<span class="verdict">' + (changes.length ? changes.join(", ") : "ничего не меняет") +
                (state ? " · " + state : "") + '</span>' +
                '<span class="detail">' + (p.processes || []).join(", ") + '</span>';

            div.style.cursor = "pointer";
            div.title = p.enabled ? "Нажмите, чтобы выключить" : "Нажмите, чтобы включить";
            div.addEventListener("click", function () {
                send("presets.set", { id: p.id, enabled: !p.enabled });
            });

            box.appendChild(div);
        });

        var active = document.getElementById("presets-active");
        if (active) {
            active.textContent = msg.active
                ? "Активный пресет: " + msg.active
                : "Активный пресет: нет";
        }
    }

    function renderSysResult(msg) {
        var btn = document.getElementById("btn-diag-sys");
        if (btn) { btn.disabled = false; btn.textContent = "Проверить всё"; }

        var box = document.getElementById("diag-sys-result");
        if (!box) return;

        var items = msg.items || [];
        box.innerHTML = "";

        items.forEach(function (c) {
            var badge = c.status === "Ok" ? "ok" : (c.status === "Warn" ? "warn" : (c.status === "Fail" ? "bad" : ""));
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + badge + '">' + (c.status === "Ok" ? "OK" : (c.status === "Skip" ? "—" : "!")) + '</span>' +
                '<span class="name">' + c.name + '</span>' +
                '<span class="verdict">' + (c.detail || "") + '</span>';
            box.appendChild(div);

            if (c.advice) {
                var adv = document.createElement("div");
                adv.className = "advice";
                adv.style.margin = "0 0 6px 0";
                adv.innerHTML = c.advice;
                box.appendChild(adv);
            }
        });

        if (msg.summary) {
            var sum = document.createElement("div");
            sum.className = "row-hint " + (msg.problems > 0 ? "diag-bad" : (msg.warnings > 0 ? "diag-warn" : "diag-ok"));
            sum.style.marginTop = "10px";
            sum.textContent = msg.summary;
            box.appendChild(sum);
        }

        box.style.display = "";
    }

    function renderProbeResult(msg) {
        var box = document.getElementById("diag-probe-result");
        var list = document.getElementById("diag-probe-list");
        var btn = document.getElementById("btn-diag-probe");
        if (btn) { btn.disabled = false; btn.textContent = "Проверить"; }
        if (!box || !list) return;

        var items = msg.items || [];
        list.innerHTML = "";

        items.forEach(function (t) {
            var ok = t.ok;
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + (ok ? "ok" : "bad") + '">' + (ok ? "OK" : "✕") + '</span>' +
                '<span class="name">' + t.name + '</span>' +
                '<span class="verdict ' + (ok ? "diag-ok" : "diag-bad") + '">' +
                    (ok ? "открывается" : "заблокировано") + '</span>' +
                '<span class="detail">' + (t.detail || "") + '</span>';
            list.appendChild(div);
        });

        document.getElementById("diag-probe-summary").innerHTML = msg.summary || "";
        box.style.display = "";
    }

    function renderTcp16(msg) {
        var btn = document.getElementById("btn-diag-tcp16");
        if (btn) { btn.disabled = false; btn.textContent = "Проверить"; }

        var box = document.getElementById("diag-tcp16-result");
        var list = document.getElementById("diag-tcp16-list");
        var verdict = document.getElementById("diag-tcp16-verdict");
        if (!box || !list) return;

        list.innerHTML = "";

        (msg.items || []).forEach(function (r) {
            var cls = r.survived ? "ok" : (r.blocked ? "bad" : "warn");
            var text = r.survived
                ? "пережило " + Math.round(r.bytes / 1024) + " КБ"
                : (r.blocked ? "ОБРЫВ на " + Math.round(r.bytes / 1024) + " КБ" : (r.detail || "—"));

            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + cls + '">' + (r.survived ? "OK" : "!") + '</span>' +
                '<span class="name">' + r.provider + '</span>' +
                '<span class="verdict">' + text + '</span>' +
                '<span class="detail">' + r.ip + ":" + r.port + '</span>';
            list.appendChild(div);
        });

        if (verdict) verdict.innerHTML = msg.verdict || "";

        box.style.display = "";
    }

    function renderDeepResult(msg) {
        var box = document.getElementById("diag-deep-result");
        var list = document.getElementById("diag-deep-list");
        var advice = document.getElementById("diag-deep-advice");
        var btn = document.getElementById("btn-diag-deep");
        if (btn) { btn.disabled = false; btn.textContent = "Определить"; }
        if (!box || !list) return;

        var items = msg.items || [];
        list.innerHTML = "";
        advice.innerHTML = "";

        items.forEach(function (t) {
            var cls = t.ok ? "ok" : (t.kind === "TIMEOUT" ? "warn" : "bad");
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + cls + '">' + (t.ok ? "OK" : t.kind) + '</span>' +
                '<span class="name">' + t.name + '</span>' +
                '<span class="verdict">' + t.russian + '</span>' +
                '<span class="detail">' + (t.detail || "") + '</span>';
            list.appendChild(div);
        });

        (msg.advice || []).forEach(function (a) {
            var div = document.createElement("div");
            div.className = "advice";
            div.innerHTML = "<b>" + a.kind + "</b> — " + a.russian +
                '<div class="hint">' + a.suggest.join(" · ") + "</div>";
            advice.appendChild(div);
        });

        box.style.display = "";
    }

    function renderTestProgress(msg) {
        var el = document.getElementById("diag-test-status");
        if (el) el.textContent = msg.line || "";
    }

    function renderTestResult(msg) {
        var btn = document.getElementById("btn-diag-test");
        if (btn) { btn.disabled = false; btn.textContent = "Подобрать"; }

        document.getElementById("diag-test-progress").style.display = "none";
        var box = document.getElementById("diag-test-result");
        var body = document.getElementById("diag-test-body");
        if (!box || !body) return;

        var items = msg.items || [];
        body.innerHTML = "";
        lastBestStrategy = null;

        items.forEach(function (s, index) {
            var tr = document.createElement("tr");
            if (index === 0 && s.okCount > 0) { tr.className = "best"; lastBestStrategy = s.id; }

            var mark = s.okCount === s.total ? "🟢" : (s.okCount > 0 ? "🟡" : "🔴");
            var failed = (s.failed && s.failed.length > 0)
                ? '<div class="detail">не прошло: ' + s.failed.join(", ") + "</div>" : "";
            var breakdown = s.breakdown
                ? '<div class="detail">' + s.breakdown + "</div>" : "";
            var scoreCls = s.score >= 80 ? "diag-ok" : (s.score >= 50 ? "diag-warn" : "diag-bad");

            tr.innerHTML =
                "<td>" + mark + "</td>" +
                "<td>" + s.name + failed + breakdown + "</td>" +
                "<td>" + s.okCount + "/" + s.total + "</td>" +
                "<td>" + (s.okCount > 0 ? s.avgMs + " ms" : "—") + "</td>" +
                "<td class=\"" + scoreCls + "\"><b>" + (s.score || 0) + "</b></td>";
            body.appendChild(tr);
        });

        document.getElementById("diag-test-summary").textContent = msg.summary || "";

        var apply = document.getElementById("btn-diag-apply");
        if (apply) {
            apply.style.display = lastBestStrategy ? "" : "none";
            apply.disabled = false;
            apply.textContent = "Применить лучшую";
        }

        box.style.display = "";
    }

    function initLists() {
        bindList("general");
        bindList("exclude");
        bindListFile("general");
        bindListFile("exclude");

        bindAction("btn-update-ipset", "lists.updateIpset", "Обновляю…",
            "Список IP-подсетей будет скачан заново и заменён. Ваши домены не пострадают. Продолжить?");

        bindAction("btn-update-exclude", "lists.updateExclude", "Дополняю…",
            "Список исключений будет дополнен из интернета. Ваши строки останутся, " +
            "но новые домены добавятся. Продолжить?");

        bindAction("btn-update-ipset-exclude", "lists.updateIpsetExclude", "Восстанавливаю…",
            "Список локальных сетей будет ПЕРЕЗАПИСАН стандартным набором (10/8, 192.168/16 и т.д.). " +
            "Ваши правки в этом файле пропадут. Продолжить?");

        bindAction("btn-fakes-scan", "fakes.scan", "Сканирую…");

        var genButton = document.getElementById("btn-fake-generate");
        if (genButton) genButton.addEventListener("click", function () {
            var sni = document.getElementById("fake-sni");
            var type = document.getElementById("fake-type");
            var box = document.getElementById("fake-result");

            if (!sni || sni.value.trim().length === 0) {
                if (box) {
                    box.textContent = "Введите домен, например example.com";
                    box.className = "row-hint diag-bad";
                    box.style.display = "";
                }
                if (sni) sni.focus();
                return;
            }

            genButton.disabled = true;
            genButton.textContent = "Собираю…";
            if (box) box.style.display = "none";

            send("fakes.generate", { sni: sni.value, type: type ? type.value : "tls" });
        });

        var search = document.getElementById("fakes-search");
        if (search) {
            var timer = null;
            search.addEventListener("input", function () {
                if (timer) clearTimeout(timer);
                // дебаунс: библиотека на 1671 файл не должна искаться на каждую букву
                timer = setTimeout(function () {
                    send("fakes.search", { query: search.value });
                }, 250);
            });
        }

        bindEngineDownload();
    }

    /// <summary>
    /// Скачивание движка по кнопке. Для нового человека это первое действие
    /// в приложении, поэтому статус пишется прямо рядом с кнопкой, а не в логи.
    ///
    /// Обработчик навешен на документ, а не на саму кнопку: notice с кнопкой
    /// может быть перерисован (например, при отсутствии моста), и прямой
    /// addEventListener тихо терялся бы вместе со старым элементом.
    /// </summary>
    function bindEngineDownload() {
        if (engineDownloadBound) return;
        engineDownloadBound = true;

        document.addEventListener("click", function (event) {
            var target = event.target;
            if (!target || typeof target.closest !== "function") return;

            // кнопка перезапуска с правами (делегирование — блок перерисовывается)
            if (target.closest("#btn-restart-admin")) {
                var adminButton = target.closest("#btn-restart-admin");
                adminButton.disabled = true;
                adminButton.textContent = t("Перезапускаюсь…");
                send("app.restartElevated");
                return;
            }

            if (!target.closest("#btn-engine-download")) return;

            var button = target.closest("#btn-engine-download");
            button.disabled = true;
            button.textContent = t("Скачиваю…");

            var status = document.getElementById("engine-download-status");
            if (status) { status.textContent = ""; status.className = "row-hint"; }

            send("engine.download");
        });
    }

    function renderEngineProgress(msg) {
        var status = document.getElementById("engine-download-status");
        if (!status) return;

        status.textContent = msg.line || "";

        if (msg.done) {
            var button = document.getElementById("btn-engine-download");
            if (button) { button.disabled = false; button.textContent = t("Скачать движок"); }

            status.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");
            if (!msg.ok) status.textContent = t("Не удалось:") + " " + (msg.error || "?");
        }
    }

    function renderFakeGenerated(msg) {
        var button = document.getElementById("btn-fake-generate");
        if (button) { button.disabled = false; button.textContent = "Собрать"; }

        var box = document.getElementById("fake-result");
        if (!box) return;

        box.style.display = "";

        if (!msg.ok) {
            box.className = "row-hint diag-bad";
            box.textContent = "Не получилось: " + (msg.error || msg.reason || "неизвестная ошибка");
            return;
        }

        box.className = "row-hint diag-ok";
        box.textContent = "Готово: " + msg.file + " · " + (msg.type === "quic" ? "QUIC Initial" : "TLS ClientHello") +
            ", " + msg.size + " Б" +
            (msg.sni ? " · SNI " + msg.sni : "") +
            (msg.keyShare ? " · ключ " + msg.keyShare : "");

        // список фейков устарел — обновим, чтобы новый файл был виден
        send("fakes.scan");
    }

    function renderFakes(msg) {
        var summary = document.getElementById("fakes-summary");
        var list = document.getElementById("fakes-list");
        if (!summary || !list) return;

        var items = msg.items || [];
        var total = msg.total || items.length;
        var shown = msg.shown || items.length;

        var text = "файлов: " + total + ", валидных: " + (msg.valid || 0);
        if (shown < total) text += " · показано " + shown;
        if (msg.query) text += " · поиск: «" + msg.query + "»";
        summary.textContent = text;

        list.innerHTML = "";

        if (items.length === 0) {
            var empty = document.createElement("div");
            empty.className = "diag-row";
            empty.textContent = msg.query ? "ничего не найдено" : "фейков нет";
            list.appendChild(empty);
            list.style.display = "";
            return;
        }

        items.forEach(function (p) {
            var div = document.createElement("div");
            div.className = "diag-row";
            var badge = p.valid ? "ok" : "bad";
            div.innerHTML =
                '<span class="badge ' + badge + '">' + (p.valid ? p.kind : "✕") + '</span>' +
                '<span class="name">' + p.file + '</span>' +
                '<span class="verdict">' + (p.valid ? (p.sni || "—") : p.reason) + '</span>' +
                '<span class="detail">' + p.size + " б</span>";
            list.appendChild(div);
        });

        list.style.display = "";
    }

    function bindList(kind) {
        var input = document.getElementById("list-" + kind + "-input");
        var save = document.getElementById("btn-list-" + kind + "-save");
        var reload = document.getElementById("btn-list-" + kind + "-reload");
        if (!input || !save || !reload) return;

        save.addEventListener("click", function () {
            save.disabled = true;
            send("lists.save", { kind: kind, text: input.value });
            setTimeout(function () { save.disabled = false; }, 400);
        });

        reload.addEventListener("click", function () {
            send("lists.get", { kind: kind });
        });
    }

    /// <summary>
    /// Импорт доменов из файла: кнопка и перетаскивание прямо в поле.
    ///
    /// Файл читается в браузере, а не в приложении: не нужен ни диалог выбора
    /// файла на стороне C#, ни передача пути — только текст, который и так
    /// уходит в «Сохранить». Работает одинаково и на Android, где никакого
    /// диалога Windows нет.
    ///
    /// Содержимое ДОБАВЛЯЕТСЯ к текущему, а не заменяет его: случайно
    /// перетащить не тот файл и потерять свой список — слишком легко.
    /// </summary>
    function bindListFile(kind) {
        var input = document.getElementById("list-" + kind + "-input");
        var button = document.getElementById("btn-list-" + kind + "-import");
        var file = document.getElementById("file-list-" + kind);
        if (!input) return;

        if (button && file) button.addEventListener("click", function () { file.click(); });

        if (file) file.addEventListener("change", function () {
            var chosen = file.files && file.files[0];
            if (chosen) importIntoTextarea(chosen, input, kind);
            file.value = "";   // иначе повторный выбор того же файла не сработает
        });

        input.addEventListener("dragover", function (e) {
            e.preventDefault();
            input.classList.add("drop");
        });

        input.addEventListener("dragleave", function () { input.classList.remove("drop"); });

        input.addEventListener("drop", function (e) {
            e.preventDefault();
            input.classList.remove("drop");
            if (!e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) return;
            importIntoTextarea(e.dataTransfer.files[0], input, kind);
        });

        // вставка большого текста из буфера тоже должна работать без сюрпризов
        if (button) button.title = "Домены добавятся к уже введённым";
    }

    function importIntoTextarea(file, textarea, kind) {
        // защита от перетаскивания exe/архива: нам нужен только текст
        if (file.size > 4 * 1024 * 1024) {
            setListHint(kind, "файл слишком большой (" + Math.round(file.size / 1024) + " КБ)", true);
            return;
        }

        var reader = new FileReader();

        reader.onload = function () {
            var raw = String(reader.result || "");

            // BOM и CRLF: файлы из Блокнота приходят именно так
            if (raw.charCodeAt(0) === 0xFEFF) raw = raw.substring(1);
            var text = raw.replace(/\r\n/g, "\n").replace(/\r/g, "\n").trim();

            if (text.length === 0) {
                setListHint(kind, "файл пуст", true);
                return;
            }

            var existing = textarea.value.replace(/\s+$/, "");
            textarea.value = existing.length > 0 ? existing + "\n" + text : text;

            var lines = text.split("\n").length;
            setListHint(kind, "добавлено " + lines + " строк из " + file.name + " — не забудьте «Сохранить»", false);
        };

        reader.onerror = function () {
            setListHint(kind, "не удалось прочитать файл", true);
        };

        reader.readAsText(file, "UTF-8");
    }

    function setListHint(kind, message, bad) {
        var hint = document.getElementById("list-" + kind + "-hint");
        if (!hint) return;
        hint.textContent = message;
        hint.className = "row-hint " + (bad ? "diag-bad" : "diag-ok");
    }

    function bindAction(id, action, busyText, confirmText) {
        var el = document.getElementById(id);
        if (!el) return;
        el.addEventListener("click", function () {
            // Подтверждение там, где откат неочевиден: hosts, перезапись исключений,
            // новый секрет прокси. Спрашиваем о ПОСЛЕДСТВИИ, а не «вы уверены?».
            if (confirmText && !window.confirm(confirmText)) return;

            var original = el.textContent;
            el.disabled = true;
            el.textContent = busyText;
            send(action);
            // вернём кнопку, даже если ответ потерялся
            setTimeout(function () {
                el.disabled = false;
                el.textContent = original;
            }, 30000);
        });
    }

    function renderLists(msg) {
        var kind = msg.kind;
        var input = document.getElementById("list-" + kind + "-input");
        var hint = document.getElementById("list-" + kind + "-hint");
        if (!input) return;

        // не затираем то, что пользователь печатает прямо сейчас
        if (document.activeElement !== input) input.value = msg.text || "";

        if (hint) {
            var parts = [];
            parts.push("записей: " + (msg.count || 0));
            if (msg.excluded > 0) parts.push("исключений: " + msg.excluded);
            if (msg.invalid > 0) parts.push("некорректных: " + msg.invalid);
            hint.textContent = parts.join(" · ");
        }
    }

    function renderListStatus(msg) {
        var el = document.getElementById("list-update-status");
        if (!el) return;
        el.textContent = msg.message || "";
        el.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");
    }

    function initHosts() {
        var apply = document.getElementById("btn-hosts-apply");
        if (apply) apply.addEventListener("click", function () {
            // hosts — системный файл. Предупреждаем о пути и о том, что свои записи целы.
            if (!window.confirm(
                "Будет изменён системный файл hosts " +
                "(C:\\Windows\\System32\\drivers\\etc\\hosts).\n\n" +
                "Ваши записи сохранятся, блок AutoZapret добавится отдельной секцией.")) return;

            var verify = document.getElementById("hosts-verify");
            apply.disabled = true;
            apply.textContent = "Собираю…";
            send("hosts.apply", { verify: verify ? verify.checked : false });
        });

        var check = document.getElementById("btn-hosts-check");
        if (check) check.addEventListener("click", function () {
            check.disabled = true;
            check.textContent = "Проверяю…";
            send("hosts.check");
        });

        var restore = document.getElementById("btn-hosts-restore");
        if (restore) restore.addEventListener("click", function () {
            if (!window.confirm(
                "hosts вернётся из резервной копии, сделанной при первом изменении.\n\n" +
                "Записи, добавленные после этого момента, исчезнут.")) return;

            restore.disabled = true;
            restore.textContent = "Восстанавливаю…";
            send("hosts.restore");
        });
    }

    function renderHosts(msg) {
        var apply = document.getElementById("btn-hosts-apply");
        // то же имя, что в разметке: иначе после первой операции кнопка
        // «Обновить» превращается обратно в «Применить»
        if (apply) { apply.disabled = false; apply.textContent = t("Обновить"); }

        var check = document.getElementById("btn-hosts-check");
        if (check) { check.disabled = false; check.textContent = "Проверить"; }

        var status = document.getElementById("hosts-status");
        if (status) {
            status.textContent = msg.message || "";
            status.className = "row-hint " + (msg.ok ? (msg.warn ? "diag-warn" : "diag-ok") : "diag-bad");
        }

        var box = document.getElementById("hosts-conflicts");
        if (!box) return;

        var conflicts = msg.conflicts || [];
        box.innerHTML = "";

        if (conflicts.length === 0) { box.style.display = "none"; return; }

        conflicts.forEach(function (c) {
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge warn">≠</span>' +
                '<span class="name">' + c.host + '</span>' +
                '<span class="verdict">' + c.ips.join(" / ") + '</span>' +
                '<span class="detail">' + c.chosen + ' · ' + c.reason + '</span>';
            box.appendChild(div);
        });

        box.style.display = "";
    }

    function renderIpstat(msg) {
        var el = document.getElementById("ipset-state");
        if (el) el.textContent = msg.text || "—";
    }

    function initProxy() {
        var toggle = document.getElementById("proxy-toggle");
        if (toggle) toggle.addEventListener("click", function () {
            var next = !toggle.classList.contains("on");
            toggle.classList.toggle("on", next);
            send("proxy.toggle", { enabled: next });
        });

        var copy = document.getElementById("btn-proxy-copy");
        if (copy) copy.addEventListener("click", function () { send("proxy.copyLink"); });

        var open = document.getElementById("btn-proxy-open");
        if (open) open.addEventListener("click", function () { send("proxy.openLink"); });

        var test = document.getElementById("btn-proxy-test");
        if (test) test.addEventListener("click", function () {
            test.disabled = true;
            test.textContent = "Проверяю…";
            send("proxy.test");
        });
    }

    function renderProxy(msg) {
        var running = !!msg.running;
        setSwitch("proxy-toggle", running);

        text("proxy-status-line", msg.statusLine || (running ? "работает" : "выключен"));
        text("proxy-address", (msg.host || "127.0.0.1") + ":" + (msg.port || 1443));
        text("proxy-active", msg.active || 0);
        text("proxy-total", msg.total || 0);
        text("proxy-failed", msg.failed || 0);

        if (msg.error) {
            var line = document.getElementById("proxy-status-line");
            if (line) { line.textContent = msg.error; line.className = "row-hint diag-bad"; }
        }
    }

    function renderProxyTest(msg) {
        var btn = document.getElementById("btn-proxy-test");
        if (btn) { btn.disabled = false; btn.textContent = "Проверить"; }

        var box = document.getElementById("proxy-test-result");
        if (!box) return;

        box.innerHTML = "";
        (msg.steps || []).forEach(function (s) {
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + (s.ok ? "ok" : "bad") + '">' + (s.ok ? "OK" : "✕") + '</span>' +
                '<span class="name">' + s.name + '</span>' +
                '<span class="verdict ' + (s.ok ? "diag-ok" : "diag-bad") + '">' + (s.detail || "") + '</span>';
            box.appendChild(div);
        });

        if (msg.advice) {
            var adv = document.createElement("div");
            adv.className = "advice";
            adv.innerHTML = msg.advice;
            box.appendChild(adv);
        }

        box.style.display = "";
    }

    function initUpdates() {
        bindAction("btn-upd-engine-check", "upd.engine.check", "Проверяю…");
        bindAction("btn-upd-app-check", "upd.app.check", "Проверяю…");

        var install = document.getElementById("btn-upd-engine-install");
        if (install) install.addEventListener("click", function () {
            install.disabled = true;
            install.textContent = "Обновляю…";
            send("upd.engine.install");
        });

        var appInstall = document.getElementById("btn-upd-app-install");
        if (appInstall) appInstall.addEventListener("click", function () {
            if (!confirm("Программа закроется, обновится и запустится снова. Продолжить?")) return;
            appInstall.disabled = true;
            appInstall.textContent = "Обновляю…";
            send("upd.app.install");
        });

        var channel = document.getElementById("set-update-channel");
        if (channel) channel.addEventListener("change", function () {
            queueSave({ updateChannel: channel.value });
        });

        var language = document.getElementById("set-language");
        if (language) language.addEventListener("change", function () {
            queueSave({ language: language.value });
        });
    }

    function renderUpdateState(msg) {
        var el = document.getElementById("upd-" + msg.target + "-state");
        if (!el) return;

        el.textContent = msg.message || "";
        el.className = "row-hint " + (msg.ok ? (msg.hasUpdate ? "diag-warn" : "diag-ok") : "diag-bad");

        var install = document.getElementById("btn-upd-" + msg.target + "-install");
        if (install) {
            install.style.display = msg.hasUpdate ? "" : "none";
            install.disabled = false;
            install.textContent = "Обновить";
        }
    }

    function renderUpdateProgress(msg) {
        var el = document.getElementById("upd-progress");
        if (!el) return;

        el.textContent = msg.line || "";
        el.style.display = msg.line ? "" : "none";
        if (msg.done) {
            el.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");
        }
    }

    function renderAuto(msg) {
        setSwitch("auto-toggle", !!msg.enabled);

        var hint = document.getElementById("auto-hint");
        if (hint) {
            if (!msg.enabled) {
                hint.textContent = "Сам проверяет обход и переключает стратегию при поломке";
                hint.className = "row-hint";
            } else if (!msg.protectionRunning) {
                hint.textContent = "Включите обход — автопилот работает только с запущенным обходом";
                hint.className = "row-hint diag-warn";
            } else {
                hint.textContent = "Автопилот включён · сеть " + (msg.network || "?");
                hint.className = "row-hint diag-ok";
            }
        }

        var box = document.getElementById("auto-status");
        if (!box) return;

        var parts = [];
        if (msg.state) parts.push(msg.state);
        if (msg.message) parts.push(msg.message);
        if (msg.nextCheck) parts.push("следующая проверка: " + msg.nextCheck);

        box.textContent = parts.join(" · ");
        box.className = "row-hint " + (msg.state === "Failed" ? "diag-bad" : "");
        box.style.display = parts.length > 0 ? "" : "none";
    }

    function initButtons() {
        var power = document.getElementById("power");
        if (power) power.addEventListener("click", function () { send("toggle"); });

        var hide = document.getElementById("btn-hide");
        if (hide) hide.addEventListener("click", function () { send("tray.hide"); });

        var exit = document.getElementById("btn-exit");
        if (exit) exit.addEventListener("click", function () { send("app.exit"); });

        var refresh = document.getElementById("btn-log-refresh");
        if (refresh) refresh.addEventListener("click", function () { send("logs.get"); });

        var folder = document.getElementById("btn-log-folder");
        if (folder) folder.addEventListener("click", function () { send("logs.openFolder"); });

        var clear = document.getElementById("btn-log-clear");
        if (clear) clear.addEventListener("click", function () { send("logs.clear"); });
    }

    // ------------------------------------------------------------ входящие

    function onMessage(event) {
        var msg = event.data;
        if (!msg || typeof msg !== "object") return;

        switch (msg.type) {
            case "state":
                renderState(msg);
                break;

            case "navigate":
                showScreen(msg.screen);
                break;

            case "strategies":
                strategyItems = msg.items || [];
                renderStrategies(strategyItems);
                break;

            case "diag.probe":
                renderProbeResult(msg);
                break;

            case "diag.system":
                renderSysResult(msg);
                break;

            case "diag.bundle":
                renderBundle(msg);
                break;

            case "presets":
                renderPresets(msg);
                break;

            case "autostart":
                renderAutoStart(msg);
                break;

            case "diag.deep":
                renderDeepResult(msg);
                break;

            case "diag.tcp16":
                renderTcp16(msg);
                break;

            case "diag.progress":
                renderTestProgress(msg);
                break;

            case "diag.test":
                renderTestResult(msg);
                break;

            case "lists":
                renderLists(msg);
                break;

            case "lists.status":
                renderListStatus(msg);
                break;

            case "hosts":
                renderHosts(msg);
                break;

            case "auto":
                renderAuto(msg);
                break;

            case "lists.ipset":
                renderIpstat(msg);
                break;

            case "fakes":
                renderFakes(msg);
                break;

            case "proxy":
                renderProxy(msg);
                break;

            case "proxy.test":
                renderProxyTest(msg);
                break;

            case "upd.state":
                renderUpdateState(msg);
                break;

            case "upd.progress":
                renderUpdateProgress(msg);
                break;

            case "net.speed":
                renderNetSpeed(msg);
                break;

            case "fakes.generated":
                renderFakeGenerated(msg);
                break;

            case "i18n":
                applyLanguage(msg.lang, msg.json);
                break;

            case "engine.progress":
                renderEngineProgress(msg);
                break;

            case "preflight":
                lastPreflight = msg.items || [];
                renderAlerts(state);
                break;

            case "needs.elevation":
                elevationHintShown = true;
                renderAlerts(state);
                break;

            case "logs":
                logLines = msg.lines || [];
                text("log-file", msg.file);
                renderLogs();
                break;

            case "log":
                if (msg.line) onLogEntry(msg.line);
                break;

            case "info":
                text("about-version", msg.version);
                text("about-os", msg.os);
                text("about-admin", msg.admin ? "администратор" : "обычный пользователь");
                text("about-portable", msg.portable ? "portable (рядом с exe)" : "installed (LocalAppData)");
                text("about-datadir", msg.dataDir);
                text("about-webview2", msg.webview2);
                break;

            case "error":
                console.warn("ошибка от ядра:", msg.message);
                break;

            default:
                console.debug("неизвестное сообщение:", msg.type);
        }
    }

    // ------------------------------------------------------------ старт

    function boot() {
        initNav();
        initSettings();
        initDiag();
        initLists();
        initHosts();
        initProxy();
        initUpdates();
        initButtons();

        // главная открыта с самого начала — опрос скорости надо запустить сразу,
        // иначе цифра появится только после перехода на другой экран и обратно
        updateNetSpeedPolling(true);

        if (host) {
            host.addEventListener("message", onMessage);
            send("ready");
        } else {
            document.getElementById("p0-notice").innerHTML =
                '<span>⚠</span><div>Мост с приложением недоступен: страница открыта вне WebView2.</div>';
        }

        // Ctrl+W / Alt+F4 не должны закрывать окно мимо логики трея
        window.addEventListener("keydown", function (e) {
            if ((e.ctrlKey && e.key === "w") || (e.altKey && e.key === "F4")) e.preventDefault();
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot);
    } else {
        boot();
    }

    // Отладочная точка входа: позволяет проверить перевод в обычном браузере,
    // без моста WebView2 (в CI и при ручной проверке вёрстки). Ничего не
    // включает и не меняет настройки — только применяет словарь.
    window.azDebug = {
        applyLanguage: applyLanguage,
        t: t,
        tf: tf,
        translateDom: translateDom,
        renderAlerts: renderAlerts,
        setPreflight: function (items) { lastPreflight = items || []; },
        missingKeys: function () {
            // какие русские строки в разметке остались без перевода
            if (!i18nDict) return [];
            var out = [];
            var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
            while (walker.nextNode()) {
                var node = walker.currentNode;
                var parent = node.parentNode;
                if (parent && (parent.nodeName === "SCRIPT" || parent.nodeName === "STYLE")) continue;
                var value = node.nodeValue;
                if (!value || !/[А-Яа-яЁё]/.test(value)) continue;
                var key = value.replace(/\s+/g, " ").trim();
                if (!i18nDict[key]) out.push(key);
            }
            return out;
        }
    };
})();
