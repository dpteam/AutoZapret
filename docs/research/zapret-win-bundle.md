# zapret-win-bundle - разбор upstream-бандла для GUI

> Объект: `E:\_DPT_Projects_E\_Harness\AutoZapret\refs\zapret-win-bundle`
> Источник: `https://github.com/bol-van/zapret-win-bundle`
> Локальная копия: shallow-clone, ветка `master`, коммит `4840909b1a26389cb72c46be5301d610b3768928`, дата `Tue Oct 27 2026 +0300` (сообщение коммита: `bundle`).
> Дата разбора: 2026-11-04. Размеры в байтах, пути относительно корня бандла.

## TL;DR - что важно для GUI

| # | Факт | Следствие |
|---|------|-----------|
| 1 | **32-битной сборки нет вообще.** 165 PE-файлов x64 + 3 ARM64. | GUI обязан требовать x64/ARM64 и внятно отказывать на x86. |
| 2 | `winws.exe` - **cygwin-бинарь**, зависит от `cygwin1.dll` и `WinDivert.dll` **рядом с собой**. | Нельзя "положить один exe". Минимальный комплект - 4 файла. |
| 3 | Манифест `winws.exe`: `requestedExecutionLevel level="requireAdministrator"`. | `CreateProcess` из неуelevated GUI получит UAC-отказ; нужен свой elevation-хелпер (`elevator.exe` уже в бандле). |
| 4 | **Ни один exe/dll в бандле не подписан Authenticode.** Подписаны только `.sys`. | SmartScreen и AV будут ругаться на весь комплект; свой GUI надо подписывать. |
| 5 | `WinDivert64.sys` подписан **не автором WinDivert**, а сторонней китайской компанией (Sectigo EV, срок истёк 2023-05). | Юридический и репутационный риск; фиксируйте SHA256 и показывайте publisher в UI. |
| 6 | В бандле **нет каталога `ipset/`** и **нет ни одного файла лицензии**. | Готовых списков IP нет; лицензионные тексты добавлять самому. |
| 7 | Два независимых движка: `winws.exe` (zapret1, `--dpi-desync=*`) и `winws2.exe` (zapret2, Lua). | GUI должен поддерживать оба либо сознательно выбрать один. |
| 8 | `winws2.exe` и `lua/*.lua` жёстко связаны `NFQWS2_COMPAT_VER` (=6). | Обновлять exe и lua только вместе, из одного релиза. |

---

## 1. Полная структура каталога

### 1.1 Сводка по каталогам

| Каталог | Файлов | Байт | Назначение |
|---|---:|---:|---|
| `.git` | 29 | 16 268 771 | shallow-клон (pack 16.1 MB). В поставку не идёт. |
| `.github` | 1 | 43 | `lockdown.yml` (`lock: true`) - только для issues/PR upstream. |
| `arm64` | 4 | 226 962 | ARM64-драйвер WinDivert + ARM64 `ip2net`/`mdig` + установщик. |
| `blockcheck` | 87 | 2 054 210 | Анализатор стратегий (zapret1 и zapret2), скрипты, свои копии winws/winws2. |
| `cygwin` | 375 | 37 621 759 | Минимальный cygwin-root: bash, coreutils, gawk, grep, sed, curl, openssl, ca-bundle. |
| `tools` | 1 | 4 096 | `elevator.exe` - запуск процесса с повышением прав. |
| `win7` | 3 | 138 487 | WinDivert 2.2.0-C (совместим с Windows 7) + установщик. |
| `windivert-hide` | 4 | 141 933 | Переименованный драйвер (`Monkey64.sys`) для обхода детекта по имени службы. |
| `zapret-winws` | 33 | 4 243 572 | **Рабочий комплект**: winws, winws2, драйвер, lua, пресеты, службы. |
| **Итого** | **539** | **60 707 269** | Без `.git` около **44.4 MB**. |

### 1.2 `zapret-winws/` - то, что реально шипится

```
zapret-winws/
  winws.exe                      223 232   x64  zapret1, nfqws для Windows (v72.12)
  winws2.exe                     656 384   x64  zapret2, nfqws2 + LuaJIT (v1.0.6)
  WinDivert.dll                   47 616   x64  user-mode API, ставит драйвер сам
  WinDivert64.sys                 94 144   x64  kernel-драйвер WinDivert 2.2.2
  cygwin1.dll                  2 954 293   x64  Cygwin POSIX Emulation DLL 3.4.10
  elevator.exe                     4 096   x64  ShellExecuteExW-раннер (requireAdministrator)
  killall.exe                     20 992   x64  cygwin killall - посылка сигналов winws
  _CMD_ADMIN.cmd                      51         cmd от админа в этой папке
  enable_timestamps.cmd              190         netsh interface tcp set global timestamps=enabled
  windivert_delete.cmd               320         sc stop/delete windivert
  service_create.cmd               2 261         шаблон установки службы (winws)
  service1_create.cmd              2 261         дубль service_create.cmd
  service2_create.cmd              3 183         шаблон установки службы (winws2)
  service_del.cmd                     91         sc delete winws1/winws2
  service_start.cmd                   34         sc start winws1/winws2
  service_stop.cmd                    34         net stop winws1/winws2
  preset1_example.cmd              1 077         интерактивный пример стратегии winws
  preset2_example.cmd              1 853         интерактивный пример стратегии winws2
  preset2_wireguard.cmd              463         обход блокировки WireGuard
  files/list-youtube.txt             277         14 доменов (hostlist)
  files/quic_initial_www_google_com.bin 1 200    фейковый QUIC Initial
  lua/zapret-lib.lua              93 714
  lua/zapret-antidpi.lua          51 866
  lua/zapret-obfs.lua             21 333
  lua/zapret-auto.lua             21 123
  lua/zapret-tests.lua            37 399
  lua/zapret-pcap.lua              1 441
  windivert.filter/README.txt      1 557
  windivert.filter/windivert_part.dht.txt               87
  windivert.filter/windivert_part.discord_media.txt    581
  windivert.filter/windivert_part.quic_initial_ietf.txt 157
  windivert.filter/windivert_part.stun.txt              96
  windivert.filter/windivert_part.wireguard.txt        166
```

**Минимальный рантайм-набор для winws:** `winws.exe` + `cygwin1.dll` + `WinDivert.dll` + `WinDivert64.sys`.
Опционально: `lua/` (нужен только `winws2.exe`), `killall.exe` (только для сигналов), `elevator.exe` (только для .cmd-обёрток), `windivert.filter/` (только если используются `--wf-raw-part`).

### 1.3 `blockcheck/` - анализатор стратегий

```
blockcheck/
  blockcheck.cmd                194   запуск zapret1-анализатора (через cygwin bash)
  blockcheck-kyber.cmd          200   то же с curl-kyber (multi-segment TLS ClientHello)
  blockcheck2.cmd               195   запуск zapret2-анализатора
  blockcheck2-kyber.cmd         201
  zapret/
    blockcheck.sh            64 327   основной скрипт (posix sh)
    blog.sh                     220   обёртка: tee в ../blockcheck.log + unix2dos
    blog_kyber.sh               106   CURL=curl-kyber exec blog.sh
    common/base.sh            8 135
    common/dialog.sh          1 156
    common/elevate.sh           530
    common/fwtype.sh            929
    common/virt.sh              966
    files/fake/*.bin      38 файлов   фейковые пейлоады (TLS CH, QUIC, STUN, WG, DHT)
    ip2net/ip2net.exe        92 672   x64  IP-листы в подсети
    mdig/mdig.exe           130 560   x64  DNS-резолвер
    nfq/{winws.exe, WinDivert.dll, WinDivert64.sys}
  zapret2/
    blockcheck2.sh           53 477
    blockcheck2.d/standard/*.sh   тесты по группам: 10-http-basic, 15-misc, 17-oob,
                                  20-multi, 23-seqovl, 24-syndata, 25-fake, 30-faked,
                                  35-hostfake, 50-fake-multi, 55-fake-faked,
                                  60-fake-hostfake, 90-quic, def.inc
    blockcheck2.d/custom/         пользовательские списки стратегий + README.txt
    lua/*.lua                     те же 6 скриптов, что в zapret-winws/lua
    ip2net/, mdig/, nfq2/{winws2.exe, WinDivert.dll, WinDivert64.sys}
```

`blockcheck2.d/custom/README.txt`: строки можно комментировать `#`; параметры со спецсимволами (`<`) больше не нужно экранировать - строка, которая не парсится как shell, повторяется буквально; пути списков можно переопределить переменными `LIST_HTTP`, `LIST_HTTPS_TLS12`, `LIST_HTTPS_TLS13`, `LIST_QUIC`.

### 1.4 `cygwin/` - среда

Полноценный минимальный cygwin-root (64-bit), 375 файлов:

- `bin/bash.exe` 854 035, `bin/cygwin1.dll` 2 954 293 (3.4.10, Red Hat), `bin/sh.exe` 105 472;
- coreutils, `grep.exe` 227 859, `sed.exe` 176 659, `gawk.exe` 689 683, `find.exe` 269 331, `tar.exe` 501 267, `zip.exe` 204 819, `unzip.exe` 177 171, `xz.exe`, `zstd.exe` 828 435, `nc.exe` 42 003, `nano.exe` 323 603;
- `bin/cygssl-1.1.dll` 523 795 и `bin/cygcrypto-1.1.dll` 2 546 707 - **OpenSSL 1.1.1w**;
- `usr/local/bin/curl.exe` 261 632, `usr/local/bin/curl-kyber.exe` 7 211 008 (статический, post-quantum TLS), `cygssl-3.dll` 1 001 472 / `cygcrypto-3.dll` 5 220 864 - **OpenSSL 3.4.0**;
- `etc/pki/tls/certs/ca-bundle.crt` 641 160 - корневые сертификаты (нужны blockcheck для DoH);
- `cygwin.cmd` / `cygwin-admin.cmd` - вход в bash (`set TERM=`, затем `cd /d %~dp0bin && bash --login -i`).

`cygwin/` нужен **только** для `blockcheck` и ручного шелла. Для работы `winws` он не требуется (там свой `cygwin1.dll` рядом с exe).

### 1.5 `win7/`, `arm64/`, `windivert-hide/`

