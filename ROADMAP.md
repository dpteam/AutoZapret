# AutoZapret — ROADMAP

> **Что это.** Один Windows-инструмент «всё-в-одном» для обхода DPI: GUI-обёртка над
> `winws.exe` (zapret 1 и zapret 2) + встроенный Telegram WS-прокси + менеджер списков
> и фейк-payload'ов. Сворачивается в трей, умеет автоподбор стратегии, обновляется сам.
>
> **Стек.** C# / **.NET Framework 4.8** / **WebView2** (UI — HTML+CSS+JS, общение через
> `chrome.webview.postMessage`), x64, WinForms-хост, `NotifyIcon` для трея.
>
> **Референс по форме проекта:** `../NFSWorldServer2` — тот же подход: без NuGet,
> без MSBuild, сборка через `build.cmd` + `csc.exe`, UI в ресурсах, `refs/` в `.gitignore`.
>
> Дата: 2026-11-04. Исследование источников: `docs/research/*.md`.

---

## 0. TL;DR

| | |
|---|---|
| **Проблема** | Все существующие обвязки над zapret — это либо `.bat`-файлы (Flowseal), либо WPF-монолит на .NET 10 (FluxRoute), либо bash для роутеров (Zapret-Manager). Для обычного пользователя Windows нет нормального GUI с треем, автоподбором и Telegram-прокси в одном окне. |
| **Решение** | `AutoZapret.exe` — один x64-бинарь на .NET 4.8 (работает даже там, где .NET 5+ не поставить), UI на WebView2, движок zapret рядом в `engine/`, прокси внутри процесса. |
| **Ключевая идея** | **Стратегии — это данные, а не код.** 22 стратегии Flowseal + 64 стратегии Zapret-Manager + автогенерируемые = один `strategies.json`. Движок (`winws`/`winws2`) — сменный. |
| **Главный технический трюк** | Запускать `winws.exe` **напрямую** с массивом аргументов (не через `.bat`), чтобы владеть процессом, PID-деревом и кодом выхода. Разбор `.bat` — только как импорт. |
| **Главная юридическая мина** | FluxRoute под **GPL-3.0** — код копировать нельзя, только идеи. zapret/zapret2/Flowseal — MIT, WinDivert — LGPLv3/GPLv2 (см. §3). |

---

## 1. Жёсткие рамки (не обсуждается без причины)

1. **.NET Framework 4.8**, x64. Не .NET 8/9/10 — приложение должно запускаться на стоковой
   Windows 10/11 без установки рантайма и на Windows 7 SP1 с обновлением.
2. **Никакого NuGet, никакого MSBuild.** Только BCL + WebView2 SDK (managed DLL из
   `lib/webview2`, как в NFSWorld2). Сборка — `build.cmd` → `csc.exe`.
3. **x64 обязателен.** `WinDivert64.sys` — единственный драйвер в бандле, 32-битной сборки
   `winws.exe` нет. Проверять `Environment.Is64BitProcess` на старте.
4. **UI — WebView2, не WinForms-контролы.** Форма — только хост и трей. Вся вёрстка в
   `web/` (`ui.html`, `app.js`, `theme.css`), вшитая ресурсами.
5. **Мост — `chrome.webview.postMessage`**, без `window.external` и без COM-скриптинга
   (в NFSWorld2 это отдельно отмечено как выстраданное решение).
6. **Данные вместо кода.** Новая стратегия = запись в JSON, а не `if (name == "ALT7")`.
7. **Никаких секретов и приватных данных в логах и на публичных экранах.**
8. **`refs/`, `engine/`, `bin/`, `data/state/` не попадают в git.**

---

## 2. Что уже есть в `refs/` (и что из этого берём)

| Репозиторий | Размер | Роль | Лицензия |
|---|---|---|---|
| `zapret-win-bundle` | 57.9 МБ | **бинарники**: `winws.exe`, `winws2.exe`, `WinDivert.dll/.sys`, `lua/`, `cygwin1.dll` | нет LICENSE-файла (zapret/zapret2 внутри — MIT) |
| `zapret-discord-youtube` | 6.0 МБ | **конфиги v1**: 22 `general*.bat`, `service.bat`, `lists/`, `bin/*.bin` | MIT (bol-van + Flowseal) |
| `snanvl-zapret2` | 6.9 МБ | **конфиги v2**: те же 22 стратегии на `winws2` + `lua/` | MIT |
| `Zapret-Manager` | 21.0 МБ | **каталог стратегий**: v1-v10, Yv01-Yv33, Dv1-Dv17, Gv1-Gv4 + логика автоподбора | MIT (проверить) |
| `IPSets-For-Bypass-in-Russia` | 13.9 МБ | **списки**: `ipset-all.txt` (33 336 CIDR), `exclude-domains.txt` (27), `hosts` (47 459 строк) | нет файла (качать по URL) |
| `tg-ws-proxy` | 3.9 МБ | **протокол прокси**: obfuscated2, AES-CTR, WS, Fake-TLS | MIT |
| `fluxroute` | 4.6 МБ | **идеи**: оркестратор, супервизия, диагностика, UX | **GPL-3.0 — только идеи!** |
| `dpi-detector` | ~1 МБ | **классификация блокировок**: тип DPI, TCP16-20, DNS-заглушки, чужие обходчики | MIT |
| `TLSpack` | 1.4 МБ | **1630 готовых фейков** `.bin` по доменам | неизвестна (см. §3.4) |
| `_Add1` | 15 КБ | **станок для фейков**: Go-утилита снятия TLS/QUIC hello | нет файла (by Ori) |

Детальные разборы: `docs/research/zapret-configs.md`, `zapret-win-bundle.md`,
`zapret-manager-ipsets.md`, `tg-ws-proxy.md`, `fluxroute.md`, `payloadgen-add1.md`.

