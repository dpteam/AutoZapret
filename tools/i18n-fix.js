// Разовая правка словаря: фильтр списков у своего движка.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Только свои цели": "Own targets only",
  "Брать списки движка (домены и подсети), как winws. Без этого движок трогает весь TCP/443 — включая банки. Для постоянной работы включайте":
    "Use the engine lists (domains and subnets) like winws. Without it the engine touches all of TCP/443 - banks included. Keep it on for permanent use",
  "Применить лучшую": "Apply the best"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);