| Файл | Размер | Архитектура | Подпись Authenticode |
|---|---:|---|---|
| `win7/WinDivert.dll` | 47 104 | x64 | нет |
| `win7/WinDivert64.sys` | 90 456 | x64 | **Valid**; CN=Ars Nova Systems (Nantes, FR), DigiCert EV Code Signing CA (SHA2), 2017-09-22 .. 2020-09-30; SHA256 `66B458D7AD2EA9DB7771F656892BF95A330CF243F4914CD01FBCDBD3B3EAACC3` |
| `arm64/WinDivert64.sys` | 79 808 | **ARM64** | self-signed `CN=divert.com(Test)`, цепочка **не доверенная** (нужен testsigning) |
| `arm64/ip2net.exe` | 23 552 | ARM64 | нет |
| `arm64/mdig.exe` | 122 368 | ARM64 | нет |
| `windivert-hide/WinDivert.dll` | 45 568 | x64 | нет (пересобран/пропатчен) |
| `windivert-hide/Monkey64.sys` | 94 144 | x64 | Valid, тот же publisher, что у основного |

`windivert-hide/README.txt`: попытка скрыть WinDivert от игр/античитов, которые проверяют **имя службы** `windivert` или **имя файла** `WinDivert64.sys`. Служба переименована в `Monkey`, файл - в `Monkey64.sys`. `Monkey64.sys` - побайтовая копия `WinDivert64.sys` (SHA256 совпадает), `WinDivert.dll` пересобран с патчем кода. Если античит проверяет имя устройства (`\Device\WinDivert`) или что-то ещё - не поможет. Удаление: `monkey_delete.cmd` (`sc stop monkey`, `sc delete monkey`).

### 1.6 Дубли и версии

| Артефакт | Копии |
|---|---|
| `winws.exe` (223 232) | `zapret-winws/`, `blockcheck/zapret/nfq/` |
| `winws2.exe` (656 384) | `zapret-winws/`, `blockcheck/zapret2/nfq2/` |
| `WinDivert.dll` (47 616) | `zapret-winws/`, `blockcheck/zapret/nfq/`, `blockcheck/zapret2/nfq2/` |
| `WinDivert64.sys` (94 144) | `zapret-winws/`, `blockcheck/zapret/nfq/`, `blockcheck/zapret2/nfq2/`, `windivert-hide/Monkey64.sys` |
| `cygwin1.dll` (2 954 293) | `cygwin/bin/`, `zapret-winws/` |
| `lua/*.lua` (6 файлов) | `zapret-winws/lua/`, `blockcheck/zapret2/lua/` |

### 1.7 32 vs 64 bit

- **Все 165 PE-файлов - `x64 / PE32+`.** Ни одного `x86/PE32` в бандле нет.
- **3 файла - `ARM64 / PE32+`**: `arm64/WinDivert64.sys`, `arm64/ip2net.exe`, `arm64/mdig.exe`.
- Upstream `docs/windows.md`: "Поскольку 32-битные windows мало востребованы, zapret-win-bundle существует только в варианте для windows x64/arm64".
- `docs/quick_start_windows.md`: "Для 32-битных систем Windows нет готового полного варианта".

### 1.8 Версии компонентов

| Компонент | Версия | Откуда |
|---|---|---|
| `winws.exe` (zapret1) | **v72.12**, commit `5cc46a9815b00e97401b1459984dff44abfec411` | строки `v72.12` и `github version %s (%s)` |
| `winws2.exe` (zapret2) | **v1.0.6**, commit `221d7dff24bfec0cd5ac0a5070b8acef2008af3d` | строки в `winws2.exe` |
| LuaJIT (внутри winws2) | **2.1.1756211046** | строка `LuaJIT 2.1.1756211046` |
| WinDivert | **2.2.2** | PDB-путь в `WinDivert64.sys`: `C:\WinDivert-2.2.2\install\MSVC\amd64\WinDivert64.pdb`; имена WFP-объектов `WinDivert2.2_*` |
| WinDivert (Win7-вариант) | **2.2.0-C** | `win7/install_win7.cmd`: `echo copying windows 7 compatible windivert 2.2.0-C` |
| Cygwin | **3.4.10** | VersionInfo `cygwin1.dll` (Red Hat) |
| zlib | 1.3.1 | VersionInfo / строка `inflate 1.3.1 Copyright 1995-2024 Mark Adler` |
| OpenSSL (cygwin/bin) | 1.1.1w | VersionInfo `cygcrypto-1.1.dll` |
| OpenSSL (curl-kyber) | 3.4.0 | VersionInfo `cygcrypto-3.dll` |
| libiconv / libintl | 1.17 / 0.22.4 | VersionInfo |
| liblzma / xz | 5.4.6 | VersionInfo |
| lz4 | 1.9.4 | VersionInfo |

**Важно для GUI:** у `winws.exe`, `winws2.exe`, `WinDivert.dll`, `WinDivert64.sys`, `elevator.exe`, `killall.exe` **нет ресурса VERSIONINFO** - `Get-Item .VersionInfo` пустой. Версию можно узнать только через `winws.exe --version` или из строк бинаря. Значит "версия бандла" в GUI должна определяться по хешу/манифесту, а не по FileVersion.

---

## 2. Контракт запуска `winws.exe`

### 2.1 Что нужно в рантайме

| Требование | Детали | Подтверждение |
|---|---|---|
| **Права администратора** | Манифест exe: `requestedExecutionLevel level="requireAdministrator"`. Без админа WinDivert не поставит драйвер. | ресурс манифеста в `winws.exe`; `docs/windows.md`: "Для работы с windivert требуются права администратора" |
| **`cygwin1.dll` рядом с exe** | `winws.exe` импортирует `cygwin1.dll` (это cygwin-программа). | таблица импорта `winws.exe`; `docs/windows.md`: "winws требует cygwin1.dll, windivert.dll, windivert64.sys или windivert32.sys" |
| **`WinDivert.dll` рядом с exe** | Импортируется напрямую (`WinDivertOpen`, `WinDivertRecvEx`, `WinDivertSend`, `WinDivertClose`). | импорт `winws.exe` |
| **`WinDivert64.sys` рядом с `WinDivert.dll`** | DLL сама создаёт и стартует kernel-службу, беря `.sys` из своей директории. | строки `WinDivert.dll`: `CreateServiceW`, `OpenServiceW`, `StartServiceW`, `DeleteService`, `WinDivertDriverInstallMutex`, `\WinDivert64.sys` |
| **`wlanapi.dll`** | Импортируется (нужен для `--ssid-filter`). На Windows Server отсутствует по умолчанию. | импорт `winws.exe`; `docs/windows.md`, раздел "Особенности Windows Server" |
| **Рабочий каталог** | **Не важен.** Все пресеты используют абсолютные пути `%~dp0...`. Переменная окружения `EXEDIR` доступна. | `preset1_example.cmd`, `preset2_example.cmd` |
| **Сеть** | Не требуется (никаких внешних серверов). | `readme.md` |

> Тонкость из `docs/windows.md`: если запускать `winws` **из cygwin-шелла**, а рядом лежит `cygwin1.dll`, процесс не стартует (конфликт двух cygwin-сред). Для GUI это неактуально, но объясняет, почему в бандле два независимых комплекта: `zapret-winws/` (со своим `cygwin1.dll`) и `cygwin/` (среда для blockcheck).

### 2.2 Как запускается

Три официальных способа:

1. **Интерактивно** - `preset1_example.cmd` / `preset2_example.cmd`: `start "zapret" /min "%~dp0winws.exe" <args>`.
2. **Служба Windows** - `sc create winws1 binPath= "\"%~dp0winws.exe\" %ARGS%" DisplayName= "zapret DPI bypass : winws1" start= auto` (`service_create.cmd`, `service2_create.cmd`). `winws.exe` умеет работать как служба: в импортах есть `StartServiceCtrlDispatcherA`, `RegisterServiceCtrlHandlerA`, `SetServiceStatus`.
3. **Планировщик задач** - `task_*.cmd` (в этом бандле отсутствуют, упомянуты в `docs/quick_start_windows.md`).

**Глобальный мьютекс:** строка `Global\winws_windivert_mutex` - защита от одновременного захвата WinDivert несколькими экземплярами.

**Для GUI:** самый предсказуемый путь - `CreateProcess` без `CREATE_NEW_CONSOLE` (или с `CREATE_NO_WINDOW`), с `lpCurrentDirectory` = каталог бандла, и с обязательным UAC-elevation самого GUI либо отдельного лаунчера. Аргументы передавать массивом, а не одной строкой: пути в бандле содержат пробелы, а upstream требует экранирования `\"` внутри .cmd.

### 2.3 Конфиг-файл

```
@<config_file>|$<config_file>
; read file for options. must be the only argument. other options are ignored.
```

То есть `winws.exe @zapret.conf` - и **никаких других аргументов**. Кодировка файла: **UTF-8 без BOM** (`docs/windows.md`). Это самый удобный способ для GUI: генерировать один файл конфигурации и не мучиться с экранированием в командной строке.

### 2.4 Полный справочник опций `winws.exe`

Ниже - полный список, извлечённый из встроенного help-текста в `winws.exe` (строки, начинающиеся с ` --` и `;`). Сгруппировано и с комментариями.

#### A. Служебные / процесс

| Опция | Смысл |
|---|---|
| `@<file>` / `$<file>` | читать опции из файла; должен быть единственным аргументом |
| `--debug=0/1/syslog/@<filename>` | 0/1 - уровень, `syslog`, либо лог в файл |
| `--version` | напечатать версию и выйти |
| `--dry-run` | проверить параметры и выйти с кодом 0 |
| `--comment=any_text` | игнорируется (метка профиля) |
| `--daemon` | демонизировать (в Windows практически бесполезно) |
| `--pidfile=<filename>` | записать PID в файл |
| `--ctrack-timeouts=S:E:F[:U]` | таймауты conntrack для SYN / ESTABLISHED / FIN / UDP (по умолчанию 60:300:60:60) |
| `--ctrack-disable=[0/1]` | 1 или без аргумента - отключить conntrack |
| `--ipcache-lifetime=<int>` | время жизни кэша hop count и доменных имён, сек (0 = бесконечно) |
| `--ipcache-hostname=[0/1]` | кэшировать ip -> hostname |

#### B. Фильтр WinDivert (что вообще перехватывать)

| Опция | Смысл |
|---|---|
| `--wf-iface=<int>[.<int>]` | номера интерфейса и субинтерфейса (`netsh int ip show int`) |
| `--wf-l3=ipv4/ipv6` | фильтр L3, можно список через запятую |
| `--wf-tcp=[~]port1[-port2]` | порты TCP, `~` - отрицание, список через запятую |
| `--wf-udp=[~]port1[-port2]` | порты UDP |
| `--wf-raw-part=<filter>/@<file>` | частичный raw-фильтр WinDivert; можно несколько; складывается по OR с `--wf-tcp`/`--wf-udp` |
| `--wf-filter-lan=0/1` | добавить исключение не-глобальных IP (по умолчанию 1) |
| `--wf-raw=<filter>/@<file>` | **полный** raw-фильтр, заменяет `--wf-tcp`, `--wf-udp`, `--wf-raw-part` |
| `--wf-save=<filename>` | сохранить итоговый фильтр в файл и выйти |

