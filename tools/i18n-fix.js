// Разовая правка словаря (десятая порция ключей).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

// этот текст живёт в C#, а не в разметке
delete dict["браузеры не найдены"];

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("removed: 1");