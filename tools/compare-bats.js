// Сравнение наборов .bat: что действительно новое, а что повтор уже известного.
//
// Зачем. «Ещё один репозиторий со стратегиями» — это обычно те же 22 строки
// Flowseal с переставленными ALT-номерами. Добавить всё подряд значит получить
// каталог из 200 почти одинаковых записей, где человек не может выбрать.
// Поэтому сравниваем по САМОЙ КОМАНДЕ winws, а не по имени файла: имена у всех
// авторов свои, а команда либо новая, либо нет.
//
// Запуск: node tools/compare-bats.js <каталог1> <каталог2> ...
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");

// ---------------------------------------------------------------- разбор .bat

function readText(file) {
    const buf = fs.readFileSync(file);

    // UTF-8 без ошибок? Тогда это UTF-8. Иначе cp1251 — так пишут старые сборки.
    const utf8 = buf.toString("utf8");

    if (!utf8.includes("\uFFFD")) return utf8;

    try {
        return new TextDecoder("windows-1251").decode(buf);
    } catch (e) {
        return utf8;
    }
}

// Склеиваем строки с `^` на конце.
//
// ПУСТАЯ строка внутри продолжения — это не конец команды, а её часть:
// в наборах Zapret-Mods и zapret2 после `^` стоит пустая строка, и cmd её
// проглатывает. Без этого правила команда обрывается на первом `^`, и все
// файлы такого вида выглядят ОДИНАКОВЫМИ — у них остаётся только `--wf-*`,
// который мы отбрасываем. Именно на этом сравнение соврало: `shizapret`
// (6 КБ, 15 профилей) «совпал» с `Minecraft (ALT 7)` (649 байт, 1 профиль).
//
// Это та же грабля, что была в StrategyParser («v2 давал 0 стратегий»):
// там она уже учтена, здесь — тоже.
function joinContinuations(text) {
    const lines = text.replace(/\r\n/g, "\n").split("\n");
    const out = [];
    let current = "";
    let continuing = false;

    for (const line of lines) {
        const trimmed = line.replace(/\s+$/, "");

        if (trimmed.length === 0) {
            if (continuing) continue;      // пустая строка внутри продолжения
            if (current.length === 0) continue;

            out.push(current);
            current = "";
            continue;
        }

        if (trimmed.endsWith("^")) {
            current += trimmed.slice(0, -1) + " ";
            continuing = true;
            continue;
        }

        current += trimmed;
        out.push(current);
        current = "";
        continuing = false;
    }

    if (current.length > 0) out.push(current);

    return out;
}