> Максимальный размер фильтра - **16 Kb** (`docs/windows.md`). Фильтрация идёт в ядре, поэтому `--wf-raw-part` критичен для CPU: без него winws получает весь поток.

#### C. Фильтр по логической сети

| Опция | Смысл |
|---|---|
| `--ssid-filter=ssid1[,ssid2,...]` | включать winws только если подключён любой из SSID (сравнение регистрозависимое) |
| `--nlm-filter=net1[,net2,...]` | то же по именам/GUID сетей Network List Manager |
| `--nlm-list[=all]` | вывести список сетей NLM (только подключённые или все) |

#### D. Мультистратегия (профили)

| Опция | Смысл |
|---|---|
| `--new` | начать новый профиль (стратегию) |
| `--skip` | не использовать этот профиль (временная деактивация) |

Профили применяются по порядку; выигрывает первый, чьи фильтры совпали. Профиль `hostlist-auto` имеет приоритет.

#### E. Фильтры внутри профиля

| Опция | Смысл |
|---|---|
| `--filter-l3=ipv4/ipv6` | версия IP для текущего профиля |
| `--filter-tcp=[~]port1[-port2]/*` | порты TCP; установка TCP-фильтра без UDP-фильтра **запрещает UDP** |
| `--filter-udp=[~]port1[-port2]/*` | порты UDP; симметрично |
| `--filter-l7=http/tls/quic/wireguard/dht/discord/stun/unknown` | фильтр L6-L7 по распознанному протоколу |

#### F. Ограничители по хостам и IP

| Опция | Смысл |
|---|---|
| `--hostlist=<file>` | дурить только перечисленные хосты; поддомены применяются автоматически; gzip поддерживается; список можно задавать многократно |
| `--hostlist-domains=<list>` | фиксированный список доменов через запятую |
| `--hostlist-exclude=<file>` | исключающий hostlist |
| `--hostlist-exclude-domains=<list>` | фиксированный исключающий список |
| `--hostlist-auto=<file>` | автоматически детектировать блокировки и пополнять список |
| `--hostlist-auto-fail-threshold=<int>` | сколько "провалов" нужно для добавления хоста (по умолчанию 3) |
| `--hostlist-auto-fail-time=<int>` | окно времени для этих провалов, сек (по умолчанию 60) |
| `--hostlist-auto-retrans-threshold=<int>` | сколько ретрансмиссий считать провалом (по умолчанию 3) |
| `--hostlist-auto-debug=<logfile>` | лог срабатываний autohostlist |
| `--ipset=<file>` | включающий список IP/CIDR (v4 и v6, gzip, несколько файлов) |
| `--ipset-ip=<list>` | фиксированный список подсетей через запятую |
| `--ipset-exclude=<file>` | исключающий ipset |
| `--ipset-exclude-ip=<list>` | фиксированный исключающий список |

#### G. TAMPER: модификация окна, TTL, флагов, дубликаты

| Опция | Смысл |
|---|---|
| `--wsize=<size>[:<scale>]` | менять window size в SYN,ACK. **OBSOLETE** |
| `--wssize=<size>[:<scale>]` | window size для серверных пакетов (scale по умолчанию 0) |
| `--wssize-cutoff=[n/d/s]N` | применять только к пакетам с номером меньше N |
| `--wssize-forced-cutoff=0/1` | 1 (по умолчанию) - авто-отключение wssize при распознавании протокола |
| `--synack-split=[syn/synack/acksyn]` | split handshake: отправить только SYN, SYN+ACK или ACK+SYN |
| `--orig-ttl=<int>` / `--orig-ttl6=<int>` | TTL / hop limit для оригинальных пакетов |
| `--orig-autottl=[<delta>[:<min>[-<max>]]/-]` | авто-TTL (по умолчанию +5:3-64); `-` отключает |
| `--orig-autottl6=...` | переопределение для IPv6 |
| `--orig-tcp-flags-set=<int/0xHEX/flaglist>` | установить TCP-флаги (`FIN,SYN,RST,PSH,ACK,URG,ECE,CWR,AE,R1,R2,R3`) |
| `--orig-tcp-flags-unset=...` | снять флаги |
| `--orig-mod-start=[n/d/s]N` / `--orig-mod-cutoff=[n/d/s]N` | диапазон применения модификации оригинала |
| `--dup=<int>` | отправить N дубликатов до оригинала |
| `--dup-replace=[0/1]` | 1 или без аргумента - оригинал не отправлять |
| `--dup-ttl=<int>` / `--dup-ttl6=<int>` | TTL дубликатов |
| `--dup-autottl=...` / `--dup-autottl6=...` | авто-TTL дубликатов (по умолчанию +1:3-64) |
| `--dup-tcp-flags-set=...` / `--dup-tcp-flags-unset=...` | флаги дубликатов |
| `--dup-fooling=<mode>[,<mode>]` | `none md5sig badseq badsum datanoack ts hopbyhop hopbyhop2` |
| `--dup-ts-increment=<int/0xHEX>` | инкремент TSval (по умолчанию -600000) |
| `--dup-badseq-increment=<int/0xHEX>` | инкремент seq (по умолчанию -10000) |
| `--dup-badack-increment=<int/0xHEX>` | инкремент ack (по умолчанию -66000) |
| `--dup-ip-id=same/zero/seq/rnd` | режим ip_id для дубликатов |
| `--dup-start=[n/d/s]N` / `--dup-cutoff=[n/d/s]N` | диапазон применения dup |
| `--ip-id=zero/seq/seqgroup/rnd` | схема назначения ip_id для сгенерированных пакетов |
| `--hostcase` | `Host:` -> `host:` |
| `--hostspell` | точное написание заголовка Host (ровно 4 символа, по умолчанию `host`) |
| `--hostnospace` | убрать пробел после `Host:` и перенести его в `User-Agent:` (сохраняя размер пакета) |
| `--domcase` | `Host: TeSt.cOm` |
| `--methodeol` | добавить `\n` перед методом и убрать пробел из `Host:` |

Несовместимость: `--hostnospace` и `--methodeol` вместе запрещены (строка `--hostnospace and --methodeol are incompatible`).

#### H. Основное: `--dpi-desync=*`

`--dpi-desync=[<mode0>,]<mode>[,<mode2>]` - попытка рассинхронизировать состояние DPI. Режимы:

`synack syndata fake fakeknown rst rstack hopbyhop destopt ipfrag1 multisplit multidisorder fakedsplit fakeddisorder hostfakesplit ipfrag2 udplen tamper`

| Опция | Смысл |
|---|---|
| `--dpi-desync-ttl=<int>` / `--dpi-desync-ttl6=<int>` | TTL / hop limit для фейковых пакетов |
| `--dpi-desync-autottl=[<delta>[:<min>[-<max>]]/-]` | авто-TTL (по умолчанию 1:3-20) |
| `--dpi-desync-autottl6=...` | переопределение для IPv6 |
| `--dpi-desync-tcp-flags-set=...` / `-unset=...` | флаги фейков |
| `--dpi-desync-fooling=<mode>[,<mode>]` | `none md5sig badseq badsum datanoack ts hopbyhop hopbyhop2` - как не дать фейку дойти до сервера |
| `--dpi-desync-repeats=<N>` | отправить каждый desync-пакет N раз |
| `--dpi-desync-skip-nosni=0/1` | 1 (по умолчанию) - не трогать ClientHello без SNI (ESNI/ECH) |
| `--dpi-desync-split-pos=N/-N/marker+N/marker-N` | список позиций разреза. Маркеры: `method,host,endhost,sld,endsld,midsld,sniext`. Полный список используют только `multisplit`/`multidisorder`; `fakedsplit`/`fakeddisorder` берут первый l7-совместимый параметр либо первое абсолютное значение |
| `--dpi-desync-split-seqovl=N/...` | перекрытие sequence перед первым отправленным сегментом |
| `--dpi-desync-split-seqovl-pattern=<...>` | чем заполнять фейковую часть overlap |
| `--dpi-desync-fakedsplit-pattern=<...>` | фейковый паттерн для fakedsplit/fakeddisorder |
| `--dpi-desync-fakedsplit-mod=mod[,mod]` | `none, altorder=0/1/2/3 + 0/8/16` |
| `--dpi-desync-hostfakesplit-midhost=marker+N/...` | дополнительный разрез реального hostname |
| `--dpi-desync-hostfakesplit-mod=mod[,mod]` | `none, host=<hostname>, altorder=0/1` |
| `--dpi-desync-ipfrag-pos-tcp=<8..9216>` | позиция IP-фрагментации TCP, кратно 8, по умолчанию 32 |
| `--dpi-desync-ipfrag-pos-udp=<8..9216>` | то же для UDP, по умолчанию 8 |
| `--dpi-desync-ts-increment=<int/0xHEX>` | инкремент TSval (по умолчанию -600000) |
| `--dpi-desync-badseq-increment=<int/0xHEX>` | инкремент seq (по умолчанию -10000) |
| `--dpi-desync-badack-increment=<int/0xHEX>` | инкремент ack (по умолчанию -66000) |
| `--dpi-desync-any-protocol=0/1` | 0 (по умолчанию) - только http и tls; 1 - любой непустой пакет данных |
| `--dpi-desync-start=[n/d/s]N` / `--dpi-desync-cutoff=[n/d/s]N` | диапазон применения desync |
| `--dpi-desync-fake-tcp-mod=mod[,mod]` | `none, seq` |

**Фейковые пейлоады (все форматы: `[+ofs]@<file>` или `0xHEX`):**

| Опция | Для чего |
|---|---|
| `--dpi-desync-fake-http=` | фейковый HTTP-запрос (по умолчанию - запрос к www.iana.org) |
| `--dpi-desync-fake-tls=` | фейковый TLS ClientHello; `!` = стандартный фейк, `![+offset]` = стандартный со смещением |
| `--dpi-desync-fake-tls-mod=mod[,mod]` | `none, rnd, rndsni, sni=<sni>, dupsid, padencap` |
| `--dpi-desync-fake-unknown=` | неизвестный протокол (по умолчанию 256 нулей) |
| `--dpi-desync-fake-syndata=` | payload для режима `syndata` |
| `--dpi-desync-fake-quic=` | фейковый QUIC Initial |
| `--dpi-desync-fake-wireguard=` | фейковый WireGuard handshake initiation |
| `--dpi-desync-fake-dht=` | фейковый DHT (по умолчанию 64 нуля) |
| `--dpi-desync-fake-discord=` | Discord Voice IP Discovery (по умолчанию 64 нуля) |
| `--dpi-desync-fake-stun=` | STUN (по умолчанию 64 нуля) |
| `--dpi-desync-fake-unknown-udp=` | неизвестный UDP (по умолчанию 64 нуля) |
| `--dpi-desync-udplen-increment=<int>` | изменить длину UDP-пакета на N байт (отрицательные - уменьшить) |
| `--dpi-desync-udplen-pattern=<...>` | чем добивать хвост UDP (по умолчанию нули) |

