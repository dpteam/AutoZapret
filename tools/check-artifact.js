// Проверка содержимого пакета из artifacts/.
// Запуск: node tools/check-artifact.js
//
// Зачем: пакет уже один раз уезжал без engine\ — а это ровно тот случай,
// когда «собралось» и «работает» не одно и то же. Проверяем по списку,
// нормализуя разделители: zip хранит их как угодно.
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const root = path.join(__dirname, "..");
const artifacts = path.join(root, "artifacts");

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

const file = path.join(artifacts, zips[0].name);
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
const names = raw.map((n) => n.replace(/\\/g, "/"));

const required = [
  "AutoZapret.exe",
  "WebView2Loader.dll",
  "assets/strategies/strategies.v1.json",
  "assets/fakes/fakes.json",
  "engine/bin/winws.exe",
  "engine/bin/WinDivert64.sys",
  "engine/lua/blobs.lua",
  "tools/fetch-engine.ps1"
];

let missing = 0;
for (const need of required) {
  if (names.includes(need)) {
    console.log("ok   " + need);
  } else {
    console.log("НЕТ  " + need);
    missing++;
  }
}

// мусор не должен попадать в пакет
const junk = names.filter((n) => /\.(bak|backup|upd)$/i.test(n));
console.log("мусорных файлов: " + junk.length);
if (junk.length > 0) junk.slice(0, 5).forEach((j) => console.log("  " + j));

const mb = Math.round((data.length / (1024 * 1024)) * 10) / 10;
console.log("файлов: " + names.length + ", размер: " + mb + " МБ");

process.exit(missing === 0 && junk.length === 0 ? 0 : 1);