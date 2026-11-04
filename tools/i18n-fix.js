// Разовая правка словаря: карточка своего движка.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Свой движок (эксперимент)": "Own engine (experiment)",
  "Техника desync": "Desync technique",
  "Перехват TCP/443 своим кодом, без winws. Выключите обход выше — два перехватчика одновременно не работают":
    "TCP/443 interception by our own code, no winws. Turn the bypass above off - two interceptors cannot run together",
  "split — разрезать": "split - cut in two",
  "multisplit — разрезать в нескольких местах": "multisplit - cut at several points",
  "multidisorder — куски в обратном порядке": "multidisorder - segments in reverse order",
  "hostfakesplit — резать перед именем сайта": "hostfakesplit - cut before the hostname",
  "fake — подставной ClientHello": "fake - decoy ClientHello",
  "fakedsplit — подставной + разрезанный": "fakedsplit - decoy plus split",
  "Параметры": "Parameters",
  "Позиция разреза, повторы фейка, перекрытие (seqovl), секунды работы":
    "Split position, decoy repeats, overlap (seqovl), seconds to run",
  "Только показать": "Dry run only",
  "Ничего не отправлять: увидеть, что было бы сделано":
    "Send nothing: see what would be done",
  "не запускался": "not started yet",
  "Запустить": "Start",
  "Остановить": "Stop",
  "Работает…": "Running…",
  "Запускаю…": "Starting…",
  "Останавливаю…": "Stopping…"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);