> Обратите внимание: `--dpi-desync-fwmark`, `--uid`, `--user`, `--qnum`, `--port`, `--bind-fix4/6`, `--dpi-desync-split-pos` в unix-варианте - часть из них в Windows **отсутствует** (нет iptables/NFQUEUE). В help-тексте `winws.exe` их нет: `--uid`, `--user`, `--qnum`, `--fwmark`, `--bind-fix*` исключены.

### 2.5 Отличия `winws2.exe` (zapret2)

`winws2.exe` - другой движок, другой контракт:

| Аспект | `winws.exe` | `winws2.exe` |
|---|---|---|
| Модель | императивные `--dpi-desync=*` | **Lua-функции** `--lua-desync=func:arg=val` |
| Загрузка кода | нет | `--lua-init=@file` или `--lua-init="lua code"` (gzip поддерживается), порядок сохраняется |
| Бинарные блобы | только через `--dpi-desync-fake-*` | `--blob=<name>:@<file>` + `0xHEX` прямо в аргументах |
| Фильтры WinDivert | `--wf-tcp`, `--wf-udp` | `--wf-tcp-in`, `--wf-tcp-out`, `--wf-udp-in`, `--wf-udp-out`, `--wf-icmp-in/out`, `--wf-ipp-in/out`, `--wf-tcp-empty` |
| Raw-фильтры | `--wf-raw-part`, `--wf-raw` | `--wf-raw-part` (OR), `--wf-raw-filter` (AND, только один), `--wf-raw` |
| Профили | `--new`, `--skip` | `--new[=<name>]`, `--name=`, `--template[=]`, `--cookie[=]`, `--import=`, `--skip` |
| Диапазоны пакетов | `--dpi-desync-start/cutoff` | `--out-range=`, `--in-range=` (режимы `n d s p b x a`) |
| Прочее | - | `--intercept=0/1` (0 = выполнить lua-init и выйти), `--chdir[=path]`, `--writable[=<dir>]`, `--lua-gc=<int>`, `--server=[0/1]`, `--payload-disable`, `--reasm-disable`, `--wf-dup-check`, `--wf-filter-loopback` |

`winws2.exe` требует `--lua-init=@zapret-lib.lua` **и** `--lua-init=@zapret-antidpi.lua` (в таком порядке) - иначе `--lua-desync` не с чем работать. Это видно во всех пресетах и в `blockcheck2.sh`.

---

## 3. WinDivert

### 3.1 Что это и какая версия

WinDivert (Windows Packet Divert) - библиотека перехвата/модификации/инъекции пакетов из user mode через драйвер WFP. В Linux эту роль играют iptables + NFQUEUE; в Windows их нет, поэтому zapret использует WinDivert как транспорт.

**Версия в бандле: 2.2.2.**
Доказательства:
- PDB-путь внутри `zapret-winws/WinDivert64.sys`: `C:\WinDivert-2.2.2\install\MSVC\amd64\WinDivert64.pdb`;
- имена объектов WFP: `WinDivert2.2_SubLayerInboundNetworkIPv4`, `WinDivert2.2_CalloutOutboundNetworkIPv6`, `WinDivert2.2_Filter*` и т.д. (UTF-16 строки в `.sys`);
- ETW-провайдер `WinDivert provider`, ключ EventLog `System\CurrentControlSet\Services\EventLog\System\WinDivert`.

Состав:
| Файл | Роль |
|---|---|
| `WinDivert.dll` (47 616) | user-mode API: `WinDivertOpen/RecvEx/Send/SetParam/Shutdown`, helper-функции разбора пакетов и компиляции фильтров. Также **устанавливает драйвер**. |
| `WinDivert64.sys` (94 144) | kernel-драйвер WFP-каллаутов. Содержит `\Device\WinDivert`, `\??\WinDivert`, обработчики `LOAD`/`UNLOAD`. |

### 3.2 Как ставится драйвер

Автоматически, при первом `WinDivertOpen()`:

1. `WinDivert.dll` берёт mutex `WinDivertDriverInstallMutex` (защита от гонки установки).
2. Проверяет службу `WinDivert` (`OpenServiceW`), при отсутствии создаёт её (`CreateServiceW`) как **kernel-драйвер** и стартует (`StartServiceW`).
3. Файл драйвера берётся из **каталога, где лежит `WinDivert.dll`** (строка `\WinDivert64.sys`), а не из `System32\drivers`. Именно поэтому `.sys` обязан лежать рядом с `winws.exe`.
4. Драйвер создаёт устройство `\Device\WinDivert` и набор WFP-каллаутов (inbound/outbound/forward для IPv4 и IPv6).

**Следствия для GUI:**
- «Установка» не требует отдельного шага, но **требует администратора** - создание kernel-службы без прав невозможно.
- Если `.sys` нет рядом с DLL, `WinDivertOpen` упадёт с ошибкой; текст ошибки в `winws.exe`: `windivert: error opening filter: %s` и подсказка `windivert: try to disable secure boot and install OS patches`.
- Успешный старт логируется как `windivert initialized. capture is started.`

### 3.3 Права

| Что | Требование |
|---|---|
| Создание/старт службы драйвера | локальный администратор (UAC, `SeLoadDriverPrivilege` через admin-токен) |
| Загрузка драйвера в ядро | драйвер должен иметь валидную подпись (или testsigning) |
| Работа с уже загруженным драйвером | формально тоже admin-контекст (WinDivert требует elevated handle) |

### 3.4 Подписи драйверов - критично

| Файл | Статус | Издатель | Срок | SHA256 |
|---|---|---|---|---|
| `zapret-winws/WinDivert64.sys` | Valid | `CN=成都密思听科技有限公司` (Chengdu, Sichuan, CN), issuer `Sectigo Public Code Signing CA EV R36` | 2022-05-25 .. 2023-05-26 (есть timestamp) | `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2` |
| `windivert-hide/Monkey64.sys` | Valid | тот же | тот же | **тот же** (побайтовая копия) |
| `win7/WinDivert64.sys` | Valid | `CN=Ars Nova Systems` (Nantes, FR), issuer `DigiCert EV Code Signing CA (SHA2)` | 2017-09-22 .. 2020-09-30 | `66B458D7AD2EA9DB7771F656892BF95A330CF243F4914CD01FBCDBD3B3EAACC3` |
| `arm64/WinDivert64.sys` | **UnknownError** | self-signed `CN=divert.com(Test)` | 2023-03-16 .. 2040-01-01 | `55CD5C827A28FC91448F2AD1AE629E3FC951F07726819AEB035969DFBF3712C9` |

> **Это главный комплаенс-риск всего бандла.** Драйвер подписан не Basil Forrester (автором WinDivert) и не сообществом zapret, а сторонней китайской компанией с EV-сертификатом, истёкшим в мае 2023. Подпись валидна только благодаря timestamp. `readme.md` прямо предлагает «сравнить файлы с оригиналами на reqrypt.org» - то есть upstream сам признаёт, что файлы переподписаны.
> Для GUI: фиксируйте SHA256 в манифесте поставки, показывайте издателя в диалоге установки, не утверждайте «подписано разработчиком WinDivert».

### 3.5 Совместимость версий

- `.sys` и `.dll` должны быть **из одного релиза**. Смешивать 2.2.2 `.dll` с 2.2.0 `.sys` нельзя: меняется ABI WFP-каллаутов и структура параметров.
- Windows 7: официальный 2.2.2 не грузится (нет SHA-256 кода в ядре до ESU). Решение - `win7/install_win7.cmd`, который подменяет **оба** файла (`WinDivert.dll` + `WinDivert64.sys`) на 2.2.0-C во всех трёх местах: `zapret-winws/`, `blockcheck/zapret/nfq/`, `blockcheck/zapret2/nfq2/`.
- Windows 11 ARM64: подписанного ARM64-драйвера не существует. `arm64/install_arm64.cmd` включает `bcdedit /set {current} testsigning on` и копирует **неподписанный** ARM64 `.sys` во все три места. Остальные бинари остаются x64 и работают через эмуляцию x64.
- Windows 8/8.1/10/11 x64: работает «как есть», но при включённом Secure Boot возможны проблемы с загрузкой драйвера - upstream советует отключить Secure Boot или включить testsigning.

### 3.6 Взаимодействие с AV/EDR

`readme.md` (раздел АНТИВИРУСЫ / ANTIVIRUS WARNING):

> «windivert может вызвать реакцию антивируса... Он может использоваться как хорошими, так и плохими программами, но сам по себе не является вирусом. Драйвер windivert64.sys подписан для возможности загрузки в 64-битное ядро windows. Любой желающий может сравнить файлы с оригиналами от автора. Но антивирусы склонны относить подобное к классам повышенного риска или хакерским инструментам. В случае проблем используйте исключения или выключайте антивирус совсем.»

Дополнительно `docs/quick_start_windows.md`: «cygwin так же имеет внушительный список несовместимостей с антивирусами».

Практика, которую стоит заложить в GUI:
- детект установленного AV (WMI `AntiVirusProduct` / `Get-MpComputerStatus`) и предложение добавить исключение на папку бандла;
- предупреждение, что драйвер может быть помечен как `HackTool`/`PUA`/`RiskWare`;
- **не** пытаться автоматически отключать AV - это отпугнёт пользователей и может нарушить политику EDR в корпоративной среде;
- `windivert-hide/` **не включать** в поставку: это обход детекта античитов, в играх это бан, а юридически - сомнительная функциональность. В самом README сказано, что помогает только от наивных проверок по имени службы.

### 3.7 Удаление

```
sc stop windivert
sc delete windivert
```
- `zapret-winws/windivert_delete.cmd` - интерактивно (`choice /C YN`), либо `windivert_delete.cmd del` для неинтерактивного режима; сам скрипт поднимает права через `elevator.exe`.
- **Порядок важен:** сначала остановить `winws.exe` (иначе `sc stop` вернёт «служба занята», т.к. открыт handle на устройство), затем `sc stop`, затем `sc delete`.
- Сам `.sys` остаётся в каталоге бандла - его удаляют вместе с папкой. Реестровой «грязи» после `sc delete` не остаётся.
- Альтернатива из `docs/quick_start_windows.md`: «достаточно закрыть окно с winws и запустить windivert_delete.cmd. Альтернатива - перезагрузить компьютер. После чего можно удалить папку с zapret.»
- Для `windivert-hide`: `monkey_delete.cmd` (`sc stop monkey`, `sc delete monkey`).

