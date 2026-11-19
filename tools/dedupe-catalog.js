// Убирает из каталога стратегии, которые делают ТО ЖЕ, что уже есть.
//
//   node tools/dedupe-catalog.js          -> только показать
//   node tools/dedupe-catalog.js --write  -> переписать каталог
//
// Зачем. Наборы приходят от разных людей, и один и тот же набор техник
// встречается под разными именами: `general (ALT9)` из папки Fortnite — это
// буквально наш `flowseal.general-alt9`; в StressOzz два раздела с одинаковой
// командой. В списке это две строки, между которыми человек выбирает наугад,
// и обе дают одинаковый результат.
//
// Что НЕ считается дублем: разные наборы портов, разные списки, разные фейки.
// Сравниваем полный набор аргументов, а не «похожесть».
//
// Кого оставляем: первого по порядку каталога. Каталог отсортирован по id,
// поэтому `flowseal.*` идёт раньше `mods.*`/`ganwo.*`, и остаётся штатный
// набор, а не чужой форк.
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const target = path.join(root, "assets", "strategies", "strategies.v1.json");
const write = process.argv.includes("--write");

const catalog = JSON.parse(fs.readFileSync(target, "utf8"));

// Ключ: все аргументы подряд, без --wf-* (мы их достраиваем сами), с
// нормализованными разделителями путей.
function key(s) {
    const args = [];

    for (const a of s.globalArgs || []) args.push(a);

    const profiles = s.profiles || [];

    for (let i = 0; i < profiles.length; i++) {
        if (i > 0) args.push("--new");
        for (const a of profiles[i].args || []) args.push(a);
    }

    return args
        .filter((a) => !a.startsWith("--wf-tcp=") && !a.startsWith("--wf-udp="))
        .map((a) => a.replace(/\\/g, "/"))
        .sort()
        .join(" ");
}

const seen = new Map();
const kept = [];
const removed = [];

for (const s of catalog.strategies) {
    const k = key(s);

    if (seen.has(k)) {
        removed.push({ id: s.id, name: s.name, sameAs: seen.get(k) });
        continue;
    }

    seen.set(k, s.id);
    kept.push(s);
}

console.log("было: " + catalog.strategies.length);
console.log("дублей: " + removed.length);
console.log("останется: " + kept.length);
console.log("");

for (const r of removed) {
    console.log("  " + r.id + "  =  " + r.sameAs);
}

if (removed.length === 0) {
    console.log("нечего убирать");
    process.exit(0);
}

if (!write) {
    console.log("");
    console.log("ничего не изменено (для записи: --write)");
    process.exit(0);
}

catalog.strategies = kept;

fs.writeFileSync(target, JSON.stringify(catalog), "utf8");

console.log("");
console.log("записано: " + target);