---

## 3. Лицензии — решить до первой строки кода

### 3.1 Можно

- **Шипить** `winws.exe`, `winws2.exe`, `lua/`, `.bin`-фейки, конфиги Flowseal — всё MIT,
  достаточно приложить тексты лицензий и ссылки на апстримы.
- **Портировать `tg-ws-proxy`** на C# — MIT, с сохранением attribution.
- **Переосмыслять идеи FluxRoute** — идеи не охраняются.

### 3.2 Нельзя

- **Копировать код FluxRoute.** GPL-3.0 заразна: производный продукт обязан быть GPL.
  Если AutoZapret не GPL — из FluxRoute берём только **подходы**, не строки и не структуру
  файлов «один-в-один».
- **Модифицировать WinDivert** и шипить модифицированный бинарь. LGPLv3 требует
  возможности замены — значит `WinDivert.dll` и `WinDivert64.sys` лежат **отдельными
  файлами рядом с exe**, а не внутри ресурсов.

### 3.3 Обязательный минимум

- [ ] `licenses/zapret-MIT.txt`, `licenses/Flowseal-MIT.txt`, `licenses/WinDivert-LGPLv3.txt`,
      `licenses/tg-ws-proxy-MIT.txt` (если портировали).
- [ ] Экран «О программе»: версии zapret/zapret2/WinDivert, ссылки, кнопка «Лицензии».
- [ ] README: «это не VPN», «WinDivert может флагаться AV», «используйте на свой риск».

### 3.4 Открытый вопрос

У `TLSpack` и `_Add1` нет файла лицензии. `TLSpack` — это вывод утилиты по публичным
доменам (фактически «сетевые наблюдения»), но юридически чище **генерировать свои фейки**
через `_Add1`-подход, а `TLSpack` использовать как локальный кэш, а не как шиппимый ассет.

---

## 4. Архитектура

### 4.1 Один exe, слои внутри

```
AutoZapret.exe  (x64, WinExe)
│
├── App/            хост: MainForm (WebView2), TrayController, WebBridge, SingleInstance
├── Core/           Json, Log, Settings, Paths, AtomicFile, Result<T>
├── Zapret/         EngineLocator, StrategyStore, WinwsLauncher, WinwsSupervisor,
│                   ServiceManager, GameFilter, WinDivertControl
├── Lists/          HostlistManager, IpsetManager, ListUpdater, HostsFile
├── Payloads/       PayloadLibrary, PayloadValidator
├── Diag/           Diagnostics, ConnectivityProbe, Scoring
├── Update/         EngineUpdater, SelfUpdater
├── Proxy/          TgProxyServer, TgProtocol, AesCtr, WsServer, WsClient, FakeTls, DcTable
└── Orchestrator/   ProbeLoop, Bandit, Evolver, NetworkFingerprint
```

Сборка — **один** вызов `csc.exe` со всеми `.cs` (`bin/sources.rsp`), UI-ассеты — `/resource:`.

### 4.2 Раскладка репозитория

```
AutoZapret/
├── PROMPT.md            бриф (этот проект)
├── ROADMAP.md           ← вы здесь
├── AGENTS.md            правила для агентов и людей
├── README.md            для пользователя
├── build.cmd            сборка без MSBuild
├── run.cmd
├── .gitignore
├── docs/
│   └── research/        5 разборов источников
├── src/AutoZapret/      *.cs
├── web/                 ui.html, app.js, theme.css
├── assets/
│   ├── AutoZapret.ico
│   ├── strategies/      strategies.v1.json, strategies.v2.json
│   ├── lists/           дефолтные hostlist/ipset (маленькие, шипятся)
│   └── fakes/           отобранные .bin + fakes.json
├── lib/webview2/        SDK, качается tools\fetch-webview2.ps1
├── engine/              winws + WinDivert + lua, качается tools\fetch-engine.ps1
└── tools/               *.ps1 (UTF-8 с BOM!)
```

### 4.3 Правила слоёв

- `App/` знает про всё; `Core/`, `Zapret/`, `Lists/` — **не знают** про UI.
- UI общается с ядром **только** через `WebBridge` (один вход — `OnWebMessage`, один выход — `Send(json)`).
- Долгие операции — в `ThreadPool`, ответ — сообщением в страницу. UI-поток не блокировать.
- Любой фоновый цикл — с `CancellationToken` и явным `Stop()`, чтобы приложение закрывалось мгновенно.

---

## 5. Модель данных

### 5.1 Стратегии — JSON, аргументы — массив токенов

Принципиально: **`args` — это массив**, а не строка. Никакой склейки и повторного разбора
кавычек (в FluxRoute это отдельно отмечено как единственно верный путь — `ArgumentList`).

```json
{
  "schema": 1,
  "engine": "v1",
  "source": { "repo": "Flowseal/zapret-discord-youtube", "version": "1.10.4", "importedAt": "2026-11-04" },
  "strategies": [
    {
      "id": "flowseal.general",
      "name": "general",
      "engine": "v1",
      "targets": ["discord", "youtube"],
      "rating": { "note": "универсал: multisplit + seqovl", "recommended": true },
      "profiles": [
        {
          "name": "QUIC",
          "label": "UDP 443 (QUIC)",
          "filter": { "udp": "443", "hostlist": ["list-general", "list-general-user"] },
          "args": [
            "--filter-udp=443",
            "--hostlist=${LISTS}/list-general.txt",
            "--hostlist=${LISTS}/list-general-user.txt",
            "--hostlist-exclude=${LISTS}/list-exclude.txt",
            "--ipset-exclude=${LISTS}/ipset-exclude.txt",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=6",
            "--dpi-desync-fake-quic=${BIN}/quic_initial_www_google_com.bin"
          ]
        }
      ]
    }
  ]
}
```

