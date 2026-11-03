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

    function renderState(s) {
        state = s || {};

        text("env-mode", s.portable ? "portable" : "installed");
        text("env-admin", s.admin ? "администратор" : "без прав админа");

        // главная
        var protection = s.protection || "off";
        text("status-text", PROTECTION_LABEL[protection] || protection);

        var detail;
        if (!s.engineReady) detail = s.engineProblem || "Движок не установлен";
        else if (protection === "running") detail = "Стратегия: " + (s.activeStrategy || "—") + " · " + fmtUptime(s.uptimeSec);
        else if (protection === "error") detail = s.protectionError || "Ошибка";
        else detail = "Движок " + (s.engineVersion || "?") + " готов";
        text("status-detail", detail);

        // предупреждение о проблеме с движком
        var notice = document.getElementById("p0-notice");
        if (notice) {
            var show = !s.engineReady;
            notice.style.display = show ? "" : "none";
            if (show) {
                document.getElementById("p0-notice-text").innerHTML =
                    "Движок не установлен. Выполните в папке с программой:<br>" +
                    "<span class='mono'>powershell -ExecutionPolicy Bypass -File tools\\fetch-engine.ps1</span>";
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
        text("proxy-state", s.proxyEnabled ? "включён" : "выключен");

        // настройки
        setSelect("set-game-filter", s.gameFilter);
        setSelect("set-ipset-mode", s.ipsetMode);
        var portEl = document.getElementById("set-proxy-port");
        if (portEl && document.activeElement !== portEl) portEl.value = s.proxyPort;
        text("set-proxy-secret", s.proxySecret || "—");
        setSwitch("set-simple-mode", s.simpleMode);
        setSwitch("set-minimize-to-tray", s.minimizeToTray);
        setSwitch("set-start-minimized", s.startMinimized);
        setSwitch("set-update-engine", s.updateEngine);
        setSwitch("set-update-app", s.updateApp);
        setSwitch("set-update-lists", s.updateLists);

        // о программе
        text("about-version", s.version);
        text("about-os", s.os);
        text("about-admin", s.admin ? "администратор" : "обычный пользователь");
        text("about-portable", s.portable ? "portable (рядом с exe)" : "installed (LocalAppData)");
        text("about-datadir", s.dataDir);
        text("about-webview2", s.webview2);
    }

    function fmtUptime(sec) {
        sec = Math.max(0, sec | 0);
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = sec % 60;
        if (h > 0) return h + " ч " + m + " мин";
        if (m > 0) return m + " мин " + s + " с";
        return s + " с";
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
        bindSwitch("set-minimize-to-tray", "minimizeToTray");
        bindSwitch("set-start-minimized", "startMinimized");
        bindSwitch("set-update-engine", "updateEngine");
        bindSwitch("set-update-app", "updateApp");
        bindSwitch("set-update-lists", "updateLists");

        var regen = document.getElementById("btn-secret-regen");
        if (regen) regen.addEventListener("click", function () { send("secret.regenerate"); });
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
        initButtons();

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
})();
