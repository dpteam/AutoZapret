// Разовая правка словаря: добавляет отсутствующие переводы для новых строк UI.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "zapret 1 (winws) или zapret 2 (winws2)": "zapret 1 (winws) or zapret 2 (winws2)",
  "Убрать записи AutoZapret": "Remove AutoZapret entries",
  "Если сайт не открывается после «Обновить» — уберите наш блок. Ваши собственные записи останутся":
    "If a site stops opening after Update - remove our block. Your own entries stay",
  "Почистить": "Clean",
  "Пересоздать с нуля": "Recreate from scratch",
  "Оставить только 127.0.0.1 localhost. Помогает, когда файл уже испорчен прошлыми правками":
    "Keep only 127.0.0.1 localhost. Helps when the file is already damaged by earlier edits",
  "Пересоздать": "Recreate"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);