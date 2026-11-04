// Разовая правка словаря (пятая порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Шаг 1б — видит ли драйвер трафик": "Step 1b — does the driver see traffic",
  "Сниффер WinDivert": "WinDivert sniffer",
  "Слушает TCP/443 и показывает, что реально проходит мимо DPI. Если здесь пусто — стратегии не помогут":
    "Listens on TCP/443 and shows what actually goes past the DPI. If this stays empty, strategies will not help",
  "Слушать 8 с": "Listen 8 s"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);