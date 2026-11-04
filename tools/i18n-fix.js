// Разовая правка словаря (тринадцатая: убираем ключ, которого нет в разметке).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

// текст подтверждения собирается в JS из частей, целиком его в разметке нет
for (const key of Object.keys(dict)) {
  if (key.indexOf("Системный DNS будет переключён") === 0) delete dict[key];
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("removed");