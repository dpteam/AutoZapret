// Сгенерировать hosts-фикс для голоса Discord на «финских» серверах.
//
//   node tools/gen-discord-voice.js
//
// Зачем генератор, а не готовый список руками: диапазон конечный и
// однообразный (finland10000..finland10199), а ручной список из 200 строк
// невозможно проверить глазами и легко испортить одной опечаткой.
//
// Откуда взялся приём: у Discord голос идёт на <регион><номер>.discord.media,
// и провайдер режет часть этих адресов. Привязка имён к общему адресу
// discord.media (Cloudflare) заставляет клиента идти через рабочий узел.
// Это тот самый фикс, который применяют в сборках Flowseal; значения взяты
// из рабочего hosts с живой машины (refs/_Add4_diags/.../hosts.backup).
const fs = require("fs");
const path = require("path");

const IP = "104.25.158.178";
const FIRST = 10000;
const LAST = 10199;

const lines = [];

lines.push("### Discord - голос (RTC) на серверах региона Finland");
lines.push("#");
lines.push("# У голоса Discord адрес вида <регион><номер>.discord.media. Провайдер");
lines.push("# режет часть этих адресов, и в голосовом канале не поднимается RTC:");
lines.push("# соединение висит на «RTC connecting», хотя обычный обход работает.");
lines.push("#");
lines.push("# Привязка имён к общему адресу discord.media отправляет клиента через");
lines.push("# рабочий узел. Это не «блокировка», а подмена адреса: сервер тот же,");
lines.push("# просто вход в него другой.");
lines.push("#");
lines.push("# Если голос снова перестанет работать - скорее всего, сменился адрес");
lines.push("# " + IP + ". Проверить: nslookup discord.media 1.1.1.1");
lines.push("#");

for (let n = FIRST; n <= LAST; n++) {
    lines.push(IP + " finland" + n + ".discord.media");
}

const target = path.join(__dirname, "..", "assets", "hosts", "discord-voice.txt");

fs.writeFileSync(target, lines.join("\n") + "\n", "utf8");

console.log("готово: " + target + ", записей: " + (LAST - FIRST + 1));