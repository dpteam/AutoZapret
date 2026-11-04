// Разовая правка словаря (порция: подсветка кнопки и все списки).
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "ВЫКЛ": "OFF",
  "ВКЛ": "ON",
  "СТАРТ": "START",
  "СТОП": "STOP",
  "ОШИБКА": "ERROR",
  "Все списки движка": "All engine lists",
  "Какой список открыть": "Which list to open",
  "Здесь лежат и ваши списки, и скачиваемые: основной, Google, исключения, IP-подсети":
    "Both your lists and the downloaded ones live here: main, Google, exclusions, IP subnets",
  "стр.": "lines"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);