# Zapret-Manager и IPSets-For-Bypass-in-Russia: разбор

Источники:
- `refs/Zapret-Manager/` — StressOzz/Zapret-Manager, консольная версия `Zapret-Manager.sh` (2048 строк, 321 972 байт, UTF-8, `ZAPRET_MANAGER_VERSION="9.91"`, строка 5).
- `refs/IPSets-For-Bypass-in-Russia/` — V3nilla, commit `1fe2025` от 2026-11-03 («Обновил подсети»).

---

## 1. Что делает Zapret-Manager end-to-end, платформы, установка

### 1.1 Платформа — только OpenWrt

| Требование | Детали | Источник |
|---|---|---|
| ОС | OpenWrt 24.10+ (рекомендуется), 22.03+ минимум | `readme.md:255-267` |
| Файрвол | **firewall4 / nftables** — все правила написаны под него | `readme.md:259`, `readme.md:729` |
| Пакетный менеджер | `opkg` (≤24.10) **или** `apk` (25.12+) — определяется автоматически | `readme.md:260`, `.sh:506` |
| Архитектура | любая; **aarch64_cortex-a53** — только для Zapret2 | `readme.md:264`, `.sh:306` |
| Keenetic / TP-Link / Asus / прошивка провайдера | **НЕ поддерживается.** «Только если на нём стоит прошивка OpenWrt. На заводской прошивке панель работать не будет» | `readme.md:1686-1688` |
| Windows | Только как *клиент*: `.bat`-установщик (внутри `plink` из PuTTY) + отдельный проект **ZapretOzz** для обхода на самом ПК | `readme.md:285-296`, `readme.md:1688` |
| systemd | отсутствует; используется procd (`USE_PROCD=1`) + `/etc/init.d/*` + `/etc/crontabs/root` | `.sh:1464`, `.sh:1471`, `.sh:50` |

Скрипт ставит себе шорткаты: `/usr/bin/zms` (консоль), `/usr/bin/zmsA` (с аргументами), `/usr/bin/zmw` (панель LuCI+WEB) — `.sh:30-32`.

### 1.2 Главное меню (`.sh:2005-2046`)

```
1) Меню Zapret
2) Установить/Удалить/Обновить Zapret2
3) Меню Steer
4) Меню Mixomo
5) Меню ByeTube
6) Меню NetShift
7) Меню TG WS Proxy
8) Меню DNS over HTTPS
9) Домены в hosts
f) Удалить → установить → настроить Zapret  (zapret_key, «под ключ»)
m) Системное меню
s) Старт/стоп выбранного движка (s1/s2 — по отдельности, если стоят оба)
w) Установить/Обновить/Удалить панель WEB+LuCI (w1 — удалить)
Enter) Выход
```
Скрытый пункт `999` — «сброс на тестовый конфиг»: переустановка + подмена `/etc/config/zapret` на файл из репо `StressOzz/Test` (`.sh:2028`).

### 1.3 Подменю Zapret (`.sh:1980-2004`)
1. Установить / Обновить / Удалить Zapret (кнопка меняет смысл по факту установки)
2. Меню стратегий
3. Меню тестирования стратегий
4. Меню автоподбора стратегий по расписанию
5. Меню настройки Discord

### 1.4 Меню стратегий `menu_str` (`.sh:789-802`)
1. v1–v10 · 2. Flowseal · 3. YouTube (Yv) · 4. Игры (Gv1–Gv4) ·
5. Вкл/выкл обход по спискам РКН · 6. Обновить список исключений ·
7. `--wssize 1:6` вкл/выкл · 8. `--methodeol` вкл/выкл · 9. `--filter-udp=443` вкл/выкл ·
0. Собрать собственную стратегию (`build_custom_strategy`, `.sh:1832-1854`).

### 1.5 Как ставится Zapret (`install_Zapret`, `.sh:514-524`)
1. Блокировка, если стоит Zapret2 и не включён expert mode.
2. `LATEST_URL = github.com/remittor/zapret-openwrt/releases/download/v72.20260307/zapret_v…_${LOCAL_ARCH}.zip` (`.sh:19`, `.sh:515`).
3. `update_packages` (при падении — автоподбор рабочего зеркала OpenWrt, `.sh:175-190`), установка `unzip` при необходимости.
4. `wget` архива → `unzip` → установка всех `zapret_*.ipk`/`zapret-*.apk`, затем `luci-app-zapret`.
5. `ADD_FAKE_FLOW` — докачка недостающих fake-файлов (`stun2.bin`, `quic_initial_*`, `tls_clienthello_sochi_park.bin`, …) из `Flowseal/zapret-discord-youtube/bin/` (`.sh:288-289`).
6. `uci set zapret.@main[0].DISABLE_CUSTOM='0'` → `ZAPRET_RESTART`.
7. Чистка `/tmp/*.ipk|*.zip|*zapret*`.

`ZAPRET_RESTART()` = `chmod +x /opt/zapret/sync_config.sh; /opt/zapret/sync_config.sh; /etc/init.d/zapret restart; sleep 1` (`.sh:132`) — то есть **каждый** перезапуск сначала синхронизирует конфиг.

«Под ключ» `zapret_key` (`.sh:617-619`): удалить → поставить → `install_strategy v7` → `Discord_menu 1` → `fix_GAME 1`. Это и есть дефолтное состояние «из коробки».

### 1.6 Остальные разделы (кратко)
- **Zapret2** (`.sh:300-321`): пакеты Routerich, только `aarch64_cortex-a53`, взаимоисключим с Zapret, отдельный `/opt/zapret2`.
- **Discord** (`.sh:567-581`): скрипты `50-stun4all`, `50-quic4all`, `50-discord-media`, `50-discord` (качаются из `bol-van/zapret`), «финские IP» (200 записей `104.25.158.178 finland10000..10199.discord.media` в `/etc/hosts`, `.sh:564`), выбор Dv, выбор fake.
- **Игры** `fix_GAME` (`.sh:605-613`): Gv1–Gv4, Xtreme-режим (`.sh:585-592`, порты `80,88,444-65535` / NFQWS `80,88,443-65535`) с явным предупреждением «может повлиять на работу приложений».
- **Hosts-блоки** (`.sh:70-119`): десятки готовых наборов IP→домены — Instagram/Facebook, Telegram Web, ntc.party, Twitch, nalog, rutor, lib.rus.ec, Gemini/Grok/ChatGPT/Claude/Copilot/DeepL/ElevenLabs/Trae/Windsurf/Manus/Notion/AIStudio, Supercell, Spotify(+EXT), github, githubusercontent, Ubisoft.
- **DoH** (`.sh:870-905`): `https-dns-proxy`, пресеты Comss / Xbox / Cloudflare-gateway / GeoHide RU-EU-US.
- **Исключение устройств** (`.sh:1858-1878`): сканирует `/tmp/dhcp.leases` + `/proc/net/arp`, опционально `ping`-sweep /24, пишет в `/opt/zapret/init.d/openwrt/custom.d/20-script.sh` правило
  `nft insert rule inet zapret postrouting_hook index 0 ip saddr { … } meta mark set meta mark | 0x40000000`.
