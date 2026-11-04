// Разовая правка словаря: сравнение техник.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Сравнить все техники": "Compare all techniques",
  "Прогоняет каждую технику против заблокированных целей и показывает, какая реально пробивает ваш DPI":
    "Runs every technique against the blocked targets and shows which one actually beats your DPI",
  "Сравнить": "Compare",
  "Сравниваю…": "Comparing…",
  "Техника": "Technique"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);