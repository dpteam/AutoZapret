// Разовая правка словаря (третья порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Переподключать при смене стратегии": "Reconnect on strategy change",
  "Выбрали другую стратегию — обход перезапустится на ней сразу. Старт занимает ~1.5 с и рвёт текущие соединения":
    "Picking another strategy restarts the bypass on it immediately. A start takes ~1.5 s and drops current connections"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);