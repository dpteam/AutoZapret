// Есть ли в winws.exe каждая опция, которую используют стратегии каталога.
//
// Это та же проверка, что EngineLocator.SupportsOption: ищем литерал имени
// опции в байтах exe. Не заменяет --dry-run (он проверяет ещё и значения), но
// ловит главное: опечатку и опцию из чужой версии движка.
//
// Запуск: node tools/check-options.js
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");

const catalog = JSON.parse(
    fs.readFileSync(path.join(root, "assets", "strategies", "strategies.v1.json"), "utf8")
);

const exePath = path.join(root, "engine", "bin", "winws.exe");

if (!fs.existsSync(exePath)) {
    console.log("[skip] нет engine/bin/winws.exe — проверять нечего");
    process.exit(0);
}

const exe = fs.readFileSync(exePath);
const exeText = exe.toString("latin1");

// Собираем все опции из всех стратегий: и из аргументов, и из тех, что
// появляются только в v2 (--lua-desync) — их проверять по winws нельзя.
const used = new Map();

function addOption(option, id) {
    if (!option.startsWith("--")) return;

    // `--dpi-desync-fake-tls=0x00` и `--dpi-desync-fake-tls` — одна опция
    const eq = option.indexOf("=");
    const name = eq > 0 ? option.slice(0, eq) : option;

    if (!used.has(name)) used.set(name, new Set());
    used.get(name).add(id);
}

for (const s of catalog.strategies) {
    for (const a of s.globalArgs || []) addOption(a, s.id);

    for (const p of s.profiles || []) {
        for (const a of p.args || []) addOption(a, s.id);
    }
}

const missing = [];

for (const [name, ids] of used) {
    if (exeText.indexOf(name) >= 0) continue;

    missing.push({ name: name, ids: [...ids] });
}

// ---------------------------------------------------------------------------
// Значения перечислимых опций, а не только их имена.
//
// `--dpi-desync-fooling` в движке есть, а значение `datagap` — нет, и winws
// отказывается стартовать. Имя опции проверку проходило, поэтому стратегия
// с недопустимым значением жила в каталоге и не могла запуститься вообще:
// ни на Windows, ни на Linux (нашли при портировании).
//
// Список допустимых значений берём из строки ошибки самого движка, а не
// из головы: он один и тот же в winws и nfqws, и обновляется вместе с ними.
function allowedValues(label) {
    const re = new RegExp(label + "[^\\x00]{0,200}");
    const m = exeText.match(re);
    if (!m) return null;

    const afterColon = m[0].split(":").slice(1).join(":");
    return afterColon.split(",").map(s => s.trim().split(/\s/)[0]).filter(Boolean);
}

const foolingAllowed = allowedValues("fooling allowed values");
const badValues = [];

if (foolingAllowed) {
    for (const s of catalog.strategies) {
        const args = [];
        for (const a of s.globalArgs || []) args.push(a);
        for (const p of s.profiles || []) for (const a of p.args || []) args.push(a);

        for (const a of args) {
            if (!a.startsWith("--dpi-desync-fooling=")) continue;

            const values = a.slice(a.indexOf("=") + 1).split(",").map(v => v.trim()).filter(Boolean);

            for (const v of values) {
                if (!foolingAllowed.includes(v)) {
                    badValues.push({ id: s.id, value: v, option: a });
                }
            }
        }
    }
}

console.log("стратегий: " + catalog.strategies.length);
console.log("разных опций: " + used.size);
console.log("нет в winws.exe: " + missing.length);

if (foolingAllowed) {
    console.log("допустимые fooling: " + foolingAllowed.join(", "));
    console.log("недопустимых значений: " + badValues.length);
} else {
    console.log("допустимые fooling: не найдены в exe — проверка значений пропущена");
}

if (missing.length > 0) {
    console.log("");

    for (const m of missing) {
        console.log("  " + m.name);
        console.log("     используется в: " + m.ids.slice(0, 4).join(", ") +
                    (m.ids.length > 4 ? " и ещё " + (m.ids.length - 4) : ""));
    }
}

if (badValues.length > 0) {
    console.log("");

    for (const b of badValues) {
        console.log("  " + b.id + " -> " + b.option);
        console.log("     значение «" + b.value + "» отсутствует в движке");
    }
}

process.exit(missing.length === 0 && badValues.length === 0 ? 0 : 1);

