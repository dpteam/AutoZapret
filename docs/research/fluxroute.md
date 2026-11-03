# FluxRoute Desktop — техническая выжимка для AutoZapret

> Источник: `refs/fluxroute` (gitlab.com/klondike0x/fluxroute, mirror of github.com/klondike0x/FluxRoute), GPL-3.0-or-later, версия сборки 1.7.1 (`FluxRoute/FluxRoute.csproj`).
> Стек: **.NET 10 / WPF + WinForms (трей)**, MVVM (CommunityToolkit.Mvvm 8.4.2), Generic Host + MS.DI, Serilog, Polly. **WebView2 не используется** — это чистый нативный WPF, а не гибрид.

---

## 0. Главное, что надо знать до чтения

1. **«ИИ-оркестратор» — это НЕ LLM.** Никакого провайдера, модели, API-ключа и промптов в коде нет: grep по `openai|anthropic|gpt-|claude|llm|gemini|ollama` по всему репозиторию даёт совпадения только в документации про маскирование ключей (`AGENTS/03-SECURITY.md`) и в списке DoH-доменов (`FluxRoute.Core/Services/DohProviderService.cs:29-30`). «AI» = **Thompson Sampling (Beta-bandit) + генетический алгоритм над типизированным геномом стратегии + Wilson lower bound + отпечаток сети**. См. `FluxRoute.AI/Services/BanditSelector.cs`, `StrategyEvolver.cs`, `FluxRoute.AI/Math/WilsonScore.cs`.
2. **FluxRoute не запускает BAT-файлы.** Он парсит BAT Flowseal и запускает `winws.exe` напрямую, чтобы быть его родителем (`FluxRoute.Core/Services/ProfileBatLauncher.cs:16-19`).
3. **Подпись обновлений не проверяется в коде.** SHA-256 только логируется; GPG/SHA256SUMS/attestation — артефакты релиза, а не runtime-проверка (`FluxRoute.Updater/Services/UpdaterService.cs:224-225`, `AppUpdaterService.cs:440-442`).
4. **Telegram-прокси — полноценная C#-реализация MTProto→WebSocket** (obfuscated2 + AES-CTR + DC-роутинг + Cloudflare-фронты), без Python (`FluxRoute/Services/TgWsProxy*.cs`).

---

## 1. Архитектура: проекты, слои, DI, как UI общается с ядром

### 1.1 Проекты и зависимости

`FluxRoute.slnx` — 5 проектов; все на `net10.0-windows`:

| Проект | Роль | Ссылки |
|---|---|---|
| `FluxRoute` | WPF UI (Views/ViewModels/Services/Styles), `WinExe`, `UseWPF` + `UseWindowsForms` | → AI, Core, Updater (`FluxRoute/FluxRoute.csproj`) |
| `FluxRoute.Core` | Модели + сервисы (оркестратор, проверки, Zapret, DoH, моды, hostlists) | Polly, MS.Extensions.Http |
| `FluxRoute.AI` | bandit, evolver, fingerprint, registry, history, materializer | → Core |
| `FluxRoute.Updater` | автообновление engine и самого приложения | → Core |
| `FluxRoute.Core.Tests` | xUnit, ~45 файлов тестов | — |

Заявленная в `AGENTS/01-ARCHITECTURE.md:65-75` цепочка `UI → AI → Core ← Updater` совпадает с фактическими `ProjectReference`.

### 1.2 Правила слоёв (декларированные)

`AGENTS/01-ARCHITECTURE.md:19-26`:
- ViewModel не знает про View (никаких `MessageBox.Show` — на практике вместо него `CustomDialog`);
- View без бизнес-логики;
- сервис не знает про UI (никаких `Dispatcher.Invoke` внутри Core);
- все зависимости через конструктор, интерфейсы `IXxxService`.

На практике Core **не ссылается на WPF**, но UI-поток пробивается через делегаты (см. 1.4).

### 1.3 DI / хостинг

`FluxRoute/App.xaml.cs:37-43` — Generic Host:
```csharp
_host = Host.CreateDefaultBuilder(e.Args)
    .UseContentRoot(AppContext.BaseDirectory)
    .UseSerilog(...)
    .ConfigureServices(ConfigureApplicationServices)
    .Build();
await _host.StartAsync();
```

Регистрация (`App.xaml.cs:228-466`):
- **Named HttpClient'ы** с `AddStandardResilienceHandler()` (Polly: retry + circuit breaker + timeouts):
  - `Updater`: 60 c total / 30 c attempt / 3 retry / exponential + jitter (`:236-251`);
  - `UpdaterDownload`: 5 мин, `HttpClientHandler` с явным `SslProtocols = Tls12|Tls13`, `MaxAutomaticRedirections = 10`, gzip/deflate (`:260-283`) — с комментарием, что `SocketsHttpHandler` игнорирует `ServicePointManager.SecurityProtocol`;
  - `AppUpdater` 30 c, `Connectivity` 10 c (UA Chrome 120 + `Accept-Language: ru-RU`), `Doh` 8 c, `Service` 30 c.
- Сервисы — **singleton** (SettingsService, IUpdaterService, IAppUpdaterService, IConnectivityChecker, IProcessRunner, DoH-стек, TaskScheduler, ModManager, NetworkFingerprintProvider, AiStrategyRegistry, AiHistoryStore, BatMaterializer, BanditSelector, StrategyEvolver, NetworkChangeWatcher, Zapret2-стек, AntivirusExclusionService, TrayIconService).
- `MainViewModel` — **singleton**, собирается вручную через `new MainViewModel(...)` c ~22 зависимостями (`:416-462`). Это, а не `AddTransient`, как советует `AGENTS/06-DI-LIFECYCLE.md:45`.
- Пути состояния: `fluxroute-ai-strategies.json` и `fluxroute-ai-history.jsonl` кладутся рядом с файлом настроек (`:377-391`).

Логирование: Serilog file sink в `%LOCALAPPDATA%\FluxRoute\logs\fluxroute-.log`, rolling по дням, 14 файлов (`App.xaml.cs:468-488`).

### 1.4 Как UI общается с ядром (ключевой паттерн)

Core/AI-сервисы **не зависят от ViewModel**. Вместо этого в конструктор передаются делегаты-порты:

`FluxRoute.AI/Services/AiOrchestratorService.cs:19-38`:
```csharp
private readonly Func<IEnumerable<ProfileItem>> _getProfiles;
private readonly Func<ProfileItem?> _getActiveProfile;
private readonly Func<ProfileItem?, Task> _switchProfile;
private readonly Func<string> _getTargetsPath;
private readonly Func<string, int, Task> _notifyScoreUpdate;
private readonly Func<string> _engineDir;
private readonly Func<AiSettings> _aiSettings;
private readonly Func<bool> _isWinwsRunning;
private readonly Func<Task> _ensureProtectionRunning;
```
То же в классическом оркестраторе (`FluxRoute.Core/Services/OrchestratorService.cs:52-67`).

Обратная связь — событие:
```csharp
public event EventHandler<OrchestratorEventArgs>? StatusChanged; // Message, IsSwitched, NewProfile, ProbeResult
```
(`OrchestratorService.cs:5-11,50`; `AiOrchestratorService.cs:48`).