---

## 4. `lua/` - самое важное отличие zapret2

### 4.1 Что это

Шесть Lua-скриптов, которые превращают `winws2.exe` из набора жёстко зашитых `--dpi-desync` в **программируемый движок обработки пакетов**. Скрипты лежат в двух местах (идентичны по размеру и содержимому):

- `zapret-winws/lua/` - рабочий комплект;
- `blockcheck/zapret2/lua/` - для анализатора.

`winws.exe` (zapret1, v72.12) **Lua не использует вообще**. Lua - только zapret2.

### 4.2 Как загружается

| Механизм | Описание |
|---|---|
| `--lua-init=@<file>` | выполнить файл. Порядок выполнения сохраняется между несколькими `--lua-init`. |
| `--lua-init="<lua code>"` | выполнить строку кода. Пример из `preset2_example.cmd`: `--lua-init="fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')"`. |
| gzip | `could not open lua file '%s' or '%s.gz'` - файлы `.lua.gz` поддерживаются. |
| `--blob=<name>:@<file>` | загрузить бинарные данные в глобальную Lua-переменную (`--blob=quic_google:@files\quic_initial_www_google_com.bin`). |
| `--lua-desync=<func>[:arg=val[:arg=val]]` | вызвать функцию на каждом пакете, прошедшем фильтры профиля. Порядок = порядок аргументов. |
| `--lua-gc=<int>` | принудительный GC каждые N секунд (0 = выключить). |
| `--writable[=<dir>]` | создать каталог для записи и передать его в Lua как `WRITABLE` (нужно `zapret-pcap.lua`). |
| `--intercept=0` | выполнить `lua-init` и выйти (режим проверки синтаксиса - удобно для GUI). |

**Версионная привязка:** `zapret-lib.lua` начинается с

```lua
NFQWS2_COMPAT_VER_REQUIRED=6

if NFQWS2_COMPAT_VER~=NFQWS2_COMPAT_VER_REQUIRED then
	error("Incompatible NFQWS2_COMPAT_VER. Use pktws and lua scripts from the same release !")
end
```

То есть **exe и lua обязаны быть из одного релиза**. GUI должен хранить их парой и проверять совместимость при обновлении.

### 4.3 `zapret-lib.lua` (93 714) - ядро

Базовая библиотека (~130 функций). Категории:

- **dissect/reconstruct**: `dis_reconstruct_l3`, `tls_dissect`, `tls_reconstruct`, `tls_dissect_ext`, `quic_tvb`, `http_dissect_req`, `http_dissect_headers`, `http_reconstruct_req`, `dis_l4_ports`, `dis_reverse`;
- **позиции**: `pos_get`, `pos_get_pos`, `pos_array_normalize`, `zero_based_pos`, `resolve_multi_pos`;
- **fooling/TTL**: `apply_fooling`, `apply_ip_id`, `autottl`, `parse_autottl`, `parse_tcp_flags`, `insert_ip6_exthdr`, `find_tcp_option`;
- **rawsend**: `rawsend_dissect_ipfrag`, `rawsend_dissect_segmented`, `rawsend_payload_segmented`, `rawsend_opts`;
- **TLS-модификации**: `tls_client_hello_mod`, `tls_mod_shim`, `is_tls_hello`, `tls_handshake_full`;
- **крипто/утилиты**: `hkdf`, `string2hex`, `gunzip_file`, `gzip_file`, `readfile`, `writefile`, `writable_file_name`, `bcryptorandom`;
- **оркестрация**: `orchestrate`, `desync_orchestrator_example`, `verdict_aggregate`, `plan_instance_execute`, `instance_cutoff_shim`.

Ключевые константы вердиктов: `VERDICT_PASS`, `VERDICT_DROP`, `VERDICT_MODIFY`, `VERDICT_PRESERVE_NEXT`.

### 4.4 `zapret-antidpi.lua` (51 866) - сами desync-функции

Это «аналог `--dpi-desync`» для zapret2. Функции (имена = аргумент `--lua-desync=`):

| Функция | Аналог в zapret1 / смысл |
|---|---|
| `fake` | `--dpi-desync=fake` - отправить фейковый пакет |
| `fakedsplit` | `fakedsplit` - разрез с фейковым первым сегментом |
| `fakeddisorder` | `fakeddisorder` |
| `multisplit` | `multisplit` |
| `multidisorder` | `multidisorder` |
| `multidisorder_legacy` | старая реализация (совместимость) |
| `hostfakesplit` | `hostfakesplit` |
| `syndata` | `syndata` - payload в SYN |
| `synack` / `synack_split` | `synack`, split handshake |
| `rst` | `rstack` - отправка RST |
| `oob` | out-of-band байт |
| `tcpseg` | ручная TCP-сегментация |
| `udplen` | изменение длины UDP |
| `wsize` / `wssize` | изменение TCP window |
| `drop` | дропнуть пакет |
| `send` | `--dup` - отправить копию (с `delay=<msec>` - отложенно) |
| `pktmod` | `--orig-*` - модифицировать текущий пакет |
| `http_hostcase` / `http_domcase` / `http_methodeol` / `http_unixeol` | `--hostcase`, `--domcase`, `--methodeol` |
| `dht_dn` | подмена DHT |
| `tls_client_hello_clone` | клонировать ClientHello |

Стандартные аргументы (из шапки файла): `dir=in/out/any`, `ip_ttl`, `ip6_ttl`, `ip_autottl=delta,min-max`, `ip6_autottl`, `ip6_hopbyhop[=hex]`, `ip6_hopbyhop2`, `ip6_destopt`, `ip6_routing`, `ip6_ah`, `tcp_seq`, `tcp_ack`, `tcp_ts`, `tcp_md5[=hex]`, `tcp_flags_set/unset`, `tcp_ts_up`, `tcp_nop_del`, `badsum`, `repeats`, `ifout`, `fwmark`, `payload`, `ip_id`, `ip_id_conn`, `ipfrag`, `ipfrag_disorder`, `ipfrag_pos_tcp/udp/icmp`, `ipfrag_next`, `fool=<func>`.

### 4.5 `zapret-auto.lua` (21 123) - оркестрация и автопилот

Позволяет принимать решения по хосту/соединению, а не по одному пакету:

- `repeater:instances=N:repeats=M` - повторять цепочку desync;
- `circular` - циклический перебор стратегий;
- `stopif:iff=<cond>` - прервать цепочку по условию;
- `condition:iff=<cond>` / `per_instance_condition` / `require_iff`;
- условия: `cond_true`, `cond_false`, `cond_random`, `cond_lua`, `cond_payload_str`, `cond_tcp_has_ts`;
- `automate_host_record`, `automate_conn_record`, `standard_hostkey` - per-host/per-connection хранилище состояния;
- `standard_failure_detector`, `standard_success_detector`, `automate_failure_check`, `automate_failure_counter` - детекторы блокировки (по ретрансмиссиям, по RST, по редиректу);
- `is_dpi_redirect` - детект HTTP-редиректа от DPI.

Это то, что позволяет делать «умные» стратегии: попробовать один вариант, при неудаче - другой, запомнить результат для хоста.

### 4.6 `zapret-obfs.lua` (21 333) - обфускация протоколов

| Функция | Что делает |
|---|---|
| `wgobfs:secret=<str>[:padmin=N][:padmax=N]` | шифрует WireGuard initiation/response/cookie (HKDF-SHA256 + AES) и меняет размер UDP-пакета. Данные и keepalive не шифруются. Требует одинакового секрета на обоих концах и **уменьшения MTU** интерфейса WG. |
| `ippxor:ippxor=<0..255>[:dataxor=<blob>]` | XOR номера IP-протокола (и опционально payload) - маскировка под другой протокол. |
| `udp2icmp[:ctype/code/stype/scode][:dataxor=]` | упаковка UDP-датаграммы в ICMP без изменения размера пакета; ICMP id = sport xor dport (для прохода NAT). Одна сторона - `--server`. |
| `synhide[:synack][:ghost=N]` | прячет SYN / подменяет handshake. |

Все четыре рассчитаны на схему «клиент + сервер» (два узла zapret2), то есть это уже не «обход DPI у провайдера», а туннельная обфускация.

### 4.7 `zapret-pcap.lua` (1 441) и `zapret-tests.lua` (37 399)

- `pcap:file=<name>[:keep]` - писать пакеты в pcap-файл (нужен `--writable=`). Незаменимо для отладки в GUI.
- `zapret-tests.lua` - самотесты C-функций (`test_all()`): crypto, bin, time, gzip, ipstr, dissect, csum, resolve, get_source_ip, ifaddrs, rawsend, timer. Запуск: `--lua-init=@zapret-lib.lua --lua-init=@zapret-tests.lua --lua-init="test_all()"`.

### 4.8 Что реально делают shipped-пресеты

`preset2_example.cmd` (полный текст, 38 строк) - это эталонный пример, который GUI должен уметь генерировать:

```
--wf-tcp-out=80,443
--lua-init=@lua\zapret-lib.lua --lua-init=@lua\zapret-antidpi.lua
--lua-init="fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')"
--blob=quic_google:@files\quic_initial_www_google_com.bin
--wf-raw-part=@windivert.filter\windivert_part.{discord_media,stun,wireguard,quic_initial_ietf}.txt
  профиль 1: tcp 80, l7=http,  out-range=-d10, payload=http_req
             lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:tcp_md5
             lua-desync=fakedsplit:ip_autottl=-2,3-20:tcp_md5
  профиль 2: tcp 443, l7=tls, hostlist=list-youtube.txt, payload=tls_client_hello
             lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com
             lua-desync=multidisorder:pos=1,midsld
  профиль 3: tcp 443, l7=tls (без hostlist)
             lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6
             lua-desync=multidisorder:pos=midsld
  профиль 4: udp 443, l7=quic, hostlist -> lua-desync=fake:blob=quic_google:repeats=11
  профиль 5: udp 443, l7=quic -> lua-desync=fake:blob=fake_default_quic:repeats=11
  профиль 6: l7=wireguard,stun,discord -> fake:blob=0x000...0:repeats=2
```

`preset2_wireguard.cmd` - отдельный сценарий для WireGuard на любых портах: `repeater:instances=2:repeats=3` + `luaexec:code="desync.rnd=brandom(math.random(32,64))"` + `fake:blob=rnd`.

