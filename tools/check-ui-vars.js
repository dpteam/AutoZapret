// Разовая проверка: ищем переменные, которые используются, но не объявлены
// в своей функции. Именно так ломались renderHosts (`apply`) и renderSystemDns
// (`on`): под "use strict" это ReferenceError, и функция молча умирает.
//
// Это не полноценный линтер, а поиск конкретной ошибки: `X.disabled` /
// `X.textContent` / `X.classList` для X, которого нет ни в var-ах функции,
// ни в списке глобальных.
const fs = require("fs");
const path = require("path");

const src = fs.readFileSync(path.join(__dirname, "..", "web", "app.js"), "utf8");
const lines = src.split("\n");

// Грубое разбиение на функции верхнего уровня по строкам вида "function name("
const starts = [];
lines.forEach((line, i) => {
    const m = line.match(/^\s{4}function\s+([A-Za-z_$][\w$]*)\s*\(/);
    if (m) starts.push({ name: m[1], line: i });
});

const globals = new Set([
    "window", "document", "console", "JSON", "Math", "Date", "String", "Number",
    "Object", "Array", "Boolean", "RegExp", "Promise", "setTimeout", "clearTimeout",
    "setInterval", "clearInterval", "parseInt", "parseFloat", "isNaN", "encodeURIComponent",
    "decodeURIComponent", "host", "state", "i18nDict", "i18nLang", "i18nRequested",
    "lastPreflight", "elevationHintShown", "logLines", "logRenderTimer", "saveTimer",
    "pendingSettings", "netSpeedTimer", "engineDownloadBound", "strategyItems",
    "busyButtons", "dangerTimer", "dangerCallback", "dangerSecondStage",
    "NodeFilter", "KeyboardEvent", "Uint8Array", "TextEncoder", "TextDecoder"
]);

let problems = 0;

for (let s = 0; s < starts.length; s++) {
    const from = starts[s].line;
    const to = s + 1 < starts.length ? starts[s + 1].line : lines.length;
    const body = lines.slice(from, to).join("\n");

    const declared = new Set();

    const varRe = /\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)/g;
    let m;
    while ((m = varRe.exec(body)) !== null) declared.add(m[1]);

    // параметры функции и вложенных function/callback
    const header = body.slice(0, body.indexOf("{") + 1);
    const params = header.replace(/^[^(]*\(/, "").replace(/\).*$/, "");
    params.split(",").forEach((p) => {
        const name = p.trim().split("=")[0].trim();
        if (/^[A-Za-z_$][\w$]*$/.test(name)) declared.add(name);
    });

    // function (a, b) { ... } — колбэки и IIFE
    const inlineRe = /\bfunction\s*\(([^)]*)\)/g;
    let inline;
    while ((inline = inlineRe.exec(body)) !== null) {
        inline[1].split(",").forEach((p) => {
            const name = p.trim().split("=")[0].trim();
            if (/^[A-Za-z_$][\w$]*$/.test(name)) declared.add(name);
        });
    }

    // for (var i = ...) и function (x) уже покрыты var-ами; ловим ещё catch (e)
    const catchRe = /\bcatch\s*\(\s*([A-Za-z_$][\w$]*)/g;
    while ((m = catchRe.exec(body)) !== null) declared.add(m[1]);

    // `document.body.classList` — это не переменная `body`
    const useRe = /(^|[^.\w$])([A-Za-z_$][\w$]*)\s*\.\s*(?:disabled|textContent|classList|innerHTML|value|checked|style|addEventListener|querySelectorAll|setAttribute)\b/g;

    const seen = new Set();
    while ((m = useRe.exec(body)) !== null) {
        const name = m[2];
        if (declared.has(name) || globals.has(name) || seen.has(name)) continue;
        seen.add(name);
        problems++;
        console.log("  " + starts[s].name + " (строка " + (from + 1) + "): " + name);
    }
}

console.log(problems === 0 ? "необъявленных переменных не найдено" : "найдено: " + problems);
process.exit(problems === 0 ? 0 : 1);