// Достаём команду winws: строку с winws.exe/winws2.exe и всё, что после неё.
function commandOf(file) {
    const text = readText(file);

    for (const line of joinContinuations(text)) {
        const trimmed = line.trim();

        if (trimmed.startsWith("::") || trimmed.startsWith("rem ")) continue;

        const m = /winws2?\.exe/i.exec(trimmed);

        if (!m) continue;

        let args = trimmed.slice(m.index + m[0].length).trim();

        // Убираем `start ""` мусор и кавычки вокруг exe
        args = args.replace(/^["']?/,"").trim();

        return { exe: m[0].toLowerCase(), args: args };
    }

    return null;
}

// Приводим команду к сравнимому виду.
//
// Убираем то, что НЕ является техникой: пути (у каждого свои), порядок
// аргументов внутри профиля (не значим), и `--wf-tcp/--wf-udp` (мы их
// достраиваем сами — в разных сборках они разные, а обход от них не зависит).
function normalize(args) {
    const tokens = tokenize(args);

    const profiles = [];
    let current = [];

    for (const t of tokens) {
        if (t === "--new") {
            profiles.push(profileKey(current));
            current = [];
            continue;
        }

        current.push(t);
    }

    profiles.push(profileKey(current));

    // Профили тоже не упорядочены по смыслу: winws применяет их по порядку,
    // но при сравнении «та же техника или нет» порядок не важен.
    return profiles.filter(p => p.length > 0).sort().join(" || ");
}

function tokenize(args) {
    const re = /"([^"]*)"|(\S+)/g;
    const tokens = [];
    let m;

    while ((m = re.exec(args)) !== null) {
        let t = m[1] !== undefined ? m[1] : m[2];

        // Кавычки внутри токена: `--hostlist="%LISTS%list.txt"` приходит как
        // один токен вместе с кавычками, а C#-парсер их снимает. Если не снять
        // здесь, каталог и .bat никогда не совпадут: у одного
        // `--hostlist=list.txt`, у другого `--hostlist=list.txt"`.
        t = t.replace(/"/g, "");

        tokens.push(t);
    }

    return tokens;
}

// Ключ одного профиля: без --wf-*, без каталогов, отсортированный.
function profileKey(tokens) {
    const kept = [];

    for (const t of tokens) {
        if (t.startsWith("--wf-tcp=") || t.startsWith("--wf-udp=")) continue;

        kept.push(stripPath(expandVars(t)));
    }

    return kept.sort().join(" ");
}

// Раскрыть переменные .bat и плейсхолдеры каталога в один канонический вид.
//
// Без этого сравнение бессмысленно: в .bat написано `%BIN%x.bin` и
// `%GameFilterTCP%`, а в каталоге — `${BIN}/x.bin` и `${GAME_TCP}`. Строки
// разные при одинаковом смысле, и все стратегии объявляются «новыми».
//
// `%GameFilter%` (одна переменная на TCP и UDP, как в Zapret-Mods) сводим к
// общему `${GAME}` вместе с `${GAME_TCP}`/`${GAME_UDP}`: для вопроса «та же
// это техника или нет» конкретные порты не важны.
function expandVars(token) {
    let t = token;

    // cmd-экранирование: `^X` означает литеральный `X`. В конфигах это
    // встречается как `--dpi-desync-fake-tls=^!` (экранирование `!`, чтобы
    // отключить delayed expansion). C#-парсер его снимает, и без этого здесь
    // каталог и .bat расходятся: у одного `^!`, у другого `!`. Из-за этого
    // вся семья FAKE TLS AUTO объявлялась «новой» при каждом сравнении.
    t = t.replace(/\^(.)/g, "$1");

    t = t.replace(/%BIN_PATH%/gi, "${BIN}/");
    t = t.replace(/%BIN%/gi, "${BIN}/");
    t = t.replace(/%LISTS%/gi, "${LISTS}/");
    t = t.replace(/%LUA%/gi, "${LUA}/");
    t = t.replace(/%GameFilterTCP%/gi, "${GAME}");
    t = t.replace(/%GameFilterUDP%/gi, "${GAME}");
    t = t.replace(/%GameFilter%/gi, "${GAME}");
    t = t.replace(/\$\{GAME_TCP\}/gi, "${GAME}");
    t = t.replace(/\$\{GAME_UDP\}/gi, "${GAME}");

    // Двойной разделитель после подстановки: `%BIN%\x` дал `${BIN}/\x`
    t = t.replace(/\$\{BIN\}\//g, "${BIN}/").replace(/\/\//g, "/");

    return t;
}

// Путь -> только имя файла: ${BIN}/x.bin и C:\build\bin\x.bin это одно и то же.
function stripPath(token) {
    let value = token.replace(/\\/g, "/");

    // Каталог может стоять после `=` у опции
    const eq = value.indexOf("=");

    if (eq > 0 && !value.slice(eq + 1).startsWith("--")) {
        const head = value.slice(0, eq + 1);
        let tail = value.slice(eq + 1);

        const slash = tail.lastIndexOf("/");
        if (slash > 0) tail = tail.slice(slash + 1);

        return head + tail;
    }

    if (value.startsWith("--")) return value;

    const slash = value.lastIndexOf("/");
    if (slash > 0) value = value.slice(slash + 1);

    return value;
}

// ---------------------------------------------------------------- каталоги

function collect(dir, acc) {
    if (!fs.existsSync(dir)) return;

    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);

        if (entry.isDirectory()) {
            if (entry.name === ".git") continue;
            collect(full, acc);
            continue;
        }

        if (!/\.bat$/i.test(entry.name)) continue;
        if (/^service/i.test(entry.name)) continue;

        acc.push(full);
    }
}

