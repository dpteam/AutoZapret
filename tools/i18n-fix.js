// Разовая правка словаря: вшитые списки.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Запас в поставке": "Bundled fallback",
  "Копия всех списков вшита в программу: работает без интернета, если GitHub недоступен":
    "A copy of every list ships with the program: works without internet when GitHub is unreachable",
  "Восстановить из поставки": "Restore from bundle",
  "Восстанавливаю…": "Restoring…"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);