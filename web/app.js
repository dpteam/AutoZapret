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

    // ------------------------------------------------------- занятые кнопки
    //
    // Кнопка, которая на время операции меняет подпись и блокируется, обязана
    // вернуться в исходное состояние. Раньше каждая делала это сама, и половина
    // забывала: «Пересоздаю…» оставалось висеть уже после того, как hosts был
    // пересоздан, и человек жал кнопку снова, не понимая, сработало ли.
    //
    // Поэтому состояние кнопки хранится здесь, а не в замыкании обработчика.
    // Только из общего места можно сбросить всё разом: после отказа в правах,
    // при исключении в ядре, по таймауту.
    var busyButtons = {};

    /// Заблокировать кнопку с новой подписью.
    ///
    /// timeoutMs — страховка. Если ответ потерялся (ранний return, исключение,
    /// отмена UAC), кнопка сама вернётся и напишет в консоль, вместо того чтобы
    /// остаться «занятой» до перезапуска программы.
    function busyBtn(id, label, timeoutMs) {
        var el = document.getElementById(id);
        if (!el) return;

        if (!busyButtons[id]) {
            busyButtons[id] = { label: el.textContent, timer: 0 };
        } else if (busyButtons[id].timer) {
            clearTimeout(busyButtons[id].timer);
            busyButtons[id].timer = 0;
        }

        el.disabled = true;
        el.textContent = label;

        var wait = timeoutMs || 30000;

        busyButtons[id].timer = setTimeout(function () {
            console.warn("кнопка " + id + " не получила ответа за " + wait + " мс");
            idleBtn(id);
        }, wait);
    }

    /// Вернуть кнопку в исходное состояние.
    function idleBtn(id) {
        var el = document.getElementById(id);
        var saved = busyButtons[id];
        if (!el || !saved) return;

        if (saved.timer) clearTimeout(saved.timer);
        delete busyButtons[id];

        el.disabled = false;
        el.textContent = saved.label;
    }

    /// Сбросить все занятые кнопки.
    function idleAllButtons() {
        Object.keys(busyButtons).forEach(idleBtn);
    }

    // ------------------------------------------------- опасное подтверждение
    //
    // Зачем своё окно, а не window.confirm: у системного диалога нет способа
    // заставить человека ПОДОЖДАТЬ. А ожидание — единственное, что работает
    // против «нажал не думая»: предупреждения читают первые два раза.
    //
    // Порядок строго такой (со слов заказчика, «как в Xiaomi»):
    //   1. «Отмена» доступна сразу — уйти можно в любой момент;
    //   2. «ОК» заблокирована и показывает обратный отсчёт;
    //   3. после согласия — ВТОРОЕ предупреждение, короткое и злое;
    //   4. только потом действие.
    //
    // Отсчёт не блокирует интерфейс: это setTimeout, а не sleep, — иначе
    // окно замирало бы и человек решил бы, что программа повисла.
    var dangerTimer = null;
    var dangerCallback = null;
    var dangerSecondStage = false;

    /// opts: { title, text, extra, wait, confirm, onConfirm }
    function dangerConfirm(opts) {
        var overlay = document.getElementById("danger-overlay");
        if (!overlay) {
            // разметки нет — не оставляем человека без возможности подтвердить
            if (window.confirm((opts.text || "") + "\n\n" + (opts.confirm || "")) && opts.onConfirm) {
                opts.onConfirm();
            }
            return;
        }

        var title = document.getElementById("danger-title");
        var text = document.getElementById("danger-text");
        var extra = document.getElementById("danger-extra");
        var ok = document.getElementById("danger-ok");
        var hint = document.getElementById("danger-hint");

        if (title) title.textContent = opts.title || t("Вы точно уверены?");
        if (text) text.textContent = opts.text || "";
        if (extra) extra.textContent = "";        // показывается после согласия
        if (hint) hint.textContent = opts.hint || "";

        dangerCallback = opts;
        dangerSecondStage = false;

        var wait = Math.max(0, opts.wait || 0);

        if (ok) {
            // wait = 0 — это «подтверждение без отсчёта», и кнопка обязана быть
            // доступна сразу. Иначе она осталась бы заблокированной навсегда:
            // отсчёт, который её включает, просто не запустится.
            ok.disabled = wait > 0;
            ok.textContent = wait > 0 ? "Подождите " + wait + "…" : t("Да, продолжить");
        }

        overlay.style.display = "flex";

        // фокус на «Отмену»: если человек нажмёт Enter не читая, он отменит,
        // а не подтвердит. Это осознанно несимметрично.
        var cancel = document.getElementById("danger-cancel");
        if (cancel) { try { cancel.focus(); } catch (e) { } }

        if (wait > 0) startDangerCountdown(wait);
    }

    function startDangerCountdown(seconds) {
        var ok = document.getElementById("danger-ok");
        var left = seconds;

        if (dangerTimer) clearInterval(dangerTimer);

        dangerTimer = setInterval(function () {
            left--;

            if (left > 0) {
                if (ok) ok.textContent = "Подождите " + left + "…";
                return;
            }

            clearInterval(dangerTimer);
            dangerTimer = null;

            if (ok) {
                ok.disabled = false;
                ok.textContent = t("Да, я понимаю риск");
            }
        }, 1000);
    }

    function closeDanger() {
        var overlay = document.getElementById("danger-overlay");
        if (overlay) overlay.style.display = "none";

        if (dangerTimer) { clearInterval(dangerTimer); dangerTimer = null; }

        dangerCallback = null;
        dangerSecondStage = false;
    }

    function bindDanger() {
        var cancel = document.getElementById("danger-cancel");
        if (cancel) cancel.addEventListener("click", function () { closeDanger(); });

        var ok = document.getElementById("danger-ok");
        if (ok) ok.addEventListener("click", function () {
            if (ok.disabled) return;               // отсчёт ещё идёт

            var opts = dangerCallback;
            if (!opts) { closeDanger(); return; }

            // Два этапа в одном обработчике, а не в двух: второй обработчик,
            // добавленный «поверх» первого, срабатывал бы вместе с ним —
            // окно закрывалось бы и одновременно заново показывало текст.
            // Этап хранится в флаге, и это единственное место, где он меняется.
            var extra = document.getElementById("danger-extra");

            if (!dangerSecondStage && extra && opts.extra) {
                dangerSecondStage = true;

                extra.textContent = opts.extra;
                ok.disabled = true;
                ok.textContent = t("Понятно, продолжить");

                // задержка без отсчёта: подтверждение уже получено, и заставлять
                // ждать второй раз — издевательство, а не забота
                setTimeout(function () { ok.disabled = false; }, 400);
                return;
            }

            closeDanger();
            if (opts.onConfirm) opts.onConfirm();
        });

        // Esc = отмена. Привычка сильнее кнопки, и это безопасное действие.
        document.addEventListener("keydown", function (e) {
            var overlay = document.getElementById("danger-overlay");
            if (!overlay || overlay.style.display === "none") return;
            if (e.key === "Escape") { e.preventDefault(); closeDanger(); }
        });
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
            send("lists.catalog");
            send("fakes.scan");
            send("hosts.raw.get");
        }
        if (name === "proxy") {
            send("proxy.state");
        }
        if (name === "logs") {
            // логи запрашиваем при входе: экран не должен открываться пустым
            // и ждать, пока человек догадается нажать «Обновить»
            send("logs.get");
        }
        if (name === "home") {
            // DNS теперь на главной: карточка должна открываться с настоящим
            // состоянием, а не с «—» до первого захода в настройки
            send("browser.doh.get");
            send("sysdns.get");
        }
        if (name === "diag") {
            // идёт ли уже проверка: экран должен открываться с этим знанием,
            // иначе кнопки выглядят свободными, а нажатие молча отклоняется
            send("diag.state");
            send("own.state");
            send("own.compat");
        }
        if (name === "settings") {
            send("service.get");
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

    /// Ядро присылает состояние проводами: "on", "off", "starting", "stopping",
    /// "error". Интерфейс сравнивает с "running" — раньше это не совпадало, и
    /// подсветка кнопки, зелёная точка и подпись ВКЛ не включались никогда.
    /// Нормализация в одном месте, чтобы значения не разъехались снова.
    function normalizeProtection(raw) {
        if (raw === "on") return "running";
        if (raw === "off" || !raw) return "off";
        return raw;
    }

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
        var protection = normalizeProtection(s.protection);
        var byService = !!s.serviceRunning;

        text("status-text", t(byService ? "Обход включён службой" : (PROTECTION_LABEL[protection] || protection)));

        var detail;
        if (!s.engineReady) detail = s.engineProblem || t("Движок не установлен");
        else if (byService) detail = t("Работает без окна: остановить можно только в «Настройках» → «Служба»");
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
            var running = protection === "running" || byService;

            power.classList.toggle("on", running);
            power.classList.toggle("busy", protection === "starting" || protection === "stopping");
            power.classList.toggle("error", protection === "error");

            // Служба держит обход — наша кнопка не имеет права ничего запускать
            power.disabled = !s.engineReady || byService;
            power.classList.toggle("service", byService);

            // Подпись внутри кнопки и подсветка всей шапки: состояние видно и без
            // чтения строки статуса. Класс на .hero нужен, чтобы светился и текст.
            var caption = document.getElementById("power-caption");
            if (caption) {
                caption.textContent =
                    byService ? "СЛУЖБА" :
                    protection === "running" ? "ВКЛ" :
                    protection === "starting" ? "СТАРТ" :
                    protection === "stopping" ? "СТОП" :
                    protection === "error" ? "ОШИБКА" : "ВЫКЛ";
            }

            var hero = document.querySelector(".hero");
            if (hero) hero.classList.toggle("on", running);

            // для скринридеров: кнопка сообщает состояние, а не только вид
            power.setAttribute("aria-pressed", running ? "true" : "false");

            var svg = power.querySelector("svg");
            if (svg) {
                svg.innerHTML = running
                    ? '<rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" stroke="none"/>'
                    : '<path d="M12 3.5v8" /><path d="M6.2 6.9a8 8 0 1 0 11.6 0" />';
            }
        }

        // стратегия
        var sel = document.getElementById("strategy-select");
        if (sel && s.strategyId && sel.value !== s.strategyId) sel.value = s.strategyId;

        // результат последнего скана для выбранной стратегии: человек выбирает
        // её здесь, значит и «проверена / 6 из 6 / счёт 98» должно быть здесь,
        // а не на другом экране
        var scanEl = document.getElementById("strategy-scan");
        if (scanEl) {
            scanEl.textContent = s.scan || "не проверялась подбором";
            scanEl.className = "row-hint mono " +
                (s.scan ? (s.scanGood ? (s.scanFresh ? "diag-ok" : "diag-warn") : "diag-warn") : "dim");
        }

        text("game-filter", GAME_FILTER_LABEL[s.gameFilter] || s.gameFilter);
        text("proxy-state", t(s.proxyEnabled ? "включён" : "выключен"));

        // какой движок работает — видно сразу на главной, без захода в настройки
        var engineLabel = s.engineKind === "own"
            ? t("свой движок")
            : (s.engineKind === "v2" ? "zapret 2 (winws2)" : "zapret 1 (winws)");

        if (s.engineVersion) engineLabel += " · " + s.engineVersion;
        if (!s.engineReady) engineLabel += " · " + t("не установлен");
        text("home-engine", engineLabel);

        // настройки
        setSelect("set-engine", s.engineKind);

        var engHint = document.getElementById("engine-hint");
        if (engHint) {
            if (s.engineKind === "own") {
                // Честно: winws не нужен, но драйвер общий — без него никак.
                engHint.textContent = s.engineOwn
                    ? t("свой движок - те же стратегии, но перехват наш. Драйвер WinDivert общий")
                    : (s.engineOwnReason || t("свой движок недоступен"));
                engHint.className = "row-hint " + (s.engineOwn ? "" : "diag-bad");
            } else if (s.engineKind === "v2") {
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

        setSwitch("set-reconnect", s.reconnectOnStrategy !== false);

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

    /// Результат смены движка. Раньше при отказе список молча возвращался
    /// к прежнему пункту, и это выглядело как «выбрал, а оно перекинуло на
    /// первый». Теперь причина видна прямо под списком.
    function renderEngineResult(msg) {
        var el = document.getElementById("engine-result");
        if (!el) return;

        el.textContent = msg.message || "";
        el.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");

        // Успешное переключение — не новость навсегда: строка живёт несколько
        // секунд, иначе она висит до перезапуска и мешает читать подсказку.
        if (msg.ok) {
            setTimeout(function () {
                if (el.textContent === msg.message) {
                    el.textContent = "";
                    el.className = "row-hint";
                }
            }, 4000);
        }
    }

    function renderStrategies(items) {
        var sel = document.getElementById("strategy-select");
        if (!sel) return;

        // Что было выбрано. Берём значение из самого списка, а если его там нет
        // (список только что перестроен) — то, что прислало ядро.
        var wanted = sel.value || state.strategyId || "";

        sel.innerHTML = "";

        if (!items || items.length === 0) {
            var empty = document.createElement("option");
            empty.textContent = "нет доступных стратегий";
            empty.value = "";
            sel.appendChild(empty);
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

        // Раньше здесь стояло `sel.value = current` без проверки: если такого
        // пункта в новом списке нет, браузер молча оставляет ПЕРВЫЙ. Со стороны
        // это выглядело как «переключил движок — стратегия слетела на первую».
        // Теперь выбираем осознанно и говорим, если выбранного не нашлось.
        if (wanted && hasOption(sel, wanted)) {
            sel.value = wanted;
        } else if (state.strategyId && hasOption(sel, state.strategyId)) {
            sel.value = state.strategyId;
        } else if (wanted) {
            console.warn("стратегия «" + wanted + "» отсутствует в списке движка " +
                         (state.engineKind || "?") + " — оставлена первая");
        }

        updateStrategyNote(items);
    }

    function hasOption(select, value) {
        for (var i = 0; i < select.options.length; i++) {
            if (select.options[i].value === value) return true;
        }
        return false;
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

    // ------------------------------------------------------------ служба

    function bindService() {
        var install = document.getElementById("btn-service-install");
        if (install) install.addEventListener("click", function () {
            if (!window.confirm(
                "Установить службу AutoZapret?\n\n" +
                "Она будет запускать обход при загрузке системы — до входа в Windows,\n" +
                "и продолжит работать после выхода из сессии.\n\n" +
                "Нужны права администратора. Удалить можно кнопкой «Удалить».")) return;

            setServiceBusy(true);
            send("service.install", { auto: true });
        });

        var uninstall = document.getElementById("btn-service-uninstall");
        if (uninstall) uninstall.addEventListener("click", function () {
            if (!window.confirm(
                "Удалить службу AutoZapret?\n\n" +
                "Обход перестанет работать до входа в систему и после выхода из неё.\n" +
                "Обычный режим (кнопка на главной) останется как был.")) return;

            setServiceBusy(true);
            send("service.uninstall");
        });

        var start = document.getElementById("btn-service-start");
        if (start) start.addEventListener("click", function () {
            setServiceBusy(true);
            send("service.start");
        });

        var stop = document.getElementById("btn-service-stop");
        if (stop) stop.addEventListener("click", function () {
            setServiceBusy(true);
            send("service.stop");
        });
    }

    /// Пока операция идёт, кнопки не нажимаются: sc start/stop занимают
    /// секунды, и повторное нажатие запускает вторую операцию поверх первой.
    function setServiceBusy(busy) {
        ["btn-service-install", "btn-service-start", "btn-service-stop", "btn-service-uninstall"]
            .forEach(function (id) {
                var b = document.getElementById(id);
                if (b) b.disabled = busy;
            });
    }

    // Состояния службы приходят из C# строками. Переводим известные,
    // остальное показываем как есть: там бывает «остановлена (проблема: …)»,
    // и подставлять перевод в такой текст нельзя.
    var SERVICE_STATES = {
        "не установлена": "не установлена",
        "работает": "работает",
        "остановлена": "остановлена",
        "запускается…": "запускается…",
        "останавливается…": "останавливается…"
    };

    function renderService(msg) {
        setServiceBusy(false);

        var state = document.getElementById("service-state");
        if (state) {
            var raw = msg.state || "—";
            state.textContent = SERVICE_STATES[raw] ? t(SERVICE_STATES[raw]) : raw;
            state.className = "tag " + (msg.running ? "diag-ok" : (msg.installed ? "diag-warn" : ""));
        }

        // Кнопки по состоянию: нельзя запустить неустановленную службу и
        // нельзя установить уже установленную — иначе человек жмёт и получает
        // отказ, который выглядит как поломка.
        var install = document.getElementById("btn-service-install");
        if (install) install.style.display = msg.installed ? "none" : "";

        var uninstall = document.getElementById("btn-service-uninstall");
        if (uninstall) uninstall.style.display = msg.installed ? "" : "none";

        var start = document.getElementById("btn-service-start");
        if (start) {
            start.style.display = msg.installed ? "" : "none";
            start.disabled = !!msg.running || !!msg.pending;
        }

        var stop = document.getElementById("btn-service-stop");
        if (stop) {
            stop.style.display = msg.installed ? "" : "none";
            stop.disabled = !msg.running || !!msg.pending;
        }

        var note = document.getElementById("service-note");
        if (note && msg.problem) {
            note.textContent = msg.problem;
            note.className = "row-hint diag-warn";
        }
    }

    function renderServiceResult(msg) {
        var note = document.getElementById("service-note");
        if (note) {
            note.textContent = msg.message || "";
            note.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");
        }
    }

    function renderOwnCompat(msg) {
        var select = document.getElementById("own-strategy");
        if (!select) return;

        var strategies = msg.strategies || [];

        // Оставляем первым пунктом «своя техника» и перестраиваем остальное.
        // Неподдерживаемые показываем тоже, но с пометкой: человек должен
        // видеть, почему стратегии из списка нет, а не гадать.
        select.innerHTML = '<option value="">' + t("— своя техника —") + "</option>";

        strategies.forEach(function (s) {
            var option = document.createElement("option");
            option.value = s.id;
            option.textContent = s.ok
                ? s.id + " — " + s.summary
                : s.id + " — не исполнить (" + s.summary + ")";
            option.disabled = !s.ok;
            select.appendChild(option);
        });
    }

    function initSettings() {
        bindService();

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
        bindSwitch("set-reconnect", "reconnectOnStrategy");
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

        var bundleDeep = document.getElementById("btn-diag-bundle-deep");
        if (bundleDeep) bundleDeep.addEventListener("click", function () {
            bundleDeep.disabled = true;
            bundleDeep.textContent = "Собираю расширенный…";

            var progress = document.getElementById("bundle-progress");
            if (progress) { progress.textContent = "собираю…"; progress.style.display = ""; }

            var box = document.getElementById("diag-bundle-result");
            if (box) box.style.display = "none";

            send("diag.bundleDeep");
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

        var netcap = document.getElementById("btn-diag-netcap");
        if (netcap) netcap.addEventListener("click", function () {
            netcap.disabled = true;
            netcap.textContent = "Слушаю…";
            document.getElementById("diag-netcap-result").style.display = "none";
            send("diag.netcap", { seconds: 8 });
        });

        var explain = document.getElementById("btn-diag-explain");
        if (explain) explain.addEventListener("click", function () {
            var sel = document.getElementById("strategy-select");
            var id = sel ? sel.value : "";

            explain.disabled = true;
            explain.textContent = "Запускаю с отладкой…";
            document.getElementById("diag-explain-result").style.display = "none";
            send("diag.explain", { id: id, seconds: 8 });
        });

        var apply = document.getElementById("btn-diag-apply");
        if (apply) apply.addEventListener("click", function () {
            if (!lastBestStrategy) return;
            send("strategy.select", { id: lastBestStrategy });
            apply.textContent = "Применено";
            apply.disabled = true;
        });

        var cancel = document.getElementById("btn-diag-cancel");
        if (cancel) cancel.addEventListener("click", function () {
            cancel.disabled = true;
            cancel.textContent = "Отменяю…";
            send("diag.cancel");
        });

        // ---- свой движок
        var ownStart = document.getElementById("btn-own-start");
        if (ownStart) ownStart.addEventListener("click", function () {
            ownStart.disabled = true;
            ownStart.textContent = "Запускаю…";

            send("own.start", {
                tech: document.getElementById("own-tech").value,
                strategy: (document.getElementById("own-strategy") || { value: "" }).value,
                position: parseInt(document.getElementById("own-position").value, 10) || 1,
                repeats: parseInt(document.getElementById("own-repeats").value, 10) || 6,
                overlap: parseInt(document.getElementById("own-overlap").value, 10) || 0,
                seconds: parseInt(document.getElementById("own-seconds").value, 10) || 30,
                dry: document.getElementById("own-dry").classList.contains("on"),
                badsum: document.getElementById("own-badsum").classList.contains("on"),
                badseq: document.getElementById("own-badseq").classList.contains("on"),
                md5sig: document.getElementById("own-md5sig").classList.contains("on"),
                datanoack: document.getElementById("own-datanoack").classList.contains("on"),
                filter: document.getElementById("own-filter").classList.contains("on"),
                profiles: document.getElementById("own-filter").classList.contains("on") &&
                    !!document.getElementById("own-profiles") &&
                    document.getElementById("own-profiles").classList.contains("on")
            });
        });

        var ownStop = document.getElementById("btn-own-stop");
        if (ownStop) ownStop.addEventListener("click", function () {
            ownStop.disabled = true;
            ownStop.textContent = "Останавливаю…";
            send("own.stop");
        });

        var ownDry = document.getElementById("own-dry");
        if (ownDry) ownDry.addEventListener("click", function () {
            ownDry.classList.toggle("on");
        });

        // Fooling: четыре независимых переключателя, одинаковое поведение
        ["own-badsum", "own-badseq", "own-md5sig", "own-datanoack"].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.addEventListener("click", function () { el.classList.toggle("on"); });
        });

        var ownFilter = document.getElementById("own-filter");
        if (ownFilter) ownFilter.addEventListener("click", function () {
            ownFilter.classList.toggle("on");
            syncOwnProfiles();
        });

        syncOwnProfiles();

        function syncOwnProfiles() {
            // Профили без фильтра бессмысленны: все три претендуют на один
            // TCP/443, и «google» забрал бы весь трафик. Гасим переключатель
            // и говорим почему — иначе выглядит как «нажал, ничего не изменилось».
            var filter = document.getElementById("own-filter");
            var profiles = document.getElementById("own-profiles");
            if (!filter || !profiles) return;

            var allowed = filter.classList.contains("on");

            profiles.classList.toggle("disabled", !allowed);
            profiles.setAttribute("aria-disabled", allowed ? "false" : "true");
            profiles.title = allowed ? "" : "Сначала включите «Только свои цели»";
        }
        var ownCompare = document.getElementById("btn-own-compare");
        if (ownCompare) ownCompare.addEventListener("click", function () {
            ownCompare.disabled = true;
            ownCompare.textContent = "Сравниваю…";

            document.getElementById("own-compare-result").style.display = "none";
            document.getElementById("diag-test-progress").style.display = "";

            send("own.compare");
        });
    }

    /// DNS: три операции, которые раньше жили в диагностике, а теперь на
    /// главной. Отдельная функция, потому что экран один, а карточка другая —
    /// искать привязку «кнопки главной» внутри initDiag больше не нужно.
    function initDns() {
        var dohOn = document.getElementById("btn-browser-doh-on");
        if (dohOn) dohOn.addEventListener("click", function () {
            dohOn.disabled = true;
            dohOn.textContent = "Включаю…";
            send("browser.doh.enable");
        });

        var dohOff = document.getElementById("btn-browser-doh-off");
        if (dohOff) dohOff.addEventListener("click", function () {
            dohOff.disabled = true;
            dohOff.textContent = "Выключаю…";
            send("browser.doh.disable");
        });

        var sysOn = document.getElementById("btn-sysdns-on");
        if (sysOn) sysOn.addEventListener("click", function () {
            // Переключение DNS системы — не мгновенное и требует админа,
            // поэтому спрашиваем о ПОСЛЕДСТВИИ, а не «вы уверены?».
            if (!window.confirm(
                "DNS всех программ будет переключён на 127.0.0.1 (наш резолвер с DoH).\n\n" +
                "Нужны права администратора. Прежние серверы запомнятся,\n" +
                "вернуть можно кнопкой «Вернуть DNS».")) return;

            sysOn.disabled = true;
            sysOn.textContent = "Включаю…";
            send("sysdns.enable");
        });

        var sysOff = document.getElementById("btn-sysdns-off");
        if (sysOff) sysOff.addEventListener("click", function () {
            sysOff.disabled = true;
            sysOff.textContent = "Возвращаю…";
            send("sysdns.disable");
        });

        var flush = document.getElementById("btn-dns-flush");
        if (flush) flush.addEventListener("click", function () {
            flush.disabled = true;
            flush.textContent = "Сбрасываю…";
            send("dns.flush");
        });
    }

    function renderDnsFlushed(msg) {
        var btn = document.getElementById("btn-dns-flush");
        if (btn) { btn.disabled = false; btn.textContent = "Сбросить кэш"; }

        var status = document.getElementById("dns-flush-status");
        if (!status) return;

        status.textContent = msg.message || "";
        status.className = "row-hint " + (msg.ok ? "diag-ok" : "diag-bad");
    }

    /// Таблица сравнения техник: та же логика, что у подбора стратегий,
    /// но другая шкала — здесь нет процесса, который может умереть.
    function renderCompare(msg) {
        var btn = document.getElementById("btn-own-compare");
        if (btn) { btn.disabled = false; btn.textContent = "Сравнить"; }

        document.getElementById("diag-test-progress").style.display = "none";

        var box = document.getElementById("own-compare-result");
        var body = document.getElementById("own-compare-body");
        if (!box || !body) return;

        body.innerHTML = "";

        if (!msg.ok) {
            var summaryEl = document.getElementById("own-compare-summary");
            if (summaryEl) {
                summaryEl.textContent = msg.error || "не получилось";
                summaryEl.className = "row-hint diag-bad";
            }
            box.style.display = "";
            return;
        }

        (msg.items || []).forEach(function (t, index) {
            var tr = document.createElement("tr");
            if (index === 0 && t.okCount > 0) tr.className = "best";

            var mark = t.okCount === t.total ? "🟢" : (t.okCount > 0 ? "🟡" : "🔴");
            var scoreCls = t.score >= 80 ? "diag-ok" : (t.score >= 50 ? "diag-warn" : "diag-bad");

            var failed = (t.failed && t.failed.length > 0)
                ? '<div class="detail">не прошло: ' + t.failed.join(", ") + "</div>" : "";
            var reasons = (t.reasons && t.okCount === 0)
                ? '<div class="detail">почему: ' + t.reasons + "</div>" : "";

            tr.innerHTML =
                "<td>" + mark + "</td>" +
                "<td>" + t.name +
                    '<div class="detail">' + (t.breakdown || "") + "</div>" +
                    failed + reasons +
                    (t.error ? '<div class="detail diag-bad">' + t.error + "</div>" : "") +
                "</td>" +
                "<td>" + t.okCount + "/" + t.total + "</td>" +
                "<td>" + (t.okCount > 0 ? t.avgMs + " ms" : "—") + "</td>" +
                "<td class=\"" + scoreCls + "\"><b>" + (t.score || 0) + "</b></td>";

            body.appendChild(tr);
        });

        var summaryEl = document.getElementById("own-compare-summary");
        if (summaryEl) {
            summaryEl.textContent = msg.summary || "";
            summaryEl.className = "row-hint " + (msg.cancelled ? "diag-warn" : "");
        }

        // Лучшая техника — в дропдаун одним нажатием. Искать её глазами в
        // таблице и потом вручную переключать селектор — лишний шаг.
        var bestBtn = document.getElementById("btn-own-best");
        if (bestBtn) {
            var best = (msg.items || [])[0];

            if (best && best.okCount > 0) {
                bestBtn.style.display = "";
                bestBtn.disabled = false;
                bestBtn.textContent = "Применить «" + best.name + "»";

                bestBtn.onclick = function () {
                    var sel = document.getElementById("own-tech");
                    if (sel) sel.value = best.name;
                    bestBtn.textContent = "Выбрано: " + best.name;
                    bestBtn.disabled = true;
                };
            } else {
                bestBtn.style.display = "none";
            }
        }

        box.style.display = "";
    }

    /// Состояние своего движка: кнопки и строка со счётчиками.
    function renderOwnState(msg) {
        var start = document.getElementById("btn-own-start");
        if (start) {
            start.disabled = !!msg.running;
            start.textContent = msg.running ? "Работает…" : "Запустить";
        }

        var stop = document.getElementById("btn-own-stop");
        if (stop) {
            stop.disabled = !msg.running;
            stop.textContent = "Остановить";
        }

        var status = document.getElementById("own-status");
        if (!status) return;

        if (msg.error) {
            status.textContent = msg.error + (msg.summary ? " · " + msg.summary : "");
            status.className = "row-hint diag-bad";
            return;
        }

        status.textContent = (msg.running ? "работает · " : "") + (msg.summary || "");
        status.className = "row-hint " + (msg.running ? "diag-ok" : "");
    }

    function renderSystemDns(msg) {
        // Кнопки по id, а не по переменной из чужой области видимости.
        //
        // Здесь стояло `if (on) { on.disabled = false; ... }`, где `on`
        // объявлена в другой функции. Под "use strict" это ReferenceError,
        // и renderSystemDns умирала на первой строке: статус не обновлялся,
        // кнопки навсегда оставались в «Включаю…», а причина отказа не
        // показывалась — человек видел «включил, а ничего не произошло».
        var on = document.getElementById("btn-sysdns-on");
        if (on) { on.disabled = false; on.textContent = "Включить"; }

        var off = document.getElementById("btn-sysdns-off");
        if (off) { off.disabled = false; off.textContent = "Вернуть DNS"; }

        var status = document.getElementById("sysdns-status");
        if (!status) return;

        if (!msg.ok) {
            status.textContent = msg.error || "не получилось";
            status.className = "row-hint diag-bad";
            return;
        }

        // Опасное состояние важнее любой статистики: система смотрит на нас,
        // а мы молчим. Это «нет интернета» целиком — пишем прямо.
        if (msg.problem) {
            status.textContent = msg.problem;
            status.className = "row-hint diag-bad";
            return;
        }

        var parts = [];
        parts.push(msg.pointed ? msg.target : "системный DNS: провайдера (не переключён)");
        parts.push("резолвер: " + (msg.status || "—"));
        if (msg.upstream) parts.push("DoH: " + msg.upstream);

        // Запасной сервер виден: человек должен знать, что выключение программы
        // не оставит его без DNS.
        if (msg.pointed && msg.fallback) parts.push("запасной DNS есть");

        status.textContent = parts.join(" · ");
        status.className = "row-hint " + (msg.enabled ? "diag-ok" : "");
    }

    // ------------------------------------------------------------ Secure DNS браузеров

    function renderBrowserDoh(msg) {
        var on = document.getElementById("btn-browser-doh-on");
        if (on) { on.disabled = false; on.textContent = "Включить"; }

        var off = document.getElementById("btn-browser-doh-off");
        if (off) { off.disabled = false; off.textContent = "Выключить"; }

        var status = document.getElementById("browser-doh-status");
        if (status) {
            // report приходит после действия, summary — при чтении состояния
            status.textContent = msg.report ? msg.report.replace(/\n/g, " · ") : (msg.summary || "");
            status.className = "row-hint " + (msg.changed ? "diag-ok" : "");
        }

        var list = document.getElementById("browser-doh-list");
        if (!list || !msg.items) return;

        list.innerHTML = "";

        msg.items.forEach(function (b) {
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML =
                '<span class="badge ' + (b.on ? "ok" : "warn") + '">' + (b.on ? "OK" : "—") + '</span>' +
                '<span class="name">' + b.name + '</span>' +
                '<span class="verdict">' + (b.on ? "Secure DNS включён" : "системный DNS") + '</span>' +
                '<span class="detail">' + (b.detail || "") + '</span>';
            list.appendChild(div);
        });
    }

    // Кнопки, которые запускают проверки: пока одна идёт, остальные недоступны.
    // Иначе «Проверить сеть» и «Подобрать» запускаются вместе и спорят за winws.
    var diagRunIds = [
        "btn-diag-sys", "btn-diag-bundle", "btn-diag-bundle-deep",
        "btn-diag-probe", "btn-diag-deep", "btn-diag-tcp16", "btn-diag-test"
    ];

    function setDiagBusy(running, title) {
        var card = document.getElementById("diag-run-card");
        if (card) card.style.display = running ? "" : "none";

        if (running) text("diag-run-title", "Идёт проверка: " + (title || ""));

        diagRunIds.forEach(function (id) {
            var b = document.getElementById(id);
            if (b) b.disabled = !!running;
        });

        var cancel = document.getElementById("btn-diag-cancel");
        if (cancel) {
            cancel.disabled = !running;
            cancel.textContent = running ? "Отменить" : "Отменить";
        }
    }

    function showDiagBusy(message) {
        var hint = document.getElementById("diag-busy-hint");
        if (!hint) return;

        hint.textContent = message || "";
        hint.style.display = message ? "" : "none";

        if (message) {
            window.setTimeout(function () {
                hint.style.display = "none";
            }, 7000);
        }
    }

    var lastBestStrategy = null;

    function renderBundle(msg) {
        var btn = document.getElementById("btn-diag-bundle");
        if (btn) { btn.disabled = false; btn.textContent = t("Собрать архив"); }

        var deep = document.getElementById("btn-diag-bundle-deep");
        if (deep) { deep.disabled = false; deep.textContent = t("Расширенный отчёт"); }

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

        // Квадратами: 14 строк в столбик — это длинная простыня, в которой
        // глаз не находит проблему. Плитки в сетке видно целиком, а проблемные
        // всё равно подсвечены цветом.
        var grid = document.createElement("div");
        grid.className = "check-grid";

        items.forEach(function (c) {
            var badge = c.status === "Ok" ? "ok" : (c.status === "Warn" ? "warn" : (c.status === "Fail" ? "bad" : ""));

            var tile = document.createElement("div");
            tile.className = "check-tile " + badge;
            tile.title = (c.detail || "") + (c.advice ? " — " + c.advice : "");

            tile.innerHTML =
                '<div class="check-head"><span class="badge ' + badge + '">' +
                    (c.status === "Ok" ? "OK" : (c.status === "Skip" ? "—" : "!")) + '</span>' +
                    '<span class="check-name">' + c.name + '</span></div>' +
                '<div class="check-detail">' + (c.detail || "") + '</div>';

            grid.appendChild(tile);
            box.appendChild(grid);

            if (c.advice) {
                var adv = document.createElement("div");
                adv.className = "advice";
                adv.style.margin = "6px 0";
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

    /// <summary>
    /// Результат сниффера: сколько пакетов, разобран ли TLS и что это значит.
    /// Здесь же ответ «драйвер вообще работает?», а не только список домены.
    /// </summary>
    function renderNetCap(msg) {
        var btn = document.getElementById("btn-diag-netcap");
        if (btn) { btn.disabled = false; btn.textContent = "Слушать 8 с"; }

        var box = document.getElementById("diag-netcap-result");
        if (!box) return;

        text("diag-netcap-summary", msg.summary || "");

        var verdict = document.getElementById("diag-netcap-verdict");
        if (verdict) {
            verdict.textContent = msg.verdict || "";
            verdict.className = "advice " + (msg.ok && msg.total > 0 ? "diag-ok" : "diag-bad");
        }

        var snis = document.getElementById("diag-netcap-snis");
        if (snis) {
            var list = msg.snis || [];
            snis.textContent = list.length > 0 ? "SNI: " + list.join(", ") : "";
            snis.style.display = list.length > 0 ? "" : "none";
        }

        box.style.display = "";
    }

    /// <summary>
    /// Отладочный прогон: что winws сделал с нашими соединениями.
    /// Показываем его слова как есть — это единственный источник правды,
    /// когда «движок запущен, а не работает».
    /// </summary>
    function renderExplain(msg) {
        var btn = document.getElementById("btn-diag-explain");
        if (btn) { btn.disabled = false; btn.textContent = "Объяснить выбранную"; }

        var box = document.getElementById("diag-explain-result");
        if (!box) return;

        var summary = document.getElementById("diag-explain-summary");
        if (summary) {
            if (!msg.ok) {
                summary.textContent = "не получилось: " + (msg.error || "?");
                summary.className = "row-hint diag-bad";
            } else {
                var probes = (msg.probes || []).join(" · ");
                summary.textContent = msg.name + ": " + msg.okCount + "/" + msg.total +
                    (probes ? " · " + probes : "") +
                    (msg.file ? " · файл: " + msg.file : "");
                summary.className = "row-hint " + (msg.okCount > 0 ? "diag-ok" : "diag-warn");
            }
        }

        var log = document.getElementById("diag-explain-log");
        if (log) {
            var lines = msg.engine || [];
            log.textContent = lines.length > 0
                ? lines.join("\n")
                : "winws не сказал ни слова про пакеты — значит правила не применились " +
                  "(не совпал фильтр) либо он не считает это ошибкой";
        }

        var stages = document.getElementById("diag-explain-stages");
        if (stages) {
            stages.innerHTML = "";

            // «до» — чтобы видеть вклад обхода, а не гадать
            var before = msg.stagesBefore || [];
            if (before.length > 0) {
                var head = document.createElement("div");
                head.className = "row-hint";
                head.textContent = "без обхода: " + before.map(function (b) {
                    return b.name + " — " + b.verdict;
                }).join(" · ");
                stages.appendChild(head);
            }

            (msg.stages || []).forEach(function (s) {
                var div = document.createElement("div");
                div.className = "diag-row";

                var cls = s.tlsOk ? "ok" : (s.tcpOk ? "bad" : "warn");

                div.innerHTML =
                    '<span class="badge ' + cls + '">' + (s.tlsOk ? "OK" : "!") + '</span>' +
                    '<span class="name">' + s.name + '</span>' +
                    '<span class="verdict ' + (s.tlsOk ? "diag-ok" : "diag-bad") + '">' + s.verdict + '</span>' +
                    '<span class="detail">DNS: ' + (s.dns || "?") +
                        ' · TCP: ' + (s.tcp || "?") + ' · TLS: ' + (s.tls || "?") +
                        (s.note ? '<br>! ' + s.note : "") + '</span>';

                stages.appendChild(div);
            });
        }

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

    /// <summary>
    /// Прогресс подбора стратегий: полоса, «N из M», прошедшее время и мини-лог.
    /// Раньше здесь висела одна строка текста, и отличить «работает» от «повисло»
    /// было нельзя — а подбор идёт минутами.
    /// </summary>
    function renderTestProgress(msg) {
        var box = document.getElementById("diag-test-progress");
        if (box) box.style.display = "";

        var status = document.getElementById("diag-test-status");
        if (status) {
            var parts = [];
            if (msg.index && msg.total) parts.push(msg.index + " / " + msg.total);
            if (msg.line) parts.push(msg.line);
            if (typeof msg.elapsedSec === "number" && msg.elapsedSec > 0) {
                parts.push("(" + fmtUptime(msg.elapsedSec) + ")");
            }
            status.textContent = parts.join(" · ");
        }

        var fill = document.getElementById("diag-test-fill");
        if (fill && msg.index && msg.total) {
            fill.style.width = Math.min(100, Math.round(msg.index * 100 / msg.total)) + "%";
        } else if (fill && msg.done) {
            fill.style.width = "100%";
        }

        // мини-лог: держим последние строки, чтобы видеть движение
        var log = document.getElementById("diag-test-log");
        if (log && msg.line) {
            var line = document.createElement("div");
            line.textContent = msg.line;
            log.appendChild(line);

            while (log.childNodes.length > 8) log.removeChild(log.firstChild);
            log.scrollTop = log.scrollHeight;
        }
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

            // почему не пробило: таймаут, DNS, TLS, сброс.
            // «0/4» без причины заставляет перебирать стратегии бесконечно.
            var reasons = (s.reasons && s.okCount === 0)
                ? '<div class="detail">почему: ' + s.reasons + "</div>" : "";

            var scoreCls = s.score >= 80 ? "diag-ok" : (s.score >= 50 ? "diag-warn" : "diag-bad");

            tr.innerHTML =
                "<td>" + mark + "</td>" +
                "<td>" + s.name + failed + reasons + breakdown + "</td>" +
                "<td>" + s.okCount + "/" + s.total + "</td>" +
                "<td>" + (s.okCount > 0 ? s.avgMs + " ms" : "—") + "</td>" +
                "<td class=\"" + scoreCls + "\"><b>" + (s.score || 0) + "</b></td>";
            body.appendChild(tr);
        });

        var summaryEl = document.getElementById("diag-test-summary");
        if (summaryEl) {
            summaryEl.textContent = msg.summary || "";
            summaryEl.className = "row-hint " + (msg.cancelled ? "diag-warn" : "");
        }

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
        bindAnyList();

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

    function bindAnyList() {
        var sel = document.getElementById("list-any-select");
        var input = document.getElementById("list-any-input");
        if (!sel || !input) return;

        sel.addEventListener("change", function () {
            if (sel.value) send("lists.get", { kind: sel.value });
        });

        var save = document.getElementById("btn-list-any-save");
        if (save) save.addEventListener("click", function () {
            if (!sel.value) return;
            save.disabled = true;
            send("lists.save", { kind: sel.value, text: input.value });
            setTimeout(function () { save.disabled = false; }, 500);
        });

        var reload = document.getElementById("btn-list-any-reload");
        if (reload) reload.addEventListener("click", function () {
            if (sel.value) send("lists.get", { kind: sel.value });
        });

        var check = document.getElementById("btn-list-any-check");
        if (check) check.addEventListener("click", function () {
            send("lists.stats", { kind: sel.value, text: input.value });
        });

        var dupes = document.getElementById("btn-list-any-dedupe");
        if (dupes) dupes.addEventListener("click", function () {
            send("lists.dedupe", { kind: sel.value, text: input.value });
        });

        var importBtn = document.getElementById("btn-list-any-import");
        var file = document.getElementById("file-list-any");
        if (importBtn && file) importBtn.addEventListener("click", function () { file.click(); });

        if (file) file.addEventListener("change", function () {
            var chosen = file.files && file.files[0];
            if (chosen) importIntoTextarea(chosen, input, "any");
            file.value = "";
        });

        input.addEventListener("dragover", function (e) { e.preventDefault(); input.classList.add("drop"); });
        input.addEventListener("dragleave", function () { input.classList.remove("drop"); });
        input.addEventListener("drop", function (e) {
            e.preventDefault();
            input.classList.remove("drop");
            if (!e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) return;
            importIntoTextarea(e.dataTransfer.files[0], input, "any");
        });

        var restore = document.getElementById("btn-lists-restore-bundled");
        if (restore) restore.addEventListener("click", function () {
            restore.disabled = true;
            restore.textContent = "Восстанавливаю…";
            send("lists.restoreBundled");
        });

        var checkUpd = document.getElementById("btn-lists-check-updates");
        if (checkUpd) checkUpd.addEventListener("click", function () {
            checkUpd.disabled = true;
            checkUpd.textContent = "Проверяю…";
            send("lists.checkUpdates");
        });

        // поиск по большому списку: на ipset в 33 000 строк без этого
        // не найти нужную подсеть глазами
        var find = document.getElementById("btn-list-any-find");
        if (find) find.addEventListener("click", function () { findInAnyList(); });

        var filter = document.getElementById("list-any-filter");
        if (filter) {
            filter.addEventListener("input", updateAnyListCount);
            filter.addEventListener("keydown", function (e) {
                if (e.key === "Enter") { e.preventDefault(); findInAnyList(); }
            });
        }

        if (input) input.addEventListener("input", updateAnyListCount);
    }

    /// Найти следующее совпадение и выделить его в поле.
    function findInAnyList() {
        var input = document.getElementById("list-any-input");
        var filter = document.getElementById("list-any-filter");
        if (!input || !filter) return;

        var query = filter.value.trim();
        if (!query) { input.focus(); return; }

        var text = input.value || "";
        var from = input.selectionEnd || 0;
        var index = text.toLowerCase().indexOf(query.toLowerCase(), from);

        // дошли до конца — начинаем сначала, чтобы «Найти» всегда находило
        if (index < 0) index = text.toLowerCase().indexOf(query.toLowerCase(), 0);
        if (index < 0) { updateAnyListCount(); return; }

        input.focus();
        input.setSelectionRange(index, index + query.length);

        // прокручиваем так, чтобы найденная строка оказалась в середине
        var before = text.substring(0, index);
        var line = before.split("\n").length - 1;
        var lineHeight = parseFloat(window.getComputedStyle(input).lineHeight) || 16;

        input.scrollTop = Math.max(0, line * lineHeight - input.clientHeight / 2);

        updateAnyListCount();
    }

    function renderListUpdates(msg) {
        var btn = document.getElementById("btn-lists-check-updates");
        if (btn) { btn.disabled = false; btn.textContent = "Проверить обновления"; }

        var hint = document.getElementById("lists-bundled-hint");
        if (hint && msg.summary) {
            hint.textContent = msg.summary;
            hint.className = "row-hint " + ((msg.outdated || 0) > 0 ? "diag-warn" : "diag-ok");
        }

        var box = document.getElementById("lists-updates-list");
        if (!box) return;

        box.innerHTML = "";

        (msg.items || []).forEach(function (u) {
            var div = document.createElement("div");
            div.className = "diag-row";

            var cls = !u.reachable ? "warn" : (u.changed ? "warn" : "ok");

            div.innerHTML =
                '<span class="badge ' + cls + '">' + (!u.reachable ? "—" : (u.changed ? "!" : "OK")) + '</span>' +
                '<span class="name">' + u.name + '</span>' +
                '<span class="verdict">' + (u.note || "") + '</span>' +
                '<span class="detail">' + u.file + '</span>';

            box.appendChild(div);
        });

        box.style.display = "";
    }

    function renderBundledLists(msg) {
        var btn = document.getElementById("btn-lists-restore-bundled");
        if (btn) { btn.disabled = false; btn.textContent = "Восстановить из поставки"; }

        var hint = document.getElementById("lists-bundled-hint");
        if (hint && msg.message) {
            hint.textContent = msg.message;
            hint.className = "row-hint " + ((msg.restored || 0) > 0 ? "diag-ok" : "");
        }
    }

    function renderListCatalog(msg) {
        var sel = document.getElementById("list-any-select");
        if (!sel) return;

        var current = sel.value;
        sel.innerHTML = "";

        (msg.items || []).forEach(function (k) {
            var opt = document.createElement("option");
            opt.value = k.id;
            opt.textContent = k.title + (k.lines ? " · " + k.lines + " стр." : " · пусто");
            opt.title = k.note || "";
            sel.appendChild(opt);
        });

        if (current) sel.value = current;
        else if (sel.options.length > 0) sel.value = sel.options[0].value;

        // в подписи — сколько вшито в поставку: человек должен знать, что
        // запас есть, ещё до того, как сеть отвалится
        var note = document.getElementById("list-any-note");
        if (note && msg.bundled) {
            note.textContent = "В поставке: " + msg.bundled +
                (msg.bundledReady ? "" : " (вшитых списков нет)");
        }

        if (sel.value) send("lists.get", { kind: sel.value });
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

        // дубли и мусор: считаем на сервере, потому что правила валидации
        // (домен, CIDR, `!` для исключений) уже написаны в C# — второй
        // экземпляр правил в JS разошёлся бы с ними на первой же правке
        var dupes = document.getElementById("btn-list-" + kind + "-dedupe");
        if (dupes) dupes.addEventListener("click", function () {
            send("lists.dedupe", { kind: kind, text: input.value });
        });

        var check = document.getElementById("btn-list-" + kind + "-check");
        if (check) check.addEventListener("click", function () {
            send("lists.stats", { kind: kind, text: input.value });
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

            busyBtn(id, busyText);
            send(action);
        });
    }

    function renderLists(msg) {
        var kind = msg.kind;

        // Списки движка (основной, Google, ipset) показываются в общем
        // редакторе: отдельных полей для них нет, и раньше обработчик молча
        // выходил, оставляя пустое поле. Именно поэтому «не редачится».
        if (kind !== "general" && kind !== "exclude") {
            renderAnyList(msg);
            return;
        }

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

    // Общий редактор: текст, подпись с размерами и предупреждение о том,
    // что скачиваемый список перезапишется при обновлении.
    function renderAnyList(msg) {
        var input = document.getElementById("list-any-input");
        if (!input) return;

        if (document.activeElement !== input) input.value = msg.text || "";

        var meta = document.getElementById("list-any-meta");
        if (meta) {
            var parts = [];
            if (msg.file) parts.push(msg.file);
            parts.push("строк: " + (msg.lines || 0));
            if (msg.bytes) parts.push(Math.round(msg.bytes / 1024) + " КБ");
            parts.push(msg.user ? "личный список" : "скачиваемый — обновление перезапишет");

            meta.textContent = parts.join(" · ");
            meta.className = "row-hint mono " + (msg.user ? "" : "diag-warn");
        }

        var desc = document.getElementById("list-any-desc");
        if (desc) desc.textContent = msg.note || "";

        updateAnyListCount();
    }

    // Сколько строк в поле и сколько совпадений с поиском: на ipset с 33 000
    // строк иначе непонятно, что ты вообще видишь.
    function updateAnyListCount() {
        var input = document.getElementById("list-any-input");
        var label = document.getElementById("list-any-count");
        if (!input || !label) return;

        var text = input.value || "";
        var lines = text.length === 0 ? 0 : text.split(/\r?\n/).length;

        var filter = document.getElementById("list-any-filter");
        var query = filter ? filter.value.trim().toLowerCase() : "";

        if (!query) {
            label.textContent = "строк в поле: " + lines;
            label.className = "row-hint mono";
            return;
        }

        var matches = 0;
        text.split(/\r?\n/).forEach(function (line) {
            if (line.toLowerCase().indexOf(query) >= 0) matches++;
        });

        label.textContent = "строк в поле: " + lines + " · совпадений: " + matches;
        label.className = "row-hint mono " + (matches > 0 ? "diag-ok" : "diag-warn");
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
            busyBtn("btn-hosts-apply", "Собираю…");
            send("hosts.apply", { verify: verify ? verify.checked : false });
        });

        var check = document.getElementById("btn-hosts-check");
        if (check) check.addEventListener("click", function () {
            busyBtn("btn-hosts-check", "Проверяю…");
            send("hosts.check");
        });

        var restore = document.getElementById("btn-hosts-restore");
        if (restore) restore.addEventListener("click", function () {
            if (!window.confirm(
                "hosts вернётся из резервной копии, сделанной при первом изменении.\n\n" +
                "Записи, добавленные после этого момента, исчезнут.")) return;

            busyBtn("btn-hosts-restore", "Восстанавливаю…");
            send("hosts.restore");
        });

        // мягкая чистка: убрать только наш блок, не трогая чужие записи
        var clean = document.getElementById("btn-hosts-clean");
        if (clean) clean.addEventListener("click", function () {
            busyBtn("btn-hosts-clean", "Чищу…");
            send("hosts.clean");
        });

        // полный сброс: остаётся только localhost
        var reset = document.getElementById("btn-hosts-reset");
        if (reset) reset.addEventListener("click", function () {
            // Здесь то самое окно с отсчётом: действие необратимо кнопкой
            // «назад», а window.confirm такой возможности не даёт вовсе.
            dangerConfirm({
                title: t("Пересоздать hosts?"),
                text: t("Останется только 127.0.0.1 localhost. Все остальные записи пропадут."),
                hint: t("Резервная копия сохранится: вернуть прежнее можно кнопкой «Восстановить»"),
                wait: 5,
                extra: t("Ну ладно, я предупредил. Резервная копия есть, но проверьте её до перезагрузки — после неё вернуть будет нечего, если она не создалась."),
                onConfirm: function () {
                    busyBtn("btn-hosts-reset", "Пересоздаю…");
                    send("hosts.reset");
                }
            });
        });

        // ---- редактор hosts
        var rawReload = document.getElementById("btn-hosts-raw-reload");
        if (rawReload) rawReload.addEventListener("click", function () { send("hosts.raw.get"); });

        var rawCheck = document.getElementById("btn-hosts-raw-check");
        if (rawCheck) rawCheck.addEventListener("click", function () {
            checkHostsText(document.getElementById("hosts-raw-input").value);
        });

        var rawDedupe = document.getElementById("btn-hosts-raw-dedupe");
        if (rawDedupe) rawDedupe.addEventListener("click", function () {
            send("hosts.dedupe", { text: document.getElementById("hosts-raw-input").value });
        });

        var rawSave = document.getElementById("btn-hosts-raw-save");
        if (rawSave) rawSave.addEventListener("click", function () {
            var text = document.getElementById("hosts-raw-input").value;

            if (!window.confirm(
                "Записать этот текст в системный файл hosts?\n\n" +
                "Резервная копия уже создана (или создастся сейчас).")) return;

            busyBtn("btn-hosts-raw-save", "Сохраняю…");
            send("hosts.raw.save", { text: text });
        });

        var fixApply = document.getElementById("btn-hosts-fixes-apply");
        if (fixApply) fixApply.addEventListener("click", function () {
            var chosen = [];

            document.querySelectorAll("#hosts-fixes-list input[data-fix-id]").forEach(function (box) {
                if (box.checked) chosen.push(box.getAttribute("data-fix-id"));
            });

            if (chosen.length === 0) {
                var status = document.getElementById("hosts-fixes-status");
                if (status) {
                    status.textContent = "Отметьте галочками, что добавить";
                    status.className = "row-hint diag-warn";
                }
                return;
            }

            // Большой hosts с блокировками (0.0.0.0) может сломать приложение:
            // предупреждаем ДО применения, а не после.
            var risky = [];
            chosen.forEach(function (id) {
                var box = document.querySelector('#hosts-fixes-list input[data-fix-id="' + id + '"]');
                if (box && box.getAttribute("data-careful") === "1") risky.push(box.getAttribute("data-fix-name"));
            });

            if (risky.length > 0 && !window.confirm(
                "В выбранном есть блокировки рекламы и трекеров (0.0.0.0):\n" +
                risky.join(", ") + "\n\n" +
                "Иногда они ломают приложения: реклама и аналитика бывают частью\n" +
                "логики входа. Применяем в редактор — вы сможете посмотреть\n" +
                "и убрать лишнее до сохранения. Продолжить?")) return;

            busyBtn("btn-hosts-fixes-apply", "Применяю…");

            send("hosts.fixes.applyMany", {
                ids: chosen,
                text: document.getElementById("hosts-raw-input").value
            });
        });

        // Обновить известные наборы из сети. Скачанное ложится в кэш и дальше
        // применяется без сети — hosts нужен именно тогда, когда сети нет.
        var fixUpdate = document.getElementById("btn-hosts-fixes-update");
        if (fixUpdate) fixUpdate.addEventListener("click", function () {
            busyBtn("btn-hosts-fixes-update", "Обновляю…", 60000);

            var status = document.getElementById("hosts-fixes-status");
            if (status) {
                status.textContent = "Скачиваю наборы…";
                status.className = "row-hint";
            }

            send("hosts.fixes.update");
        });

        var selectAll = document.getElementById("btn-hosts-fixes-all");        if (selectAll) selectAll.addEventListener("click", function () {
            document.querySelectorAll("#hosts-fixes-list input[data-fix-id]").forEach(function (b) { b.checked = true; });
            updateFixesSelected();
        });

        var selectNone = document.getElementById("btn-hosts-fixes-none");
        if (selectNone) selectNone.addEventListener("click", function () {
            document.querySelectorAll("#hosts-fixes-list input[data-fix-id]").forEach(function (b) { b.checked = false; });
            updateFixesSelected();
        });
    }

    /// Счётчик выбранного: видно, сколько применять, не открывая список глазами.
    function updateFixesSelected() {
        var label = document.getElementById("hosts-fixes-selected");
        if (!label) return;

        var boxes = document.querySelectorAll("#hosts-fixes-list input[data-fix-id]");
        var chosen = 0;

        boxes.forEach(function (b) { if (b.checked) chosen++; });

        label.textContent = boxes.length === 0
            ? ""
            : "выбрано " + chosen + " из " + boxes.length;

        label.className = "row-hint mono " + (chosen > 0 ? "diag-ok" : "");
    }

    // локальная проверка: дубли и конфликты считаем по тому, что в поле,
    // не дожидаясь ответа ядра — так видно результат прямо при правке
    function checkHostsText(text) {
        var rows = 0, hosts = 0;
        var byHost = {};

        String(text || "").split(/\r?\n/).forEach(function (line) {
            var t = line.trim();
            if (!t || t.charAt(0) === "#") return;

            var hash = t.indexOf("#");
            if (hash > 0) t = t.substring(0, hash).trim();

            var parts = t.split(/[\s\t]+/);
            if (parts.length < 2) return;

            rows++;

            for (var i = 1; i < parts.length; i++) {
                var h = parts[i].toLowerCase();
                if (!h || h === "localhost") continue;

                if (!byHost[h]) { byHost[h] = []; hosts++; }
                if (byHost[h].indexOf(parts[0]) < 0) byHost[h].push(parts[0]);
            }
        });

        var conflicts = [];
        Object.keys(byHost).forEach(function (h) {
            if (byHost[h].length > 1) conflicts.push(h + " → " + byHost[h].join(", "));
        });

        var summary = document.getElementById("hosts-raw-summary");
        if (summary) {
            summary.textContent = "записей " + rows + ", имён " + hosts +
                (conflicts.length ? ", имён с несколькими адресами " + conflicts.length : "");
            summary.className = "row-hint " + (conflicts.length ? "diag-warn" : "diag-ok");
        }

        var box = document.getElementById("hosts-raw-problems");
        if (!box) return;

        box.innerHTML = "";

        conflicts.slice(0, 10).forEach(function (c) {
            var div = document.createElement("div");
            div.className = "diag-row";
            div.innerHTML = '<span class="badge warn">!</span>' +
                '<span class="name mono">' + c + '</span>' +
                '<span class="verdict">одно имя, разные адреса</span>';
            box.appendChild(div);
        });

        box.style.display = conflicts.length ? "" : "none";
    }

    // ---- редактор hosts: полный текст и разбор от ядра
    function renderHostsRaw(msg) {
        idleBtn("btn-hosts-raw-save");
        idleBtn("btn-hosts-raw-reload");

        var status = document.getElementById("hosts-raw-status");

        if (!msg.ok) {
            if (status) {
                status.textContent = msg.error || "не удалось прочитать hosts";
                status.className = "row-hint diag-bad";
            }
            return;
        }

        var input = document.getElementById("hosts-raw-input");
        if (input && typeof msg.text === "string" && input.value.trim() !== msg.text.trim()) {
            input.value = msg.text;
        }

        checkHostsText(msg.text || "");

        if (status) {
            status.textContent = msg.summary || "";
            status.className = "row-hint";
        }

        send("hosts.fixes");
    }

    /// Отчёт об обновлении наборов: по строке на набор, удачные и неудачные.
    ///
    /// Отдельно от renderHostsFixes: там список галочек, а здесь результат
    /// операции. Смешивать нельзя — список перерисовывается из C# отдельным
    /// сообщением, и если писать отчёт в тот же контейнер, он исчезнет.
    function renderHostsUpdated(msg) {
        idleBtn("btn-hosts-fixes-update");

        var status = document.getElementById("hosts-fixes-status");
        if (!status) return;

        var items = msg.items || [];
        var ok = msg.ok || 0;
        var total = msg.total || items.length;

        if (total === 0) {
            status.textContent = "Известных наборов нет";
            status.className = "row-hint diag-warn";
            return;
        }

        var bad = [];
        items.forEach(function (r) {
            if (!r.ok) bad.push(r.name + ": " + (r.error || "ошибка"));
        });

        if (bad.length === 0) {
            status.textContent = "Обновлено наборов: " + ok + " из " + total;
            status.className = "row-hint diag-ok";
            return;
        }

        // Частичный успех — самый частый случай: GitHub доступен, зеркало нет.
        // Пишем и то, и другое, иначе «не обновилось» без причины.
        status.textContent = "Обновлено " + ok + " из " + total + " · не вышло: " + bad.join("; ");
        status.className = "row-hint " + (ok > 0 ? "diag-warn" : "diag-bad");
    }

    function renderHostsFixes(msg) {
        var list = document.getElementById("hosts-fixes-list");
        if (!list) return;

        list.innerHTML = "";

        var items = msg.items || [];

        if (items.length === 0) {
            var empty = document.createElement("div");
            empty.className = "row-hint";
            empty.textContent = "фиксов нет";
            list.appendChild(empty);
            updateFixesSelected();
            return;
        }

        items.forEach(function (f) {
            // три вида: статичный фикс, вшитый файл, скачиваемый список
            var kind = f.remote ? "remote" : (f.bundled ? "bundled" : "fix");

            // У скачиваемых в бейдже не «сеть», а состояние: скачан ли и когда.
            // Иначе список всегда выглядит одинаково, и человек нажимает
            // «применить», не зная, что именно применит.
            var badge = kind === "remote"
                ? (f.state || "сеть")
                : (kind === "bundled" ? (f.state || "поставка") : "фикс");

            var row = document.createElement("label");
            row.className = "fix-row";

            row.innerHTML =
                '<input type="checkbox" data-fix-id="' + f.id + '"' +
                    ' data-careful="' + (f.careful ? "1" : "0") + '"' +
                    ' data-fix-name="' + String(f.name || "").replace(/"/g, "&quot;") + '">' +
                '<span class="fix-body">' +
                    '<span class="fix-head">' +
                        '<span class="fix-name">' + f.name + '</span>' +
                        '<span class="badge">' + badge + '</span>' +
                        (f.careful ? '<span class="badge warn">блокировки</span>' : '') +
                    '</span>' +
                    (f.note ? '<span class="fix-note">' + f.note + '</span>' : '') +
                '</span>';

            list.appendChild(row);
        });

        list.querySelectorAll("input[data-fix-id]").forEach(function (box) {
            box.addEventListener("change", updateFixesSelected);
        });

        updateFixesSelected();
    }

    function renderHosts(msg) {
        // Кнопки возвращаем по id, а не через переменную из другого замыкания:
        // раньше здесь стояла ссылка на `apply` из initHosts, которой в этой
        // области видимости нет — под "use strict" это ReferenceError, и вся
        // функция умирала на первой строке. Симптом ровно тот, на который
        // жалуются: «Пересоздаю…» висит, статус не обновляется, кнопка
        // «Обновить» больше не нажимается.
        idleBtn("btn-hosts-apply");
        idleBtn("btn-hosts-check");
        idleBtn("btn-hosts-restore");
        idleBtn("btn-hosts-clean");
        idleBtn("btn-hosts-reset");

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

            case "engine.result":
                renderEngineResult(msg);
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

            case "diag.bundleProgress":
            {
                var progress = document.getElementById("bundle-progress");
                if (progress) { progress.textContent = msg.line || ""; progress.style.display = ""; }
                break;
            }

            case "presets":
                renderPresets(msg);
                break;

            case "autostart":
                renderAutoStart(msg);
                break;

            case "diag.deep":
                renderDeepResult(msg);
                break;

            case "diag.netcap":
                renderNetCap(msg);
                break;

            case "diag.explain":
                renderExplain(msg);
                break;

            case "diag.tcp16":
                renderTcp16(msg);
                break;

            case "diag.progress":
                renderTestProgress(msg);
                break;

            case "diag.state":
                setDiagBusy(!!msg.running, msg.title || "");
                break;

            case "browser.doh":
                renderBrowserDoh(msg);
                break;

            case "sysdns":
                renderSystemDns(msg);
                break;

            case "own.state":
                renderOwnState(msg);
                break;

            case "own.compare":
                renderCompare(msg);
                break;

            case "own.compat":
                renderOwnCompat(msg);
                break;

            case "service":
                renderService(msg);
                break;

            case "service.result":
                renderServiceResult(msg);
                break;

            case "dns.flushed":
                renderDnsFlushed(msg);
                break;

            case "diag.busy":
                showDiagBusy(msg.message || "Уже идёт другая проверка");
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

            case "hosts.raw":
                renderHostsRaw(msg);
                break;

            case "hosts.edited":
            {
                var input = document.getElementById("hosts-raw-input");
                if (input && typeof msg.text === "string") {
                    input.value = msg.text;
                    checkHostsText(msg.text);
                }

                var st = document.getElementById("hosts-raw-status");
                if (st) { st.textContent = msg.summary || ""; st.className = "row-hint diag-ok"; }

                // тот же отчёт дублируем в карточке фиксов: кнопка нажимается там
                var fs = document.getElementById("hosts-fixes-status");
                if (fs) {
                    fs.textContent = msg.summary || "";
                    fs.className = "row-hint " + (msg.partial ? "diag-warn" : "diag-ok");
                }

                idleBtn("btn-hosts-fixes-apply");
                break;
            }

            case "hosts.fixes":
                renderHostsFixes(msg);
                break;

            case "hosts.fixes.updated":
                renderHostsUpdated(msg);
                break;

            case "lists.stats":
            {
                var hint = document.getElementById("list-" + (msg.kind || "general") + "-hint");
                if (hint) {
                    hint.textContent = msg.stats || "";
                    hint.className = "row-hint " + ((msg.duplicates || 0) > 0 ? "diag-warn" : "diag-ok");
                }
                break;
            }

            case "lists.edited":
            {
                var target = document.getElementById("list-" + (msg.kind || "general") + "-input");
                if (target && typeof msg.text === "string") target.value = msg.text;

                var hint = document.getElementById("list-" + (msg.kind || "general") + "-hint");
                if (hint) { hint.textContent = msg.summary || ""; hint.className = "row-hint diag-ok"; }
                break;
            }

            case "lists.catalog":
                renderListCatalog(msg);
                break;

            case "lists.bundled":
                renderBundledLists(msg);
                break;

            case "lists.updates":
                renderListUpdates(msg);
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
        bindDanger();
        initDns();
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
        // окно опасного подтверждения — для проверки глазами и для тестов:
        // без этого его нельзя вызвать вручную, не нажимая настоящую кнопку
        dangerConfirm: dangerConfirm,
        // список стратегий: проверяется «не слетел ли выбор при перестройке»
        renderStrategies: renderStrategies,
        renderState: renderState,
        // карточка DNS: проверяется, что опасное состояние видно и кнопки
        // возвращаются из «Включаю…»
        renderSystemDns: renderSystemDns,
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