`preset1_example.cmd` (winws, без Lua) - зеркальный пример на `--dpi-desync`: `fake,fakedsplit` + `--dpi-desync-autottl=2` + `--dpi-desync-fooling=md5sig` для tcp/80, `fake,multidisorder` + `--dpi-desync-split-pos=1,midsld` + `repeats=11` для tcp/443 по hostlist, `multidisorder` + `fooling=badseq,md5sig` + `repeats=6` для остального tcp/443, `fake` для QUIC.

**Вывод для GUI:** `lua/` - это не «плагины на будущее», а **основной способ строить стратегии в zapret2**. Если GUI ориентируется на zapret2, ему нужен редактор цепочек `--lua-desync` с валидацией имён функций по `zapret-antidpi.lua` и аргументов по шапке файла.

---

## 5. `ipset/` и `files/`

### 5.1 Главное: каталога `ipset/` в бандле НЕТ

Проверено рекурсивно: ни одного файла или каталога с именем, содержащим `ipset`. Каталог `ipset/` существует только в **основном** репозитории `github.com/bol-van/zapret/ipset` (там лежат `zapret-hosts-google.txt`, `zapret-hosts-user.txt`, `zapret-hosts-user-exclude.txt`, `zapret-hosts-user-ipban.txt` и генераторы на основе `ip2net`).

В `zapret-win-bundle` поставляются **только**:

| Путь | Размер | Что это |
|---|---:|---|
| `zapret-winws/files/list-youtube.txt` | 277 | hostlist, 14 доменов, plain text |
| `zapret-winws/files/quic_initial_www_google_com.bin` | 1 200 | фейковый QUIC Initial (бинарный блоб) |
| `blockcheck/zapret/files/fake/*.bin` | 38 файлов, 74..1812 | фейковые пейлоады для анализатора |
| `blockcheck/zapret2/blockcheck2.d/custom/list_*.txt` | 261..500 | **списки стратегий** для blockcheck2, не hostlist |

`zapret-winws/files/list-youtube.txt` целиком:

```
googlevideo.com
youtubei.googleapis.com
ytimg.com
yt3.ggpht.com
yt4.ggpht.com
youtube.com
youtubeembeddedplayer.googleapis.com
ytimg.l.google.com
jnn-pa.googleapis.com
youtube-nocookie.com
youtube-ui.l.google.com
yt-video-upload.l.google.com
wide-youtube.l.google.com
youtu.be
```

Это **пример**, а не «готовый список блокировок». В `preset1_example.cmd` он подставлен как демонстрация синтаксиса `--hostlist=`.

### 5.2 Формат hostlist

Из встроенного help `winws.exe`:

> `--hostlist=<filename>` : apply dpi desync only to the listed hosts (one host per line, subdomains auto apply, gzip supported, multiple hostlists allowed)

Детали из `docs/readme.md` (upstream):
- один хост на строку;
- поддомены применяются автоматически, **если строка не начинается с `^`** (`^` = «только точное совпадение»);
- список читается при старте и хранится в виде иерархической структуры для быстрого поиска;
- **при изменении mtime файл перечитывается автоматически** - перезапуск не нужен;
- gzip распознаётся и распаковывается автоматически;
- списков может быть много, пустой общий список = его отсутствие;
- хосты берутся из заголовка `Host:` (HTTP) и из SNI (TLS ClientHello);
- `--hostlist-domains=` - то же, но список прямо в командной строке, `#` в начале комментирует домен.

### 5.3 Формат ipset

> `--ipset=<filename>` : ipset include filter (one ip/CIDR per line, ipv4 and ipv6 accepted, gzip supported, multiple ipsets allowed)

- один IP или CIDR на строку, IPv4 и IPv6 вперемешку;
- gzip поддерживается;
- несколько файлов складываются;
- автоперечитывание по mtime;
- `--ipset-exclude=` - исключающий список, `--ipset-ip=` / `--ipset-exclude-ip=` - фиксированные списки через запятую.

### 5.4 Как обновлять

| Механизм | Описание |
|---|---|
| **Автоперечитывание** | hostlist/ipset перечитываются при изменении mtime. GUI достаточно перезаписать файл - winws подхватит без рестарта. |
| **`ip2net.exe`** | `blockcheck/zapret/ip2net/ip2net.exe` (92 672, x64). Превращает список IP/доменов в компактный список подсетей. В upstream используется для генерации `ipset/*.txt` из «сырых» списков. |
| **`mdig.exe`** | `blockcheck/zapret/mdig/mdig.exe` (130 560, x64). DNS-резолвер: `mdig_resolve_all` в blockcheck резолвит домены в IP-списки. |
| **`--hostlist-auto`** | winws сам детектирует блокировки (по ретрансмиссиям, по RST, по редиректу) и дописывает хосты в файл. Это «самообучающийся» hostlist, не требующий внешних источников. |
| **Внешние источники** | upstream `docs/readme.md` содержит отдельный раздел «Способы получения списка заблокированных IP» (реестры РКН и т.п.). В бандл они **не входят**. |

### 5.5 Критичное ограничение Windows

`docs/windows.md`:

> «Все функции работоспособны, однако функционал ipset в ядре отсутствует. Он реализован в user mode. Фильтры по большому количеству IP адресов невозможны. Работа с проходящим трафиком, например в случае "расшаривания" соединения, невозможна.»

То есть:
- на Linux ipset - это ядерная структура с O(1) поиском; на Windows `--ipset` - это проверка в user mode;
- **большие списки (тысячи подсетей) в Windows неприменимы** - это ударит по CPU и памяти;
- если нужен именно IP-фильтр, лучше вынести его в `--wf-raw-part` (ядро WinDivert), но язык фильтров WinDivert не умеет работать со списками - только с явными диапазонами.

**Рекомендация для GUI:** основной механизм ограничения - **hostlist**, а не ipset. ipset показывать как «продвинутый» режим с предупреждением.

### 5.6 `windivert.filter/` - кернел-фильтры по payload

Пять частичных фильтров, подключаемых через `--wf-raw-part=@<file>`:

| Файл | Условие |
|---|---|
| `windivert_part.dht.txt` | `udp.Length>=5 and udp.Payload[0]=0x64 and udp.Payload[1] in 0x31..0x32` |
| `windivert_part.discord_media.txt` | `outbound`, UDP dst 50000-50099 или 19294-19344, `PayloadLength=74`, `Payload32[0]=0x00010046`, остальные 17 слов = 0 |
| `windivert_part.quic_initial_ietf.txt` | `outbound`, `PayloadLength>=256`, первый байт 0xC0..0xCF, `Payload[1]=0`, `Payload16[1]=0`, `Payload[4]=1` |
| `windivert_part.stun.txt` | `outbound`, `PayloadLength>=20`, `Payload32[1]=0x2112A442`, `Payload[0]<0x40` |
| `windivert_part.wireguard.txt` | `PayloadLength=148 & Payload32[0]=0x01000000` или `92 & 0x02000000` или `64 & 0x03000000` |

`windivert.filter/README.txt` объясняет смысл: отсекать полезную нагрузку **в ядре**, не гоняя целый поток в user mode. Язык фильтров WinDivert не умеет работать с битовыми полями, сдвигами и побитовой логикой, поэтому фильтры «слабые» и могут пропускать лишнее - дофильтрация уже в winws. Документация языка: `https://reqrypt.org/windivert-doc.html#filter_language`.

Пример из README: `winws --wf-raw-part=@windivert_part.discord_media.txt --wf-raw-part=@windivert_part.stun.txt --filter-l7=stun,discord --dpi-desync=fake`.

---

## 6. Собственные рекомендации upstream

### 6.1 Дисклеймер: это не «кнопка»

`readme.md`:

> «Это не однокнопочное решение, готовых стратегий и кнопки "открыть сайты" нет. Нужно понимать как работает zapret, иначе бесполезно.»
> «`preset1_example.cmd` : интерактивный запуск стратегии-примера на базе winws (**не является готовым лекарством**)»

`docs/quick_start_windows.md`:

> «Обход DPI является хакерской методикой... автоматически не гарантирована работоспособность в любых условиях и на любых ресурсах, требуется настройка под специфические условия у вашего провайдера. Условия могут меняться со временем, и методика может начинать или переставать работать.»
> «Нет универсальной таблетки. Везде ситуация разная. В сети гуляют написанные кем-то откровенные глупости... В худших случаях еще и устраивают флуд в сети.»
> «Особо осторожным нужно быть со сторонними сборками. Там могут быть вирусы.»

### 6.2 «Это не VPN»

Прямой формулировки «not a VPN» в бандле нет, но смысл разложен в нескольких местах:

- `readme.md`: «Автономное средство противодействия DPI, **которое не требует подключения каких-либо сторонних серверов**». То есть нет туннеля, нет шифрования, нет смены IP.
- `docs/readme.md`: «zapret **не может пробить блокировку по IP адресу**».
- `docs/readme.md`: «Если блокировка осуществляется по IP... Проект нацелен на обман DPI, который в силу ограниченности ресурсов и большого трафика вынужден интерпретировать его лишь ограниченно. Обмануть полноценный стек ОС и полноценные серверные приложения не получится.»
- `docs/quick_start_windows.md`: «Не будет лишним иметь обходные каналы проксирования трафика на случай, если обход DPI не помогает.»
- `docs/readme.md`, раздел про VPS: «VPN-протоколы предсказуемы *снаружи* и *внутри* почти по каждому пакету... DPI отлично детектит VPN-протоколы».

**Для GUI:** нельзя позиционировать продукт как VPN/анонимайзер. Это обход DPI на уровне пакетов, без шифрования и без смены exit-IP.

### 6.3 Производительность

Ключевая мысль `docs/windows.md`:

> «Фильтрация windivert производится в ядре. Это несравнимо легче по ресурсам, чем перенаправлять пакеты в пространство user mode... Разница в нагрузке на процессор колоссальна. В первом случае - до 100% одного ядра cpu в зависимости от объема исходящего udp трафика (привет, торрент и uTP), во втором - близко к 0. Кроме нагрузки на процессор еще можете порезать себе скорость, тк одно ядро не будет справляться с обработкой вашего гигабитного интернета. А на старых ноутах еще и получите самолетный вой системы охлаждения.»

`docs/quick_start_windows.md`:

> «Фильтр должен быть минимально необходимым. Перехват лишнего трафика приведет только к бессмысленному расходованию ресурсов процессора и замедлению интернета.»

Практические правила:
- базовый фильтр: `--wf-tcp=80,443 --wf-udp=443`;
- для WireGuard/Discord/STUN - **обязательно** `--wf-raw-part` вместо «все порты»;
- `--wf-save=` позволяет посмотреть итоговый фильтр и вручную его ужать;
- лимит размера фильтра - 16 Kb;
- можно запускать несколько `winws.exe`, но **фильтры не должны пересекаться**;
- `winws.exe` однопоточный по обработке пакетов (одно ядро на инстанс).

