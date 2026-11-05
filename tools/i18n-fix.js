// Разовая правка словаря: карточка службы.
const fs = require("fs");
const path = require("path");

const p = path.join(__dirname, "..", "web", "i18n", "en.json");
const dict = JSON.parse(fs.readFileSync(p, "utf8"));

const add = {
  "Служба Windows": "Windows service",
  "Режим службы": "Service mode",
  "Обход работает без окна: до входа в систему, после выхода из неё, на машине без монитора. Пока служба работает, кнопка включения на главной заблокирована — два перехватчика не делят драйвер":
    "The bypass runs without a window: before sign-in, after sign-out, on a machine without a monitor. While the service runs, the power button on the home screen is disabled - two interceptors cannot share the driver",
  "Установить": "Install",
  "Удалить": "Uninstall",
  "Установка требует прав администратора. Служба ставится в автозапуск и перезапускается сама при падении":
    "Installing needs administrator rights. The service is set to start automatically and restarts itself if it crashes",
  "Обход включён службой": "Bypass is on (service)",
  "Работает без окна: остановить можно только в «Настройках» → «Служба»":
    "Runs without a window: stop it in Settings -> Service",
  "СЛУЖБА": "SERVICE",
  "запускается…": "starting…",
  "останавливается…": "stopping…",
  "работает": "running",
  "остановлена": "stopped",
  "не установлена": "not installed"
};

let added = 0;
for (const key of Object.keys(add)) {
  if (dict[key]) continue;
  dict[key] = add[key];
  added++;
}

fs.writeFileSync(p, JSON.stringify(dict, null, 2), "utf8");
console.log("added: " + added);