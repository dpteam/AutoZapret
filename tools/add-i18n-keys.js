// Разовая утилита: добавляет недостающие ключи перевода и проверяет сирот.
// Запуск: node tools/add-i18n-keys.js "ключ=перевод" "ключ2=перевод2"
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const dictPath = path.join(root, "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(dictPath, "utf8"));

let added = 0;

for (const arg of process.argv.slice(2)) {
  const eq = arg.indexOf("=");
  if (eq <= 0) continue;

  const key = arg.substring(0, eq);
  const value = arg.substring(eq + 1);

  if (dict[key]) continue;

  dict[key] = value;
  added++;
  console.log("added: " + key);
}

if (added > 0) {
  fs.writeFileSync(dictPath, JSON.stringify(dict, null, 2), "utf8");
}

// проверка сирот
const flat = (s) => s.replace(/\s+/g, " ");
const html = flat(fs.readFileSync(path.join(root, "web", "ui.html"), "utf8"));
const js = flat(fs.readFileSync(path.join(root, "web", "app.js"), "utf8"));

const orphans = Object.keys(dict).filter(
  (k) => !k.startsWith("_") && !html.includes(flat(k)) && !js.includes(flat(k))
);

const cyrillic = Object.keys(dict).filter((k) => !k.startsWith("_") && /[\u0400-\u04FF]/.test(dict[k]));

console.log("keys: " + Object.keys(dict).length + ", added: " + added);
console.log("orphans: " + orphans.length + (orphans.length ? " " + JSON.stringify(orphans) : ""));
console.log("untranslated: " + cyrillic.length + (cyrillic.length ? " " + JSON.stringify(cyrillic) : ""));

process.exit(orphans.length === 0 && cyrillic.length === 0 ? 0 : 1);