### 6.4 MTU и сегментация

Из `docs/readme.md` и `blockcheck.sh`:

- `md5sig` добавляет TCP MD5 signature (18 байт опции). Для multi-segment TLS (Kyber) это легко **выводит пакет за MTU** -> ошибка `message too long`.
- `blockcheck.sh:1273`: «WARNING ! fakedsplit/fakeddisorder with md5sig fooling and low split position causes MTU overflow with multi-segment TLS (kyber)».
- `blockcheck.sh:785` (upstream readme): «Использование `fakedsplit` или `fakeddisorder` на TLS kyber с `md5sig` fooling приводит к ошибке "message too long", если первый сплит мал, поскольку будет превышен MTU из-за md5 tcp option.»
- `seqovl` не должен превышать MTU: «Отсюда выбирайте длину сплита с `seqovl` такой, чтобы MTU не был превышен в любом случае.»
- `--dpi-desync-fakedsplit-mod=altorder=0/1/2/3 + 0/8/16` и `--dpi-desync-hostfakesplit-midhost` - инструменты подгонки под MTU.
- Для `wgobfs` в `zapret-obfs.lua` прямо: «reduce MTU of wireguard interface to avoid ip fragmentation».

### 6.5 Семантика `--dpi-desync-*` в двух словах

| Режим | Идея |
|---|---|
| `split`/`multisplit` | разрезать ClientHello/HTTP-запрос на несколько TCP-сегментов; DPI не собирает поток |
| `disorder`/`multidisorder` | отправить сегменты в обратном порядке (out-of-window), DPI отбрасывает |
| `fake` | отправить фейковый пакет с «плохим» TTL/seq/checksum, чтобы DPI запомнил неверное состояние, а сервер его не получил |
| `fakedsplit`/`fakeddisorder` | комбинация: фейк + разрез |
| `hostfakesplit` | разрез с подменой hostname |
| `syndata` | payload в SYN (нулевая фаза, до установления соединения) |
| `synack`/`rstack` | манипуляции с handshake / RST |
| `oob` | out-of-band байт в потоке |
| `ipfrag1`/`ipfrag2` | IP-фрагментация (v1 - простые фрагменты, v2 - с настройкой позиции/порядка) |
| `udplen` | изменить длину UDP-пакета |
| `tamper` | модифицировать оригинальный пакет |
| `hopbyhop`/`destopt` | вставить IPv6 extension headers |
| `fakeknown` | фейк для «известных» протоколов |

Fooling-режимы (`--dpi-desync-fooling`) - как гарантировать, что фейк не дойдёт до сервера: `md5sig` (невалидная MD5-опция), `badseq` (сдвинутый seq), `badsum` (битая контрольная сумма), `datanoack` (данные без ACK), `ts` (сдвинутый timestamp), `hopbyhop`/`hopbyhop2` (IPv6-заголовки, которые сервер отвергнет).

### 6.6 DNS - первое, что надо проверить

`docs/quick_start_windows.md`:

> «blockcheck в начале проверяет DNS. Если выводятся сообщения о подмене адресов, то нужно будет решить проблему с DNS... если вы не настроите решение этой проблемы, обход будет работать только для тех программ, которые сами реализуют механизмы SecureDNS.»

`blockcheck.sh` содержит `DNSCHECK_DNS="8.8.8.8 1.1.1.1 77.88.8.1"`, `DNSCHECK_DOM="pornhub.com ej.ru rutracker.org www.torproject.org bbc.com"`, список DoH-серверов (cloudflare, google, quad9, adguard, yandex) и переключается на DoH при детекте подмены.

**Для GUI:** диагностика должна начинаться с проверки DNS, а не со стратегий.

### 6.7 Виртуальные машины

`blockcheck/zapret/common/virt.sh`:

> «!!! WARNING. vmware and virtualbox are known to break most of the DPI bypass techniques when network is NATed using internal hypervisor NAT !!! if this is your case make sure you are bridged not NATed !!!»

### 6.8 Прочие эксплуатационные предупреждения

- `service_create.cmd` / `service1_create.cmd` / `service2_create.cmd` - все три начинаются с:
  > «THIS BATCH FILE REQUIRES MANUAL EDIT. SERVICE INSTALL IS COMMENTED TO PREVENT SCRIPT KIDDIES FROM DAMAGING THEIR SYSTEMS WITHOUT KNOWING HOW TO RECOVER.»
  > «УСТАНОВКА СЛУЖБЫ ЗАКОММЕНТИРОВАНА, ЧТОБЫ ОГРАДИТЬ НИЧЕГО НЕ ПОНИМАЮЩИХ НАЖИМАТЕЛЕЙ НА ВСЕ ПОДРЯД ОТ ПРОБЛЕМ... ИНАЧЕ БУДЕТЕ ПИСАТЬ ПОТОМ ВОПРОСЫ "У МЕНЯ ПРОПАЛ ИНТЕРНЕТ, КАК ВОССТАНОВИТЬ"»
- `enable_timestamps.cmd` - `netsh interface tcp set global timestamps=enabled`; нужен для `ts` fooling, по умолчанию в Windows timestamps выключены.
- `blockcheck.cmd` - «Запускать только с остановленным zapret и другими средствами обхода DPI!»
- Кодировка путей: «Если в путях присутствуют национальные символы, то при вызове winws из cmd или bat кодировку нужно использовать OEM (866 для русского). Пути с пробелами нужно брать в кавычки. При использовании @<config_file> кодировка в файле должна быть UTF-8 без BOM.»

---

## 7. Лицензирование и что обязан сделать GUI

### 7.1 В бандле НЕТ ни одного файла лицензии

Проверено рекурсивно: файлов, начинающихся с `LICENSE`, `COPYING`, `NOTICE`, нет. Это значит, что при распространении бандла **вы обязаны добавить лицензионные тексты сами** - upstream этого не сделал.

### 7.2 Лицензии компонентов

| Компонент | Лицензия | Источник |
|---|---|---|
| **zapret** (`winws.exe`, `blockcheck.sh`, `preset*.cmd`, `lua/`) | **MIT**, `Copyright (c) 2016-2024 bol-van` | `github.com/bol-van/zapret/docs/LICENSE.txt` |
| **zapret2** (`winws2.exe`, `lua/`) | **MIT**, `Copyright (c) 2016-2026 bol-van` | `github.com/bol-van/zapret2/docs/LICENSE.txt` |
| **WinDivert** (`WinDivert.dll`, `WinDivert64.sys`) | **Двойная: LGPL v3 ИЛИ GPL v2** на выбор | `github.com/basil00/WinDivert/README`, раздел 3 License |
| **Cygwin** (`cygwin1.dll`) | **LGPL v3 или новее** + Cygwin Linking Exception | `cygwin.com/licensing.html` |
| **Cygwin-утилиты** (`bash`, `grep`, `sed`, `gawk`, `tar`, `find`, `coreutils`, ...) | в основном **GPL v2/v3**, часть public domain / X11 | `cygwin.com/licensing.html`: «Most of the tools are covered by the GNU GPL... if you give out any binaries, you must also make the source available» |
| **OpenSSL 3.4.0** (`cygssl-3.dll`, `cygcrypto-3.dll`, `curl-kyber.exe`) | **Apache License 2.0** | VersionInfo / стандарт |
| **OpenSSL 1.1.1w** (`cygssl-1.1.dll`) | OpenSSL License + SSLeay License (BSD-подобная, с advertising clause) | VersionInfo |
| **curl** | MIT-подобная (curl license) | - |
| **LuaJIT** (внутри `winws2.exe`) | **MIT** | - |
| **zlib** | zlib license | VersionInfo: «inflate 1.3.1 Copyright 1995-2024 Mark Adler» |
| **libiconv / libintl** | **LGPL** | VersionInfo: «LGPLed libiconv / libintl for Windows» |
| **liblzma / xz** | public domain / 0BSD | VersionInfo |
| **lz4** | BSD 2-clause | VersionInfo: «Yann Collet» |
| **libpcre / pcre2** | BSD-3 | - |

### 7.3 Что конкретно обязан сделать GUI при редистрибуции

**Обязательно (жёсткие требования):**

1. **Включить текст MIT-лицензии zapret** и указание `Copyright (c) 2016-2024/2026 bol-van` в `THIRD-PARTY-NOTICES` и в окне «О программе».
2. **Включить полный текст LGPL v3** (для WinDivert и cygwin1.dll) и **указать, где взять исходники**. Для LGPL это можно сделать «written offer» - письменным предложением предоставить исходники, действительным минимум 3 года, либо ссылкой на upstream-репозиторий, если вы распространяете немодифицированный бинарь.
3. **Не перелицензировать.** Нельзя писать «наш продукт распространяется под MIT» - только «содержит компоненты под MIT/LGPL/GPL/Apache».
4. **Отметить модификации.** Если вы патчите `lua/*.lua` или конфиги - это ваши производные файлы, их лицензия остаётся MIT, но факт изменения надо указать.
5. **Не заявлять о поддержке/одобрении авторами.** Ни `bol-van`, ни `basil` не связаны с вашим GUI. Формулировка «based on zapret by bol-van» - корректна, «official zapret GUI» - нет.
6. **Указать издателя драйвера.** Фактический подписант `WinDivert64.sys` - `成都密思听科技有限公司`, а не автор WinDivert. Скрывать это нельзя: пользователь увидит издателя в свойствах файла и в UAC-диалоге.

**Желательно:**

7. Публиковать **SHA256 всех бинарей** в манифесте релиза и в UI - это единственный способ дать пользователю проверить, что вы не подсунули модифицированный `winws.exe`.
8. Ссылаться на upstream-репозитории и версии: `zapret-win-bundle @ 4840909`, `zapret v72.12`, `zapret2 v1.0.6`, `WinDivert 2.2.2`.
9. Не менять сами бинари. Любая правка `winws.exe` = производное произведение + потеря доверия к подписи драйвера.

### 7.4 Отдельный риск: GPL-утилиты cygwin

`cygwin/bin/*.exe` - это в основном **GPL v2/v3** программы (bash, grep, sed, gawk, tar, find, coreutils, diff...). Если вы шипите каталог `cygwin/` целиком (375 файлов, 37.6 MB), вы распространяете GPL-бинарники. По cygwin.com/licensing.html: «if you give out any binaries, you must also make the source available».

Практические варианты:
- **не шипить `cygwin/`** в основной поставке, а скачивать по требованию (только для `blockcheck`), с отдельным экраном «загрузить окружение анализа»;
- либо шипить, но приложить `SOURCE-OFFER.txt` со ссылками на исходники каждой GPL-программы (cygwin package sources) - это законно и не требует класть исходники в архив.

