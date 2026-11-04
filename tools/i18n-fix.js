// Разовая правка словаря (седьмая порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Объяснить выбранную": "Explain selected",
  "«Объяснить» запускает одну стратегию с отладочным выводом winws и показывает, что он сделал с соединениями. Полезно, когда все стратегии дают нули":
    "\"Explain\" runs one strategy with winws debug output and shows what it did with the connections. Useful when every strategy scores zero",
  "Запускаю с отладкой…": "Starting with debug…"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);