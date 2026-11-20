// Перепроверка набора «YouTube — ловушки кэшей».
//
//   node tools/verify-youtube-traps.js
//   node tools/verify-youtube-traps.js --offline   (только формат, без сети)
//
// Зачем. Набор утверждает: «147 имён, и каждое ведёт в российскую сеть
// провайдера». Это утверждение со временем устареет: имена переезжают,
// адреса меняются, РКН добавляет новые. Скрипт отвечает на вопрос заново,
// а не пересказывает прошлый ответ.
//
// Что проверяется:
//   1. Формат: каждая строка это `0.0.0.0` + валидное имя кэша, без дублей.
//   2. Резолв: имя вообще существует.
//   3. Гео: адрес принадлежит российской сети (иначе это рабочий сервер,
//      и блокировать его нельзя — записи в hosts не ускоряют YouTube,
//      они убирают имя из выбора).
//
// ВАЖНО про резолверы. Cloudflare ушёл из России в 2022, поэтому 1.1.1.1
// отвечает из европейских точек. Один резолвер покажет «что видит европеец»,
// а не «что видит человек на российском провайдере». Поэтому спрашиваем
// у двух: Яндекс (77.88.8.8) и 1.1.1.1, и сравниваем.
//
// Код возврата: 0 — всё сходится, 1 — есть расхождения.
const fs = require("fs");
const path = require("path");
const dns = require("dns");
const https = require("https");

const OFFLINE = process.argv.includes("--offline");

const TARGET = path.join(__dirname, "..", "assets", "hosts", "youtube-traps.txt");

// --- 1. читаем и проверяем формат -------------------------------------------

const text = fs.readFileSync(TARGET, "utf8");
const lines = text.split("\n");

const hosts = [];
const badLines = [];
const seen = new Set();
const dupes = [];

for (const raw of lines) {
  const line = raw.trim();

  if (line.length === 0) continue;
  if (line.startsWith("#")) continue;

  const m = line.match(/^(\S+)\s+(\S+)$/);

  if (!m || m[1] !== "0.0.0.0") {
    badLines.push(line);
    continue;
  }

  const host = m[2].toLowerCase();

  if (!/^rr?\d+---[a-z0-9-]+\.(googlevideo|c\.youtube)\.com$/.test(host)) {
    badLines.push(line);
    continue;
  }

  if (seen.has(host)) {
    dupes.push(host);
    continue;
  }

  seen.add(host);
  hosts.push(host);
}

console.log("=== формат ===");
console.log("имён:            " + hosts.length);
console.log("битых строк:     " + badLines.length);
console.log("дублей:          " + dupes.length);

badLines.slice(0, 10).forEach((l) => console.log("  битая: " + l));
dupes.slice(0, 10).forEach((l) => console.log("  дубль: " + l));

if (OFFLINE) {
  console.log("");
  console.log("--offline: резолв пропущен");
  process.exit(badLines.length === 0 && dupes.length === 0 ? 0 : 1);
}

// --- 2. резолв двумя резолверами -------------------------------------------

const yandex = new dns.promises.Resolver({ timeout: 6000, tries: 2 });
yandex.setServers(["77.88.8.8"]);

const cloudflare = new dns.promises.Resolver({ timeout: 6000, tries: 2 });
cloudflare.setServers(["1.1.1.1"]);

async function resolveOne(resolver, host) {
  try {
    const a = await resolver.resolve4(host);
    return a;
  } catch (e) {
    return null;
  }
}

// --- 3. гео ----------------------------------------------------------------

function getJson(url) {
  return new Promise((resolve) => {
    https
      .get(url, { timeout: 10000 }, (res) => {
        let d = "";
        res.on("data", (c) => (d += c));
        res.on("end", () => {
          try {
            resolve(JSON.parse(d));
          } catch (e) {
            resolve(null);
          }
        });
      })
      .on("error", () => resolve(null))
      .on("timeout", () => resolve(null));
  });
}

