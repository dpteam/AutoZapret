// Проверка содержимого пакета из artifacts/.
// Запуск: node tools/check-artifact.js [путь-к-zip]
//
// ЗАЧЕМ
//   Пакет уже уезжал без engine\ — а это ровно тот случай, когда «собралось»
//   и «работает» не одно и то же.
//
//   Второй раз он уехал с ОБРАТНЫМИ слешами в именах записей zip
//   (`assets\fixes\packs.json`). Прежняя версия этой проверки их
//   нормализовала — то есть специально смотрела сквозь дефект — и потому
//   пропустила. Снаружи это «в паке исправлений пусто»: 7-Zip и WinRAR
//   считают обратный слеш частью имени файла, и вместо дерева каталогов
//   получается один файл.
//
//   Поэтому теперь разделители проверяются отдельно и строго, а состав —
//   по всему, что нужно для работы БЕЗ интернета: движок, фейки, списки,
//   hosts, исправления, стратегии, инструменты.
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const artifacts = path.join(root, "artifacts");

let file = process.argv[2];

if (!file) {
  if (!fs.existsSync(artifacts)) {
    console.log("artifacts/ нет — сначала tools\\ci-local.ps1");
    process.exit(1);
  }

  const zips = fs
    .readdirSync(artifacts)
    .filter((f) => f.endsWith(".zip"))
    .map((f) => ({ name: f, time: fs.statSync(path.join(artifacts, f)).mtimeMs }))
    .sort((a, b) => b.time - a.time);

  if (zips.length === 0) {
    console.log("в artifacts/ нет zip");
    process.exit(1);
  }

  file = path.join(artifacts, zips[0].name);
}

if (!fs.existsSync(file)) {
  console.log("нет файла: " + file);
  process.exit(1);
}

const data = fs.readFileSync(file);

// Разбор центрального каталога zip вручную: не тянем зависимости ради listing.
function listEntries(buffer) {
  const names = [];

  for (let i = 0; i < buffer.length - 4; i++) {
    if (buffer.readUInt32LE(i) !== 0x02014b50) continue;   // PK\x01\x02

    const nameLen = buffer.readUInt16LE(i + 28);
    const extraLen = buffer.readUInt16LE(i + 30);
    const commentLen = buffer.readUInt16LE(i + 32);

    const name = buffer.toString("utf8", i + 46, i + 46 + nameLen);
    names.push(name);

    i += 45 + nameLen + extraLen + commentLen;
  }

  return names;
}

const raw = listEntries(data);

let problems = 0;

// ---- 1. Разделители
//
// Это отдельная проверка, а не часть поиска: архив с обратными слешами
// выглядит целым и содержит все нужные файлы, но распаковывается в мусор.
const backslash = raw.filter((n) => n.includes("\\"));

if (backslash.length > 0) {
  console.log("[FAIL] записей с обратными слешами: " + backslash.length + " из " + raw.length);
  backslash.slice(0, 5).forEach((n) => console.log("       " + n));
  console.log("       Такой zip распакуется в один файл вместо дерева каталогов.");
  console.log("       Причина: Compress-Archive в PowerShell 5.1. Упаковывать через tools\\pack-zip.ps1.");
  problems++;
} else {
  console.log("[ok]   разделители в записях прямые (" + raw.length + " записей)");
}

// ---- 2. Топ-каталог
//
// Всё внутри одной папки: иначе распаковка рассыпает файлы по текущему
// каталогу, вперемешку с тем, что там уже лежит.
const tops = new Set(raw.map((n) => n.split("/")[0]));

if (tops.size !== 1) {
  console.log("[FAIL] в архиве несколько корней: " + Array.from(tops).slice(0, 5).join(", "));
  problems++;
} else {
  const top = Array.from(tops)[0];
  console.log("[ok]   один корневой каталог: " + top);
}

const names = raw.map((n) => n.replace(/^[^/]+\//, ""));

// ---- 3. Состав
//
// Всё, что нужно, чтобы программа работала БЕЗ интернета. Требование
// заказчика: у человека может быть плохо с сетью, и докачивать ему нечем.
const required = [
  // сама программа
  "AutoZapret.exe",
  "WebView2Loader.dll",

  // движок: без него приложение не перехватывает ничего
  "engine/bin/winws.exe",
  "engine/bin/winws2.exe",
  "engine/bin/WinDivert64.sys",
  "engine/lua/blobs.lua",

  // фейки: на них ссылаются стратегии
  "assets/fakes/fakes.json",
  "assets/fakes/stun.bin",

  // стратегии
  "assets/strategies/strategies.v1.json",
  "assets/strategies/strategies.v2.json",

  // списки: домены и подсети
  "assets/lists/ipset-all.txt",
  "assets/lists/list-general.txt",
  "assets/lists/list-google.txt",

  // hosts и исправления
  "assets/hosts/fixes.json",
  "assets/fixes/packs.json",

  // инструменты: ручное обновление движка без интернета
  "tools/fetch-engine.ps1"
];

let missing = 0;
for (const need of required) {
  if (names.includes(need)) {
    console.log("[ok]   " + need);
  } else {
    console.log("[FAIL] " + need);
    missing++;
  }
}

if (missing > 0) {
  console.log("       Отсутствует " + missing + " обязательных файлов из " + required.length + ".");
  problems++;
}

// ---- 4. Содержимое папок, где важен не один файл, а количество
const counts = {
  "assets/fakes/": names.filter((n) => n.startsWith("assets/fakes/")).length,
  "assets/lists/": names.filter((n) => n.startsWith("assets/lists/")).length,
  "engine/bin/": names.filter((n) => n.startsWith("engine/bin/")).length,
  "engine/lists/": names.filter((n) => n.startsWith("engine/lists/")).length
};

for (const key of Object.keys(counts)) {
  if (counts[key] === 0) {
    console.log("[FAIL] пусто: " + key);
    problems++;
  } else {
    console.log("[ok]   " + key + " — " + counts[key] + " файлов");
  }
}

// ---- 5. Мусор
const junk = names.filter((n) => /\.(bak|backup|upd)$/i.test(n));
console.log((junk.length === 0 ? "[ok]   " : "[FAIL] ") + "мусорных файлов: " + junk.length);
if (junk.length > 0) {
  junk.slice(0, 5).forEach((j) => console.log("       " + j));
  problems++;
}

const mb = Math.round((data.length / (1024 * 1024)) * 10) / 10;
console.log("");
console.log("файлов: " + names.length + ", размер: " + mb + " МБ");
console.log(problems === 0 ? "PACKAGE: ok" : "PACKAGE: " + problems + " problems");

process.exit(problems === 0 ? 0 : 1);