Учитывая, что `cygwin/` - это 37.6 MB из 44.4 MB бандла, вариант «скачивать по требованию» экономит ещё и размер дистрибутива.

### 7.5 WinDivert: LGPL и переподписанный драйвер

LGPL v3 требует, чтобы получатель мог **заменить** библиотеку на свою версию (для DLL это выполняется автоматически, т.к. линковка динамическая) и чтобы были доступны исходники **той версии**, которая распространяется.

Проблема: `WinDivert64.sys` в бандле - это **не** бинарь с reqrypt.org. Он переподписан третьей стороной. Формально:
- это всё ещё LGPL-произведение (модификация/пересборка), значит исходники должны быть доступны;
- исходники именно этой сборки вам недоступны - доступны только upstream-исходники WinDivert 2.2.2;
- значит, строго говоря, вы распространяете LGPL-бинарь без возможности предоставить его исходники.

**Что делать:**
- либо брать `WinDivert.dll`/`WinDivert64.sys` **с reqrypt.org** (официальная подпись), а не из бандла - тогда LGPL-комплаенс тривиален;
- либо явно указывать в NOTICES: «WinDivert 2.2.2, LGPLv3, исходники: github.com/basil00/WinDivert; бинарь в составе поставки переподписан третьей стороной, SHA256 ...».

### 7.6 Товарные знаки

`zapret`, `WinDivert`, `Cygwin` - имена проектов. Использование в описании совместимости («works with zapret») допустимо, использование в названии продукта (`ZapretGUI Pro`) - рискованно. Назовите GUI своим именем и укажите «использует zapret».


### 7.7 Смежные компоненты (проверить отдельно)

Если GUI переиспользует сторонние обвязки - пресеты/конфиги из `zapret-discord-youtube` (MIT, Flowseal) или код и архитектуру GPL-проектов - их лицензии применяются **отдельно** от zapret. GPL-3.0 вирусна: производный продукт обязан распространяться под GPL-3.0. Это решение надо принять до первой строки кода, а не после.


---

## 8. x86 vs x64, версии Windows, подпись и SmartScreen

### 8.1 Выбор архитектуры

| Платформа | Что делать | Статус в бандле |
|---|---|---|
| **Windows x64** (7/8/8.1/10/11, Server 2008R2+) | основной сценарий, всё работает «из коробки» | полный комплект x64 |
| **Windows 11 ARM64** | запустить `arm64/install_arm64.cmd`, перезагрузиться; остальное работает через эмуляцию x64 | 3 ARM64-файла + установщик |
| **Windows x86 (32-bit)** | **не поддерживается**, готового варианта нет | ничего |
| **Windows Server** | требуется `Install-WindowsFeature -Name Wireless-Networking` (из-за `wlanapi.dll`) | - |

`arm64/install_arm64.cmd` делает:
1. проверяет `%PROCESSOR_ARCHITECTURE%==ARM64` и build >= 22000 (Windows 11);
2. `net stop windivert`;
3. `bcdedit /set {current} testsigning on`;
4. копирует ARM64 `WinDivert64.sys` в `zapret-winws/`, `blockcheck/zapret/nfq/`, `blockcheck/zapret2/nfq2/`;
5. копирует ARM64 `ip2net.exe` и `mdig.exe` в оба blockcheck-каталога;
6. требует перезагрузки, если testsigning не был включён ранее (на рабочем столе появится надпись «тестовый режим»).

`win7/install_win7.cmd` делает:
1. проверяет `AMD64` и build 7600/7601;
2. копирует `WinDivert.dll` + `WinDivert64.sys` версии **2.2.0-C** во все три места.

### 8.2 Версии Windows

Из `readme.md` (requirements):

> - Windows 7 x64, Windows server 2008 R2 (**ESU updates or windivert files replacement required**)
> - Windows 8+ x64, Windows server 2012+ (**may require to disable secure boot**)
> - Windows 11+ ARM64, Windows server 2025+ (**requires testsigning mode**)
> - Windows Server requires installing of wireless networking feature

Дополнительно из `docs/windows.md`:
- Windows 7: требования к подписи драйверов изменились в 2021; бесплатные обновления кончились в 2020. Нужны ESU-обновления ядра (там появляется поддержка SHA-256 в подписях) либо подмена на 2.2.0-C. «Этот вариант проверен и должен работать. Тем не менее патч 10-летней давности, который включает SHA256 сигнатуры, все еще необходим.»
- Secure Boot: «На системах с включенным secure boot могут быть проблемы из-за подписи драйвера. В этом случае отключите secureboot или включите режим testsigning.»

**Матрица для GUI:**

| ОС | Поддержка | Что показать пользователю |
|---|---|---|
| Win 7 x64 | да, с оговорками | предложить `install_win7.cmd`, объяснить про ESU |
| Win 8/8.1 x64 | да | предупредить про Secure Boot |
| Win 10 x64 | да, лучший вариант | - |
| Win 11 x64 | да | - |
| Win 11 ARM64 | да, через testsigning | объяснить watermark и риск |
| Win Server 2012+ | да, после установки Wireless-Networking | подсказать команду |
| Win x86 | нет | заблокировать установку |

### 8.3 Подпись кода и SmartScreen

Факты по бандлу:

| Файл | Authenticode |
|---|---|
| `winws.exe` | **NotSigned** |
| `winws2.exe` | **NotSigned** |
| `WinDivert.dll` (все 3 копии) | **NotSigned** |
| `cygwin1.dll` (обе копии) | **NotSigned** |
| `elevator.exe`, `killall.exe`, `ip2net.exe`, `mdig.exe`, `curl.exe`, `curl-kyber.exe` | **NotSigned** |
| весь `cygwin/bin/*` (165 PE) | **NotSigned** |
| `WinDivert64.sys` | Valid (сторонний издатель) |
| `Monkey64.sys` | Valid (тот же) |
| `win7/WinDivert64.sys` | Valid (Ars Nova Systems) |
| `arm64/WinDivert64.sys` | **UnknownError** (self-signed) |

Следствия:

1. **SmartScreen** будет показывать «Windows защитила ваш компьютер» при запуске скачанного ZIP/EXE. Единственное лечение - подписать **свой** установщик и **свой** GUI сертификатом EV или OV с накопленной репутацией.
2. **Подписать чужие бинари вы не можете** (и не должны) - подпись `winws.exe` потребовала бы его изменения.
3. **MotW (Mark-of-the-Web)** наследуется от ZIP на все распакованные файлы. Установщик должен либо снимать `Zone.Identifier` (`Unblock-File`) при установке, либо ставить файлы из своего (подписанного) инсталлятора.
4. **AV/EDR** будет агрессивнее реагировать на неподписанный `winws.exe` + подписанный «чужим» именем kernel-драйвер. Это классический паттерн «hacktool», и эвристики срабатывают.
5. **Defender Application Control / Smart App Control** (Win11) может заблокировать запуск неподписанных бинарей полностью. В корпоративной среде это стоп-фактор.

**Рекомендации по упаковке:**
- подписанный MSI/WiX или Inno Setup установщик;
- `Unblock-File` для всех файлов при установке;
- опция «добавить папку в исключения Defender» (через `Add-MpPreference -ExclusionPath`) - **только с явного согласия пользователя**, а не автоматически;
- показывать SHA256 и publisher драйвера в диалоге установки;
- не шипить `windivert-hide/` и `arm64/` по умолчанию (последнее - только на ARM64).

### 8.4 Чек-лист GUI перед релизом

- [ ] Проверка архитектуры ОС, отказ на x86 с внятным текстом.
- [ ] Проверка прав: elevation через манифест GUI или `elevator.exe`.
- [ ] Проверка наличия 4 ключевых файлов рядом (`winws.exe`, `cygwin1.dll`, `WinDivert.dll`, `WinDivert64.sys`) + сверка SHA256.
- [ ] Выбор движка: `winws` (v72.12, `--dpi-desync`) или `winws2` (v1.0.6, Lua) - и соответствующая пара «exe + lua».
- [ ] Генерация конфига в UTF-8 **без BOM** и запуск через `@config` (единственный аргумент).
- [ ] Проверка `--dry-run` перед реальным стартом.
- [ ] Детект DNS-подмены до подбора стратегий.
- [ ] Остановка winws **перед** `sc stop windivert` при деинсталляции.
- [ ] `THIRD-PARTY-NOTICES` с MIT (zapret/zapret2), LGPLv3 (WinDivert, cygwin1), Apache-2.0 (OpenSSL 3), GPL + source offer (cygwin-утилиты).
- [ ] Подписанный установщик + снятие MotW.
- [ ] Предупреждение «это не VPN» и «не помогает при блокировке по IP».

---

## Приложение A. Быстрая шпаргалка «что копировать»

| Сценарий | Файлы |
|---|---|
| Только winws (zapret1) | `winws.exe`, `cygwin1.dll`, `WinDivert.dll`, `WinDivert64.sys` |
| Только winws2 (zapret2) | `winws2.exe`, `cygwin1.dll`, `WinDivert.dll`, `WinDivert64.sys`, `lua/zapret-lib.lua`, `lua/zapret-antidpi.lua` (+ `zapret-auto.lua`, `zapret-obfs.lua` по вкусу) |
| С hostlist и фейками | + `files/list-youtube.txt`, `files/quic_initial_www_google_com.bin` |
| С kernel-фильтрами | + `windivert.filter/*.txt` |
| Автозапуск службой | + `service_create.cmd` (или свои `sc create`) |
| Диагностика | + весь `blockcheck/` + `cygwin/` (37.6 MB) + `tools/elevator.exe` |
| Windows 7 | + `win7/*` (2.2.0-C) |
| Windows 11 ARM64 | + `arm64/*` |

## Приложение B. Ссылки

- zapret (движок winws): https://github.com/bol-van/zapret
- zapret2 (движок winws2 + Lua): https://github.com/bol-van/zapret2
- бандл: https://github.com/bol-van/zapret-win-bundle
- WinDivert: https://reqrypt.org/windivert.html , https://github.com/basil00/WinDivert
- язык фильтров WinDivert: https://reqrypt.org/windivert-doc.html#filter_language
- лицензия zapret: https://github.com/bol-van/zapret/blob/master/docs/LICENSE.txt (MIT)
- лицензия WinDivert: https://github.com/basil00/WinDivert/blob/master/LICENSE (LGPLv3 / GPLv2)
- лицензирование Cygwin: https://cygwin.com/licensing.html
- quick start для Windows: https://github.com/bol-van/zapret/blob/master/docs/quick_start_windows.md
- особенности Windows: https://github.com/bol-van/zapret/blob/master/docs/windows.md