- **Резервная копия** (`.sh:623-628`): `tar -czf /opt/zapret_backup/zapret.tar.gz -C /opt zapret` + копия `/etc/config/zapret` + `date_backup.txt`; восстановление — снести `/opt/zapret` и распаковать.
- **Пакетный установщик** `PAKET_INSTALL` (`.sh:1883-1976`): ставит `*.ipk`/`*.apk` из `/root` в заданном порядке.

---

## 2. Каталог стратегий (verbatim)

### 2.0 Общие константы (`.sh:44`, `.sh:46`, `.sh:68`)
```
PORTS_UDP="88,1024-2407,2409-4499,4502-19293,19345-49999,50101-65535"
PORTS_TCP="2099,2802,2302,2502,3478-3480,3724,6000-8000,8085,8090,8100,8903,8904,25565,27015-27030,27036-27037,35500-35600,50001,60442"
PARALLEL=8
DOMAINS= youtu.be youtube.com i.ytimg.com i9.ytimg.com yt3.ggpht.com yt4.ggpht.com googleapis.com
 jnn-pa.googleapis.com googleusercontent.com signaler-pa.youtube.com youtubei.googleapis.com
 manifest.googlevideo.com yt3.googleusercontent.com + 22 хоста rr*.googlevideo.com
```

### 2.1 v1–v10 (основные, весь TCP/443 кроме исключений)

Общий префикс всех десяти:
`--filter-tcp=443` + `--hostlist-exclude=/opt/zapret/ipset/zapret-hosts-user-exclude.txt`

| # | Аргументы (далее) | Заметка |
|---|---|---|
| v1 | `--dpi-desync=split2 --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=/opt/zapret/files/fake/stun.bin` | самый «мягкий» split2; минимум вмешательства |
| v2 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/stun.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | fake+multisplit, fooling=ts (требует TCP timestamps на клиенте) |
| v3 | `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=ozon.ru --dpi-desync-repeats=4 --dpi-desync-fooling=ts,md5sig --dpi-desync-badseq-increment=0` | hostfakesplit с подменой SNI на ozon.ru (RU-хост, «доверенный» для DPI) |
| v4 | `--dpi-desync=multisplit --dpi-desync-split-seqovl=582 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=…/stun.bin` | чистый multisplit без fake |
| v5 | `--dpi-desync=fake,fakeddisorder --dpi-desync-split-pos=1 --dpi-desync-fake-tls=…/stun.bin --dpi-desync-fake-tls-mod=none --dpi-desync-fakedsplit-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fooling=badseq,badsum --dpi-desync-badseq-increment=0` | fakeddisorder + badseq/badsum |
| v6 | `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=i2.photo.2gis.com --dpi-desync-hostfakesplit-midhost=host-2 --dpi-desync-split-seqovl=726 --dpi-desync-fooling=badsum,badseq --dpi-desync-badseq-increment=0` | midhost=host-2, хост 2gis |
| v7 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=654 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq,badsum --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/stun.bin --dpi-desync-fake-tls=…/stun.bin --dpi-desync-badseq-increment=0` | **дефолт для «под ключ»** (`STR_VERSION_AUTOINSTALL="v7"`, `.sh:5`) |
| v8 | `--dpi-desync=fake --dpi-desync-fooling=ts --dpi-desync-fake-tls=…/4pda.bin --dpi-desync-fake-tls-mod=none` | один fake-пакет, минимальный набор |
| v9 | `--dpi-desync=hostfakesplit --dpi-desync-fooling=badseq,badsum --dpi-desync-hostfakesplit-mod=host=ozon.ru --dpi-desync-badseq-increment=0` | как v3, но без repeats |
| v10 | `--dpi-desync=fake,split2 --dpi-desync-split-pos=2 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-hostfakesplit-mod=host=maxcdn.bootstrapcdn.com --dpi-desync-fake-tls-mod=rnd,sni=maxcdn.bootstrapcdn.com --dpi-desync-fooling=ts` | fake+split2, SNI → bootstrapcdn |

Источник: `.sh:742-751`; дубль в `Strategies.md:4-116`.
**Расхождение:** `Strategies.md:80` даёт для v7 `--dpi-desync-repeats=6`, а `.sh:748` — `repeats=8`. Скрипт — истина.

### 2.2 Yv01–Yv33 (YouTube)

Общий префикс: `--filter-tcp=443` + `--hostlist=/opt/zapret/ipset/zapret-hosts-google.txt`
(у Yv33 вместо hostlist — `--filter-l7=tls`).
Источник: `files/StrYoutube` (33 блока, 283 строки) и `Strategies_For_Youtube.md:12-295` (только Yv01–Yv27).

| # | Аргументы (далее) | Заметка |
|---|---|---|
| Yv01 | `--ip-id=zero --dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` | |
| Yv02 | `--dpi-desync=multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-split-seqovl=1` | |
| Yv03 | `--dpi-desync=fake,multisplit --dpi-desync-split-pos=2,sld --dpi-desync-fake-tls=0x0F0F0F0F --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=ggpht.com --dpi-desync-split-seqovl=620 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fooling=badsum,badseq` | |
| Yv04 | `--dpi-desync=split2 --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` | |
| Yv05 | `--dpi-desync=fake,fakeddisorder --dpi-desync-split-pos=10,midsld --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=fonts.google.com --dpi-desync-fake-tls=0x0F0F0F0F --dpi-desync-fake-tls-mod=none --dpi-desync-fakedsplit-pattern=…/tls_clienthello_vk_com.bin --dpi-desync-split-seqovl=336 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_gosuslugi_ru.bin --dpi-desync-fooling=badseq,badsum --dpi-desync-badseq-increment=0` | смесь gosuslugi/vk fake |
| Yv06 | `--dpi-desync=multidisorder --dpi-desync-split-pos=7,sld+1 --dpi-desync-fake-tls=0x0F0F0F0F --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-fooling=badseq --dpi-desync-autottl 2:2-12` | autottl |
| Yv07 | `--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld,endhost-1 --dpi-desync-repeats=2 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | |
| Yv08 | `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=google.com --dpi-desync-fooling=ts` | **дефолт**, если Yv нет в конфиге (`ADD_Yv`, `.sh:741`) |
| Yv09 | `--dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-fake-quic=…/quic_initial_www_google_com.bin` | QUIC-fake в TCP-блоке (артефакт) |
| Yv10 | `--dpi-desync=multisplit --dpi-desync-split-pos=1,2 --dpi-desync-split-seqovl=4 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | |
| Yv11 | `--dpi-desync=multidisorder --dpi-desync-split-pos=2,5,105,host+5,sld-1,endsld-5,endsld` | 7 точек разреза |
| Yv12 | `--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=2` | самый короткий |
| Yv13 | `--dpi-desync=fake,multidisorder --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats=2 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=fonts.google.com` | |
| Yv14 | `--dpi-desync=fake,multidisorder --dpi-desync-split-pos=10,midsld --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=0x0F0F0F0F --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=fonts.google.com --dpi-desync-split-seqovl=336 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fooling=badseq` | |
| Yv15 | `--dpi-desync=fake,multisplit --dpi-desync-split-pos=2,sld --dpi-desync-fake-tls=0x0F0F0F0F --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=ggpht.com --dpi-desync-split-seqovl=2108 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fooling=badsum,badseq` | seqovl=2108 — самый большой |
| Yv16 | `--dpi-desync=multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-split-seqovl=1 --dpi-desync-fooling=badsum,badseq --dpi-desync-badseq-increment=0` | |
| Yv17 | `--dpi-desync=fakeddisorder --dpi-desync-fooling=md5sig --dup=1 --dup-cutoff=n2 --dup-fooling=md5sig --dpi-desync-split-pos=method+2` | единственная с `--dup` |
| Yv18 | `--ip-id=zero --dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1 --dpi-desync-fooling=ts` | altorder=1 |
| Yv19 | `--dpi-desync=fake,multisplit --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=! --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=2 --dpi-desync-fooling=badseq --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | `!` = отключить fake-tls |
| Yv20 | `--ip-id=zero --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` | |
| Yv21 | `--ip-id=zero --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` | |
| Yv22 | `--dpi-desync=split2 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl=681 --dpi-desync-split-seqovl-pattern=…/stun.bin` | |
| Yv23 | `--dpi-desync=fake,fakeddisorder --dpi-desync-split-pos=1 --dpi-desync-fake-tls=…/stun.bin --dpi-desync-fake-tls-mod=none --dpi-desync-fakedsplit-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fooling=badseq,badsum --dpi-desync-badseq-increment=0` | |
| Yv24 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=654 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq,badsum --dpi-desync-split-seqovl-pattern=…/stun.bin --dpi-desync-fake-tls=…/stun.bin --dpi-desync-badseq-increment=0` | YouTube-версия v7 |
| Yv25 | `--dpi-desync=hostfakesplit --dpi-desync-fooling=badseq,badsum --dpi-desync-hostfakesplit-mod=host=mapgl.2gis.com --dpi-desync-badseq-increment=0` | |
| Yv26 | `--dpi-desync=multidisorder --dpi-desync-split-pos=1,sniext+1,host+1,midsld-2,midsld,midsld+2,endhost-1` | |
| Yv27 | `--dpi-desync=multidisorder --dpi-desync-split-pos=1,2,3,5,105,host+5,sld-1,endsld-5,endsld --dpi-desync-fooling=badsum` | |
| Yv28 | `--dpi-desync=fake,multidisorder --dpi-desync-split-seqovl=midsld-4 --dpi-desync-split-pos=2,midsld+3,sniext+2,endhost-2,midsld-3,host+1,-10 --dpi-desync-fooling=ts --dpi-desync-repeats=23 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_sferum_ru.bin --dpi-desync-fake-tls=…/stun2.bin --dpi-desync-fake-tls=…/tls_clienthello_www_sferum_ru.bin --dpi-desync-fake-tls=…/stun.bin --dpi-desync-fake-http=…/tls_clienthello_max_ru.bin` | «тяжёлая артиллерия»: repeats=23, 3 fake-tls + fake-http |
| Yv29 | `--dpi-desync=hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --dpi-desync-hostfakesplit-midhost=midsld+1` | |
| Yv30 | `--dpi-desync=hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --dpi-desync-hostfakesplit-midhost=sld+1` | |
| Yv31 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=717 --dpi-desync-split-pos=sld,sld+3,endhost-1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com_2.bin --dpi-desync-fake-tls=…/tls_clienthello_www_google_com_2.bin` | использует `_2.bin` (есть в `files/`) |
| Yv32 | `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=ozon.ru --dpi-desync-repeats=4 --dpi-desync-fooling=ts,md5sig --dpi-desync-badseq-increment=0` | копия v3, но для google-хостов |
| Yv33 | `--filter-l7=tls --dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-cutoff=s34229` | **единственная без hostlist** — бьёт весь TLS; cutoff по числу пакетов |

