// Разовая правка словаря: кэш DNS.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Сбросить кэш DNS": "Flush DNS cache",
  "Windows держит ответы в кэше: после правки hosts или смены DNS старые адреса продолжают работать. Сбрасывается автоматически при записи hosts и при включении системного DNS — эта кнопка на всякий случай":
    "Windows caches answers: after editing hosts or changing DNS the old addresses keep working. Flushed automatically when hosts is written and when system DNS is enabled - this button is for everything else",
  "Сбросить кэш": "Flush cache",
  "Сбрасываю…": "Flushing…"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);