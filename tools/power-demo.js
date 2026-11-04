// Снимок кнопки питания: собирает HTML+CSS как в приложении и рендерит
// два состояния рядом (ВЫКЛ / ВКЛ), чтобы глазами проверить подсветку.
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const theme = fs.readFileSync(path.join(root, "web", "theme.css"), "utf8");

const html = `<!doctype html>
<html><head><meta charset="utf-8">
<style>
${theme}
body { background: #0B0E18; padding: 30px; font-family: "Segoe UI", sans-serif; }
.card { max-width: 620px; }
.demo { display: flex; gap: 60px; padding: 20px 0; }
.demo > div { display: flex; flex-direction: column; align-items: center; gap: 10px; }
.label { color: #7a8299; font-size: 12px; }
</style></head>
<body>
<div class="card">
  <div class="hero">
    <button class="power" aria-pressed="false">
      <svg viewBox="0 0 24 24" width="34" height="34" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">
        <path d="M12 3.5v8" /><path d="M6.2 6.9a8 8 0 1 0 11.6 0" />
      </svg>
      <span class="power-caption">ВЫКЛ</span>
    </button>
    <div class="hero-text">
      <div class="hero-status"><span class="status-dot"></span>Обход выключен</div>
      <div class="hero-detail">Движок zapret 1 · v72.9</div>
    </div>
  </div>

  <div class="hero on">
    <button class="power on" aria-pressed="true">
      <svg viewBox="0 0 24 24" width="34" height="34" fill="currentColor" stroke="none">
        <rect x="6" y="6" width="12" height="12" rx="2"/>
      </svg>
      <span class="power-caption">ВКЛ</span>
    </button>
    <div class="hero-text">
      <div class="hero-status"><span class="status-dot on"></span>Обход включён</div>
      <div class="hero-detail">Стратегия general (ALT12) · 12 мин</div>
    </div>
  </div>
</div>
</body></html>`;

fs.writeFileSync(path.join(root, "bin", "power-demo.html"), html, "utf8");
console.log("bin/power-demo.html");