Плюсы: Core тестируется без WPF, нет циклической зависимости, легко подменить движок. Минусы: `MainViewModel` — god-object на 102 КБ, разбитый на partial-файлы:

```
MainViewModel.cs (102 KB)  MainViewModel.Process.cs (28 KB)   MainViewModel.Orchestrator.cs (90 KB)
MainViewModel.Ai.cs        MainViewModel.Diagnostics.cs (35 KB) MainViewModel.TgProxy.cs
MainViewModel.Logs.cs      MainViewModel.AutoTune.cs          MainViewModel.Updates.cs
MainViewModel.Service.cs   MainViewModel.Zapret2.cs
```
Feature-логика вынесена в отдельные VM и подмешана свойствами: `Updates`, `Service`, `Diagnostics`, `Doh`, `ModsViewModel`, `Hostlists` (`MainViewModel.cs:792-799`).

### 1.5 Навигация и состояние

Единый контракт вкладок — `FluxRoute/ViewModels/MainNavigation.cs:13-23`:
```csharp
new(0, "ГЛАВНАЯ"), new(1, "КОМПОНЕНТЫ"), new(2, "ОРКЕСТРАТОР"), new(3, "ХОСТЛИСТЫ"),
new(4, "МОДИФИКАЦИИ"), new(5, "ДИАГНОСТИКА"), new(6, "ЛОГИ"), new(7, "НАСТРОЙКИ")
```
Режим защиты как чистая политика: `ProtectionMode { Manual, Automatic }` ↔ `OrchestratorEnabled` (`FluxRoute/ViewModels/ProtectionMode.cs:15-25`), отображается как «Вручную» / «Автовыбор».

### 1.6 Права администратора

`App.xaml.cs:51-102`: если не админ — окно `AdminPromptWindow` с «продолжить без прав» и «запомнить выбор» (`AppSettings.RememberAdminChoice`, `AdminChoiceContinueWithout`). `RestartAsAdmin` — `Verb="runas"` + проброс аргументов (`:497-521`). Неудачный UAC сбрасывает «запомнено» и показывает промпт снова (`:73-76`).

### 1.7 Онбординг

`App.xaml.cs:106-159` + `OnboardingViewModel.cs`:
- миграция: если `LastProfileFileName` уже задан, но `FirstRunComplete == false` — считаем онбординг пройденным (`:111-116`);
- если `engine/` пуст — онбординг **пропускается, флаг не ставится** (`:127-132`);
- 3 шага: компонент (Zapret), цели (YouTube / Discord / оба), «Настроить и продолжить» vs «Продолжить без проверки» (`OnboardingViewModel.cs:43-65,125-144`);
- стратегия выбирается автоматически — первый `.bat` (`:76-109`), пользователь её не выбирает.

---

## 2. «ИИ-оркестратор»: что это конкретно

### 2.1 Модель/провайдер

Отсутствуют. Вся «интеллектуальность» — статистика:

| Компонент | Файл | Что делает |
|---|---|---|
| `BanditSelector` | `FluxRoute.AI/Services/BanditSelector.cs` | выбор генома: ε-explore по `ExplorationRatePermil` + Thompson Sampling |
| `AiStrategyRegistry` | `.../AiStrategyRegistry.cs` | персист геномов и `(genomeId, networkHash) → (α, β)` |
| `AiHistoryStore` | `.../AiHistoryStore.cs` | append-only JSONL журнал проб |
| `StrategyEvolver` | `.../StrategyEvolver.cs` | кроссовер + мутации, материализация BAT |
| `NetworkFingerprintProvider` / `NetworkChangeWatcher` | `.../NetworkFingerprintProvider.cs`, `.../NetworkChangeWatcher.cs` | «сеть» как ключ политики |
| `AiOrchestratorService` | `.../AiOrchestratorService.cs` (63 КБ) | цикл, переключения, эволюция, очистка, мьютекс |

### 2.2 Что именно решает

`AiOrchestratorService.RunCycleAsync` (`:530-643`):
1. ротация истории (`KeepHistoryDays`);
2. снимок отпечатка сети, `MarkNetworkSeen`;
3. если сеть «грязная» → `RepickAfterNetworkChangeAsync`;
4. если текущий геном отключён/удалён → переподбор;
5. проба текущей стратегии → `ProbeOutcome` в историю;
6. успех/неудача в bandit (`α+1` / `β+1`), `Notify` с результатом;
7. `MaybeEvolveAsync` (эволюция по расписанию);
8. если неудач подряд ≥ `RequiredFailuresBeforeSwitch` (2) → `SwitchToAlternativeAsync`.

Параметры по умолчанию (`AiOrchestratorService.cs:9-14`): `CheckInterval = 20 min`, `FailThreshold = 0.5`, `RequiredFailuresBeforeSwitch = 2`, цели = YouTube, Discord, Google, Twitch, Instagram, Telegram.

`AiSettings` (`FluxRoute.Core/Models/AiSettings.cs`):
```csharp
Enabled, ExplorationRatePermil = 100, MaxEvolvedStrategies = 24,
EvolutionIntervalMinutes = 60, MinProbesBeforeEvolve = 6,
KeepHistoryDays = 14, AutoDeleteBelowScore = 60
```

### 2.3 Выбор стратегии (математика)

