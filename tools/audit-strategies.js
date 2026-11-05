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
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

const root = path.join(__dirname, "..");
const engineDir = path.join(root, "engine");

if (!fs.existsSync(path.join(engineDir, "bin", "winws.exe"))) {
  console.log("[skip] нет engine/bin/winws.exe — аудит возможен только с движком");
  process.exit(0);
}

// ------------------------------------------------------------- права
//
// winws.exe/winws2.exe собраны с манифестом requireAdministrator: КАЖДЫЙ
// запуск без прав поднимает UAC. Сорок четыре стратегии — сорок четыре
// запроса, и это невыносимо.
//
// Поэтому: если мы не администратор, перезапускаем себя через один UAC
// (Start-Process -Verb RunAs выполняет весь скрипт целиком), вывод пишем в
// файл и печатаем после — иначе повышенная консоль закроется вместе с
// результатом.

function isElevated() {
  try {
    const result = spawnSync("net", ["session"], { stdio: "ignore", windowsHide: true });
    return result.status === 0;
  } catch {
    return false;
  }
}

if (!isElevated() && !process.env.AUTOZAPRET_AUDIT_ELEVATED) {
  const outDir = path.join(root, "artifacts");
  fs.mkdirSync(outDir, { recursive: true });

  const out = path.join(outDir, "audit-strategies.txt");
  const marker = "1";
  const args = process.argv.slice(2).join(" ");

  const bat = path.join(os.tmpdir(), "autozapret-audit-" + Date.now() + ".cmd");

  fs.writeFileSync(
    bat,
    "@echo off\r\n" +
    "cd /d \"" + root + "\"\r\n" +
    "set AUTOZAPRET_AUDIT_ELEVATED=" + marker + "\r\n" +
    "node \"" + __filename + "\" " + args + " > \"" + out + "\" 2>&1\r\n",
    "ascii"
  );

  console.log("нужны права администратора: winws.exe имеет манифест requireAdministrator.");
  console.log("запрашиваю ОДИН раз — дальше все 44 запуска пройдут без UAC…");

  spawnSync(
    "powershell",
    ["-NoProfile", "-Command", "Start-Process -FilePath '" + bat.replace(/'/g, "''") + "' -Verb RunAs -Wait"],
    { windowsHide: true }
  );

  try { fs.unlinkSync(bat); } catch (e) { }

  if (!fs.existsSync(out)) {
    console.log("[FAIL] повышенный запуск не выполнился (UAC отклонён?)");
    process.exit(2);
  }

  const report = fs.readFileSync(out, "utf8");
  process.stdout.write(report);

  // файл — единственный источник правды: консоль повышенного процесса
  // закрылась, и статус пришёл только текстом
  const failed = report.indexOf("ИТОГ: проблем нет") < 0;
  process.exit(failed ? 1 : 0);
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

    let code = -1;
    let text = "";

    // Коды вида 0xC0000142 (STATUS_DLL_INIT_FAILED) — это сбой запуска
    // процесса, а не отказ движка принять аргументы: при частых стартах
    // Windows иногда не может инициализировать процесс. Один-два повтора
    // отличают «аргументы плохие» от «система не дала запуститься».
    for (let attempt = 0; attempt < 3; attempt++) {
      const result = spawnSync(line, {
        cwd: path.join(engineDir, "bin"),
        encoding: "buffer",
        timeout: 30000,
        windowsHide: true,
        shell: true
      });

      code = result.status === null ? -1 : result.status;

      const raw = Buffer.concat([result.stdout || Buffer.alloc(0), result.stderr || Buffer.alloc(0)]);
      text = raw.toString("latin1").replace(/\u0000/g, " ").replace(/\s+/g, " ").trim();

      if (code === 0) break;

      const unsigned = code < 0 ? code + 0x100000000 : code;
      const transient = unsigned >= 0xC0000000;

      if (!transient) break;
    }

    if (code === 0) {
      ok++;
      continue;
    }

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

// ---- свой движок: не exe, а перевод стратегии в профили
//
// «Не алло» у своего движка — это чаще всего «стратегия разобралась в ноль
// профилей» или «все профили выброшены как неподдержанные». Проверяем, что
// каждая v1-стратегия даёт хотя бы один исполнимый профиль.
if ((!want || want === "own") && fs.existsSync(path.join(root, "bin", "AutoZapret.exe"))) {
  const exe = path.join(root, "bin", "AutoZapret.exe");

  const result = spawnSync('"' + exe + '" --own-compat', {
    cwd: root,
    encoding: "buffer",
    timeout: 120000,
    windowsHide: true,
    shell: true
  });

  const text = (result.stdout || Buffer.alloc(0)).toString("utf8");

  // отчёт пишется и в файл — читаем его, чтобы не зависеть от кодировки консоли
  let report = text;
  const file = path.join(root, "bin", "own-compat.txt");
  if (fs.existsSync(file)) report = fs.readFileSync(file, "utf8");

  const total = /исполнимых стратегий: (\d+) из (\d+)/.exec(report);

  console.log("");
  console.log("=== own (перевод стратегий в профили) ===");

  if (!total) {
    failed++;
    console.log("  [FAIL] не удалось прочитать отчёт --own-compat");
  } else {
    const usable = parseInt(total[1], 10);
    const all = parseInt(total[2], 10);

    if (usable === all) {
      console.log("  все " + all + " стратегий дают исполнимые профили");
    } else {
      failed += all - usable;
      console.log("  [FAIL] исполнимых " + usable + " из " + all + " — см. bin\\own-compat.txt");
    }
  }
}

console.log("");
console.log(failed === 0 ? "ИТОГ: проблем нет" : "ИТОГ: проблем — " + failed);

process.exit(failed === 0 ? 0 : 1);