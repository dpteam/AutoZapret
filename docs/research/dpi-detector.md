# dpi-detector (Runnin4ik) — разбор для AutoZapret

> Источник: `refs/dpi-detector` (github.com/Runnin4ik/dpi-detector), **MIT**, Python 3.8+,
> 2.4k звёзд, обновлён 2026-11-03. 65 файлов, ~1 МБ. Собран в один exe через PyInstaller.
> Дата разбора: 2026-11-04.

---

## 0. Зачем это нам

Мой базовый замер (P1) отвечает на вопрос «сайт открывается или нет». Это слишком грубо:

- `youtube` не открылся — а почему? DPI рвёт TLS по SNI, или провайдер подменил DNS,
  или это классический обрыв на 16-20 КБ, или сервер вообще не отвечает?
- От ответа зависит **какая стратегия поможет**. Для TLS-RST нужен `fake`/`fakedsplit`,
  для обрыва на 16-20 КБ — `multisplit` с `seqovl`, для подмены DNS — вообще не zapret,
  а DoH или hosts.

`dpi-detector` делает ровно это: **классифицирует тип блокировки**. Его enum `ProbeStatus`
(`utils/error_classifier.py:8-27`) — готовый словарь состояний, который надо перенять.

---

## 1. Классификация блокировок (главное)

Из `utils/error_classifier.py`:

| Статус | Что значит | Что делать |
|---|---|---|
| `OK` | Открылось | — |
| `REDIR` | Легитимный редирект | — |
| `REDIR_SUSPECT` | Редирект на чужой домен | подозрительно |
| `BLOCKED` | Явная блокировка | zapret |
| `ISP PAGE` | Подменная страница провайдера | DNS/hosts |
| `LOCAL IP` | Домен резолвится в локальный/приватный IP | DNS/hosts |
| `TIMEOUT` | Тишина, ни RST, ни ответа | «тихий» DPI, часто 16-20 КБ |
| `TCP16-20` | Обрыв на 16-20 КБ | multisplit + seqovl |
| `TLS RST` | TCP RST в ответ на ClientHello | fake / fakedsplit |
| `TLS ALERT` | Поддельный TLS-алерт от DPI | fake-tls |
| `TLS BLOCK` | Алерт protocol_version | блок версии TLS |
| `TLS MITM` | Подмена сертификата | серьёзная блокировка |
| `TLS SPOOF` | Мусор вместо TLS («wrong version number», record overflow) | fake-tls |
| `TLS EOF` | Обрыв на handshake | fake |
| `NO_TLS13` | TLS 1.3 не проходит, 1.2 проходит | ограничить версию |
| `DNS FAIL` | DNS не отвечает | сменить DNS |
| `OS ERR` | Ошибка ОС (см. errno) | диагностика |

### 1.1 Как различаются TLS-ошибки

Ключевые эвристики (`classify_ssl_error`, строки 96-160):

```python
"wrong version number"            -> TLS SPOOF   # подмена ответа
"record overflow" / "decode error"-> TLS SPOOF   # мусор
"unrecognized_name"               -> TLS ALERT   # SNI-блок
"handshake_failure"               -> TLS ALERT   # DPI-алерт
"protocol_version"                -> TLS BLOCK
"eof" при bytes_read == 0         -> TLS RST     # RST замаскирован под EOF
```

Важно: **`EOF` при нуле прочитанных байт — это почти всегда RST**, а не «сервер закрыл».
Windows маскирует RST под EOF, и наивная проверка «нет данных = таймаут» врёт.

### 1.2 Windows errno

`config.yml`:
```
WSAECONNRESET: 10054   WSAECONNREFUSED: 10061
WSAETIMEDOUT: 10060    WSAENETUNREACH: 10051
WSAEHOSTUNREACH: 10065 WSAECONNABORTED: 10053
```
В .NET это `SocketException.SocketErrorCode`, но коды те же — можно сопоставлять.

---

## 2. Тест «TCP 16-20 КБ» (`core/tcp16_scanner.py`)

Это отдельная техника, которой у меня нет, а она **критична для РФ**: провайдер рвёт
соединение после передачи ~16-20 КБ, при этом handshake проходит нормально.

**Как детектируют:**
1. TCP-соединение к конкретному IP (не домену — чтобы исключить SNI-фильтр).
2. `HEAD` запросы **по одному keep-alive соединению**.
3. Начиная со второго запроса добавляют заголовок `X-Pad` со случайной строкой
   (`FAT_CHUNK_SIZE` = 4000 байт по умолчанию), чтобы протолкнуть через соединение
   ещё ~4 КБ.
4. `FAT_CHUNKS_COUNT` = 10 → всего ~40 КБ.
5. Если соединение умерло на chunk N — считают объём и сравнивают с
   `TCP_BLOCK_MIN_KB=12` .. `TCP_BLOCK_MAX_KB=36`.
6. **Динамический таймаут по RTT**: `max(rtt*3, 1.5)` сек — чтобы не ждать 12 секунд
   на быстром канале.

**Почему IP, а не домен:** блокировка по объёму работает на уровне TCP-потока и
не зависит от SNI. Тестируя IP напрямую, отделяют «обрыв по объёму» от «блок по SNI».