// ---------------------------------------------------------------- сравнение

const dirs = process.argv.slice(2);

// `--copy-new <каталог>` — сложить в каталог ТОЛЬКО те .bat, которых ещё нет.
//
// Зачем. Наборы на 90% состоят из уже известного: у DNS GANWO 45 стратегий,
// из них 22 — те же самые, что мы уже возим. Импортировать всё значит показать
// человеку двадцать две строки, которые отличаются только именем файла, и
// спрятать среди них те двадцать семь, ради которых набор и брали.
//
// Копируем с префиксом папки в имени: иначе `general (ALT14).bat` из чужого
// набора совпадёт по имени с нашим и при импорте затрёт его.
const copyIndex = dirs.indexOf("--copy-new");
let copyTarget = null;

if (copyIndex >= 0) {
    copyTarget = dirs[copyIndex + 1];
    dirs.splice(copyIndex, 2);
}

if (dirs.length === 0) {
    console.error("укажите каталоги: node tools/compare-bats.js <каталог> ... [--copy-new <каталог>]");
    process.exit(2);
}

// Что уже есть в каталоге программы
const known = new Map();

const cachePath = path.join(root, "assets", "strategies", "strategies.v1.json");

if (fs.existsSync(cachePath)) {
    const cache = JSON.parse(fs.readFileSync(cachePath, "utf8"));

    for (const s of cache.strategies || []) {
        // Собираем аргументы в тот же вид, что даёт разбор .bat: глобальные,
        // потом профили через --new. Иначе ключ каталога и ключ файла
        // строятся по разным правилам и никогда не совпадают — сравнение
        // объявляет «новыми» все стратегии подряд.
        const args = [];

        for (const a of s.globalArgs || []) args.push(a);

        const profiles = s.profiles || [];

        for (let i = 0; i < profiles.length; i++) {
            if (i > 0) args.push("--new");

            for (const a of profiles[i].args || []) args.push(a);
        }

        known.set(normalize(args.join(" ")), s.id);
    }
}

console.log("известных стратегий в каталоге: " + known.size);
console.log("");

const seen = new Map();
let total = 0;
let unparsed = 0;

if (copyTarget) {
    fs.mkdirSync(copyTarget, { recursive: true });
}

let copied = 0;

for (const dir of dirs) {
    const files = [];
    collect(dir, files);

    console.log("=== " + path.basename(dir) + ": " + files.length + " .bat ===");

    for (const file of files) {
        total++;

        const cmd = commandOf(file);

        if (!cmd) {
            unparsed++;
            console.log("  [нет winws] " + path.basename(file));
            continue;
        }

        const key = normalize(cmd.args);
        const name = path.basename(file, ".bat");

        if (known.has(key)) {
            console.log("  [дубль]     " + name + "  = " + known.get(key));
            continue;
        }

        if (seen.has(key)) {
            console.log("  [повтор]    " + name + "  = " + seen.get(key));
            continue;
        }

        seen.set(key, path.basename(dir) + "/" + name);
        console.log("  [НОВАЯ]     " + name + "  (" + cmd.exe + ")");

        if (copyTarget) {
            // Папка автора в имени файла: иначе `general (ALT14).bat` из чужого
            // набора совпадёт по имени с нашим и при импорте затрёт его.
            const owner = path.basename(path.dirname(file));
            const safe = (owner === path.basename(dir) ? path.basename(dir) : owner)
                .replace(/[^\w\-]+/g, "-");

            const dest = path.join(copyTarget, safe + " - " + path.basename(file));

            fs.copyFileSync(file, dest);
            copied++;
        }
    }

    console.log("");
}

if (copyTarget) {
    console.log("скопировано в " + copyTarget + ": " + copied);
    console.log("");
}

console.log("всего файлов: " + total);
console.log("без winws:   " + unparsed);
console.log("новых:       " + seen.size);

if (seen.size > 0) {
    console.log("");
    console.log("=== новые команды ===");

    let i = 0;

    for (const [key, name] of seen) {
        i++;
        console.log("");
        console.log(i + ") " + name);
        console.log("   " + key.slice(0, 400));
    }
}