**Плейсхолдеры** (подставляются в рантайме, всегда абсолютными путями):

| Плейсхолдер | Значение |
|---|---|
| `${BIN}` | `<app>/engine/bin` |
| `${LISTS}` | `<app>/engine/lists` |
| `${FAKES}` | `<app>/assets/fakes` |
| `${GAME_TCP}` / `${GAME_UDP}` | порты из `game_filter.enabled` (`1024-65535` или `12`) |
| `${WF_TCP}` / `${WF_UDP}` | базовые порты перехвата (`80,443,2053,...`) |

Глобальные аргументы (`--wf-tcp=`, `--wf-udp=`, `--new`-разделители) собираются
`WinwsLauncher` из профилей — их не дублируем в каждом профиле.

### 5.2 Настройки

`data/state/settings.json` — атомарная запись (`temp` → `File.Replace` → `.bak`), при битом
файле — `*.corrupt-<ts>` и дефолты (это выстраданное решение FluxRoute, повторяем).

```json
{
  "schema": 1,
  "engine": { "kind": "v1", "path": "engine", "version": "1.10.4" },
  "zapret": {
    "strategyId": "flowseal.general",
    "gameFilter": "tcp+udp",
    "ipsetMode": "loaded",
    "autoStart": true,
    "serviceMode": false
  },
  "proxy": { "enabled": false, "host": "127.0.0.1", "port": 1443, "secret": "dd…", "cfProxy": true },
  "ui": { "theme": "dark", "simpleMode": true, "minimizeToTray": true, "startMinimized": false },
  "updates": { "engine": true, "app": true, "lists": true, "channel": "stable" }
}
```

### 5.3 Списки

| Файл | Роль | Источник | Шипим? |
|---|---|---|---|
| `list-general.txt` | домены-цели | Flowseal | да |
| `list-general-user.txt` | цели пользователя; `!domain` = исключение | UI | создаётся |
| `list-exclude.txt` | исключения | Flowseal + `exclude-domains.txt` V3nilla (27 доменов) | да |
| `list-exclude-user.txt` | исключения пользователя | UI | создаётся |
| `list-google.txt` | YouTube/Google | Flowseal | да |
| `ipset-all.txt` | CIDR-цели | V3nilla (33 336 записей, **CRLF → нормализовать**) | качать |
| `ipset-exclude.txt` | CIDR-исключения | V3nilla `exclude.txt` (6 записей) | да |
| `list-youtube.txt` | доп. YouTube | zapret-win-bundle | да |

