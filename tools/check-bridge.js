// Проверка: каждое действие, которое страница отправляет мосту, имеет
// обработчик в AppHost.cs.
//
// Зачем. Кнопка вызывает send("что-то"), мост не знает такого действия —
// и ничего не происходит. Ни ошибки, ни сообщения: снаружи это выглядит как
// «кнопка не работает», и искать причину в C# никто не пойдёт, потому что
// симптом в HTML.
//
// Обратное (зарегистрировано, но не зовётся) — не ошибка: часть действий
// вызывается из других мест (трей, автозапуск) или оставлена для отладки.
// Поэтому такие случаи только печатаются.
//
// Запуск: node tools/check-bridge.js
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");

const js = fs.readFileSync(path.join(root, "web", "app.js"), "utf8");
const host = fs.readFileSync(path.join(root, "src", "App", "AppHost.cs"), "utf8");

const sent = new Set();
for (const m of js.matchAll(/send\(\s*"([a-zA-Z0-9._]+)"/g)) sent.add(m[1]);

// Часть кнопок подключается через bindAction(id, action, ...) — там действие
// тоже литерал, но send() в этой строке нет. Без этого правила проверка
// объявляет живой обработчик «неиспользуемым», и настоящая ошибка тонет в шуме.
for (const m of js.matchAll(/bindAction\(\s*"[^"]+"\s*,\s*"([a-zA-Z0-9._]+)"/g)) sent.add(m[1]);

const registered = new Set();
for (const m of host.matchAll(/bridge\.Register\("([^"]+)"/g)) registered.add(m[1]);

const missing = [...sent].filter((s) => !registered.has(s)).sort();
const unused = [...registered].filter((r) => !sent.has(r)).sort();

console.log("действий из страницы: " + sent.size);
console.log("обработчиков в мосте: " + registered.size);

console.log("без обработчика: " + (missing.length === 0 ? "нет" : missing.length));
missing.forEach((m) => console.log("  ! " + m));

if (unused.length > 0) {
  console.log("зарегистрировано, но страница не зовёт: " + unused.length);
  unused.forEach((u) => console.log("  - " + u));
}

// Каждое действие моста, вызванное из страницы, должно существовать.
// Обратное направление проверяется глазами: часть действий зовёт трей.
process.exit(missing.length === 0 ? 0 : 1);
