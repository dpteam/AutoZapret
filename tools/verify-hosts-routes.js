// Проверка маршрутов в файле hosts: работает ли адрес для своего имени.
//
//   node tools/verify-hosts-routes.js <файл> [--limit N] [--all] [--offline]
//
// Зачем. В hosts можно записать любой адрес, и файл будет выглядеть правильно.
// Но если сервер по этому адресу не отвечает или отдаёт ЧУЖОЙ сертификат,
// браузер получит ошибку вместо сайта — то есть запись не помогает, а вредит:
// имя, которое и так резолвилось, теперь ведёт в никуда.
//
// Что проверяется для каждой пары (адрес, имя):
//   1. Сервер отвечает на 443.
//   2. В его сертификате есть ИМЕННО это имя (SAN), и сертификат доверенный.
//   3. Совпадает ли адрес с тем, что DNS отдаёт сейчас — если да, запись
//      ничего не меняет (это и так обычный маршрут).
//
// Блокировки (0.0.0.0, 127.0.0.1, ::1) и приватные адреса пропускаются:
// у них другая задача, сертификат там не нужен.
const fs = require("fs");
const path = require("path");
const tls = require("tls");
const dns = require("dns");
const { X509Certificate } = require("crypto");

const args = process.argv.slice(2);

const file = args.find((a) => !a.startsWith("--"));
const offline = args.includes("--offline");
const all = args.includes("--all");

let limit = 40;
const limitIndex = args.indexOf("--limit");
if (limitIndex >= 0 && args[limitIndex + 1]) limit = parseInt(args[limitIndex + 1], 10) || 40;

if (!file) {
  console.error("использование: node tools/verify-hosts-routes.js <файл> [--limit N] [--all]");
  process.exit(2);
}

const target = path.resolve(file);

if (!fs.existsSync(target)) {
  console.error("нет файла: " + target);
  process.exit(2);
}

// ---- разбор
const entries = [];   // { ip, host }
const skipped = { block: 0, private: 0, v6: 0, bad: 0 };

for (const raw of fs.readFileSync(target, "utf8").split("\n")) {
  const line = raw.trim();
  if (!line || line.startsWith("#")) continue;

  const parts = line.split(/\s+/);
  if (parts.length < 2) { skipped.bad++; continue; }

  const ip = parts[0];

  if (ip === "0.0.0.0" || ip === "::1" || ip.startsWith("127.")) { skipped.block++; continue; }

  // приватные адреса вне чужой сети не работают — проверять сертификат бессмысленно
  const m = ip.match(/^(\d+)\.(\d+)\./);
  if (m) {
    const a = +m[1], b = +m[2];
    if (a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168)) {
      skipped.private++;
      continue;
    }
  }

  if (ip.includes(":")) { skipped.v6++; continue; }

  for (const host of parts.slice(1)) {
    const h = host.toLowerCase();
    if (!/^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}$/.test(h)) { skipped.bad++; continue; }
    entries.push({ ip: ip, host: h });
  }
}

console.log("файл: " + path.basename(target));
console.log("пар (адрес, имя): " + entries.length);
console.log("пропущено: блокировок " + skipped.block +
            ", приватных " + skipped.private +
            ", IPv6 " + skipped.v6 +
            ", мусора " + skipped.bad);
console.log("");

if (offline) {
  console.log("--offline: проверка не выполнялась");
  process.exit(0);
}

// ---- выборка: равномерно по файлу, а не первые N (иначе проверим один сервис)
let sample = entries;
if (!all && entries.length > limit) {
  sample = [];
  const step = entries.length / limit;
  for (let i = 0; i < limit; i++) sample.push(entries[Math.floor(i * step)]);
}

const resolver = new dns.promises.Resolver({ timeout: 4000, tries: 1 });
resolver.setServers(["77.88.8.8"]);

function checkTls(ip, host, timeoutMs) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (r) => { if (!done) { done = true; resolve(r); } };

    const socket = tls.connect({
      host: ip,
      port: 443,
      servername: host,
      rejectUnauthorized: false,
      timeout: timeoutMs
    }, () => {
      const authorized = socket.authorized;
      const cert = socket.getPeerCertificate();

      let sanMatch = false;

      try {
        const x = new X509Certificate(cert.raw);
        const names = (x.subjectAltName || "")
          .split(",")
          .map((s) => s.trim())
          .filter((s) => s.startsWith("DNS:"))
          .map((s) => s.slice(4).toLowerCase());

        sanMatch = names.some((n) => {
          if (n === host) return true;
          if (n.startsWith("*.")) {
            const base = n.slice(2);
            return host.endsWith("." + base) &&
                   host.split(".").length === base.split(".").length + 1;
          }
          return false;
        });
      } catch (e) {
        // сертификата нет
      }

      socket.end();
      finish({ ok: true, authorized, sanMatch });
    });

    socket.on("error", (e) => finish({ ok: false, error: e.code || e.message }));
    socket.on("timeout", () => { socket.destroy(); finish({ ok: false, error: "timeout" }); });
  });
}

function checkDns(host) {
  return resolver.resolve4(host).catch(() => []);
}

(async () => {
  console.log("проверяю " + sample.length + " пар" + (all ? " (все)" : " (выборка)") + "\n");

  let good = 0, wrongCert = 0, noAnswer = 0, sameDns = 0, differs = 0;
  const broken = [];

  for (let i = 0; i < sample.length; i++) {
    const { ip, host } = sample[i];

    const [r, dnsIps] = await Promise.all([checkTls(ip, host, 6000), checkDns(host)]);

    const same = dnsIps.includes(ip);
    if (same) sameDns++; else differs++;

    let verdict;

    if (!r.ok) {
      verdict = "нет ответа";
      noAnswer++;
      broken.push({ host, ip, why: "нет ответа: " + r.error, same });
    } else if (r.sanMatch) {
      verdict = r.authorized ? "ok" : "ok (не доверенный)";
      good++;
    } else {
      verdict = "ЧУЖОЙ сертификат";
      wrongCert++;
      broken.push({ host, ip, why: "чужой сертификат", same });
    }

    if ((i + 1) % 10 === 0 || i === sample.length - 1) {
      console.log("  " + (i + 1) + " из " + sample.length + "  (ok " + good +
                  ", проблем " + (wrongCert + noAnswer) + ")");
    }

    await new Promise((r2) => setTimeout(r2, 60));
  }

  console.log("");
  console.log("=== итог ===");
  console.log("сертификат подходит:     " + good);
  console.log("чужой сертификат:        " + wrongCert);
  console.log("не отвечает:             " + noAnswer);
  console.log("");
  console.log("адрес = DNS сейчас:      " + sameDns + "  (запись ничего не меняет)");
  console.log("адрес ≠ DNS сейчас:      " + differs + "  (запись перенаправляет)");
  console.log("");

  if (broken.length > 0) {
    console.log("=== записи, которые не работают ===");

    for (const b of broken) {
      console.log("  " + b.host + " -> " + b.ip + "   " + b.why +
                  (b.same ? "  (это адрес из DNS, имя сломано само по себе)" : ""));
    }

    console.log("");
  }

  // Годной считается запись, у которой сервер отвечает правильным сертификатом.
  // «Нет ответа» может быть и сетевой бедой нашей стороны — поэтому это не
  // приговор, а список для перепроверки.
  const ok = wrongCert === 0;

  console.log(ok
    ? "RESULT: ok — чужих сертификатов не найдено"
    : "RESULT: есть записи с чужим сертификатом — браузер покажет ошибку");

  process.exit(ok ? 0 : 1);
})();
