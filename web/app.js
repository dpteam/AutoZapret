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

        // экраны с данными подтягивают их при открытии
        if (name === "lists") {
            send("lists.get", { kind: "general" });
            send("lists.get", { kind: "exclude" });
            send("fakes.scan");
        }
        if (name === "proxy") {
            send("proxy.state");
        }
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
        setSelect("set-engine", s.engineKind);
        var engHint = document.getElementById("engine-hint");
        if (engHint) {
            if (s.engineKind === "v2") {
                engHint.textContent = s.engineV2
                    ? "zapret 2 — новее, но lua-скрипты чувствительны к версии"
                    : (s.engineV2Reason || "zapret 2 не установлен");
                engHint.className = "row-hint " + (s.engineV2 ? "" : "diag-bad");
            } else {
                engHint.textContent = "zapret 1 — проверенный, совместимый";
                engHint.className = "row-hint";
            }
        }
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

            tr.innerHTML =
                "<td>" + mark + "</td>" +
                "<td>" + s.name + failed + "</td>" +
                "<td>" + s.okCount + "/" + s.total + "</td>" +
                "<td>" + (s.okCount > 0 ? s.avgMs + " ms" : "—") + "</td>";
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

        bindAction("btn-update-ipset", "lists.updateIpset", "Обновляю…");
        bindAction("btn-update-exclude", "lists.updateExclude", "Дополняю…");
        bindAction("btn-update-ipset-exclude", "lists.updateIpsetExclude", "Восстанавливаю…");
        bindAction("btn-fakes-scan", "fakes.scan", "Сканирую…");

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

    function bindAction(id, action, busyText) {
        var el = document.getElementById(id);
        if (!el) return;
        el.addEventListener("click", function () {
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
            send("hosts.restore");
        });
    }

    function renderHosts(msg) {
        var apply = document.getElementById("btn-hosts-apply");
        if (apply) { apply.disabled = false; apply.textContent = "Применить"; }

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
    }

    function renderUpdateState(msg) {
        var el = document.getElementById("upd-" + msg.target + "-state");
        if (!el) return;

        el.textContent = msg.message || "";
        el.className = "row-hint " + (msg.ok ? (msg.hasUpdate ? "diag-warn" : "diag-ok") : "diag-bad");

        if (msg.target === "engine") {
            var install = document.getElementById("btn-upd-engine-install");
            if (install) {
                install.style.display = msg.hasUpdate ? "" : "none";
                install.disabled = false;
                install.textContent = "Обновить";
            }
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