### 2.3 Dv1–Dv17 (Discord media, TCP 2053/2083/2087/2096/8443)

Общий префикс: `--filter-tcp=2053,2083,2087,2096,8443` + `--hostlist-domains=discord.media`
Источник: `.sh:528-544`. Отдельно `discord_str_add` (`.sh:805-809`) добавляет UDP-блок голоса:
```
--new --filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake
--dpi-desync-fake-discord=…/stun.bin --dpi-desync-fake-stun=…/stun.bin --dpi-desync-repeats=6
```

| # | Аргументы (далее) |
|---|---|
| Dv1 | `--dpi-desync=multisplit --dpi-desync-split-seqovl=652 --dpi-desync-split-pos=2 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` |
| Dv2 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` |
| Dv3 | `--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=none` |
| Dv4 | `--dpi-desync=multisplit --dpi-desync-split-seqovl=652 --dpi-desync-split-pos=2 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` *(побайтово совпадает с Dv1)* |
| Dv5 | `--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=1000 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` |
| Dv6 | `--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` |
| Dv7 | `--dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1 --dpi-desync-split-seqovl=679 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin` |
| Dv8 | `--dpi-desync=fake --dpi-desync-fake-tls-mod=none --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2` |
| Dv9 | `--dpi-desync=fake,fakedsplit --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync-repeats=8 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` |
| Dv10 | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` |
| Dv11 | `…` = Dv2 (побайтово идентична Dv2) |
| Dv12 | `--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` |
| Dv13 | `--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` |
| Dv14 | `--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin` |
| Dv15 | `--dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=…/tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` |
| Dv16 | `--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1 --dpi-desync-fooling=ts` |
| Dv17 | `--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com` |

### 2.4 Gv1–Gv4 (игры, UDP + TCP)

`strategy_Gv1` (`.sh:596`):
```
#Gv1
--new
--filter-udp=$PORTS_UDP
--dpi-desync=fake
--dpi-desync-cutoff=d2
--dpi-desync-any-protocol=1
--dpi-desync-fake-unknown-udp=/opt/zapret/files/fake/stun.bin
```
`strategy_Gv N` для N=2..4 (`.sh:597`):
```
#Gv$N
--new
--filter-udp=$PORTS_UDP
--dpi-desync=fake
--dpi-desync-repeats=10
--dpi-desync-any-protocol=1
--dpi-desync-fake-unknown-udp=/opt/zapret/files/fake/stun.bin
--dpi-desync-cutoff=n$N
```
`strategy_TCP_common` (`.sh:595`) добавляется к любой Gv:
```
--new
--filter-tcp=$PORTS_TCP
--dpi-desync-any-protocol=1
--dpi-desync-cutoff=n5
--dpi-desync=multisplit
--dpi-desync-split-seqovl=582
--dpi-desync-split-pos=1
--dpi-desync-split-seqovl-pattern=/opt/zapret/files/fake/stun.bin
```
Xtreme (`.sh:585-592`): `GV_XTREME_PORTS="80,88,444-65535"`, `GV_XTREME_NFQWS_PORTS="80,88,443-65535"`, состояние сохраняется в `/opt/zapret/tmp/GvXtreme` (5 строк) для откатa.

### 2.5 Flowseal — динамический набор

`download_strategies` (`.sh:765-779`) качает ZIP `Flowseal/zapret-discord-youtube` (`main`), распаковывает, берёт все `general*.bat` **кроме `general (ALT5).bat`**, оставляет только файлы, где найден один из маркеров:
`--filter-udp=19294-19344,50000-50100` | `--filter-tcp=%GameFilterTCP%` | `--filter-udp=%GameFilterUDP%` | `--filter-tcp=2053,2083,2087,2096,8443` | `--filter-tcp=443 --hostlist="%LISTS%list-google.txt"` | `--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt"`.

