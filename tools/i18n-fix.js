// Разовая правка словаря (одиннадцатая порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Убрать дубли": "Remove duplicates",
  "Редактор hosts": "hosts editor",
  "Готовые фиксы": "Ready-made fixes",
  "Пользовательские записи для hosts: Xbox, авторизация Microsoft и другие. Применяются в редактор — сохраните, когда проверите":
    "User entries for hosts: Xbox, Microsoft sign-in and others. Applied to the editor - save when you have checked them",
  "Проверить": "Check",
  "Применить": "Apply"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);