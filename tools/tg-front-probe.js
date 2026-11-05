// Проба: можно ли достучаться до WS-шлюза Telegram через «front»-приём
// из refs/_Add15 (подключение по IP + маскирующий SNI + настоящий Host).
//
//   node tools/tg-front-probe.js
//
// Зачем: наш прокси сейчас подключается по имени kws*.web.telegram.org,
// а это блокируется на уровне TCP. Если пройти по IP Telegram с чужим SNI
// и правильным Host — блок обходится без всякого прокси.
const tls = require("tls");
const net = require("net");

const HOST = "kws2.web.telegram.org";
const PATH = "/apiws";
const SNIS = ["sprinthost.ru", "selectel.ru", "timeweb.com", "beget.com"];

const TARGETS = [
    { name: "bot-api gw .220", ip: "149.154.167.220" },
    { name: "bot-api gw .110", ip: "149.154.166.110" },
    { name: "DC2 .51", ip: "149.154.167.51" },
    { name: "DC2 .151", ip: "149.154.167.151" },
    { name: "DC4 .91", ip: "149.154.167.91" }
];

function probe(ip, sni, timeoutMs) {
    return new Promise((resolve) => {
        const started = Date.now();
        let settled = false;

        const done = (ok, detail) => {
            if (settled) return;
            settled = true;
            resolve({ ok, detail, ms: Date.now() - started });
        };

        const socket = net.connect({ host: ip, port: 443 });
        socket.setTimeout(timeoutMs);

        socket.on("timeout", () => { done(false, "таймаут TCP"); socket.destroy(); });
        socket.on("error", (e) => done(false, e.code || e.message));

        socket.on("connect", () => {
            const secure = tls.connect({
                socket,
                servername: sni,
                rejectUnauthorized: false,
                ALPNProtocols: ["http/1.1"]
            });

            secure.setTimeout(timeoutMs);
            secure.on("timeout", () => { done(false, "таймаут TLS"); secure.destroy(); });
            secure.on("error", (e) => done(false, "TLS: " + (e.code || e.message)));

            secure.on("secureConnect", () => {
                // Host — настоящий, SNI — маскирующий
                secure.write(
                    "GET " + PATH + " HTTP/1.1\r\n" +
                    "Host: " + HOST + "\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                    "Sec-WebSocket-Version: 13\r\n" +
                    "Sec-WebSocket-Protocol: binary\r\n" +
                    "\r\n");

                let data = "";

                secure.on("data", (chunk) => {
                    data += chunk.toString("latin1").slice(0, 200);

                    if (data.indexOf("\r\n") >= 0) {
                        const status = data.split("\r\n")[0];
                        done(status.indexOf("101") >= 0, status);
                        secure.destroy();
                    }
                });
            });
        });
    });
}

(async () => {
    console.log("цель: " + HOST + PATH);
    console.log("");

    for (const target of TARGETS) {
        for (const sni of [SNIS[0], HOST]) {
            const r = await probe(target.ip, sni, 6000);
            const mark = r.ok ? "OK  " : "----";
            console.log(mark + " " + target.name.padEnd(16) + " sni=" + sni.padEnd(22) +
                        " " + String(r.ms).padStart(5) + " мс  " + r.detail);
        }
    }
})();
