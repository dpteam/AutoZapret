// Показать строки отчёта selftest без проблем с кодировкой консоли.
const fs = require("fs");

const text = fs.readFileSync(process.argv[2] || "bin/selftest.txt", "utf8");
const re = new RegExp(process.argv[3] || "RESULT|FAIL|SKIP", "i");

text.split("\n").forEach((line) => {
    if (re.test(line)) console.log(line.trimEnd());
});