Затем — 20+ `sed`-замен: пути `%BIN%`/`%LISTS%` → `/opt/zapret/files/fake/…` и `/opt/zapret/ipset/…`, `%GameFilterTCP%`/`%GameFilterUDP%` → `$PORTS_TCP`/`$PORTS_UDP`, имя файла без `.bat` становится `#Имя`.

**Критично:** строки `--hostlist="%LISTS%list-general.txt"`, `--ipset="%LISTS%ipset-all.txt"`, `--ipset-exclude=…`, `--hostlist-exclude="%LISTS%list-exclude-user.txt"` **удаляются** (`.sh:770-772`) — то есть ipset-подход Flowseal на OpenWrt сознательно выпиливается в пользу hostlist.

### 2.6 Когда что выигрывает (эмпирика из доков, не из кода)

Прямых «эта стратегия для МТС/Ростелекома» в репозитории нет — автор принципиально не маппит стратегии на провайдеров. Есть только:
- v7 — дефолт для автоустановки (`.sh:5`), значит автор считает его наиболее универсальным.
- Yv08 — дефолт для YouTube (`.sh:741`).
- Gv1 — дефолт для игр (`.sh:619`).
- v3/v9/Yv32 — `hostfakesplit` с `host=ozon.ru`: подмена SNI на крупный RU-хост; класс стратегий, побеждающий там, где DPI пропускает «доверенные» российские домены.
- v2/v7/Yv24 — `fake,multisplit` + `fooling=ts`/`badseq`: базовый «рабочая лошадка» набор.
- Yv28 — `repeats=23` + три fake-tls + fake-http: последний вариант, когда ничего не берёт.
- Yv33 — без hostlist, весь TLS: ядерный вариант, ломает всё подряд.
- `readme.md:658` — «Автоподбор… новая ставится только если она открывает больше сайтов».

---

## 3. Логика «какая стратегия подходит»: автотест, blockcheck, скоринг

### 3.1 Blockcheck НЕ используется
Ни `blockcheck.sh`, ни `--dpi-desync-autottl`-калибровки из bol-van в скрипте нет. Вместо этого — собственный «перебор + curl».

### 3.2 Единица измерения — успешный `curl` до домена

`check_url` (`.sh:1093`):
```sh
curl -sL --connect-timeout 4 --max-time 6 --speed-time 3 --speed-limit 1 \
     --range 0-65535 -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) curl/8.0" \
     -o /dev/null "$LINK"
```
`--range 0-65535` — чтобы поймать DPI-обрыв на первых килобайтах, а не только TLS-handshake.

`check_all_urls` (`.sh:1094-1098`): параллелизм **8** (`PARALLEL=8`), PID-ы в файл, при Ctrl+C — `kill_pid_tree` по всем.

`check_access` (`.sh:723`) для YouTube-режима: `curl -s --connect-timeout 1 -m 2`, порог «ok» — **≥ половины** доменов; при недоборе печатает «возможны проблемы с воспроизведением видео».

### 3.3 Контрольный замер «без Zapret»

`check_zpr_off` (`.sh:1090-1092`): останавливает `/etc/init.d/zapret`, гоняет тот же список, пишет строку
`Контрольный тест (Zapret выключен) → OK/TOTAL` и **запускает Zapret обратно**.
Цветовой скоринг `result_color` (`.sh:1062-1063`):
- 🟢 `ok == total` — открылось всё;
- 🟡 `ok > ctrl` — лучше, чем без Zapret;
- 🔴 иначе — пользы нет.

`print_results_colored` (`.sh:1064-1068`) сортирует по `ok` убыв. и вставляет контрольную строку в правильное место рейтинга.

### 3.4 Набор тестовых URL

`prepare_urls` (`.sh:1099-1102`): 24 захардкоженных домена —
`gosuslugi.ru, esia.gosuslugi.ru, nalog.ru, lkfl2.nalog.ru, rutube.ru, ntc.party, instagram.com, facebook.com, rutor.info, rutracker.org, epidemz.net.co, nnmclub.to, openwrt.org, sxyprn.net, spankbang.com, pornhub.com, discord.com, x.com, filmix.my, forum.ru-board.com, play.google.com, anitokyo.tv, cub.red, downloads.openwrt.org`
\+ сам скрипт с raw.githubusercontent.com.
Плюс динамически: `RAW = hyperion-cs/dpi-checkers/ru/tcp-16-20/suite.v2.json`, парсинг `"id"…"host"` → `id|host` (`.sh:37`, `.sh:1102`).

### 3.5 Полный цикл перебора `run_test_core` (`.sh:1118-1128`)
1. `prepare_urls`, `TOTAL = |URLS|`, `TOTAL_STR = grep -c '^#' $STR_FILE`.
2. `start_test_trap` — Ctrl+C ставит флаг-файл; `test_interrupted` проверяется на каждой итерации.
3. `check_zpr_off` → `CTRL_OK`.
4. `cp $CONF $BACK` — **бэкап до теста**.
5. Для каждой стратегии: вырезать блок из `$STR_FILE` по строкам `^#`, подставить в `option NFQWS_OPT '…'` через `awk`, `ZAPRET_RESTART`, `check_all_urls`, записать `NAME → OK/TOTAL`.
6. При прерывании — `restore_after_test_interrupt "$BACK"` (`.sh:136`): вернуть конфиг, перезапустить, выйти.
7. Иначе: `sort -t'/' -k1 -nr`, вернуть `$BACK`, `ZAPRET_RESTART`, `show_single_result`.

**Таймауты:** нет ограничения на стратегию целиком; на URL — 6 с (`--max-time 6`) + 4 с коннект. При 24 доменах и 8 параллелях худший кейс ≈ 3×6 = 18 с на стратегию; 40+ стратегий → 12+ минут.

**Повторных прогонов лидера в .sh НЕТ.** `readme.md:653` (для панели 4.99) обещает «лидер перепроверяется ещё двумя прогонами, в рейтинг идёт худший из трёх», но `CHANGELOG.md:13` (5.000) это отменяет, а в консольном скрипте такой логики нет вовсе.

### 3.6 Режимы тестирования
| Режим | Функция | Что перебирает |
|---|---|---|
| Текущая стратегия | `check_current_strategy` `.sh:1103-1107` | ничего не меняет; отдельно считает YouTube-домены |
| v1–v10 | `run_test_versions` `.sh:1113-1114` | `strategy_v1..vN` до первой несуществующей |
| Flowseal | `run_test_flowseal` `.sh:1111-1112` | скачанный набор |
| v + Flowseal | `run_all_tests` `.sh:1115-1117` | оба |
| По домену | `run_test_by_domain` `.sh:1076-1089` | ввод доменов вручную (в панели лимит 30) |
| YouTube (все сразу) | `run_test_core_youtube` `.sh:677-686` | Yv01–Yv33 по `$DOMAINS` |
| YouTube (по одной, с подтверждением) | `auto_stryou` + `_stryou_try_one` `.sh:687-722` | интерактивно: Enter — применить, S — стоп, N — дальше |
| Свои | `TEST_CUSTOM` `.sh:1129-1132` | `/root/custom_test.txt` (формат: `#Имя` + строки) |

### 3.7 Автоподбор по расписанию `auto_apply_best_strategy` (`.sh:358-380`)

