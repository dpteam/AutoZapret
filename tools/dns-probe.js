// Проверка локального DNS: послать A-запрос и напечатать ответ.
// Запуск: node tools/dns-probe.js [port]
const dgram = require("dgram");

const port = parseInt(process.argv[2] || "5399", 10);
const host = "www.google.com";

function buildQuery(name) {
  const parts = [0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0];
  for (const label of name.split(".")) {
    parts.push(label.length);
    for (const ch of label) parts.push(ch.charCodeAt(0));
  }
  parts.push(0, 0, 1, 0, 1); // end, QTYPE=A, QCLASS=IN
  return Buffer.from(parts);
}

const socket = dgram.createSocket("udp4");
const started = Date.now();

socket.on("message", (msg) => {
  const elapsed = Date.now() - started;
  const rcode = msg[3] & 0x0f;
  const answers = (msg[6] << 8) | msg[7];

  console.log("ответ: " + msg.length + " байт за " + elapsed + " мс, RCODE=" + rcode + ", ответов=" + answers);

  // вытащим IPv4-адреса из секции ответов
  const ips = [];
  let offset = 12;
  const questions = (msg[4] << 8) | msg[5];
  for (let i = 0; i < questions; i++) {
    while (offset < msg.length && msg[offset] !== 0) {
      if ((msg[offset] & 0xc0) === 0xc0) { offset += 2; break; }
      offset += msg[offset] + 1;
    }
    if (msg[offset] === 0) offset++;
    offset += 4;
  }

  for (let i = 0; i < answers; i++) {
    while (offset < msg.length && msg[offset] !== 0) {
      if ((msg[offset] & 0xc0) === 0xc0) { offset += 2; break; }
      offset += msg[offset] + 1;
    }
    if (msg[offset] === 0) offset++;
    const type = (msg[offset] << 8) | msg[offset + 1];
    const len = (msg[offset + 8] << 8) | msg[offset + 9];
    offset += 10;
    if (type === 1 && len === 4) ips.push(msg.slice(offset, offset + 4).join("."));
    offset += len;
  }

  console.log(ips.length > 0 ? "IP: " + ips.join(", ") : "адресов нет");
  socket.close();
  process.exit(ips.length > 0 ? 0 : 2);
});

socket.on("error", (e) => {
  console.log("ошибка сокета: " + e.message);
  process.exit(1);
});

setTimeout(() => {
  console.log("таймаут: ответа нет за 8 с");
  socket.close();
  process.exit(3);
}, 8000);

socket.send(buildQuery(host), port, "127.0.0.1");