(async () => {
  console.log("");
  console.log("=== резолв и гео ===");
  console.log("(медленно: два резолвера и geoip на каждое имя)");
  console.log("");

  const alive = [];
  const dead = [];
  const differ = [];

  let n = 0;

  for (const host of hosts) {
    n++;
    if (n % 20 === 0) console.log("  проверено " + n + " из " + hosts.length);

    const a = await resolveOne(yandex, host);
    const b = await resolveOne(cloudflare, host);

    if (!a && !b) {
      dead.push(host);
      continue;
    }

    // Расхождение — это не ошибка само по себе (у провайдеров разный
    // split-horizon), но знать о нём надо: значит ответ зависит от точки.
    const sa = (a || []).slice().sort().join(",");
    const sb = (b || []).slice().sort().join(",");

    if (sa !== sb) differ.push(host);

    alive.push({ host: host, addrs: (a || b).slice() });
  }

  // гео по уникальным адресам
  const addrs = new Set();
  for (const x of alive) for (const a of x.addrs) addrs.add(a);

  const geo = new Map();
  let g = 0;

  for (const ip of addrs) {
    g++;
    if (g % 25 === 0) console.log("  гео " + g + " из " + addrs.size);

    const info = await getJson("https://ipinfo.io/" + ip + "/json");

    if (info && info.country) {
      geo.set(ip, { country: info.country, org: info.org || "" });
    }

    // бесплатный тариф ipinfo не любит спешку
    await new Promise((r) => setTimeout(r, 60));
  }

  // классифицируем имена
  const ru = [];
  const foreign = [];
  const unknown = [];

  for (const x of alive) {
    const countries = new Set();

    for (const a of x.addrs) {
      const info = geo.get(a);
      countries.add(info ? info.country : "?");
    }

    const hasRu = countries.has("RU");
    const hasOther = [...countries].some((c) => c !== "RU" && c !== "?");

    if (hasRu && !hasOther) ru.push(x.host);
    else if (hasOther) foreign.push({ host: x.host, countries: [...countries] });
    else unknown.push(x.host);
  }

  console.log("");
  console.log("=== итог ===");
  console.log("ведут в Россию:                " + ru.length);
  console.log("не резолвятся:                 " + dead.length);
  console.log("ведут ЗА ГРАНИЦУ (нельзя!):    " + foreign.length);
  console.log("страна не определилась:        " + unknown.length);
  console.log("резолверы расходятся:          " + differ.length);

  if (foreign.length > 0) {
    console.log("");
    console.log("ПРОБЛЕМА: эти имена ведут на рабочие зарубежные серверы.");
    console.log("Блокировать их нельзя: записи в hosts убирают имя из выбора,");
    console.log("и убрать хороший сервер значит оставить клиенту только ловушки.");
    console.log("Убирайте из TRAPS в tools/gen-youtube-traps.js и пересобирайте.");

    for (const f of foreign) {
      console.log("  " + f.host + "  [" + f.countries.join(",") + "]");
    }
  }

  if (dead.length > 0) {
    console.log("");
    console.log("не резолвятся (проверьте соседние номера rr1..rr12: если rr6+");
    console.log("мертвы у всего семейства — это выдуманные имена, а не локальные):");
    dead.slice(0, 20).forEach((h) => console.log("  " + h));
    if (dead.length > 20) console.log("  ... и ещё " + (dead.length - 20));
  }

  if (unknown.length > 0) {
    console.log("");
    console.log("страна не определилась (geoip не ответил) — перепроверьте вручную:");
    unknown.slice(0, 20).forEach((h) => console.log("  " + h));
  }

  const ok = badLines.length === 0 && dupes.length === 0 && foreign.length === 0;

  console.log("");
  console.log(ok ? "RESULT: ok" : "RESULT: есть проблемы");

  process.exit(ok ? 0 : 1);
})();
