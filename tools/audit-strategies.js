// Аудит аргументов стратегий: прогоняет КАЖДУЮ стратегию через настоящий
// движок с --dry-run и говорит, какая не запустится.
//
//   node tools/audit-strategies.js            -> обе версии
//   node tools/audit-strategies.js v2         -> только v2
//
// Почему так, а не «прочитать список флагов»: список флагов не отвечает на
// вопрос «а эта комбинация работает». `--dry-run` у winws/winws2 проверяет
// ровно то, что проверяет при старте: имена опций, значения, файлы, форматы
// портов и блобов. Это единственный честный аудит.
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const root = path.join(__dirname, "..");
const engineDir = path.join(root, "engine");

if (!fs.existsSync(path.join(engineDir, "bin", "winws.exe"))) {
  console.log("[skip] нет engine/bin/winws.exe — аудит возможен только с движком");
  process.exit(0);
}

// Плейсхолдеры — те же, что подставляет программа (EngineLocator.BuildPlaceholders)
const vars = {
  BIN: path.join(engineDir, "bin"),
  LISTS: path.join(engineDir, "lists"),
  LUA: path.join(engineDir, "lua"),
  FAKES: path.join(engineDir, "bin"),
  GAME_TCP: "12",
  GAME_UDP: "12",
  WF_TCP: "80,443,2053,2083,2087,2096,8443",
  WF_UDP: "443,19294-19344,50000-50100"
};

function substitute(arg) {
  let out = arg;
  for (const key of Object.keys(vars)) out = out.split("${" + key + "}").join(vars[key]);
  return out;
}

function load(file) {
  const json = JSON.parse(fs.readFileSync(path.join(root, "assets", "strategies", file), "utf8"));
  return json.strategies || json;
}

function buildArgs(strategy) {
  const args = [];
  for (const a of strategy.globalArgs || []) args.push(substitute(a));

  const profiles = strategy.profiles || [];
  profiles.forEach((p, i) => {
    if (i > 0) args.push("--new");
    for (const a of p.args || []) args.push(substitute(a));
  });

  return args;
}

function quote(arg) {
  // cmd /c: проще всего обернуть каждый аргумент в кавычки и не иметь дела
  // с экранированием пробелов внутри путей
  return '"' + String(arg).replace(/"/g, '\\"') + '"';
}

function audit(engine, exeName) {
  const exe = path.join(engineDir, "bin", exeName);
  const list = load(engine === "v1" ? "strategies.v1.json" : "strategies.v2.json");

  const problems = [];
  let ok = 0;

  for (const strategy of list) {
    const args = ["--dry-run", ...buildArgs(strategy)];

    // Через cmd /c, а не напрямую: winws — cygwin-бинарь, и прямой спавн
    // из Node висит без вывода. Через cmd он отвечает кодом, как из консоли.
    const line = quote(exe) + " " + args.map(quote).join(" ");

    const result = spawnSync(line, {
      cwd: path.join(engineDir, "bin"),
      encoding: "buffer",
      timeout: 30000,
      windowsHide: true,
      shell: true
    });

    const code = result.status === null ? -1 : result.status;

    if (code === 0) {
      ok++;
      continue;
    }

    const raw = Buffer.concat([result.stdout || Buffer.alloc(0), result.stderr || Buffer.alloc(0)]);
    const text = raw.toString("latin1").replace(/\u0000/g, " ").replace(/\s+/g, " ").trim();

    problems.push({
      id: strategy.id,
      name: strategy.name,
      code,
      text: text.slice(0, 400),
      args: args.join(" ")
    });
  }

  return { engine, total: list.length, ok, problems };
}

const want = (process.argv[2] || "").toLowerCase();

const targets = [];
if (!want || want === "v1") targets.push(["v1", "winws.exe"]);
if (!want || want === "v2") targets.push(["v2", "winws2.exe"]);

let failed = 0;

for (const [engine, exe] of targets) {
  const report = audit(engine, exe);

  console.log("");
  console.log("=== " + engine + " (" + exe + "): " + report.ok + " из " + report.total + " запускаются ===");

  for (const p of report.problems) {
    failed++;
    console.log("");
    console.log("  [FAIL] " + p.id + " — «" + p.name + "» (код " + p.code + ")");
    if (p.text) console.log("         движок: " + p.text);
    console.log("         аргументы: " + p.args.slice(0, 500));
  }

  if (report.problems.length === 0) console.log("  все стратегии принимаются движком");
}

console.log("");
console.log(failed === 0 ? "ИТОГ: проблем нет" : "ИТОГ: проблем — " + failed);

process.exit(failed === 0 ? 0 : 1);