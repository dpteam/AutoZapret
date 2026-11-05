// Собрать автономный HTML из web/ui.html для просмотра в браузере.
//
//   node tools/preview.js            -> preview.html в корне (не коммитится)
//
// Зачем: разметку и стили надо видеть глазами, а WebView2 в CI нет.
// Скрипт делает ровно то, что делает сборка: подставляет theme.css и app.js
// вместо плейсхолдеров. Ничего не меняет в исходниках.
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");

const html = fs.readFileSync(path.join(root, "web", "ui.html"), "utf8");
const theme = fs.readFileSync(path.join(root, "web", "theme.css"), "utf8");
const app = fs.readFileSync(path.join(root, "web", "app.js"), "utf8");

// Порядок замен важен: сначала тема, потом скрипт. app.js может содержать
// текст «//{{THEME}}» только в комментарии, но полагаться на это не будем.
let out = html.replace("/*{{THEME}}*/", () => theme);
out = out.replace("//{{APP}}", () => app);

const target = path.join(root, "preview.html");
fs.writeFileSync(target, out, "utf8");

console.log("preview: " + target + " (" + out.length + " байт)");