Вызывается как `Zapret-Manager.sh --auto-best` (`.sh:2047`).

1. Lock-файл `$AUTO_LOCK` с PID, `trap` на EXIT/INT; лог `$AUTO_LOG`.
2. `cp $CONF $AUTO_BACK`; запоминает текущие номера `ORIG_YV_NUM`, `ORIG_DV_NUM`, `ORIG_GV_NUM`.
3. Режим (`AUTO_MODE_FILE`, `.sh:325-330`): `1` = только v, `2` = только Flowseal, `3` (дефолт) = v + Flowseal.
4. Собирает стратегии: `for N in $(seq 1 100); do strategy_v"$N" … || break; done`, `sed -i '/#Y/d'` (YouTube-блоки из общего теста исключены).
5. `prepare_urls`, `check_zpr_off`.
6. Перебор → `NAME → OK/TOTAL` в `$AUTO_RESULTS` → `sort_results_desc` (`.sh:344`, awk берёт первое число в строке и сортирует по нему `-nr`).
7. `BEST_LINE = head -n1` (после фильтра контрольной строки), `BEST_NAME` — до `→`.
8. Восстанавливает `$AUTO_BACK`, затем **вставляет** блок победителя, снова добавляет Discord-порты (`19294-19344,50000-50100` в UDP, `2053,2083,2087,2096,8443` в TCP), восстанавливает Yv/Dv/Gv через `restore_yv_number` / `restore_dv_number` / `fix_GAME`, восстанавливает `#CustomStart…#CustomEnd`.
9. Финальный `ZAPRET_RESTART`.

Расписание (`.sh:381-386`): `0 $HOUR * * * $AUTO_CRON_CMD >/dev/null 2>&1` в `/etc/crontabs/root`, где `AUTO_CRON_CMD` = `/usr/bin/zmsA --auto-best`; затем `/etc/init.d/cron restart`. Есть защита: если `date +%Y < 2020` — отказ («расписание будет работать некорректно»), плюс отдельное меню NTP/таймзоны (`.sh:331-342`).

Остановка `stop_auto_best` (`.sh:347-348`): флаг-файл, ожидание до 90 с, затем `kill -KILL` по PID из лока и восстановление `$AUTO_BACK`.

**Слабое место:** в отличие от панели (`readme.md:658` — «ставится только если открывает больше сайтов»), консольный `--auto-best` **всегда** применяет первого в отсортированном списке, даже если он хуже текущей стратегии.

---

## 4. Списки и ipset: как качаются, форматы, cron

### 4.1 Какие списки использует скрипт

| Роль | Путь | Источник | Где в коде |
|---|---|---|---|
| Исключения (hostlist-exclude) | `/opt/zapret/ipset/zapret-hosts-user-exclude.txt` | `raw.githubusercontent.com/StressOzz/Zapret-Manager/refs/heads/main/zapret-hosts-user-exclude.txt` (`EXCLUDE_URL`, `.sh:42`) | `.sh:761`, `.sh:798`, `.sh:856` |
| Google/YouTube | `/opt/zapret/ipset/zapret-hosts-google.txt` | ставится пакетом zapret; дополняется `ADD_GP_DOMAINS` (`.sh:783`) — 14 доменов: `gvt1.com, googleplay.com, play.google.com, beacons.gvt2.com, play.googleapis.com, play-fe.googleapis.com, lh3.googleusercontent.com, android.clients.google.com, connectivitycheck.gstatic.com, play-lh.googleusercontent.com, play-games.googleusercontent.com, prod-lt-play-storegatewayadapter-pa.googleapis.com, youtubei.youtube.com, cloudfunctions.net` | `.sh:783` |
| Свои хосты / РКН | `/opt/zapret/ipset/zapret-hosts-user.txt` | `IndeecFOX/zapret4rocket/…/extra_strats/TCP/RKN/List.txt` (`RKN_URL`, `.sh:38`) | `.sh:729-737` |
| Discord | `/opt/zapret/ipset_def/zapret-hosts-user.txt` | из пакета | `.sh:54` |
| Zapret2-эквивалент | `/opt/zapret2/ipset/zapret_hosts_user_exclude.txt` | тот же `EXCLUDE_URL` | `.sh:313` |
| Zapret2 Discord | `/opt/zapret2/ipset/zapret_hosts_discord.txt` | `files/Zapret2/zapret_hosts_discord.txt` | `.sh:318` |

### 4.2 Механика скачивания
Везде один паттерн: `rm -f "$EXCLUDE_FILE"; wget -q -U "Mozilla/5.0" -O "$EXCLUDE_FILE" "$EXCLUDE_URL"` (`.sh:761`, `.sh:798`, `.sh:856`, `.sh:1850`). **Нет** ни проверки размера, ни атомарной подмены через `.tmp` — при обрыве файл останется обрезанным. Единственная защита — размерный гейт для РКН.

### 4.3 Переключение «весь трафик» ↔ «только списки РКН»

Ключевой трюк (`.sh:729-737`): подменяется **вся семантика hostlist в стратегии**:
```
--hostlist-exclude=/opt/zapret/ipset/zapret-hosts-user-exclude.txt   ← обход «всего, кроме исключений»
                       ↓  enable_rkn
--hostlist=/opt/zapret/ipset/zapret-hosts-user.txt                   ← обход «только доменов из списка»
```
- `enable_rkn`: если `$HOSTLIST_FILE` ≤ **1 800 000 байт**, делает бэкап в `$BACKUP_FILE`, копию в `$HOSTS_USER`, дописывает `curl -fsSL "$RKN_URL" >>`. Гейт 1.8 МБ — защита от повторного дописывания при каждом включении.
- `disable_rkn`: обратная `sed`-замена + восстановление из бэкапа (или `: >` если бэкапа нет).
- `RKN_Check` (`.sh:736-737`): статус «РКН» показывается только если в конфиге `--hostlist=…user.txt` **и** файл > 1.8 МБ.
- Список можно обновить вручную: пункт 6 меню стратегий (`.sh:798`) — stop → `wget` → start.

### 4.4 ipset в Zapret-Manager отсутствует полностью

`grep -i ipset` по `Zapret-Manager.sh` даёт **только** пути `/opt/zapret/ipset/…` (каталог) и строки **удаления** ipset-директив Flowseal (`.sh:771`). Ни `ipset create`, ни `ipset restore`, ни `--ipset=` скрипт не порождает. В `readme.md` и `CHANGELOG.md` слова «ipset» нет ни разу.

**Вывод для GUI:** если нужен именно ipset-режим (а он нужен — см. §5), его придётся проектировать с нуля; Zapret-Manager тут не образец.

### 4.5 Cron / таймеры
- Только `/etc/crontabs/root` + `/etc/init.d/cron restart`. systemd-таймеров нет.
- В консольном скрипте — **одно** расписание: `0 H * * * /usr/bin/zmsA --auto-best`.
- В панели (LuCI/WEB) расписаний много и они помечены `# zm-…` (`readme.md:1968-1991`): `# zm-rpcd-watch` (2 мин), `# zm-versions`, `# zm-autobest`, **`# zm-excl-auto` (обновление списка исключений раз в 2/4/6/12 ч)**, `# zm-dohwatch`, `# zm-adblock`, `# zm-steer`, `# zm-wfix`, `# zm-subupd`, `# zm-awgfix`, `# zm-tg-auto-*`, `# zm-tailscale`, `# zm-sched-reboot`, `# zm-autoclean`.
- Удаление zapret вычищает cron: `crontab -l | grep -v -i -E "zapret|/usr/bin/zmsA --auto-best" | crontab -` (`.sh:651`, `.sh:659`).