`BanditSelector.Pick` (`:20-82`):
- фильтр по `_blockedUntil`;
- exploration: `rng.NextDouble()*1000 < explorationPermil` → берём **наименее опробованную** на этой сети (тай-брейк случайный);
- exploitation: для каждого генома `sampleOrUcb = SampleBeta(α, β)` на текущей сети; при `pulls < 1` — Beta, агрегированная по всем сетям; при полном холодном старте — `Uniform(0,1)` вместо детерминированного UCB (комментарий: «исправление #62», `:60-65`);
- Beta сэмплится через Gamma (Marsaglia–Tsang) + нормальный сэмплер (`:152-192`) — без внешних библиотек.

«Промпты» отсутствуют; вместо них — **форма входа** решения: `(список геномов, networkHash, explorationPermil)`.

### 2.4 Наказания и cooldown'ы (safety rails)

`RegisterFailure` (`:112-144`):
```csharp
backoffMs = streak switch { 1 => 300, 2 => 700, 3 => 1500, _ => 3000 };
jitter = 1 + (rng.NextDouble()*0.7 - 0.35);
_blockedUntil[id] = now + backoff*jitter;      // не выбирать это геном
_familyCooldown[$"{id:N}|{DesyncMode}"] = now + 15s;
_sigCooldown[$"{id:N}|{failureSignature}"]  = now + 15s;  // "winws_failed" | "network_failed"
```

### 2.5 Геном стратегии и связь с BAT

`FluxRoute.AI/Models/StrategyGenome.cs`:
```csharp
Guid Id; List<Guid> ParentIds; int Generation; StrategyOrigin Origin; // Builtin|Evolved
string FilterTcp, FilterUdp; string DesyncMode = "split";
int? SplitPos; string? SplitPosSemantic;   // host|endhost|midsld|sniext|endsld|method|extlen
int? FakeTtl; bool AutoTtl; string? FakeTlsMod; string? Hostlist; int? RepeatCount;
List<string> ExtraArgs; string DisplayName; string? BatFileName, SourceBatPath;
bool OrchestratorEnabled; int? LastVerificationScore; DateTimeOffset? LastVerifiedAt;
```
- BAT → геном: `BatGenomeParser.FromLaunchPlan(plan, name, origin)` (`BatGenomeParser.cs:16-105`) — разбирает `--filter-tcp/--filter-udp/--dpi-desync/--dpi-desync-split-pos/--dpi-desync-fake-tls-mod/--dpi-desync-ttl/--dpi-desync-autottl/--hostlist/--dpi-desync-repeats`, остальное в `ExtraArgs`.
- геном → аргументы: `BatMaterializer.BuildWinwsArgs` (`BatMaterializer.cs:41-107`) — всегда добавляет `--new` перед `ExtraArgs`.
- геном → BAT: `BatMaterializer.BuildBatContent` (`:10-29`):
```bat
@echo off
set "BIN=C:\...\engine\bin\"
set "LISTS=C:\...\engine\lists\"
set "GameFilter=1024-65535"
set "GameFilterTCP=1024-65535"
set "GameFilterUDP=12"
"%BIN%winws.exe" --filter-tcp=80,443 --dpi-desync=split --dpi-desync-split-pos=midsld ... --new
```
- валидация: `StrategyGenomeValidator.IsValid/Normalize` — `fake-tls-mod` допустим только для fake-режимов, TTL вне 1..128 → null (`StrategyGenomeValidator.cs:10-28`).

### 2.6 Эволюция

`StrategyEvolver.Evolve(net)` (`:36-113`):
- пул = `OrchestratorEnabled` геномы; нужно ≥ 2;
- ранжирование родителей по **Wilson lower bound** (`WilsonScore.LowerBound(succ, trials, z=1.96)`, `:5-16`) на истории **этой сети**, тай-брейк по числу проб;
- родители — топ-6 (или случайные 4, если Wilson у всех 0);
- 25 попыток: `Crossover` (по-полевой выбор одного из двух родителей) + `Mutate` (6 операторов: semantic split-pos, desync mode, TTL ±{1,2,4,8} clamp 3..48, fake-tls-mod, autottl toggle, numeric split 16..180) + `Normalize`; отбраковка невалидных и дубликатов по `GenomeSignature`;
- имя `FR-ev-{generation}-{rand 1000..9999}`, файл `engine/ai-evolved/<name>.bat`;
- `GarbageCollectEvolved()` — держит `MaxEvolvedStrategies` (≥4), удаляет худших по Wilson и их BAT.

Триггер: `MaybeEvolveAsync` — не чаще `EvolutionIntervalMinutes` (минимум 5) и не раньше `MinProbesBeforeEvolve` проб (`AiOrchestratorService.cs:700-725`).

### 2.7 Как проверяется успех

Единый механизм — `ProfileProbeService` (`FluxRoute.Core/Services/ProfileProbeService.cs`):
```csharp
WaitForProcessAsync("winws", 8s, poll 250ms)  // стартовал ли
Task.Delay(StableWait=1.5s)                   // не умер ли сразу
CheckAllAsync(targets, useCurl, parallel=6)   // HTTP/ping по целям
score = ProfileScoringService.Calculate(...)
```
`ProfileProbeResult.IsWorking(0.5)` → успех. Никакой «верификации контента» — только код ответа и задержка.

### 2.8 Защитные механизмы (это самое ценное для переноса)

| Механизм | Где | Зачем |
|---|---|---|
| Глобальный мьютекс `_aiGate` (SemaphoreSlim 1,1) вокруг цикла, purge, эволюции, импорта скана | `AiOrchestratorService.cs:45,100-136,530-643` | bandit/реестр не портятся параллельными записями |
| `RunSerializedAsync` + `PurgeWeakEvolutionsCoreAsync` (без захвата) | `:100-136,950-958` | SemaphoreSlim **не реентрантный** — иначе дедлок |
| Отбрасывание пробы при смене сети: сравнение hash **и** `NetworkChangeWatcher.Generation` до/после | `:828-845` | разрыв сети с возвратом к тому же отпечатку не ловится хэшем |
| Автоудаление эволюции только если **на этой сети** есть проходящая встроенная стратегия | `:897-923,981-1004` | «сеть агрессивна» — удалять несправедливо (#62) |
| Purge удаляет только по **последнему** результату на **текущей** сети | `:968-989` | глобальный `LastVerificationScore` не привязан к сети |
| Не удалять запись реестра, если файл не удалился | `:918-919,1007-1013` | иначе BAT остаётся «бесхозным» |
| Сопоставление геном↔профиль по каноническому пути BAT | `:261-291` | одноимённые builtin и evolved — разные стратегии (#97) |
| «Лечение» устаревшего пути после переезда portable | `:298-324` | абсолютные пути в реестре |
| «Проверить сейчас» не чистит стратегии скрыто | `:407-414` | issue #89 |

---

## 3. Интеграция zapret: запуск, аргументы, служба, супервизия

### 3.1 Прямой запуск winws.exe вместо BAT

`ProfileBatLauncher.TryCreateLaunchPlan` (`FluxRoute.Core/Services/ProfileBatLauncher.cs:24-134`):
1. читает BAT в UTF-8, склеивает продолжения `^` (`:242-270`);
2. собирает переменные из `set X=Y` (`:272-295`);
3. стартовые переменные: `BIN`, `BIN_PATH`, `LISTS`, `LISTS_PATH`, `GameFilter`, `GameFilterTCP`, `GameFilterUDP` (`:164-180`);
4. раскрывает `%~dp0`, `%~n0`, `%~nx0`, `%~f0`, затем `%VAR%` из словаря или окружения (`:297-320`);
5. ищет токен, оканчивающийся на `winws.exe`, резолвит путь, берёт остальные токены как аргументы;
6. **инжектит пользовательские хостлисты**, если в BAT их нет (`:87-111`):
```csharp
args.Add("--hostlist");         args.Add(engine\lists\list-general-user.txt);
args.Add("--hostlist-exclude"); args.Add(engine\lists\list-exclude-user.txt);
```
(только если файл содержит «реальные» домены — `HasRealDomains`, `:392-407`);
7. возвращает `WinwsLaunchPlan(ExecutablePath, Arguments, WorkingDirectory, SourceProfilePath)` (`:10-14`).

Запуск (`:136-151`):
```csharp
new ProcessStartInfo { FileName = plan.ExecutablePath, WorkingDirectory = plan.WorkingDirectory,
    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
foreach (var arg in plan.Arguments) psi.ArgumentList.Add(arg);   // без склейки строк
```

`PrepareRuntime` (`:153-157`): создаёт `lists/ipset-exclude-user.txt` (`203.0.113.113/32`), `list-general-user.txt` и `list-exclude-user.txt` (`domain.example.abc`) и вызывает
```csharp
netsh interface tcp set global timestamps=enabled   // WaitForExit(2500), ошибки игнорируются
```
(`:221-240`).

Fallback: если парсинг не удался — `cmd.exe /c "<profile>.bat"` с `CreateNoWindow` (`MainViewModel.Process.cs:424-459`), флаг `_startedViaBatFallback` меняет семантику `Stop()`.

### 3.2 GameFilter и IPSet (файловые переключатели Flowseal)

- `engine/utils/game_filter.enabled` содержит `all|tcp|udp`; маппинг в порты (`ProfileBatLauncher.cs:182-196`, дублируется в `BatMaterializer.cs:109-123`):
```csharp
"all" => ("1024-65535","1024-65535","1024-65535"),
"tcp" => ("1024-65535","1024-65535","12"),
"udp" => ("1024-65535","12","1024-65535"),
_     => ("1024-65535","1024-65535","1024-65535")
```
- IPSet-режим определяется по `engine/lists/ipset-all.txt` (`ServiceViewModel.cs:154-167`): пусто → `any`; одна строка `203.0.113.113/32` → `none`; иначе `loaded`.

### 3.3 Супервизия процесса

`MainViewModel.Process.cs`:
- прямой режим: `_trackedPids = { winws.Pid }`, затем 20 итераций по 100 мс собирают дерево PID через `CreateToolhelp32Snapshot`/`Process32First/Next` (`:461-518`, `:708-748`), после чего `WaitForExitAsync`; по выходу — сброс UI, лог `winws.exe завершился (код выхода: N)`.
- BAT-режим: до 10 с ищет процесс `winws` по имени, добирает PID дерева, дальше то же (`:520-618`).
- `Stop()` (`:620-706`): отмена трекинга, `Kill(entireProcessTree: true)` по всем отслеживаемым PID; в BAT-режиме дополнительно убивает **все** `winws` (совместимость с `cmd/start`); `WaitForExit(1500)`; сброс статусов; остановка сервисов оркестратора (если не подавлено флагом `_suppressOrchestratorStop`).
- «Починить защиту» — Stop → 800 мс → Start → 600 мс → RefreshDiagnostics (`:155-192`).
- «Полное восстановление» — `cmd /c net stop zapret & sc delete zapret` через `Verb="runas"`, таймаут 12 с, затем Start (`:199-283`).
- «Освободить WinDivert» — `sc stop WinDivert & sc delete WinDivert` через runas (`:289-345`).

### 3.4 Установка службы

Своей реализации нет — делегируется `engine/service.bat admin` через `cmd /c` + runas (`ServiceViewModel.cs:371-404`). Принудительная остановка: `net stop zapret & taskkill /IM winws.exe /F & net stop WinDivert` (`:417`).

### 3.5 Второй движок (Zapret2 / winws2)

Отдельный компонент с собственным хостом: `FluxRoute.Core/Services/Zapret2ProcessHost.cs` — запуск `winws2.exe` с `ArgumentList`, учёт **только своих** PID (`_ownedProcessIds`), `StopAsync` убивает лишь их; `Zapret2StatusService.cs` публикует `ProtectionStatus { Stopped, Starting, Stopping, Healthy, Degraded, Error, Repairing }` (`FluxRoute.Core/Models/ProtectionStatus.cs`), `RepairAsync` = stop → delay 250 мс → start.

### 3.6 Логирование winws

Вывод `winws.exe` **не перехватывается** (`RedirectStandardOutput` не выставлен в `StartWinws` и `Zapret2ProcessHost.StartAsync`) — только факт запуска/кода выхода. Внутренние логи: `ObservableCollection<string> Logs` + Serilog.

---

## 4. Модель конфига и стратегий

### 4.1 Форматы

| Что | Формат | Где |
|---|---|---|
| Стратегии (builtin) | **BAT** Flowseal в `engine/*.bat` (исключая `service.bat` и опечатку `service,.bat`) | `AiOrchestratorService.cs:1137-1139`, `MainViewModel.Diagnostics.cs:202-203` |
| Эволюционированные | BAT в `engine/ai-evolved/*.bat` | `BatMaterializer.WriteBat` |
| Настройки | `fluxroute-settings.json` | `SettingsService.cs` |
| Реестр ИИ | `fluxroute-ai-strategies.json` (genomes + bandit + generation + seen hashes) | `AiStrategyRegistry.cs:15-21` |
| История проб | `fluxroute-ai-history.jsonl` (append-only) | `AiHistoryStore.cs` |
| Пресеты | внутри настроек (`List<ConfigPreset>`) | `SettingsService.cs:39,179-196` |
| Моды | `mods/<name>/manifest.json` + скрипты + `mods/status.json` | `docs/mods.md`, `ModManager.cs` |
| Списки доменов | `engine/lists/*.txt` | см. 4.4 |

JSON не описывает стратегии — только настройки и «геномы». Ini нет.

### 4.2 Где лежат настройки

`SettingsService` (`:224-262`):
- если exe **под `Program Files`** → `%LOCALAPPDATA%\FluxRoute\fluxroute-settings.json`;
- иначе (portable) → рядом с exe;
- `IsPortable` = «не под Program Files»;
- чтение: основной → `.bak` → legacy-путь рядом с exe (старые installer-сборки) → дефолт;
- запись: temp-файл + `File.Replace(temp, target, backup)` (атомарно на NTFS, старая версия остаётся в `.bak`), `:404-419`;
- битый файл уезжает в `*.corrupt-{yyyyMMddHHmmss}` (`:379-402`);
- `Normalize()` защищает от null-коллекций и делает миграции (например, сброс `TgProxy.Domain == "www.google.com"`, `:457-461`).

### 4.3 Пресеты и триггеры по процессам

`ConfigPreset` (`SettingsService.cs:179-196`): `ProfileFileName`, `GameFilterEnabled`, `GameFilterProtocol`, `IpSetMode`, `TriggerProcess`.

Мониторинг — `MainViewModel.Orchestrator.cs:1516-1652`: цикл каждые **3 с**, `Process.GetProcessesByName(Path.GetFileNameWithoutExtension(trigger))`; при появлении процесса применяется пресет и запоминается предыдущий профиль (`_profileBeforeTrigger` или `DefaultProfileFileName`); при исчезновении — возврат (Stop → 1000 мс → Start).

### 4.4 Hot-swap и per-domain списки

Hot-swap — это **перезапуск winws**, не переконфигурация:
```csharp
// MainViewModel.Orchestrator.cs:324-361
Stop(); SelectedProfile = profile; await Task.Delay(1500); Start();
```
(1500 мс — «даём время на завершение WinDivert и winws.exe».)

Per-domain списки:
- `engine/lists/list-general-user.txt` — цели; строка `!domain` = **исключение** (`UserHostlistImporter.Classify`, `:37-66`);
- `engine/lists/list-exclude-user.txt` — все строки исключения, даже без `!` (`:41,53`);
- `engine/lists/ipset-exclude-user.txt` — исключения по IP.

Синхронизация из UI — **идемпотентная**: `HostlistSyncPolicy.NeedsWrite` сравнивает эффективное множество доменов (комментарии `#`, `;` игнорируются) и не переписывает файл, если набор совпадает (`HostlistSyncPolicy.cs:24-53`) — чтобы не терять комментарии пользователя на каждом старте (issue #77).

---

## 5. Механизм обновлений

### 5.1 Engine (`FluxRoute.Updater/Services/UpdaterService.cs`)

- Версия: `raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt`, цепочка: primary → пользовательские `FallbackMirrors["engine.version"]` → SourceForge-зеркало (`:33-45,152-187`, `MirrorUrls.cs`).
- ZIP: `https://github.com/Flowseal/zapret-discord-youtube/releases/download/{v}/zapret-discord-youtube-{v}.zip`.
- Локальная версия: `engine/version.txt` → fallback `set LOCAL_VERSION=` из `service.bat` (regex `^set\s+"?LOCAL_VERSION=([^"]+)"?`) (`:81-124`).
- Установка (`:191-316`), шаги:
  1. скачать (named client 300 c), проверка «не HTML» по размеру `< 1024` байт, **печать SHA-256**;
  2. распаковать в `%TEMP%`, найти корень по наличию `*.bat` (до 2 уровней, `:415-436`);
  3. копировать в `engine/.staging`, верификация — есть ли `*.bat` (`:441-452`);
  4. **остановить защиту**: `sc query` → если RUNNING/STOP_PENDING → `sc stop` → polling до `STOPPED` 10 с; `taskkill /IM winws.exe /F`; затем служба `WinDivert`; `Thread.Sleep(1500)` (`:319-412`);
  5. backup `engine/.rollback` (кроме `.staging`/`.rollback`);
  6. применить staging → engine, **не перезаписывая** `ipset-exclude-user.txt`, `list-general-user.txt`, `list-exclude-user.txt` (`:591-600`); копирование с 12 попытками ×250 мс, при блокировке — `*.upd` + `File.Move` (`:480-514,553-578`);
  7. при ошибках — rollback из `.rollback`;
  8. записать `version.txt`.

### 5.2 Самообновление приложения (`AppUpdaterService.cs`)

- Проверка: `GET api.github.com/repos/klondike0x/FluxRoute/releases/latest`, токен из `GH_TOKEN`/`GITHUB_TOKEN` (`:113-143`); при **403** или сетевой ошибке — fallback на **Atom-ленту** `releases.atom` (без лимитов), парсинг первого `<entry><id>tag:github.com,.../v1.4.1</id>` (`:208-315`); кэш результата 1 час (`:56`).
- Валидация тега (`:280-288`): `Version.TryParse` и запрет `AI`, `fork`, `custom`, `-` (pre-release).
- Тип установки (`:322-369`): маркер `HKLM\Software\FluxRoute\InstallLocation` (оба registry view) **или** наличие `unins*.exe` → installer-сборка, иначе portable.
- Portable: скачать `FluxRoute-{tag}-portable.zip`, распаковать, сгенерировать BAT-заменщик (`:533-597`), который:
  - ждёт завершения PID приложения (`tasklist /FI "PID eq {pid}"` в цикле);
  - `taskkill /IM winws.exe|winws2.exe|WinDivert.exe /F`, `net stop WinDivert`;
  - до 30 с ждёт исчезновения winws/winws2 и `STOPPED` для `WinDivert` (иначе — ветка abort с отдельной консолью, `:574-582`);
  - `xcopy /E /Y /I`, запуск новой версии, `del "%~f0"`.
- Installer: скачать `FluxRoute-{tag}-installer.exe`, BAT с `start /wait ... /SILENT /NORESTART /CLOSEAPPLICATIONS`.
- Права (`UpdateElevationPolicy.cs`): `runas` **только** если защита реально осталась запущена и процесс не повышен (`NeedsElevation`), причём «защита» = процессы движка **ИЛИ** kernel-служба `WinDivert` через `sc query` — у драйвера нет процесса, проверка только по именам его «не видит» (`:26-34,94-106`). Отмена UAC (`Win32Exception 1223`) → честный `(false, "Обновление отменено...")` (`AppUpdaterService.cs:621-628`).

### 5.3 Чего нет

- **Нет верификации подписи** (ни GPG, ни Authenticode) в рантайме; SHA-256 только печатается в лог/UI.
- Нет delta-обновлений, нет своего feed-сервера (жёстко зашитые URL, README советует форкам менять endpoint).

---

## 6. Диагностика

### 6.1 Цели проверки

`ConnectivityChecker.BuiltinSites` (`FluxRoute.Core/Services/ConnectivityChecker.cs:56-97`):

| Группа | Цели |
|---|---|
| YouTube | `https://www.youtube.com`, `https://i.ytimg.com`, `https://www.youtube-nocookie.com` |
| Discord | `https://discord.com`, `https://gateway.discord.gg`, `https://cdn.discordapp.com` |
| Google | `https://www.google.com`, ping `8.8.8.8` |
| Twitch | `https://www.twitch.tv`, `https://static-cdn.jtvnw.net` |
| Instagram | `https://www.instagram.com`, `https://scontent.cdninstagram.com` |
| Telegram | `https://web.telegram.org`, `https://t.me` |
| TikTok | `www.tiktok.com`, `studio.tiktok.com`, `p16-sign.tiktokcdn.com`, `live.tiktok.com` |

Плюс пользовательские домены и файл целей (`TargetEntry.ParseFile`) — `BuildTargets` (`AiOrchestratorService.cs:1200-1217`), дедуп по `Kind|Key|Value`.

### 6.2 Как проверяется «youtube работает»

Приоритет — **curl.exe** (`ConnectivityChecker.cs:246-276`):
```
curl.exe --location --insecure --silent --show-error
  --connect-timeout 4 --max-time 8 --output NUL
  --user-agent "Mozilla/5.0 ... Chrome/120.0.0.0 Safari/537.36"
  --write-out "FR_CURL_RESULT:%{http_code}|%{time_total}|%{remote_ip}|%{errormsg}"
  <url>
```
Разбор маркера `FR_CURL_RESULT:` (`:436-459`). Успех: `exit==0 && 200 <= status < 500` (`:196`).

Fallback — `HttpClient` с `ResponseHeadersRead`, таймаут 8 с, успех `status < 500` (`:278-344`).
Ping — 3000 мс, успех `IPStatus.Success` (`:346-388`).

**Важно:** это проверка «не блокируют», а не «видео играет»; `--insecure` означает, что битый сертификат/подмена не отличимы от успеха. Параллелизм по умолчанию 6, clamp 1..16 (`:39,143`).

### 6.3 Скоринг

`ProfileScoringService.Calculate` (`FluxRoute.Core/Services/ProfileScoringService.cs:7-38`):
```
0   если RequireWinwsProcess && !processStarted
+20 winws запущен
+15 процесс стабилен (жив после StableWait)
+55 * successRate
+10/7/4/1 за среднюю задержку (<=500 / <=1000 / <=2000 / иначе)
-20 если ВСЕ проверки провалились
score = min(score-20, 35) если процесс нестабилен
clamp 0..100
```

### 6.4 Классический оркестратор

`OrchestratorService.cs`:
- `ScanAndRankAsync` (`:156-228`): последовательно, `StopAfterProbe = true`, `-1` в UI как «pending», рейтинг пишется в настройки;
- `RankProfiles` (`:364-375`): score ↓, затем `ProcessStable`, `SuccessRate`, «предпочтительный профиль», имя;
- цикл (`:125-154`): скан (если кэша нет) → запуск лучшего → каждые `CheckInterval` (20 мин) проверка;
- переключение (`:249-329`): 2 неудачи подряд < 50 % → перебор следующих по рейтингу с `StopAfterProbe=false`; если ни одна не прошла → `_switchProfile(null)` (стоп).
- Кэш рейтинга переживает перезапуск: `RestoreRankedProfiles` / `ClearRankedProfiles` (`:103-123`).

### 6.5 Auto-Tune

`ServiceViewModel.RunAutoTuneAsync` (`FluxRoute/ViewModels/ServiceViewModel.cs:711-1038`):
- 12 комбинаций (`:750-758`): `IPSet ∈ {loaded, none, any}` × `GameFilter ∈ {TCP и UDP, TCP, UDP, Выкл}`;
- на комбинацию: применить → 400 мс → проверка 5 целей с `CancelAfter(5s)` **и** жёстким `Task.WhenAny(checkTask, Task.Delay(6s))` (`:863-899`);
- `AutoTuneResult.CalculateCompositeScore` (`FluxRoute.Core/Models/AutoTuneResult.cs:22-39`): **60 % successRate + 30 % latency (нормировка на 2000 мс) + 10 % стабильность (1 − (max−min)/max)**;
- исходные настройки восстанавливаются (`:968-990`), лучшая комбинация применяется кнопкой (`:672-681`);
- ⚠️ Фактический выбор «лучшей» идёт по `SuccessCount → SuccessRate → AvgLatency → Duration` (`:996-1001`), а не по `CompositeScore` — композитный балл считается, но в ранжировании не участвует. Это расхождение с README (`README.md:310-316`).

### 6.6 Расширенная диагностика (реплика `service.bat` п.10)

`MainViewModel.Diagnostics.cs:358-592` — 12 проверок:
1. **BFE** (`sc query BFE`) — обязательна для zapret;
2. системный прокси (реестр `Internet Settings\ProxyEnable/ProxyServer`);
3. **TCP timestamps** (`netsh interface tcp show global`) — при выключенных включает автоматически, если есть админ;
4. процесс `AdguardSvc.exe`;
5. службы Killer;
6. Intel Connectivity Network Service;
7. Check Point (`TracSrvWrapper`, `EPWD`);
8. SmartByte;
9. наличие `*.sys` в `engine/bin` (WinDivert64.sys);
10. VPN-службы по имени;
11. `hosts` на записи `youtube.com`/`youtu.be`;
12. конфликтующие bypass-службы: `GoodbyeDPI`, `discordfix_zapret`, `winws1`, `winws2`.

Каждая команда выполняется как `cmd /c chcp 437 >nul & <cmd>` с декодированием CP437 (`:646-715`) — чтобы `netsh`/`sc`/`tasklist` отвечали по-английски независимо от локали. Плюс `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` (`:344-356`).

Диагностический бандл (`:77-139`): ZIP с `diagnostics.txt`, `app_log.txt`, `orchestrator_log.txt`, `update_log.txt`, `service_log.txt`, `settings.json`, последние 3 Serilog-файла.

### 6.7 Auto-tune loop для стратегий

Отдельного «крути параметры, пока не заработает» нет — эту роль играет эволюция (§2.6) плюс ручная кнопка «Подобрать стратегию для домена» (`MainViewModel.Ai.cs:44-230`): топ-10 по Wilson → проба по одному домену → стоп на `Score > 70` → иначе эволюция → balloon-уведомление.

---

## 7. UI/UX: что стоит украсть

### 7.1 Тема

`FluxRoute/Styles/Colors.xaml` — тёмная палитра с явными токенами:
```xml
BgMain #0D0F1A · BgSidebar #080A12 · BgCard #0E1120 · BgInput #111520
AccentBlue #55AAFF · AccentPurple #9933DD · AccentGreen #00D68F
AccentRed #E74C3C · AccentYellow #F0B429
TextPrimary #DDE4F5 · TextMuted #3D4260
NavActiveBrush  — градиент #CC4B3FAA → #AA6630DD
PowerRingBrush  — градиент #55AAFF → #9933DD (кольцо кнопки питания)
```
Плюс `Styles/ButtonStyles.xaml` (21 КБ), `InputStyles.xaml` (13 КБ), `ScrollBarStyle.xaml`, `CommonStyles.xaml`. Тем — одна (мультитемность заявлена только у конкурентов в таблице README).

### 7.2 Каркас окна

`Views/MainWindow.xaml` + `Views/Shell/TitleBar.xaml` (кастомный заголовок) + `Views/Shell/Sidebar.xaml` (навигация с анимированным индикатором — `Converters/NavIndicatorYConverter.cs`). Адаптивность: `ViewModels/AdaptiveHomeLayout.cs`, `AdaptiveSidebarLayout.cs` с тестами (`FluxRoute.Core.Tests/Adaptive*Tests.cs`).

### 7.3 Главный экран

`Views/Tabs/HomePage.xaml` (43 КБ) + свойства в `MainViewModel.cs`:
- одна большая кнопка `MainActionButtonText => IsRunning ? "⏹ Остановить" : "▶ Запустить"` (`:758`);
- `MainStatusText` → «Обход DPI активен / неактивен» (`:678-682`);
- `StrategyStatusText`, `PidText`, `UptimeText`, `RunningScriptName`;
- живой трафик ↓/↑ (`NetworkTrafficMonitor` + `NetworkTrafficDisplay.Create(...)`, `:782-790`);
- переключатель режима Manual/Automatic (`:845-864`);
- счётчик активных сервисов (`ActiveServicesCount`) — Zapret + DoH + TG proxy.

### 7.4 Трей

`FluxRoute/Services/TrayIconService.cs`:
- `NotifyIcon` с **генерируемыми** иконками-оверлеями: зелёная/красная точка рисуется GDI+ поверх базовой иконки (`:170-197`) — не нужны 3 набора ассетов;
- ЛКМ → показать окно, ПКМ → собственное WPF-окно `Views/Tray/TrayPopupWindow.xaml` (не нативное меню), состояние: стратегия, защита, оркестратор, TG proxy, game filter (`:77-91`);
- balloon'ы на события (`ShowBalloon(3000)`), tooltip обрезается до 127 символов (`:68-72`) — ограничение Win32;
- все обращения к иконке через `Dispatcher.BeginInvoke` + глотание `InvalidOperationException` (handle умирает при завершении) (`:138-168`).

### 7.5 Прогресс и модальность

Единый глобальный оверлей вместо россыпи диалогов: `GlobalOverlayVisible/Title/Content/CloseCommand` (`MainViewModel.cs:836-840`), в него подставляются `Controls/ScanProgressView.xaml` (13 КБ) и `Controls/AutoTuneProgressView.xaml`. Auto-Tune дополнительно имеет отдельное окно с пошаговым списком (`Views/AutoTuneWindow.xaml`, `AutoTuneStepViewModel.cs`, статусы Running/Result).

### 7.6 Логи

`FluxRoute/ViewModels/LogsViewModel.cs`:
- единый журнал из нескольких источников с категориями: «Все», «Приложение», «Оркестратор», «ИИ», «TG Proxy», «Обновления», «Сервис», «Ошибки» (`:54-64`);
- поиск, «только ошибки», автопрокрутка;
- лимит 1500 записей;
- текст для отображения пересобирается с дебаунсом 250 мс (`DispatcherTimer`, `:37-49`) — иначе UI умирает на потоке логов.

### 7.7 Прочие продуктовые приёмы

- `CustomDialog.Show(title, message, ok, cancel, isDanger: true)` вместо MessageBox — единый стиль, красная кнопка для опасных действий (`Views/CustomDialog.xaml`).
- Справка по вкладке через «?» → `TabHelp.Show(topic)` с человеческим текстом на каждую вкладку (`Services/TabHelp.cs`).
- `SimpleMode` (упрощённый/расширенный интерфейс) в настройках (`SettingsService.cs:79`).
- Автозапуск: либо `HKCU\...\Run` (`Services/AutoStartService.cs`), либо **Task Scheduler** с `RunLevel.Highest`, `LogonTrigger` + задержка 30 с, `Priority = AboveNormal`, аргумент `--minimized` (`Services/TaskSchedulerService.cs:34-74`) — комментарий прямо говорит: «Надёжнее реестра — не блокируется антивирусами».
- `StartupWindowMode { Minimal }` (`FluxRoute.Core/Models/StartupWindowMode.cs`), `--minimized`, `--autostart`.
- `DonateWindow` — донат-кнопка как отдельное окно.
- Трей-попап можно отлаживать отдельно: `TrayPopupService.IsPreviewRequested()` + `ShowPreview()` (`App.xaml.cs:169-175`) — окно скрывается, показывается только попап. Отличный приём для итераций по UI.

---

## 8. Telegram / WS-прокси

### 8.1 Что это

Полностью встроенный C#-мост MTProto→WebSocket, без Python и без внешнего процесса (`FluxRoute/Services/TgWsProxyServer.cs`, `TgWsProxyProtocol.cs`, `TgWsProxyWebSocket.cs`, `TgWsProxyDnsResolver.cs`). Легаси-Python из `<app>/tg-proxy/` убивается на старте, чтобы не держал порт 1443 (`MainViewModel.TgProxy.cs:134-167`).

### 8.2 Секрет и диплинк

```csharp
TgProxySecret = "dd" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // :192-196
TgDeepLink => $"tg://proxy?server={Host}&port={Port}&secret={Secret}";                              // :325-326
```
Сервер требует ровно 16 байт секрета (`TgWsProxyServer.cs:80-81`). Порт по умолчанию 1443 (`SettingsService.cs:156`).

### 8.3 Handshake (obfuscated2)

`TgWsProxyProtocol.TryDecodeHandshake` (`:74-110`):
```
key = SHA256(handshake[8..40] || secret)      // 32 байта
iv  = handshake[40..56]                        // 16 байт
plain = AES-CTR(key, iv).Transform(handshake)
tag = plain[56..60] → EFEFEFEF (abridged) | EEEEEEEE (intermediate) | DDDDDDDD (secure)
rawDc = int16 LE at plain[60..62]; dc = |rawDc|; media = rawDc < 0; test = dc >= 10000
валидно: 1..5 или 203
```
Ответный (relay) handshake генерируется с защитой от «зарезервированных» префиксов — `HEAD`, `POST`, `GET `, `0xEE..`, `0xDD..`, `16 03 01 02` (TLS) и ненулевых байт 4..7 (`:112-141`).

Крипто-мост — 4 потока AES-CTR (`CreateCrypto`, `:143-175`), причём ключ для шифрования клиента берётся из **перевёрнутого** key material (так устроен MTProto), а первые 64 байта keystream «проглатываются» (`TransformZeros(64)`).

`AesCtr` реализован вручную поверх AES-ECB с инкрементом счётчика и `ZeroMemory` при Dispose (`:190-257`).

### 8.4 Нарезка пакетов

`TgPacketSplitter` (`:259-356`): расшифровывает поток «плоским» ключом relay-handshake, читает длины (abridged: 1 байт, либо `0x7F/0xFF` + 3 байта слов; intermediate/secure: 4 байта LE & `0x7FFFFFFF`), режет на пакеты для отдельных WS-фреймов; при `length <= 0` — passthrough всего остатка.

### 8.5 Роутинг

`BuildWebSocketCandidates` (`TgWsProxyServer.cs:451-501`):
1. `kws{dc}.web.telegram.org` и `kws{dc}-1.web.telegram.org` (media предпочитает `-1`);
2. Cloudflare-фронты: пользовательский домен (с подстановкой `{dc}`), пользовательские Worker-домены, затем **20 захардкоженных** `.co.uk`-доменов (`:50-57`);
3. `CloudflarePriority` вставляет фронты первыми (кроме media);
4. DC 203 → WS-DC 2 (`GetWebSocketDataCenter`, `TgWsProxyProtocol.cs:177`).

Путь: `/apiws` (или `/apiws_test` для test DC), таймаут коннекта 8 с (`:205-208`).

### 8.6 Устойчивость

- cooldown маршрута: 30 с для фронтов, 60 с для прямых (`:524-529`), «предпочтительный маршрут» запоминается per `dc|media` (`:531-536`);
- **детектор «не релеит»**: если клиент отправил байты, а Telegram не ответил за 10 с → `UpstreamDidNotRelayException`, сессия закрывается, чтобы Telegram сам переподключился другим маршрутом (`:325-369`);
- после начала моста повторный маршрут не пробуется — поток нельзя «переиграть» (`:226-232`);
- лимит сессий — **очередь, а не дроп** (`SemaphoreSlim`, `:99-102,136-145`);
- TCP-fallback напрямую на DC IP:443 с тем же handshake (`:371-430`);
- буферы 256 КБ по умолчанию, `NoDelay`, предпочтение IPv4, `ArrayPool` для буферов.

### 8.7 Настройки

`TgProxySettings` (`SettingsService.cs:153-177`): `Host=127.0.0.1`, `Port=1443`, `Secret`, `DcIps="2:149.154.167.220\n4:149.154.167.220"`, `CfProxyEnabled=true`, `CfProxyPriority=false`, `CfDomain*`, `BufKb=256`, `PoolSize=16`, `LogMaxMb=5.0`, `AutoStartOnAppLaunch=true`.

### 8.8 Что взять для AutoZapret

Схема «локальный TCP-листенер + свой протокол + пул апстримов с cooldown и предпочтением» переносится 1:1 на любой WS-прокси. Отдельно ценен приём «не ответил за N секунд → рвём сессию, клиент переподключится сам» — не нужно вручную ретраить внутри моста.

---

## 9. Грабли и выстраданные уроки (по коду и комментариям)

### 9.1 Процессы и драйвер

1. **`winws.exe` надо запускать самим**, а не через BAT/`start` — иначе нельзя ни отследить PID, ни аккуратно убить дерево (`ProfileBatLauncher.cs:16-19`).
2. **У kernel-службы `WinDivert` нет процесса.** Проверка «защита запущена» только по именам процессов даёт ложное «остановлено» → обновление падает на sharing violation, а UI рапортует успех. Лечится `sc query WinDivert` (`UpdateElevationPolicy.cs:14-22,26-34`).
3. **`sc stop` возвращается раньше выгрузки драйвера** → нужен polling до `STOPPED` (до 10 с) и `Thread.Sleep(1500)` перед заменой файлов (`UpdaterService.cs:348-412`).
4. **Файлы движка могут быть заняты** → 12 попыток ×250 мс, затем `*.upd` + `File.Move` (`UpdaterService.cs:494-503,553-578`).
5. **UAC-отмена должна возвращать ошибку**, а не «успех» (`AppUpdaterService.cs:621-628`).
6. **BAT-аборт при обновлении должен сообщать пользователю отдельной консолью** — иначе «UI сказал успех, версия старая» (`AppUpdaterService.cs:574-582`).
7. При `Stop()` в BAT-режиме убиваются **все** `winws` — совместимость с профилями, запущенными через `cmd/start` (`MainViewModel.Process.cs:645-658`).

### 9.2 Локализация и консоль

8. **`netsh`/`sc`/`tasklist` локализованы.** Решение: `cmd /c chcp 437 >nul & <cmd>` + `Encoding.GetEncoding(437)` и `CodePagesEncodingProvider` (`MainViewModel.Diagnostics.cs:646-715`). Без этого парсинг «Метки времени: включены» ломается на каждой локали по-своему.
9. `LoadUserProfile = true` нужен для наследования elevated-токена при `UseShellExecute = false` (`MainViewModel.Diagnostics.cs:687`).

### 9.3 Состояние и файлы

10. **Portable-переезд ломает абсолютные пути** в реестре ИИ → «лечение» пути по текущей папке `engine/ai-evolved` (`AiOrchestratorService.cs:298-324`).
11. **Одноимённые builtin и evolved BAT — разные стратегии.** Сопоставление только по каноническому пути; иначе результат пробы пишется в bandit под чужим Id (`:252-291`).
12. **Портативная установка под `Program Files` не может писать рядом с exe** → настройки в `LocalAppData`, с чтением legacy-пути (`SettingsService.cs:238-299`).
13. **Атомарная запись настроек** через `File.Replace` + `.bak`, битый файл — в `.corrupt-<ts>` (`:379-419`).
14. **Синхронизация хостлистов должна быть идемпотентной**, иначе каждый старт затирает комментарии пользователя (issue #77, `HostlistSyncPolicy.cs`).
15. **`!domain` — это исключение, а не цель.** Отбрасывание `!` превращало исключение во включение (`UserHostlistImporter.cs:16-23`).
16. **Удаление файла может не удаться** → запись в реестре не убирать, иначе BAT остаётся «бесхозным» и снова подхватывается `LoadProfiles` (`AiOrchestratorService.cs:1007-1013`).

### 9.4 Конкурентность

17. **`SemaphoreSlim` не реентрантный** → разделение на `PurgeWeakEvolutionsAsync` (захватывает) и `PurgeWeakEvolutionsCoreAsync` (требует удержания) (`:950-958`).
18. **Импорт результатов скана должен быть атомарен вместе с измерением**, а не только с записью (`:1063-1072`).
19. **Смена сети во время пробы** → результат выбрасывается; сравнивать и hash, и `Generation`, потому что «ушла и вернулась» даёт тот же hash (`:828-845`; `NetworkChangeWatcher.cs:16-49`).
20. **Счётчик неудач привязан к геному**: при смене стратегии его надо обнулять, иначе новая снимается раньше срока (`:385-390,556-560`).

### 9.5 Проверки и метрики

21. **`curl --insecure`** маскирует TLS-ошибки: «доступность» ≠ «корректный TLS» (`ConnectivityChecker.cs:260`).
22. **Порог 50 % и «2 неудачи подряд»** — дешёвый антифлаппинг; без него одна турбулентная проба роняет рабочую стратегию (`OrchestratorService.cs:275-288`).
23. **Холодный старт bandit'а**: при `pulls < 1` нужен случайный сэмпл, а не одинаковый UCB — иначе первая стратегия выигрывает всегда (исправление #62, `BanditSelector.cs:56-72`).
24. **Вес успеха должен быть дробным** (`score/100`), иначе 76 % и 85 % дают одинаковый Beta(2,1) и выбор решается порядком перечисления (`AiStrategyRegistry.cs:194-217`).
25. **Auto-Tune: композитный балл считается, но выбор идёт по SuccessCount** (`ServiceViewModel.cs:996-1001` vs `AutoTuneResult.cs:22-39`) — расхождение с README.

### 9.6 Прочее

26. **GitHub API rate limit** (60/ч без токена) → Atom-лента как fallback, кэш 1 ч (`AppUpdaterService.cs:42-56,131-143`).
27. **Фильтр тегов** отсекает форки/пре-релизы: `AI`, `fork`, `custom`, `-` (`:280-288`).
28. **WinDivert флагается антивирусами** (`Not-a-virus:RiskTool.Multi.WinDivert`, `HackTool`) — нужен честный раздел в README и объяснение, что делать (`README.md:393-405`).
29. **Моды исполняют произвольные `.bat/.exe/.ps1/.py`** от имени пользователя (`ModManager.RunScriptAsync`, `:964-1011`; `docs/mods.md`) — только с предупреждением в UI.
30. **Правила проекта**: комментарии по-русски, `Result<T>` вместо исключений, `IHttpClientFactory` вместо `new HttpClient()`, никогда не блокировать UI-поток (`AGENTS/00-GOLDEN-RULES.md:9-20`). На практике `Result<T>` в коде почти не используется — расхождение декларации и реализации.

---

## Приложение: что переносить в AutoZapret (приоритет)

1. **Прямой запуск `winws.exe` с `ArgumentList` + разбор BAT** (`ProfileBatLauncher`) — база, без неё нет управления процессом.
2. **Триггеры по процессам + пресеты** — самая дешёвая «магия» для геймеров.
3. **Скоринг + антифлаппинг (2 неудачи подряд)** и кэш рейтинга между запусками.
4. **Идемпотентная синхронизация хостлистов** — иначе пользователи теряют правки.
5. **Мьютекс + `*CoreAsync`-разделение** для любой фоновой подсистемы, которая может пересечься с UI-командой.
6. **Проверки: BFE, WinDivert-служба, TCP timestamps, конфликтующие bypass-сервисы, `chcp 437`** — 80 % поддержки закрывается этими шестью.
7. **Трей с генерируемыми оверлей-иконками и WPF-попапом** — дёшево и выглядит дорого.
8. **Обновление через BAT-заменщик с ожиданием PID и проверкой `STOPPED` WinDivert** — работает без отдельного updater.exe.
9. **Telegram-прокси как локальный листенер с пулом маршрутов, cooldown и «no relay → kill session»**.
10. **Не копировать**: god-object `MainViewModel`, отсутствие проверки подписи, `--insecure` в проверках, расхождение README и кода по Auto-Tune.
