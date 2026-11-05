// Добавить перевод в web/i18n/en.json одной командой.
//
//   node tools/i18n-add.js "Служба Windows" "Windows service"
//   node tools/i18n-add.js --file pairs.json
//
// Зачем отдельный инструмент: словарь ключуется РУССКИМ текстом из разметки,
// и руками это значит «скопируй строку целиком, не ошибись в пробеле, не
// сломай кавычки». Одна лишняя буква — и перевод молча не применяется,
// а `add-i18n-keys.js` показывает missing, но не говорит, где опечатка.
//
// Существующий ключ не перезаписываем: словарь правится людьми, и затереть
// чужую формулировку из-за повторного запуска — обидно.
const fs = require("fs");
const path = require("path");

const file = path.join(__dirname, "..", "web", "i18n", "en.json");

function load() {
    return JSON.parse(fs.readFileSync(file, "utf8"));
}

function save(dict) {
    fs.writeFileSync(file, JSON.stringify(dict, null, 2), "utf8");
}

function addPair(dict, ru, en) {
    if (!ru) return 0;

    if (dict[ru] !== undefined) {
        console.log("skip (уже есть): " + ru);
        return 0;
    }

    dict[ru] = en || "";
    console.log("added: " + ru + " -> " + (en || "(пусто)"));
    return 1;
}

const argv = process.argv.slice(2);

if (argv.length === 0) {
    console.log("usage: node tools/i18n-add.js <русский> <english>");
    console.log("       node tools/i18n-add.js --file pairs.json   ({\"рус\": \"eng\"})");
    process.exit(2);
}

const dict = load();
let added = 0;

if (argv[0] === "--file") {
    const pairs = JSON.parse(fs.readFileSync(argv[1], "utf8"));

    for (const ru of Object.keys(pairs)) added += addPair(dict, ru, pairs[ru]);
} else {
    added += addPair(dict, argv[0], argv[1]);
}

if (added > 0) save(dict);

console.log("keys: " + Object.keys(dict).length + ", added: " + added);
