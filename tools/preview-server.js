// Минимальный статический сервер для просмотра preview.html в браузере.
// Только для разработки: слушает 127.0.0.1 и отдаёт файлы из корня проекта.
const http = require("http");
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const port = Number(process.argv[2] || 8791);

const types = {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".txt": "text/plain; charset=utf-8"
};

http.createServer(function (req, res) {
    const url = decodeURIComponent(req.url.split("?")[0]);
    const file = path.join(root, url === "/" ? "preview.html" : url);

    // никаких выходов за пределы проекта: сервер отладочный, но дыру
    // в нём оставлять не за чем
    if (!file.startsWith(root)) { res.writeHead(403); res.end(); return; }

    fs.readFile(file, function (err, data) {
        if (err) { res.writeHead(404); res.end("not found"); return; }

        res.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream" });
        res.end(data);
    });
}).listen(port, "127.0.0.1", function () {
    console.log("preview server: http://127.0.0.1:" + port + "/preview.html");
});