### 4.6 Слияние источников
Практически нет — только два случая «дописать»:
- `ADD_GP_DOMAINS` — `grep -Fxv -f "$fileGP"` → дописать только отсутствующие (дедуп есть);
- `enable_rkn` — `curl >> "$HOSTLIST_FILE"` без дедупа (отсюда и гейт по размеру).

---

## 5. IPSets-For-Bypass-in-Russia

### 5.1 `ipset-all.txt` — точный формат

| Свойство | Значение |
|---|---|
| Размер файла | **577 240 байт** (564 КБ) |
| Строк | **33 336**, пустых — 0, комментариев (`#`) — 0 |
| Дубликаты | нет (`Sort-Object -Unique` = 33 336) |
| Формат | **только CIDR**, ни одного диапазона `a-b`, ни одного одиночного IP |
| IPv4 | **26 485** строк, строки 1–26 485 |
| IPv6 | **6 851** строк, строки 26 486–33 336 |
| Сортировка | v4 по возрастанию адреса, затем v6; граница: `223.29.160.0/20` → `2001:218::/32` |
| Переводы строк | **CRLF** (`\r\n`) во всех 33 336 строках, файл заканчивается переводом строки |
| Кодировка | ASCII/UTF-8 (только hex и цифры) |

Примеры: `1.0.0.0/24`, `1.178.4.0/22`, `1.178.144.0/20`, `152.114.0.0/17`, `2a14:f181:104::/48`, `2c0f:fc00:b011::/48`.

> ⚠ **CRLF — главная ловушка.** `ipset restore` / `ipset -exist add` на OpenWrt получат строки с `\r` на конце и молча/шумно провалятся. При доставке файла на роутер нужен `tr -d '\r'` или `sed -i 's/\r$//'`.

### 5.2 `exclude.txt` (83 байта, 6 строк)

```
0.0.0.0/8
10.0.0.0/8
127.0.0.0/8		← в конце строки TAB
192.168.0.0/16
185.71.66.225
185.71.67.221
```
Назначение — **RFC1918/loopback + два конкретных адреса** (последние два — судя по нумерации, инфраструктура/зеркала, которые нельзя гнать через обход). Формат — тот же, что `ipset-all.txt`, то есть годится для `ipset-exclude`/`--ipset-exclude=`.

⚠ `127.0.0.0/8` с хвостовым TAB — при наивном парсинге даст «невалидный префикс».

### 5.3 `exclude-domains.txt` (332 байта, 27 доменов)

```
2ch.hk 2ip.ru habr.com mail.ru mts.ru sberbank.ru vk.com yandex.com yandex.kz yandex.ru
ozon.ru wildberries.ru rf4game.com rf4game.ru dns-shop.ru citilink.ru ya.ru 3dnews.ru
nvidia.com microsoft.com akamaitechnologies.com msi.com multimc.org donationalerts.com
valve.net gosuslugi.ru gu-st.ru faceit.com
```
Это **доменный** список исключений — ровно то, что нужно класть в `--hostlist-exclude=`. Набор показателен: банк (сбербанк), госуслуги (`gosuslugi.ru`, `gu-st.ru`), маркетплейсы (ozon, wildberries, dns-shop, citilink), яндекс, вк, мейл.ру, мтс, хабра, 2ch, анти-DPI-хосты CDN (akamaitechnologies) и игровые (valve.net, faceit, rf4game).

### 5.4 Папка «AS Parser» — как генерируется список

Два скрипта, оба читают RIPE Stat и пишут `ipset-all.txt` в текущий каталог.

**`AS_Parser.py`** (5 961 байт, `requests` + `ThreadPoolExecutor`):
- `API_URL = https://stat.ripe.net/data/announced-prefixes/data.json`
- `params = {"resource": asn, "min_peers_seeing": 1}`
- `CONNECT_TIMEOUT=10`, `READ_TIMEOUT=30`, `WORKERS=6`
- `Retry(total=5, backoff_factor=1.5, status_forcelist=(429,500,502,503,504))`
- Фильтры: `ipaddress.ip_network(prefix, strict=False)`, отброс `prefixlen == 0` и `not net.is_global`
- Слияние: `set` → `ipaddress.collapse_addresses(sorted(...))` — **схлопывание подсетей**, поэтому 33 336 строк, а не «сырые» анонсы
- Запись: сначала все v4, потом все v6

**`AS_parser_async.py`** (7 521 байт, 306 строк): то же на `httpx2` (`AsyncClient`), автоустановка пакета через `pip` при отсутствии (`ensure_package`), тот же `collapse_addresses`.

**Список ASN — 98 записей** (хардкод в обоих скриптах). Покрытие:
- гиперскейлеры/облака: Scaleway, Hetzner ×4, DigitalOcean ×3, Contabo ×3, OVH ×2, Vultr, Oracle ×2, Amazon ×3, Google Cloud (AS15169), Microsoft Azure (AS8075), Ionos ×2, Hostinger ×2, GoDaddy ×2, DreamHost, HostGator/BlueHost, Melbicom ×2, M247 ×2, HostPapa/ColoCrossing, Hosteur, ITL DC, Scalaxy, Zenlayer, Firstcolo, CreaNova, Clouvider, Fellowship, LogicForge, FranTech;
- CDN: Akamai **×20** (AS20940, 16625, 12222, 33905, 21342, 32787, 35994, 12400, 15802, 18209, 24319, 25019, 26008, 31108, 34164, 49846, 17204, 213120, 393234, 393560), Akamai Cloud/Linode, Cloudflare **×5** (13335, 14789, 132892, 395747, 209242), Fastly, BunnyCDN, Edgio ×2, StackPath ×2, KeyCDN, CacheFly, Imperva/Incapsula, Datacamp/CDN77 ×2, G-Core ×2;
- транзит: Cogent, GTT, NTT, Telia, Lumen, Tata, Verizon, Hurricane Electric, Telecom Italia Sparkle, Orange, GlobeNet;
- игры: Riot Games (AS6507), I3DNET/Discord (AS49544).

⚠ Логическая нестыковка: список позиционируется как «IP, ставшие недоступными», но включает **Cloudflare (AS13335)** целиком — а через Cloudflare живёт половина рунета и куча банков. Такой ipset в лоб = массовые ложные срабатывания.

### 5.5 Рекомендуемая частота обновления

В `README.md` частота **не указана**. Косвенные признаки:
- `README.md:7`: «Список IP которые стали недоступны **10.06.2025–xx.xx.2026**» — то есть набор ведётся как «накопительный журнал», а не снимок.
- Единственный коммит в клоне — `1fe2025 2026-11-03 «Обновил подсети»`, т.е. обновление **за день до** даты анализа. Репозиторий живой и обновляется вручную, нерегулярно.

Практический вывод: тянуть **раз в сутки–неделю** с ETag/If-Modified-Since и обязательной валидацией; при неудаче — оставлять предыдущую версию.

### 5.6 Дополнительные папки (не описаны в README)

