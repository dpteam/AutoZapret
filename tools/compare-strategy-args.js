// Сравнение: аргументы стратегии из assets/strategies/*.json против
// дословной командной строки из Flowseal .bat (docs/research/zapret-configs.md).
// Запуск: node tools/compare-strategy-args.js flowseal.general
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const id = process.argv[2] || "flowseal.general";

const json = JSON.parse(fs.readFileSync(path.join(root, "assets", "strategies", "strategies.v1.json"), "utf8"));
const list = json.strategies || json;
const s = list.find((x) => x.id === id);

if (!s) {
  console.log("нет стратегии " + id);
  process.exit(1);
}

const ph = {
  BIN: "<BIN>",
  LISTS: "<LISTS>",
  LUA: "<LUA>",
  FAKES: "<FAKES>",
  GAME_TCP: "1024-65535",
  GAME_UDP: "1024-65535",
  WF_TCP: "80,443,2053,2083,2087,2096,8443",
  WF_UDP: "443,19294-19344,50000-50100"
};

function expand(a) {
  return String(a).replace(/\$\{(\w+)\}/g, (m, k) => (ph[k] !== undefined ? ph[k] : m));
}

const ours = [];
for (const g of s.globalArgs || []) ours.push(expand(g));

let first = true;
for (const p of s.profiles || []) {
  if (!first) ours.push("--new");
  first = false;
  for (const a of p.args || []) ours.push(expand(a));
}

console.log("стратегия: " + id + ", профилей: " + (s.profiles || []).length);
console.log("аргументов у нас: " + ours.length);

// нормализуем: только имена опций и их значения, пути -> <PATH>
function norm(tok) {
  let t = tok.replace(/^--/, "");
  const eq = t.indexOf("=");
  if (eq < 0) return t;

  const opt = t.slice(0, eq);
  let val = t.slice(eq + 1);

  // любые пути и файлы приводим к одному маркеру: сравниваем опции, а не пути
  if (/<BIN>|<LISTS>|<LUA>|<FAKES>/.test(val) || /\.(txt|bin|lua)$/.test(val) || /[\\/]/.test(val)) {
    val = "<P>";
  }

  return opt + "=" + val;
}

const opts = ours.map(norm);

// .bat берём из research-документа: вытаскиваем все токены строки general.bat
const doc = fs.readFileSync(path.join(root, "docs", "research", "zapret-configs.md"), "utf8").replace(/\r\n/g, "\n");
const m = doc.match(/\*\*`general\.bat`\*\*[\s\S]*?```bat\n([\s\S]*?)```/);

if (!m) {
  console.log("не нашёл general.bat в документе");
  process.exit(1);
}

const bat = m[1]
  .replace(/\^\s*\n/g, " ")
  .replace(/start "[^"]*" \/min "%BIN%winws\.exe"/, "")
  .split(/\s+/)
  .filter((t) => t.length > 0)
  .map((t) => t.replace(/"/g, ""))
  .map((t) => t.replace(/%BIN%/g, "<BIN>").replace(/%LISTS%/g, "<LISTS>"))
  .map((t) => t.replace(/%GameFilter(TCP|UDP)%/g, "1024-65535"));

const batOpts = bat.map(norm);

console.log("аргументов в .bat: " + bat.length);

// сравнение как мультимножеств
function counts(arr) {
  const c = {};
  for (const a of arr) c[a] = (c[a] || 0) + 1;
  return c;
}

const a = counts(opts);
const b = counts(batOpts);
const keys = new Set([...Object.keys(a), ...Object.keys(b)]);

let diff = 0;
for (const k of keys) {
  const ca = a[k] || 0;
  const cb = b[k] || 0;
  if (ca === cb) continue;
  diff++;
  console.log("  " + (ca > cb ? "У НАС ЛИШНИЙ/ЧАЩЕ" : "В .BAT ЕЩЁ") + ": " + k + "  (" + ca + " vs " + cb + ")");
}

console.log(diff === 0 ? "состав опций совпадает" : "расхождений: " + diff);