**Список целей** — `tcp16.json`, 11 КБ: ~120 записей
`{id, asn, provider, ip, port}`. Провайдеры: Hetzner, Cloudflare, CDN77, Akamai,
AWS, DigitalOcean, OVH и т.д. Порт 443 и 80.

---

## 3. Обнаружение подмены DNS (`core/dns_scanner.py`)

Логика (`_check_tls_single`, `tls_scanner.py:111-150`):

1. Сначала собрать **IP-заглушки провайдера**: резолвить заведомо заблокированные домены
   и найти IP, которые повторяются у разных доменов или попадают в приватные диапазоны.
2. При проверке домена: если он резолвится в такой IP → `ISP PAGE` (страница-заглушка).
3. `get_fake_ip_type(ip)`: `fakeip` (198.18.0.0/15 — Clash fake-ip), `local`
   (127/8, 10/8, 192.168/16), `isp` (из списка заглушек).

**Вывод для нас:** прежде чем ругать zapret, надо проверить, а не DNS ли виноват.
Если провайдер подменяет DNS — zapret не поможет вообще, нужен DoH/hosts.

---

## 4. Обнаружение чужих обходчиков (`utils/bypass_detector.py`)

Готовый список имён процессов (Windows: `tasklist /FO CSV /NH`):

```
zapret:        nfqws, tpws, zapret, dvtws, dbproxy, winws
GoodbyeDPI:    goodbyedpi, goodbye-dpi, windivert
ByeDPI:        byedpi
SpoofDPI:      spoofdpi
DPI-Blocker:   dpi-blocker, dpiblocker
Green Tunnel:  greentunnel, green-tunnel
PowerTunnel:   powertunnel
Hysteria2:     hysteria
TROJAN:        trojan, trojan-go
NaiveProxy:    naive
Wstunnel:      wstunnel
UDP2RAW:       udp2raw
dnscrypt:      dnscrypt-proxy
xray:          xray
FlClashX:      flclashx, flclashcore
AmneziaWG:     amneziawg
Clash:         mihomo, clash-meta, clash-verge, clash_verge, verge-mihomo
sing-box:      sing-box
Hiddify:       hiddify
Tailscale:     tailscale
ZeroTier:      zerotier
Mullvad:       mullvad
WARP:          warp-svc, cloudflarewarp
NordVPN:       nordvpn
```

Это ровно то, что нужно в диагностику: «у тебя запущен Hiddify — он и перехватывает,
поэтому zapret не работает».

---

## 5. `whitelist_sni.txt`

3.2 КБ, ~200 доменов, которые DPI **пропускает**: `vk.com`, `2gis.ru`, `alfabank.ru`,
`gosuslugi.ru`, `max.ru`, `avito.ru`, `wildberries.ru`, `yandex.ru`…

Совпадает по смыслу с `refs/TLSpack/whitelist/` и с тактикой `hostfakesplit host=ozon.ru`.
**Ценность:** это готовый список SNI для подстановки в фейки — DPI видит «доверенный»
российский домен и пропускает.

---

## 6. Что переносим в AutoZapret

### 6.1 Обязательно (P4)

- [x] **Классификация типа блокировки** вместо бинарного ok/fail.
      Минимум: `OK / TIMEOUT / TLS_RST / TLS_ALERT / TLS_SPOOF / DNS_FAIL / TCP16-20`.
- [x] Различение «EOF при 0 байт = RST» (иначе все RST выглядят таймаутами).
- [x] Определение подмены DNS (резолв в приватный/повторяющийся IP → `ISP PAGE`).
- [x] Список чужих обходчиков в диагностику.

### 6.2 Очень желательно

- [ ] **Тест TCP 16-20 КБ** с `tcp16.json` — это самый частый тип блокировки в РФ.
- [ ] Динамический таймаут по RTT (`max(rtt*3, 1.5)`) — иначе перебор медленный.
- [ ] `whitelist_sni.txt` как источник SNI для фейков.

### 6.3 Не переносим

- `httpx`/`asyncio` — у нас net48 и своя модель потоков.
- `trace_hook` httpx — заменяется на прямой `SslStream` + `TcpClient`.
- PyInstaller-упаковку, Docker, web-режим — не наша задача.

---

## 7. Как это меняет перебор стратегий

Сейчас: «стратегия дала 4/5 — берём её».

Станет: **рекомендация по типу блокировки**.

| Тип блокировки | Что пробовать в первую очередь |
|---|---|
| `TLS RST` | `fake`, `fakedsplit`, `SIMPLE FAKE*` |
| `TLS SPOOF` | `FAKE TLS AUTO*` (нужен правдоподобный ClientHello) |
| `TLS ALERT` (SNI-блок) | `hostfakesplit host=<whitelist-домен>`, `ALT9`, `ALT13` |
| `TCP16-20` | `multisplit` + `seqovl`, `ALT2`, `ALT7`, `general` |
| `TIMEOUT` | `syndata`, `ALT5`, `EXP` |
| `DNS_FAIL` / `ISP PAGE` | **не zapret**: DoH или hosts |
| `NO_TLS13` | ограничить TLS 1.3 в браузере |

Это превращает «перебор 22 стратегий наугад» в «перебор 3-4 подходящих».
Экономия времени — в разы.

---

## 8. Лицензия

MIT — можно изучать, переписывать и использовать идеи. Код на Python, порт на C#
не является производной работой в юридическом смысле, но attribution в README
всё равно поставим (мы это делаем для всех).
