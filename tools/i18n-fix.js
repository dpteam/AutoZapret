// Разовая правка словаря (шестая порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "13 проверок системы": "13 system checks"
};

const drop = ["12 проверок системы"];

let added = 0, removed = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}
for (const key of drop) {
  if (!dict[key]) continue;
  delete dict[key];
  removed++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added + ", removed: " + removed);