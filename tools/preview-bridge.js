// Собирает preview-bridge.html: preview.html плюс подставной мост
// chrome.webview, который складывает отправленные сообщения в window.__sent.
//
//   node tools/preview.js && node tools/preview-bridge.js
//
// Зачем. В обычном браузере моста нет, поэтому send() уходит в пустоту:
// кнопку нажать можно, обработчик отработает, а что именно он отправил в C# —
// не видно. В WebView2 это видно в логе, в браузере — нет. С подставным мостом
// проверка становится настоящей:
//
//   document.getElementById("btn-x").click();
//   window.__sent   // [{ action: "strategy.share", id: "…" }, …]
//
// Это ловит класс ошибок, который иначе виден только руками: кнопка шлёт не то
// действие, не те поля, или не шлёт вообще ничего.
//
// Файл — артефакт сборки, в git не едет.
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const source = path.join(root, "preview.html");

if (!fs.existsSync(source)) {
    console.error("нет preview.html — сначала: node tools/preview.js");
    process.exit(1);
}

const html = fs.readFileSync(source, "utf8");

// Мост вставляем в самом начале <head>, чтобы он существовал РАНЬШЕ app.js:
// `var host = window.chrome && window.chrome.webview ? … : null` вычисляется
// один раз при загрузке скрипта. Вставив мост после, получим host === null и
// пустой __sent — то есть проверку, которая всегда «проходит».
const stub =
    "<script>window.chrome={webview:{" +
    "postMessage:function(m){window.__sent=window.__sent||[];window.__sent.push(m);}," +
    // boot() подписывается на message и вызывает addEventListener — без этих
    // двух методов страница падает на старте.
    "addEventListener:function(){},removeEventListener:function(){}}};</script>";

const at = html.indexOf("<head>");

if (at < 0) {
    console.error("в preview.html нет <head> — формат страницы изменился");
    process.exit(1);
}

const out = html.slice(0, at + "<head>".length) + stub + html.slice(at + "<head>".length);

const target = path.join(root, "preview-bridge.html");
fs.writeFileSync(target, out, "utf8");

console.log("preview-bridge: " + target + " (" + out.length + " байт)");