**(а) «Разблокировка множества сервисов(пример - ChatGPT)»**
- `hosts` — **1 387 758 байт, 47 459 строк**, из них 47 322 data-строки, 67 комментариев, 49 пустых, **всего 21 уникальный IP**. Шапка: `# dns.malw.link: hosts file + dns.geohide.ru: hosts file`, «Последнее обновление: 22 мая 2026», источники: `dns.geohide.ru:8443`, `info.dns.malw.link/hosts`, `iplist.opencck.org/ru`, `freedom.mafioznik.xyz/file/hosts`, `lolz.live/threads/7884871/`, `github.com/Flowseal/zapret-discord-youtube`.
- `Инструкция.txt` — «перебрасываем трафик не на родные IP, а на зарубежные»; ставить в `C:\Windows\System32\drivers\etc\hosts`.
- `iptables скрипты под linux` — 3 правила DNAT (только «инста с рутором»):
  `31.13.72.174 → 31.13.72.172`, `193.46.255.29 → 172.64.33.155`, `193.46.255.26 → 173.245.59.155`.
- `Адреса для zapret под Instagram.txt` — 4 домена для `list-general.txt`: `instagram.com, cdninstagram.com, facebook.com, fbcdn.net` + совет поставить DNS 1.1.1.1 и `ipconfig /flushdns`.
- `Обход замедления Telegram на ПК.txt` — одна ссылка на `github.com/Flowseal/tg-ws-proxy`.

**(б) «Список доменов для обхода»**
- `Обязательно прочитать!.txt`: «Я не знаю, что будет с вашим процессором если вы добавите все домены отсюда в list-general.txt, но скажу сразу — **на свой страх и риск**, тут все заблокированные домены в РФ.»
- `Сам список.txt` — **9 714 615 байт, 499 231 домен**, простой список по одному в строке (пиратские/лордфильм/фильм-домены в начале).

Оба блока — «сырьё», не для автоматической загрузки.

### 5.7 Годится ли для шиппинга

| Артефакт | Размер | Шипить? |
|---|---|---|
| `ipset-all.txt` | 564 КБ | **Лучше качать.** Это сгенерированный артефакт, меняется, и в нём есть CRLF. Шипить как fallback-снапшот — приемлемо (0.5 МБ), но с `tr -d '\r'` на входе. |
| `exclude.txt` | 83 Б | **Шипить** — крошечный, стабильный, но вычистить TAB. |
| `exclude-domains.txt` | 332 Б | **Шипить** — идеальный дефолт для `hostlist-exclude`. |
| `hosts` (GeoHide/malw) | 1.39 МБ | Качать, не шипить. |
| `Сам список.txt` | 9.7 МБ / 499k | **Не шипить никогда.** |

Лицензии в репозитории нет (только `README.md`), что для шиппинга — отдельный юридический риск; безопаснее качать по URL.

---

## 6. Что взять в Windows GUI

Приоритезировано по соотношению «ценность / стоимость».

### 6.1 Обязательно (must)
1. **Каталог стратегий как данные, а не код.** У Zapret-Manager стратегии — bash-функции `strategy_v1..v10` (`.sh:742-751`) + `$'…'`-переменные `Dv1..Dv17` (`.sh:528-544`) + внешний `StrYoutube`. Для GUI: один `strategies.json`/`strategies.toml` с полями `id, name, protocol(tcp/udp), target(main/youtube/discord/game), args[], requires_fake[], notes`. Это ровно то, что даёт таблица §2.
2. **Три независимых слота стратегии + сборка.** Основная (v) + YouTube (Yv) + Discord (Dv) + Игры (Gv) собираются в один конфиг через `--new` (`.sh:1847-1848`, `build_custom_strategy`). Это самая сильная идея всего проекта: пользователь меняет YouTube, не трогая основную.
3. **Контрольный замер «без обхода» + цветовой скоринг.** 🟢/🟡/🔴 относительно `CTRL_OK`, а не относительно нуля (`.sh:1062-1068`, `.sh:1090`). Без этого «10/12» ничего не значит.
4. **Автотест с бэкапом и гарантированным откатом.** `cp config config.bak` → перебор → вернуть `.bak` → применить только победителя. Плюс Ctrl+C → восстановление (`.sh:136`, `.sh:1127`). В GUI: отменяемая задача + «тест идёт в фоне, вкладку можно закрыть» (`readme.md:635`).
5. **Параллельный curl-пробник с таймаутами и Range.** `--connect-timeout 4 --max-time 6 --speed-time 3 --speed-limit 1 --range 0-65535`, 8 потоков (`.sh:1093-1094`). Именно `--range` отличает «DPI режет на первых КБ» от «сайт недоступен».
6. **Различение «DPI» и «блокировка по IP/DNS».** `readme.md:654`: если ни одна стратегия не открыла адрес (проверено ≥4), писать «Zapret тут не поможет — нужен туннель». Это снимает 80 % бесполезных тикетов.
7. **Исключения как first-class.** Три списка (`readme.md:695-700`): exclude, свои хосты, Google. Плюс отдельно **исключение устройств по IP** через nft-правило с mark (`.sh:1877`) — и предупреждение «закрепите IP в DHCP, иначе после ребута правило слетит» (`readme.md:725`, `readme.md:1321`).
8. **Переключение «весь трафик» ↔ «только список»** одной галочкой через подмену `--hostlist-exclude` → `--hostlist` (`.sh:729-735`). Это дешёвый и очень эффективный «режим безопасности» для банков/госуслуг.

### 6.2 Сильно желательно (should)
9. **Автоподбор по расписанию с правилом «только если лучше».** У панели это есть (`readme.md:658`), у консольного скрипта — нет (§3.7). В GUI обязательно второе.
10. **Сервис-менеджмент с состоянием `N/M` процессов.** `NFQ_RUN/NFQ_ALL` через `pgrep`/`init.d info` (`.sh:508-509`) — показывать «запущен [2/2]», а не просто «запущен».
11. **Синхронизация портов из стратегии.** `readme.md:622-626`: парсить `--filter-tcp`/`--filter-udp` из активной стратегии и добавлять/убирать в `NFQWS_PORTS_TCP/UDP`; при неудачном старте — откат портов.
12. **Логи с уровнями и «Сохранить в файл».** Результаты теста + список неоткрывшихся доменов как txt (`readme.md:657`). Плюс журнал событий с ограничением (у панели — 300 записей, `readme.md:2021`).
13. **Backup/restore как атомарный формат с хешами.** `.zmb` = tar(`manifest` + `files.lst` + `cron.lst` + `files.tgz`), SHA-256 на `files.tgz` и на списки, отказ восстанавливать повреждённую копию (`readme.md:2056-2070`). Плюс **авто-бэкап перед каждой установкой/удалением/обновлением** (`readme.md:1725`).
14. **Сторож (watchdog).** Каждые 2 мин: поднять упавший сервис, вернуть DNS, вернуть конфиг после прерванной операции (`readme.md:1996-2010`). В Windows — Scheduled Task + идемпотентная проверка.
15. **Импорт чужих стратегий.** Конвертер `.bat` → внутренний формат (`.sh:765-779`) — 20 `sed`-замен и выбрасывание несовместимых директив. Для GUI: свой маппинг `%BIN%`, `%LISTS%`, `%GameFilterTCP%` и **явный лог «что выброшено»**.
16. **Редактор списков с лимитами.** До 5000 доменов за раз, обрезка `https://` и `www.`, поиск по родительскому домену, «не открывать списки > 256 КБ в редакторе» (`readme.md:701-703`).
17. **Флаг «не изменять» (`#nochange`).** Строка-маркер в стратегии/списке, запрещающая программе их трогать (`readme.md:2083`). Дешёво и очень успокаивает пользователя.

