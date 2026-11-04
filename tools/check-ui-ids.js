// Разовая проверка целостности UI: нет ли кнопок без обработчиков
// и обработчиков, ссылающихся на несуществующие id.
// Запуск: node tools\check-ui-ids.js
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const html = fs.readFileSync(path.join(root, "web", "ui.html"), "utf8");
const js = fs.readFileSync(path.join(root, "web", "app.js"), "utf8");

const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]);
const unique = [...new Set(ids)];

// id, которые собираются в js из частей: "list-" + kind + "-input", "screen-" + name
const DYNAMIC_PREFIXES = ["screen-", "list-", "btn-list-", "file-list-", "upd-"];

const unused = unique.filter((id) => {
  if (js.includes('"' + id + '"') || js.includes("'" + id + "'")) return false;

  for (const prefix of DYNAMIC_PREFIXES) {
    if (id.startsWith(prefix)) return false;   // собирается конкатенацией
  }

  return true;
});

// getElementById("...") в js, которых нет в разметке: обработчик в пустоту
const referenced = [...js.matchAll(/getElementById\("([^"]+)"\)/g)].map((m) => m[1]);
const missing = [...new Set(referenced)].filter((id) => !unique.includes(id));

console.log("id в разметке: " + unique.length);
console.log("не упомянуты в app.js: " + unused.length);
unused.forEach((u) => console.log("  - " + u));

console.log("getElementById без элемента в разметке: " + missing.length);
missing.forEach((m) => console.log("  ! " + m));

// ---------------------------------------------------------------------------
//  Состояние обхода: ядро присылает "on"/"off", интерфейс сравнивает с
//  "running". Однажды это уже разъехалось: кнопка питания не подсвечивалась
//  вообще, а подпись всегда говорила «ВЫКЛ». Проверяем, что нормализация на
//  месте и что никто не начал сравнивать сырое значение снова.
// ---------------------------------------------------------------------------
const problems = [];

if (!js.includes("function normalizeProtection")) {
  problems.push("нет normalizeProtection: состояние проводами не совпадёт с ожидаемым");
}

if (!/normalizeProtection\(s\.protection\)/.test(js)) {
  problems.push("renderState не нормализует s.protection");
}

// прямое сравнение сырого s.protection с "running" — та самая ошибка
if (/s\.protection\s*===\s*"running"/.test(js)) {
  problems.push("сравнение s.protection с \"running\" напрямую: ядро присылает \"on\"");
}

// кнопка должна получать класс и подпись из нормализованного значения
if (!js.includes('power.classList.toggle("on"')) {
  problems.push("кнопка питания не переключает класс on");
}

if (!js.includes("power-caption")) {
  problems.push("у кнопки питания нет подписи ВКЛ/ВЫКЛ");
}

console.log("состояние обхода в UI: " + (problems.length === 0 ? "ok" : problems.length + " проблем"));
problems.forEach((p) => console.log("  ! " + p));

process.exit(missing.length === 0 && problems.length === 0 ? 0 : 1);