⚠️ Синхронизация пользовательских списков — **идемпотентная**: сравнивать множество
доменов и не перезаписывать файл, если набор тот же (иначе каждый старт затирает
комментарии пользователя — issue #77 у FluxRoute).

---

## 6. Фазы

### P0 — Фундамент `[x]`

Цель: пустое окно с WebView2, иконка в трее, настройки, логи. Никакого zapret.

- [x] `build.cmd` — обёртка над `dotnet build` (C# 7.3 / net48 / x64), автогасит запущенный exe.
- [x] `tools/fetch-webview2.ps1` — качает SDK, сам находит путь `lib/net462` в пакете.
- [x] `Program.cs`: `--selftest`, `--minimized`, `--version`, обычный запуск.
- [x] `SingleInstance` — мьютекс `Local\AutoZapret` + событие активации; второй запуск показывает первый.
- [x] `MainForm` — WebView2 `Dock=Fill`, `NavigateToString`, отключены DevTools/меню/зум/автозаполнение.
- [x] `WebBridge` — `chrome.webview.postMessage` в обе стороны, диспетчеризация по `action`.
- [x] `TrayController` — иконка + меню, **оверлей генерируется** (зелёная/жёлтая/красная точка через GDI+).
- [x] Закрытие окна = сворачивание в трей; выход — через трей или «О программе».
- [x] `Settings` + `AtomicFile` + `Paths` (portable/installed), `Normalize()`, карантин битого файла.
- [x] `Log` — файл + кольцевой буфер 1500, категории, `Secret()` для маскировки.
- [x] `Json` — свой парсер/билдер (в net48 нет System.Text.Json).
- [x] UI: 5 экранов (Главная, Списки, Логи, Настройки, О программе), тёмная тема, дебаунс логов 250 мс.
- [x] `--selftest`: 40+ проверок, пишет `selftest.txt`, восстанавливает настоящие настройки.

**DoD выполнен:** `build.cmd` без ошибок и предупреждений; `--selftest` → `RESULT: ok`;
окно открывается, закрывается в трей, трей-меню работает; `settings.json` переживает перезапуск.

---

### P1 — Движок zapret v1 `[x]`

Цель: запускать любую из 22 стратегий Flowseal напрямую, владеть процессом.

- [x] `tools/fetch-engine.ps1` — **через `git clone`** (zip блокирует антивирус), пишет `manifest.json` с SHA-256.
- [x] `EngineLocator` — проверка winws/cygwin1/WinDivert/driver, версия, плейсхолдеры, game filter.
- [x] `StrategyParser` — разбор `general*.bat`: склейка `^`, `set`-переменные, раскрытие `%~dp0`,
      токенизация с кавычками, разрез по `--new`, замена путей на `${BIN}/${LISTS}`.
- [x] `StrategyStore` — импорт всех 22 стратегий, JSON-кэш, round-trip.
- [x] `CommandLine` — **своё квотирование** (в net48 нет `ArgumentList`), по правилам MS C runtime.
- [x] `WinwsSupervisor` — запуск, надзор, `taskkill /T` для дерева, детект чужого winws.
- [x] `ZapretService` — связка для UI, ленивый импорт, кэш.
- [x] `Elevation` — `runas`-перезапуск (winws требует админа), внятное объяснение.
- [x] `ConnectivityProbe` — curl `-4` с `--write-out`, фолбэк на HttpClient.
- [x] `Baseline` — **базовый замер «что заблокировано» без обхода** + отпечаток сети.
- [x] `StrategyTester` — перебор только против заблокированных целей, рейтинг, авто-откат.

**Проверено вживую:**
- winws стартует и держится, `Stop()` убивает всё дерево (`--test-winws` → `RESULT: ok`);
- базовая линия: youtube/ytimg/discord/discord-cdn/telegram заблокированы, google/twitch работают;
- перебор нашёл рабочие стратегии: `general (ALT13)` и `general (ALT12)` — 4/5 целей.

**DoD выполнен:** стратегия выбирается в UI, winws стартует/останавливается, рейтинг строится
по реальной доступности сайтов, а не по «процесс жив».

---

### P2 — UI v1 `[~]`

Цель: одна кнопка и понятный статус. Всё остальное — вторично.

- [x] Экран **Главная**: кнопка питания, статус, селектор стратегий (22 шт. с описанием),
      игровой фильтр, состояние прокси.
- [x] Экран **Диагностика**: три шага (что заблокировано → почему → какая стратегия),
      таблица рейтинга с указанием непробитых целей, «применить лучшую».
- [x] Экран **Логи**: категории, файл, обновление, очистка, открыть папку.
- [x] Экран **Настройки**: game filter, ipset-режим, порт/секрет прокси, интерфейс, обновления.
- [x] Экран **О программе**: версии, окружение, лицензии компонентов, выход.
- [x] Глобальная тёмная тема, токены цветов, SVG-иконки.
- [x] Дебаунс логов 250 мс.
- [ ] Экран **Списки**: добавить/удалить домены, импорт файла, drag-n-drop, `!domain` = исключение.
- [ ] Скорость ↓/↑ и uptime на главной (uptime уже приходит, скорость нет).
- [ ] Подтверждение на опасные действия (удаление службы, «освободить WinDivert»).
- [ ] Простой/расширенный режим (настройка есть, UI не реагирует).

---

### P3 — Фейки и списки `[~]`

- [x] `HostlistManager` — чтение/запись пользовательских списков, **идемпотентная**
      синхронизация (тот же набор = файл не перезаписывается, комментарии сохраняются),
      `!domain` = исключение, валидация доменов и IP/CIDR.
- [x] `ListsUpdater` — ipset (V3nilla, 33 336 CIDR), домены-исключения, ipset-исключения.
      Защита: не HTML, границы размера, ≥100 валидных записей, атомарная подмена,
      **обязательные локальные диапазоны** (10/8, 192.168/16…) всегда на месте.
- [x] **CRLF → LF** для ipset (иначе winws не разбирает).
- [x] Экран **Списки**: два поля (цели/исключения), три кнопки обновления, состояние ipset.
- [x] `--update-lists` для консольной проверки.
- [ ] `PayloadLibrary` — манифест `assets/fakes/fakes.json` (файл, тип, SNI, размер, источник).
- [ ] Отбор фейков из `TLSpack` + `Flowseal/bin`, приоритет — `whitelist/*` (домены, которые DPI пропускает).
- [ ] `PayloadValidator` — проверка перед подстановкой:
      TLS: `len>=43 && b[0]==0x16 && b[5]==0x01 && be16(b[3..5])+5==len`;
      QUIC: `len==1200 && (b[0]&0x80) && !(b[0]&0x40) && b[1..5]==00000001`;
      STUN: `len>=20 && be16(b[0])%4==0 && b[4..8]==21 12 A4 42`.
- [ ] UI: выбор фейка по домену, превью размера/даты.

**Проверено:** ipset 33 336 записей / 531 КБ / без CR; winws с ним стартует;
selftest покрывает валидацию, идемпотентность и нормализацию.

---

### P4 — Диагностика и автоподбор `[~]`

- [x] `ConnectivityProbe` — curl `-4` + `--write-out`, фолбэк HttpClient, параллелизм 4.
- [x] **Контрольный замер** (`--probe`): что заблокировано БЕЗ обхода + отпечаток сети.
      Тестируем только сломанное — экономит десятки секунд на стратегию.
- [x] `StrategyTester` (`--test-all`): перебор, рейтинг 🟢/🟡/🔴, авто-откат в «выключено».
- [x] **`DeepProbe` (`--deep`): тип блокировки** (идея из `dpi-detector`, MIT):
      `OK / TIMEOUT / TLS RST / TLS ALERT / TLS SPOOF / DNS FAIL / ISP PAGE / LOCAL IP`,
      определение IP-заглушек провайдера, рекомендация семейства стратегий под тип блокировки.
      curl используется как «источник правды», чтобы не объявлять рабочий сайт заблокированным.
- [ ] Тест **TCP 16-20 КБ** (обрыв по объёму) — самый частый тип в РФ, нужен `tcp16.json`.
- [ ] Перенести замер, `--deep` и перебор в UI (сейчас только консоль).
- [ ] `Scoring`: `+20` процесс жив, `+15` стабилен, `+55 × successRate`, бонус за задержку.
- [ ] Антифлаппинг: переключение только после **2 неудач подряд**, порог 50 %.
- [ ] Диагностика (12 проверок): BFE, системный прокси, TCP timestamps, Adguard, Killer,
      Intel Connectivity, Check Point, SmartByte, `*.sys` на месте, VPN-службы,
      `hosts` с youtube.com, конфликтующие обходчики (`GoodbyeDPI`, `winws1/2`, `discordfix_zapret`).
- [ ] **`chcp 437`** для всех вызовов `netsh`/`sc`/`tasklist` + декодирование CP437.
- [ ] Диагностический ZIP: `diagnostics.txt`, `app.log`, `settings.json`, версии.
- [ ] `netsh interface tcp set global timestamps=enabled` (нужно для `fooling=ts`).

---

### P5 — Обновления `[ ]`

- [ ] `EngineUpdater`: версия из `raw.githubusercontent.com/.../.service/version.txt` →
      ZIP из GitHub Releases → staging → verify → **остановить службу и дождаться `STOPPED`** →
      backup в `engine/.rollback` → apply → rollback при ошибке.
      Никогда не перезаписывать `*-user.txt` и пользовательские списки.
- [ ] Копирование с ретраями (12 × 250 мс), при блокировке файла — `*.upd` + `File.Move`.
- [ ] `SelfUpdater`: GitHub API → при 403 fallback на Atom-ленту; проверка тега (не pre-release);
      замена через BAT-заменщик: ждать PID, `taskkill winws`, ждать `STOPPED` WinDivert, `xcopy`, запуск.
- [ ] **Проверка SHA-256** скачанного (не только лог — реальная сверка с `checksums.txt` из релиза).
- [ ] Канал `stable` / `beta`.
- [ ] Кэш проверки обновлений 1 час; ручная кнопка «Проверить сейчас».
- [ ] **GitHub Actions**: сборка на каждый push/PR + артефакты.
      - `build.yml`: `windows-latest`, `dotnet build` (net48/x64), `--selftest`, upload `AutoZapret.exe` + `WebView2Loader.dll` + `selftest.txt` как артефакт.
      - `release.yml`: на тег `v*` — собрать, приложить `AutoZapret-portable.zip` (exe + dll + assets + licenses) к GitHub Release, сгенерировать `checksums.txt` (SHA-256).
      - Отдельно: `licenses/` в артефакте обязателен (MIT/LGPL требования).
      - Кэш `lib/webview2` между прогонами, чтобы не качать SDK каждый раз.

**DoD:** обновление движка и приложения проходит без «файл занят» и без потери настроек;
отмена UAC = честная ошибка, а не «успех».

---

### P6 — Telegram WS Proxy `[ ]`

Цель: замена `tg-ws-proxy` (Python) на встроенный C# — без внешнего процесса.

- [ ] `WsServer` — **руками** на `TcpListener`: чтение запроса, ответ `101`, ручной кодек кадров.
      `System.Net.WebSockets` в BCL — только клиент, серверной части нет.
- [ ] `TgProtocol`: obfuscated2 handshake — `key = SHA256(preinit[8..40] || secret)`,
      `iv = preinit[40..56]`, `AES-256-CTR`, тег в `[56..60]`, `dc = |int16 LE [60..62]|`, media = отрицательный.
- [ ] `AesCtr` — свой CTR поверх AES-ECB, **128-битный big-endian инкремент счётчика**
      (не по младшим 32 битам!), шифрование блоками по 4-64 КБ.
- [ ] Обратный handshake: `key = SHA256(reverse(preinit[8..56])[0..32] || secret)`, пропуск 64 байт keystream.
- [ ] `TgPacketSplitter`: abridged (`0x7F`/`0xFF` + int24 слов), intermediate/padded (int32 LE & `0x7FFFFFFF`).
- [ ] `WsClient` к Telegram: `GET /apiws`, `Upgrade`, `Sec-WebSocket-Protocol: binary`, маскирование исходящих.
- [ ] `DcTable`: DC 1-5 + 203, IP по умолчанию, `kws{N}.web.telegram.org`, media → `kws{N}-1`.
- [ ] Cloudflare-фронты: свой домен, Worker, fallback-список; cooldown 30/60 с, «предпочтительный маршрут».
- [ ] **Детектор «не релеит»**: клиент отправил, Telegram молчит 10 с → рвать сессию
      (Telegram переподключится сам другим маршрутом).
- [ ] `tg://proxy?server=…&port=…&secret=dd…` — генерация ссылки + кнопка «Открыть в Telegram».
- [ ] UI: вкл/выкл, порт, секрет (регенерация), статус соединений, «скопировать ссылку».

**DoD:** Telegram Desktop работает через наш прокси; при недоступности WS — падение на TCP-fallback;
при смерти маршрута сессия восстанавливается без вмешательства.

**Не в v1:** HTTP/2-мультиплексирование (`/api`) и Fake-TLS (`ee`-секрет) — см. `docs/research/tg-ws-proxy.md` §10.

---

### P7 — zapret2 (`winws2`) `[ ]`

- [ ] `strategies.v2.json` — импорт из `snanvl-zapret2` тем же парсером, но с другим синтаксисом:
      `--wf-tcp-out/--wf-udp-out`, `--lua-init=@lua/*.lua`, `--name=`, `--payload=`, `--lua-desync=func:k=v`.
- [ ] Таблица перевода v1→v2 (готова в `docs/research/zapret-configs.md` §1.5) — для миграции
      пользовательских стратегий.
- [ ] `EngineLocator` учится выбирать движок; `winws2.exe` требует `lua/` рядом.
- [ ] UI: переключатель движка v1/v2 с пояснением, чем отличаются.

**DoD:** обе линейки запускаются из одного GUI; пользователь может держать обе установленными.

---

### P8 — Оркестратор (то, что FluxRoute называет «ИИ») `[ ]`

Явно фиксируем: **никаких LLM, API-ключей и промптов.** Только статистика.

- [ ] `NetworkFingerprint` — хэш сети (шлюз, DNS-суффикс, SSID, публичный IP-класс);
      `Generation`-счётчик, чтобы ловить «ушла и вернулась».
- [ ] `Bandit` — Thompson Sampling (Beta через Gamma-сэмплер), `explorationPermil=100`,
      ключ `(strategyId, networkHash)`, вес успеха **дробный** (`score/100`).
- [ ] `Evolver` — кроссовер + мутации над геномом (`desync`, `split-pos`, `ttl`, `fake-tls-mod`,
      `repeats`), Wilson lower bound для ранжирования, лимит `maxEvolved=24`, GC худших.
- [ ] `ProbeLoop` — интервал 20 мин, `2` неудачи подряд → переключение, мьютекс на цикл,
      разделение `*Async` / `*CoreAsync` (SemaphoreSlim **не реентрантный**).
- [ ] Сеть сменилась во время пробы → пробу **выбросить** (сравнивать hash **и** Generation).
- [ ] UI: режим `Вручную` / `Автовыбор`, история проб, рейтинг стратегий по текущей сети.

**DoD:** при смене сети (дом → мобильный интернет) обход сам находит рабочую стратегию
за разумное время и не флапает.

---

### P9 — Полировка `[ ]`

- [ ] **Обновление hosts** — `info.dns.malw.link/hosts` + `dns.geohide.ru` (объединение,
      дедуп, бэкап оригинала, атомарная подмена, кнопка «восстановить»).
- [ ] Пресеты по процессам: появился `Discord.exe` → применить «Discord»-стратегию и game filter,
      закрылся → вернуть предыдущую (опрос раз в 3 с).
- [ ] Автозапуск: `HKCU\...\Run` **или** Task Scheduler (`RunLevel.Highest`, задержка 30 с) — второй надёжнее.
- [ ] Уведомления в трее на события (стратегия переключена, обновление, ошибка).
- [ ] Локализация RU/EN (файлы `web/i18n/*.json`).
- [ ] Инсталлятор (Inno Setup) + portable-zip.
- [ ] Подпись кода (EV / Azure Trusted Signing) — снимает SmartScreen.
- [ ] «Снять свежие фейки» — вызов `payloadgen` (см. `docs/research/payloadgen-add1.md` §5.3, вариант A).

---

### P10 — СВОЙ АНАЛОГ ZAPRET `[ ]` ⭐ задача со звёздочкой, самый конец

> Это «попробуем», а не обещание. Делается **только после** того, как P0-P9 закрыты и
> продукт живёт. Смысл: перестать зависеть от `winws.exe` (cygwin-бинарь под MIT,
> но чужой) и получить полностью свой движок — с сохранением совместимости с его конфигами.

**Почему это вообще возможно.** `winws.exe` — это `nfqws` для Windows: перехват через
WinDivert + модификация пакетов. Вся «магия» — не в перехвате, а в **наборе desync-техник**,
которые описаны в `lua/zapret-antidpi.lua` (27 функций) и в help-тексте exe (113 опций).
Перехват WinDivert доступен из C# через P/Invoke (`WinDivertOpen/Recv/Send/Close`) —
`WinDivert.dll` даёт плоский C API, никакого cygwin не нужно.

#### Этапы

- [ ] **P10.1 — WinDivert-слой на C#.** P/Invoke над `WinDivert.dll`:
      `WinDivertOpen(filter, layer, priority, flags)`, `WinDivertRecv`, `WinDivertSend`,
      `WinDivertHelperParsePacket`, `WinDivertHelperCalcChecksums`.
      Свой слой `NETWORK`/`NETWORK_FORWARD`, очередь пакетов, реинжект.
      Цель: перехватить TCP/443 и вернуть пакет без изменений — байт-в-байт.
- [ ] **P10.2 — Разбор пакетов.** IPv4/IPv6, TCP (seq/ack/flags/options/timestamps),
      UDP, определение L7 по первым байтам (`tls_client_hello`, `http_req`, `quic_initial`,
      `stun`, `discord_ip_discovery`). Аналог `--payload=` из winws2.
- [ ] **P10.3 — Desync-техники, порт с lua.** По одной, с тестом на каждом шаге:
      `fake` (подмена пейлоада + свои seq/ack/ts), `fakedsplit`, `multisplit` (с `seqovl`),
      `multidisorder`, `hostfakesplit`, `syndata`, `oob`, `udplen`, `wsize`.
      Источник семантики — `docs/research/zapret-win-bundle.md` §4.4 (разбор `zapret-antidpi.lua`).
- [ ] **P10.4 — Fooling.** `badseq` (+increment), `badsum`, `md5sig`, `ts`, `hopbyhop`,
      `datanoack`. Проверка: DPI должен отбросить фейк, а получатель — принять настоящий пакет.
- [ ] **P10.5 — Совместимость конфигов.** Парсер аргументов `winws`/`winws2` → внутренняя
      модель. Цель: **тот же `strategies.json`, что в P1/P7**, но исполняется нашим движком.
      Пользователь переключает «движок: встроенный / zapret» и видит те же стратегии.
- [ ] **P10.6 — Производительность.** Фильтрация в ядре через `--wf-raw-part`-эквивалент
      (WinDivert-фильтр, а не разбор всего в user mode); zero-copy буферы; цель — не хуже
      `winws` на 1 Гбит/с.
- [ ] **P10.7 — Свои фичи, которых у zapret нет.** Профили по процессам на уровне движка,
      горячая смена стратегии без перезапуска, статистика по соединениям, «объясни, почему
      пакет изменён» для отладки.

#### Что взять из zapret как спецификацию

| Артефакт | Что даёт |
|---|---|
| help-текст `winws.exe` (113 опций) | полный список параметров и их смысл |
| `lua/zapret-antidpi.lua` (51 866 байт, 27 функций) | точная семантика каждой техники |
| `lua/zapret-lib.lua` (93 714 байт) | работа с пакетами, seq/ack, checksums |
| `lua/zapret-auto.lua` | детекторы блокировок, автоподбор |
| `lua/zapret-obfs.lua` | обфускация (wireguard, udp2icmp, synhide) |
| `blockcheck2.sh` | как проверять, что техника реально работает |
| `docs/readme.md` zapret | семантика `--dpi-desync-*`, MTU, ограничения |

#### Риски

- **Юридически чисто:** MIT позволяет изучать и переписывать; свои строки — свои.
  Но `cygwin1.dll` (LGPLv3) и утилиты cygwin (GPL) — **не тащить**; цель как раз избавиться от них.
- **Технически:** desync — это гонка с DPI. То, что работает сегодня, может умереть завтра.
  Свой движок обязан **быстро обновляться**, иначе он хуже чужого.
- **WinDivert остаётся** (LGPLv3) — от него не уйти, он и есть механизм перехвата.
- **Не блокирует релизы.** Если P10 не взлетит — продукт остаётся GUI над `winws`, и это нормально.

---

### P11 — Mihomo / Xray: полноценный VPN `[ ]` ⭐ задача со звёздочкой, самый конец

> Смысл: AutoZapret перестаёт быть «только обходом DPI» и становится комбайном
> в духе **Hiddify** и **Happ** — один интерфейс, несколько движков, каждый для своей задачи.
> Делается **после** P10, когда свой движок уже есть.

**Зачем это вообще нужно рядом с zapret.** Обход DPI не спасает, когда:
- блокировка по IP (zapret прямо об этом пишет: «не VPN и не помогает при блокировке по IP»);
- нужен выход в другой стране (стриминг, региональные сервисы);
- провайдер режет по SNI целиком, и подделка hello уже не проходит;
- нужен стабильный туннель для конкретного приложения, а не «всё подряд».

Тогда нужен именно прокси/VPN, а не desync. Комбайн даёт выбор: **desync там, где он
работает (быстро, без сервера), туннель там, где без него никак.**

#### P11.1 — Mihomo (Clash Meta)

- [ ] Скачивание и версионирование `mihomo.exe` (x64), проверка SHA-256.
- [ ] Генерация `config.yaml` из подписок: `proxies`, `proxy-groups`, `rules`,
      `dns` (DoH), `tun` (system proxy vs TUN).
- [ ] Подписки: загрузка по URL, парсинг base64-списка `vmess://`/`vless://`/`ss://`/`trojan://`,
      конвертация в формат Clash (`proxies:`), автообновление по расписанию.
- [ ] Управление: `external-controller` (REST API на 127.0.0.1:9090) — выбор узла,
      задержки, переключение группы, режим `rule`/`global`/`direct`.
- [ ] TUN-режим требует прав администратора и драйвера (wintun) — отдельный сценарий
      повышения прав, как у WinDivert.
- [ ] UI: список узлов с пингом, группы, «выбрать лучший по задержке».

#### P11.2 — Xray (v2ray-core)

- [ ] Скачивание `xray.exe`, проверка SHA-256.
- [ ] Генерация `config.json`: `inbounds` (socks/http), `outbounds` (vless/vmess/trojan/shadowsocks),
      `routing` (домены/IP → прямо или через прокси), `dns`.
- [ ] Поддержка `vless://` с `reality`, `xtls-rprx-vision`, `ws`, `grpc`, `httpupgrade` —
      это то, что чаще всего дают в подписках.
- [ ] Подписки: тот же загрузчик, что у Mihomo (общий слой `SubscriptionStore`).
- [ ] Гео-файлы `geoip.dat` / `geosite.dat` — скачивание и обновление.

#### P11.3 — Общая обвязка (делает комбайн комбайном)

- [ ] `RoutingPolicy` — **одна** таблица правил на все движки:
      «этот домен → через zapret», «этот → через Mihomo», «этот → напрямую»,
      «этот процесс → всегда туннель».
- [ ] `EngineKind { Zapret1, Zapret2, Builtin, Mihomo, Xray }` — единый интерфейс
      `IProtectionEngine { Start, Stop, Status, ApplyPolicy }`.
- [ ] Автовыбор движка: проба показала, что desync не помогает → предложить туннель.
      И наоборот: туннель не нужен, если desync справляется (экономия трафика и батареи).
- [ ] Один экран «Подключение»: тумблер, движок, профиль/узел, статус, скорость.
- [ ] Никаких конфликтов: одновременно активен **один** движок; переключение = стоп → старт.
- [ ] Импорт подписок из Hiddify/Happ/v2rayN (форматы `vless://`, `vmess://`, `clash://`).

#### Риски

- **Юридически:** Mihomo (GPL-3.0), Xray (MPL-2.0) — отдельные процессы, не линкуемся.
  Шипить можно, но тексты лицензий и source offer обязательны. GPL-3.0 у Mihomo —
  значит **нельзя** встраивать его код в наш exe, только запускать отдельным процессом.
- **Технически:** TUN-режим = драйвер + маршруты + DNS, и это конфликтует с WinDivert.
  Нужна аккуратность: пока активен туннель, zapret должен быть выключен.
- **Продуктово:** комбайн легко превращается в «настройки на 200 полей». Держать
  простой режим: одна кнопка + выбор «обход / туннель / напрямую».
- **Не блокирует релизы.** Это отдельная ветка продукта, не обязательство.

---

## 7. Бэклог идей из `refs/` (что взять, что нет)

### 7.1 Берём

| Идея | Откуда | Фаза | Почему |
|---|---|---|---|
| Прямой запуск winws + разбор BAT | FluxRoute | P1 | Владение процессом |
| Каталог стратегий как данные | Zapret-Manager (v1-v10, Yv01-33, Dv1-17, Gv1-4) | P1 | 64 готовых рецепта |
| Три слота стратегии (основная / YouTube / Discord / Игры) | Zapret-Manager | P2 | Меняешь YouTube, не трогая основное |
| Контрольный замер без обхода | Zapret-Manager | P4 | Иначе рейтинг врёт |
| `chcp 437` для netsh/sc | FluxRoute | P4 | Русская Windows |
| Проверка kernel-службы WinDivert | FluxRoute | P1 | У драйвера нет процесса |
| Идемпотентная запись хостлистов | FluxRoute | P3 | Не терять правки |
| Генерируемые оверлеи иконки трея | FluxRoute | P0 | Без 3 наборов ассетов |
| Дебаунс логов 250 мс | FluxRoute | P2 | Иначе UI умирает |
| Пресеты по процессам | FluxRoute | P9 | Дешёвая магия для геймеров |
| Thompson Sampling вместо «перебора» | FluxRoute | P8 | Меньше проб, быстрее сходится |
| Wilson lower bound | FluxRoute | P8 | Не даёт 1 пробе выиграть |
| «Не релеит 10 с → рвать сессию» | FluxRoute / tg-ws-proxy | P6 | Само переподключится |
| Библиотека фейков по доменам | TLSpack | P3 | Готовые 1630 `.bin` |
| `whitelist` домены как SNI для фейков | TLSpack / Zapret-Manager (`host=ozon.ru`) | P3 | Самая рабочая тактика |
| Станок для фейков | `_Add1` | P9 | Не устаревать |
| Hosts-апдейтер | IPSets + PROMPT | P9 | Доп. слой обхода |

### 7.2 Не берём (сознательно)

| Идея | Почему нет |
|---|---|
| Копирование кода FluxRoute | GPL-3.0 |
| God-object ViewModel на 100 КБ | Не масштабируется |
| `MainViewModel` + 22 зависимости руками | У нас WebBridge, а не MVVM-граф |
| `--insecure` в проверках доступности | Маскирует TLS-подмену; проверять по-настоящему |
| «ИИ» с LLM/API-ключами | Нет задачи, которую это решает лучше статистики |
| Батч-тест всех 64 стратегий «по умолчанию» | 12+ минут; только по запросу |
| Свой `blockcheck` на первом этапе | Сначала используем `engine/blockcheck`, потом свой |

---

## 8. Риски и грабли (заполняется по мере наступления)

| Риск / грабля | Решение |
|---|---|
| `WinDivert64.sys` детектится AV как `RiskTool`/`HackTool` | Не лечится подписью; честный раздел в README + предложение исключения Defender по согласию |
| `sc stop` возвращается раньше выгрузки драйвера | Polling до `STOPPED` + пауза 1500 мс |
| Файлы движка заняты при обновлении | 12 попыток × 250 мс, затем `*.upd` + `File.Move` |
| Остановка `winws`, запущенного через `cmd/start` | Убивать **все** `winws.exe`, не только свои PID |
| Русская Windows ломает парсинг `netsh`/`sc` | `cmd /c chcp 437` + CP437 |
| `ipset-all.txt` с CRLF | Нормализовать в LF при скачивании |
| Cloudflare целиком в `ipset-all.txt` | Обязательный `--ipset-exclude`, предупреждение в UI |
| Провайдер требует `fooling=ts`, а timestamps выключены | Проверять и включать `netsh … timestamps=enabled` |
| Сеть сменилась во время пробы | Выбросить пробу (hash + Generation) |
| `SemaphoreSlim` не реентрантный | Разделять `*Async` / `*CoreAsync` |
| `!domain` в hostlist | Это **исключение**, а не цель |
| Битый `settings.json` | Атомарная запись + `.bak` + `*.corrupt-<ts>` |
| Win7 без ESU | Отдельная ветка `engine/win7` |
| Secure Boot на Win8/10 | Документировать; `WinDivert` подписан, но политика может мешать |
| 32-битная Windows | Отказ на старте с внятным сообщением |
| SmartScreen на каждом билде | EV-сертификат или Trusted Signing |
| `TLSpack` без лицензии | Не шипить; генерировать свои через `_Add1`-подход |

---

## 9. Открытые вопросы (нужны ответы до P1)

1. **Тулчейн сборки.** Стоковый `csc.exe` из Framework 4.8 = **C# 5** (нет `?.`, `$"..."`,
   `nameof`, кортежей; `async/await` есть). Либо `dotnet build` с `LangVersion=7.3` и
   `TargetFramework=net48` (SDK 9.0.320 уже стоит) — тогда современный синтаксис, но
   сборка требует SDK. **Что выбираем?**
2. **Движок: v1, v2 или оба?** От этого зависит, делать ли P7 сразу после P1.
3. **Бинарники шипим или качаем?** `engine/` — 58 МБ. Шипить в инсталлятор (офлайн-готовность)
   или качать при первом запуске (тонкий установщик)?
4. **Прокси: порт по умолчанию.** `1443` (как у Flowseal) или `1080`/`8080`?
5. **Прокси: Fake-TLS (`ee`-секрет) нужен?** В `tg-ws-proxy` GUI его не отдаёт, только CLI.
6. **Оркестратор (P8) — нужен в v1.0?** Это самая дорогая фича после прокси.
7. **Язык UI:** только RU, или RU+EN сразу?
8. **Имя/брендинг.** `AutoZapret` остаётся? Иконка — `refs/Zapret.ico`?
9. **Админ-режим:** всегда elevated или `asInvoker` + повышение на операции?
10. **Обновление hosts** (`info.dns.malw.link`) — это отдельная вкладка или часть «Списков»?

---

## 10. Порядок работ (сводно)

```
P0 Фундамент ──► P1 Движок v1 ──► P2 UI v1 ──► P3 Фейки/списки ──► P4 Диагностика/автоподбор
                                                     │
                                                     ├──► P5 Обновления
                                                     ├──► P6 TG Proxy
                                                     ├──► P7 zapret2
                                                     ├──► P8 Оркестратор
                                                     ├──► P9 Полировка
                                                     ├──► P10 ⭐ свой движок
                                                     └──► P11 ⭐ Mihomo / Xray (комбайн)
```

**Минимально полезный релиз (MVP) = P0 + P1 + P2 + P4.** Дальше — по одной фиче.
P10 и P11 — только когда всё остальное работает и живёт своей жизнью.