### 6.3 Полезно (nice)
18. **Экспорт/импорт профиля** (роутер A → роутер B) с честным предупреждением, что отличается.
19. **«Куда пойдёт запрос»** — диагностика по домену/IP: напрямую / через обход / исключение / блок (`readme.md:873`).
20. **Доп. секция для части устройств** (до 32 IP, своя стратегия, отдельный процесс `nfqws`, предупреждение при <200 МБ ОЗУ) — `readme.md:709-729`.
21. **Проверка синтаксиса стратегии до применения** («заведомо нерабочую строку сохранить не даст», `readme.md:607`).
22. **Обновление с валидацией файла до запуска**: первая строка `#!/bin/sh`, наличие маркера версии, финальное сообщение, `sh -n` (`readme.md:2045-2049`).
23. **Идемпотентный дедуп при мерже списков** — `grep -Fxv -f` (`.sh:783`); у Zapret-Manager это есть только для Google-доменов, для РКН — нет.

### 6.4 Чего НЕ копировать
- `sed -i` по живому `/etc/config/zapret` без `.tmp` + `mv` (гонки, порча при Ctrl+C).
- `wget -O файл` без `.tmp` (§4.2).
- `sort -t'/' -k1 -nr` по строкам вида `Name → 5/12` — работает случайно; нужен явный парсинг полей.
- `repeats=8` в v7 при `repeats=6` в документации (§2.1).
- Автоподбор, применяющий победителя без сравнения с текущей стратегией (§3.7).
- 499k-доменный список «на всякий случай».

---

## 7. Предупреждения в доках: ложные срабатывания, банки/госуслуги, исключения

### 7.1 Прямые предупреждения (цитаты)

| Тема | Текст | Источник |
|---|---|---|
| Банки ломаются от обхода | «**Исключения** — сайты, которые Zapret не трогает. *Зачем:* некоторые сайты (например, **банки**) ломаются, если их "переодевать".» | `readme.md:696` |
| Банк/госуслуги перестали работать | «Добавьте сайт в **исключения**: в Zapret — список "Исключения"… Такие сайты будут открываться как обычно, без обхода.» | `readme.md:1716-1718` |
| Обход ломает обычные сайты | «**Обход по спискам РКН** — обход работает только для сайтов из списка заблокированных… а не "для всего, кроме исключений". *Зачем:* **если стратегия ломает обычные сайты**.» | `readme.md:603` |
| Исключения для туннелей | «Домены, IP и подсети, которые **всегда идут напрямую**, мимо WARP и VPN… *Зачем:* **банки, госуслуги** и сервисы, которые ломаются через VPN. Сайты исключайте именно доменом.» | `readme.md:855`, `readme.md:918` |
| Российские сайты через зарубежный DNS | «зарубежный сервер иногда отдаёт адреса российских сайтов не в России — **Госуслуги, банки** и онлайн-кинотеатры могут работать хуже.» | `readme.md:1124` |
| Xtreme-режим | «Внимание: Xtreme режим **может повлиять на работу приложений и соединений**! Используйте его только для проверки работы игр!» | `.sh:592` |
| Антивирус на установщик | «Антивирус или Windows может ругаться — **это ложная тревога**» (внутри `.bat` — `plink` из PuTTY) | `readme.md:298-308` |
| Несовместимость движков | Zapret ⟂ Zapret2 (оба обрабатывают один трафик); Steer ⟂ Forkozz/NetShift/splify2; Forkozz ⟂ Mixomo/DoH/Podkop/PassWall/NextDNS; Mixomo ⟂ ByeTube | `readme.md:1777-1788` |
| Ключи в бэкапе | «В `files.tgz` лежат **ключи WARP и AmneziaWG, ссылки подписок и вход Tailscale** в открытом виде. Не публикуйте файл копии.» | `readme.md:2072-2073` |
| Порт 7788 | «**Не пробрасывайте порт 7788 в интернет.**» | `readme.md:2078` |
| TCP timestamps на клиенте | «Для работы этой стратегии нужно один раз в терминале Windows выполнить: `netsh int tcp set global timestamps=enabled`» — срабатывает на любую стратегию с `fooling=…ts` | `.sh:293-294`, `readme.md:1467` |
| Flow Offloading | «Zapret некорректно работает с включённым Flow Offloading» → нужен FIX | `.sh:2013-2014`, `readme.md:1465` |
| IPv6 | «Обнаружен IPv6! Включите IPv6 в системе явно» | `.sh:2007` |
| Конфликтующие пакеты | «Обнаружены установленные `byedpi`/`youtubeUnblock`! Zapret может работать некорректно» | `.sh:2009-2010` |
| Discord/игры требуют закреплённого IP | «правила… привязаны к адресу — без закрепления они могут "слететь" после перезагрузки» | `readme.md:1321`, `readme.md:1661` |
| IPv6 в доп. секции | «IPv6-трафик устройства идёт по основной стратегии» (т.е. правило секции не сработает) | `readme.md:725` |
| Telegram на юге РФ | «Разблокировка Telegram если вы живете на юге России — **Невозможна**, используйте SOCKS5 или MTProto прокси. Блокировка реализована **без использования ТСПУ** провайдерами… zapret тут не поможет» | `IPSets README.md:17` |

### 7.2 Что отсюда следует для Windows GUI

1. **Исключения — не опция, а обязательный дефолт.** `exclude-domains.txt` из V3nilla (27 доменов: сбербанк, госуслуги, ozon, wildberries, dns-shop, citilink, яндекс, вк, мтс, mail.ru) — готовый стартовый набор. У Zapret-Manager свой, на 723 записи (`zapret-hosts-user-exclude.txt`), и он начинается с TLD-заглушек: `ru, by, рф, su, xn--p1ai, xn--p1acf, xn--80aswg, xn--d1acj3b, xn--80adxhks, xn--80asehdb` — то есть **весь .ru/.by/.su/.рф по умолчанию исключён**.
2. **Два режима обхода нужно показывать явно:** «весь трафик, кроме исключений» (риск сломать банк) vs «только список заблокированных» (безопасно, но список неполный). Дефолт для неопытного — второй.
3. **Не шипить 499k-доменный список.** Дословное предупреждение автора про процессор (`Обязательно прочитать!.txt`).
4. **CRLF-баг** в `ipset-all.txt` (§5.1) — при первом же импорте в GUI нужно нормализовать переводы строк, иначе весь ipset молча не загрузится.
5. **Cloudflare в ipset** (§5.4) — если шипить `ipset-all.txt` как есть, через обход пойдёт треть интернета, включая то, что работает и так. Нужен либо `--ipset-exclude` с `exclude.txt`, либо предупреждение в UI.
6. **Предупреждение про `fooling=ts`** должно быть привязано к выбранной стратегии, а не показываться один раз глобально (как `.sh:293-294` с флагом `ts_warning_shown`).
