# tg-ws-proxy (Flowseal) — разбор реализации под порт на C# / .NET Framework 4.8

Версия в репозитории: `1.11.4` (`proxy/__init__.py:4`). Лицензия MIT. Всё ниже — по фактическому коду, не по README.

---

## 1. Архитектура: точки входа, потоки/задачи, границы модулей, конфиг

### 1.1 Три точки входа

| Точка входа | Файл | Назначение |
|---|---|---|
| `tg-ws-proxy` → `proxy.tg_ws_proxy:main` | `proxy/tg_ws_proxy.py:593` | Консольный прокси, только argparse |
| `tg-ws-proxy-tray-win` → `windows:main` | `windows.py:563` | GUI + трей + прокси в фоновом потоке |
| `tg-ws-proxy-tray-linux` / `-macos` | `linux.py:388`, `macos.py:531` | То же для Linux/macOS |

`pyproject.toml:56-60` объявляет эти console_scripts. Docker (`Dockerfile:48-60`) запускает `proxy/tg_ws_proxy.py` напрямую с env-переменными `TG_WS_PROXY_*`.

### 1.2 Потоки и задачи

```
[main thread]  argparse / tray UI (pystray) / CTk mainloop (отдельный поток)
     │
     ├─ threading.Thread(name="proxy", daemon=True)   utils/tray_common.py:394
     │     └─ asyncio.new_event_loop() → loop.run_until_complete(_run(stop_event))
     │           ├─ asyncio.start_server(client_cb, host, port)      tg_ws_proxy.py:422
     │           ├─ Task на каждое TCP-подключение клиента           tg_ws_proxy.py:418
     │           ├─ Task log_stats (раз в 5 с / отчёт раз в 60 с)    tg_ws_proxy.py:471-494
     │           ├─ Task serve_forever + Task _listener_watchdog     tg_ws_proxy.py:510-521
     │           └─ ws_pool: Task _refill / _rotate на каждый (dc,is_media,is_test)  pool.py:66-171
     │
     ├─ threading.Thread(name="cfproxy-domains-refresh", daemon)     config.py:199-204
     ├─ threading.Thread(name="ctk-root", daemon)                    tray_common.py:525-536
     └─ threading.Thread(name="update-check"/"manual-update")        tray_common.py:492, windows.py:293
```

Один клиент = одна asyncio-задача + 2 подзадачи (`tcp_to_ws`, `ws_to_tcp`, `bridge.py:416-417`). Один WS на клиента — мультиплексирования в WS-маршруте **нет**.

### 1.3 Границы модулей

| Модуль | Ответственность |
|---|---|
| `proxy/tg_ws_proxy.py` | listener, разбор входа, handshake, выбор маршрута, генерация ссылок |
| `proxy/utils.py` | **все константы байтовой раскладки**, таблицы DC, домены WS, `get_link_host`, цензура доменов в логах |
| `proxy/fake_tls.py` | Fake-TLS: проверка ClientHello, сборка ServerHello, TLS-обёртка стрима, зондирование профиля |
| `proxy/bridge.py` | ре-шифрование, `MsgSplitter` (нарезка на MTProto-пакеты), fallback-цепочка |
| `proxy/raw_websocket.py` | ручной WS-клиент (RFC 6455), маскирование, `set_sock_opts` |
| `proxy/pool.py` | пул предпрогретых WS на DC, ротация, backoff |
| `proxy/balancer.py` | выбор CF-домена на DC |
| `proxy/cf_h2.py` + `h2_transport.py` | CF media через HTTP/2 POST `/api` |
| `proxy/config.py` | `ProxyConfig`, парсинг `DC:IP`, список CF-доменов |
| `proxy/_aes.py` | AES-CTR (cryptography или ctypes→libcrypto) |
| `proxy/event_loop.py` | обход бага CPython self-pipe на Windows Proactor |
| `ui/*` | CTk-формы, i18n (ru/en), connectivity-тесты |
| `utils/tray_common.py` | **жизненный цикл прокси, config.json, single-instance, `tg_proxy_url`** |

### 1.4 Конфиг: CLI и JSON

Два независимых набора. CLI — `proxy/tg_ws_proxy.py:594-647`, JSON — `utils/default_config.py:14-32` + `docs/RU/TrayConfig.md`.

| CLI | JSON key | По умолч. | Смысл |
|---|---|---|---|
| `--port` | `port` | `1443` | Порт прослушивания |
| `--host` | `host` | `127.0.0.1` | Адрес; `0.0.0.0` → `get_link_host` подставит LAN-IP |
| `--secret` | `secret` | random 16 B hex | 32 hex-символа, ровно; иначе `sys.exit(1)` |
| `--dc-ip DC:IP` | `dc_ip[]` | `2:149.154.167.220`, `4:149.154.167.220` | Цель для DC; повторяемый; пустой `--dc-ip` = очистить |
| `--buf-kb` | `buf_kb` | `256` | `max(4,n)*1024` → `SO_RCVBUF`/`SO_SNDBUF` |
| `--pool-size` | `pool_size` | `4` | Предпрогретых WS на (DC, media). `0` = отключить прямой WS |
| `--no-cfproxy` | `cfproxy` | `true` | CF-proxy fallback |
| `--no-h2` | `h2` | `true` | Мультиплексирование медиа по HTTP/2 |
| `--cfproxy-domain` | `cfproxy_user_domain[]` + `..._enabled` | `[]` / `false` | Свои CF-домены |
| `--cfproxy-worker-domain` | `cfproxy_worker_domain[]` + `..._enabled` | `[]` / `false` | CF Worker домены |
| `--no-secure` | `no_secure` | `false` | Порт 80 для CF-плеч |
| `--fake-tls-domain` | **нет в JSON** | `''` | Включает Fake-TLS (`ee`-секрет). Только CLI |
| `--force-test-dc` | `force_test_dc` | `false` | Весь трафик на тестовые DC |
| `--proxy-protocol` | **нет в JSON** | `false` | Принимать PROXY protocol v1 |
| `-v` | `verbose` | `false` | DEBUG |
| `--log-file` | — | `null` | Файл лога |
| `--log-max-mb` | `log_max_mb` | `5` | Ротация |
| `--log-backups` | — | `1` | Кол-во бэкапов, минимум 1 (`utils/logging_setup.py:32-33`) |
| — | `check_updates` | `true` | Проверка GitHub Releases |
| — | `appearance` | `auto` | `auto`/`light`/`dark` |
| — | `language` | system | `ru`/`en` |
| — | `autostart` | `false` | Только Windows |

Пути: Windows `%APPDATA%\TgWsProxy`, macOS `~/Library/Application Support/TgWsProxy`, Linux `$XDG_CONFIG_HOME/TgWsProxy` (`utils/tray_common.py:31-36`). Портативный режим — каталог `TgWsProxy_data` рядом с exe или флаг `--portable` (`tray_common.py:53-67`).

Производный флаг (`config.py:79-82`):

```python
h2_enabled = cfproxy_h2_media and fallback_cfproxy and not disable_secure and not force_test_dc
```

---

## 2. Протокол: точный формат провода

### 2.1 Секрет

Секрет — **16 произвольных байт**, в конфиге/CLI — 32 hex-символа. Префиксы `dd`/`ee` в самом секрете **не хранятся** — они добавляются только при генерации ссылки (`tg_ws_proxy.py:433-441`):

```python
dd_link = f"tg://proxy?server={link_host}&port={port}&secret=dd{secret}"
domain_hex = ftls.encode('ascii').hex()
ee_link = f"tg://proxy?server={link_host}&port={port}&secret=ee{secret}{domain_hex}"
```

- `dd` + 32 hex = 18 байт секрета в base16 (префикс `dd` = 0xDD 0xDD как первые два байта «расширенного» секрета). Клиент понимает: обфускация с random padding.
- `ee` + 32 hex + hex(domain) = Fake-TLS, домен — SNI/маскировка.
- Валидация: `len==32` и `bytes.fromhex()` (`tg_ws_proxy.py:663-672`).

**Префикса `7` в этом репозитории нет вообще** (grep по всему дереву пуст). Медиа-режим определяется не секретом, а знаком DC-индекса в handshake — см. 2.3. В экосистеме MTProxy ведущая `7` — конвенция «media-only proxy», но здесь она не генерируется и не разбирается; при порте не закладывайтесь на неё.

### 2.2 Обфусцированный MTProto handshake (obfuscated2)

Константы — `proxy/utils.py:16-38`:

```python
ZERO_64=64*'\x00'; HANDSHAKE_LEN=64; SKIP_LEN=8; PREKEY_LEN=32; KEY_LEN=32; IV_LEN=16
PROTO_TAG_POS=56; DC_IDX_POS=60
PROTO_TAG_ABRIDGED=b'\xef\xef\xef\xef'
PROTO_TAG_INTERMEDIATE=b'\xee\xee\xee\xee'
PROTO_TAG_SECURE=b'\xdd\xdd\xdd\xdd'
```

**Входной 64-байтовый init от клиента:**

```
0..7     random (8 B, пропускаются)
8..39    prekey (32 B)
40..55   iv (16 B)
56..59   proto tag
60..61   dc_idx  int16 little-endian, signed
62..63   random
```

Разбор (`tg_ws_proxy.py:50-74`):

```
key = SHA256(prekey || secret)        # 32 B → AES-256
dec = AES-256-CTR(key, iv)            # состояние с нуля
plain = dec.update(handshake[0:64])
tag = plain[56:60]                    # должен быть EF/EE/DD ×4
dc_idx = int16_le(plain[60:62])
dc = abs(dc_idx); is_media = dc_idx < 0
```

**Ключевая асимметрия направлений** (`tg_ws_proxy.py:212-253`) — это главное, что легко испортить при порте:

| Направление | Ключ | IV | Пропуск 64 B |
|---|---|---|---|
| клиент → прокси | `SHA256(prekey‖secret)` | `handshake[40:56]` | да |
| прокси → клиент | `SHA256(rev[0:32]‖secret)`, где `rev = handshake[8:56][::-1]` | `rev[32:48]` | нет |
| прокси → Telegram | `relay_init[8:40]` **без secret** | `relay_init[40:56]` | да |
| Telegram → прокси | `rev2[0:32]`, `rev2 = relay_init[8:56][::-1]` | `rev2[32:48]` | нет |

То есть к клиенту — с хешем секрета, к Telegram — «сырой» ключ из init (сервер Telegram не знает секрета). Реверс 48-байтового блока (`prekey‖iv`) даёт ключ/IV обратного направления — это требование MTProxy-спеки.

**Генерация relay_init** (`tg_ws_proxy.py:77-108`):

```
1. rnd = 64 случайных байта; отбраковать и перегенерировать, если
     rnd[0] == 0xEF
     rnd[0:4] in {HEAD, POST, "GET ", EFEFEFEF, DDDDDDDD, 16 03 01 02, OPTI}
     rnd[4:8] == 00 00 00 00
2. enc_key = rnd[8:40], enc_iv = rnd[40:56]
3. ks = AES-256-CTR(enc_key, enc_iv); прогнать 64 нулевых байта → keystream
4. tail_plain = tag(4) || int16_le(dc_idx)(2) || random(2)
5. rnd[56:64] = tail_plain XOR keystream[56:64]
```

Зарезервированные старты (`utils.py:33-38`) выбраны так, чтобы init не выглядел как HTTP-запрос (`HEAD`/`POST`/`GET `/`OPTI`) или как начало TLS-записи (`16 03 01 02`). `dc_idx` при отправке = `-dc`, если медиа, иначе `dc` (`tg_ws_proxy.py:307`) — знак сохраняется, Telegram сам выбирает медиа-эндпоинт.

Тестовые DC: `dc >= 10000` → `dc -= 10000`, флаг `is_test_dc` (`tg_ws_proxy.py:295-298`).

### 2.3 Прототипы транспорта и `MsgSplitter`

`proto_int`: `0xEFEFEFEF` abridged, `0xEEEEEEEE` intermediate, `0xDDDDDDDD` padded intermediate (`tg_ws_proxy.py:300-305`).

Нарезка потока на отдельные MTProto-пакеты, чтобы каждый шёл своим WS-фреймом (`bridge.py:98-135`):

- **abridged**: `plain[off]`; если `0x7F`/`0xFF` → длина `int24_le(plain[off+1:off+4]) * 4`, заголовок 4 B; иначе `(b & 0x7F) * 4`, заголовок 1 B.
- **intermediate / padded**: `int32_le & 0x7FFFFFFF` = payload, заголовок 4 B.
- длина 0 → splitter сам себя отключает (`_disabled = True`), дальше passthrough.
- `flush()` отдаёт хвост при EOF.

### 2.4 WS-клиент к Telegram (RFC 6455 руками)

`proxy/raw_websocket.py:92-165`. Апгрейд-запрос **ровно такой** (никаких `Origin`, `User-Agent`, `Accept-Encoding`):

```
GET /apiws HTTP/1.1
Host: {domain}
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: {base64(16 random bytes)}
Sec-WebSocket-Version: 13
Sec-WebSocket-Protocol: binary

```

Важно: **`Sec-WebSocket-Accept` не проверяется** — принимается любой `101` (`raw_websocket.py:152-153`). Не-101 → `WsHandshakeError(status, line, headers, location)`.

Пути: `/apiws`, тестовые — `/apiws_test` (`utils.py:60-61`).

Кодек кадров (`raw_websocket.py:263-298`):

```
b0 = 0x80 | opcode           opcode: 0x0 CONT, 0x2 BINARY, 0x8 CLOSE, 0x9 PING, 0xA PONG
b1 = (mask?0x80:0) | len
  len<126  → len в b1
  len<65536→ b1=126, +2 B BE
  иначе    → b1=127, +8 B BE
mask: 4 B ключ, payload[i] ^= key[i & 3]
```

Прокси **всегда маскирует** исходящие (роль клиента). `MAX_MESSAGE_LEN = 16 MiB`; превышение — `ConnectionError` до чтения тела. Фрагментация: `fin=0` + `CONT` собираются в `_frag`; control-фреймы между фрагментами пропускаются. `PING` → сразу `PONG` с тем же payload.

`_xor_mask` (`raw_websocket.py:43-54`) для `len >= 512` использует 4 таблицы трансляции `bytes(v ^ key for v in range(256))` — быстрый путь.

### 2.5 «Домен-фронтинг» / CDN-трюк — четыре разных механизма

**A. Прямой WS (pool, основной путь).** `pool.py:173-192`:

```
TCP connect(target_ip, 443)            # target_ip = dc_redirects[dc], т.е. IP Telegram
TLS  SNI = kws{dc}.web.telegram.org    # полная проверка сертификата
WS   Host: kws{dc}.web.telegram.org    path=/apiws
```

**B. Фронтинг (fallback внутри пула).** Тот же IP Telegram, но:

```
SNI = "sprinthost.ru"                  # посторонний домен
Host: kws{dc}.web.telegram.org
ssl_context = create_ssl_context(check_hostname=False)   # raw_websocket.py:27
```

DPI видит SNI чужого сайта; TLS-сертификат не проверяется. Порядок попыток запоминается в `self.try_fronting_first` (`pool.py:175,186`).

**C. CF-proxy (свой домен в Cloudflare).** `bridge.py:248-257`:

```
TCP connect(kws{dc}.{cf_domain}, 443)  # DNS клиента → Cloudflare
SNI = Host = kws{dc}.{cf_domain}
path = /apiws
```

Cloudflare в режиме **Flexible** терминирует TLS и идёт к origin по HTTP/80. Origin задаётся A-записями пользователя (`docs/RU/CfProxy.md:17-23`):

```
kws1 → 149.154.175.50   kws2 → 149.154.167.51   kws3 → 149.154.175.100
kws4 → 149.154.167.91   kws5 → 149.154.171.5    kws203 → 91.105.192.100
```

Список доменов по умолчанию зашит в обфусцированном виде (`config.py:22-58`): `_dd()` сдвигает каждую букву метки назад на число букв в метке и приписывает `.co.uk` (`_S = chr(46,99,111,46,117,107)`); напр. `virkgj` → `pclead`. Обновление — HTTP GET `raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt` раз в час, применяется только если валидных доменов ≥ 3 (`config.py:164-185`).

**D. CF Worker (чистый TCP-туннель).** `pool.py:373-389`, `bridge.py:203-207`:

```
path = /apiws?dst={fallback_ip}&dc={dc}
```

Worker (`docs/RU/CfWorker.md:74`) делает `connect({hostname: dst, port: 443})` и перекачивает байты — то есть внутрь уходит уже наш 64-байтовый relay_init, а не MTProto-пакеты.

**Что делает трафик похожим на обычный HTTPS:** TLS 1.3 с X25519/X25519MLKEM768, валидный SNI реального домена, валидная цепочка (certifi, `utils.py:153-161`), ALPN `h2`,`http/1.1` при зондировании, и на Fake-TLS-входе — синтезированный ServerHello с реалистичными размерами записей, снятыми с настоящего сайта.

### 2.6 Медиа через HTTP/2 (`/api`, не `/apiws`)

`cf_h2.py:722-726`:

```
POST https://kws{dc}.{cf_domain}/api
content-type: application/octet-stream
accept-encoding: identity
body = ровно один MTProto-пакет (plaintext после расшифровки клиента)
```

Ответ — один MTProto-пакет, выровненный по 4, либо 4 байта `int32_le` отрицательного кода (`cf_h2.py:768-771`), который отдаётся клиенту как транспортная ошибка. Preflight — `HEAD https://host/api`, требуется HTTP/2 и код 2xx либо 405/501 (`cf_h2.py:644-655`). Мультиплексирование: один TCP/TLS на (DC, домен) — «lane», до 64 стримов.

---

## 3. Режимы: Fake-TLS vs plain, «media only», источники DC-списков

### 3.1 Два режима входа

`masking = proxy_config.fake_tls_domain` (`tg_ws_proxy.py:275`).

**Plain (masking пусто).** Первый байт + ещё 63 = init, сразу в разбор (`tg_ws_proxy.py:202-209`).

**Fake-TLS (masking задан).** `tg_ws_proxy.py:139-200`:

1. Первый байт `0x16` → читаем 4 B заголовка, `record_len = be16(hdr[3:5])`, читаем тело. Таймаут 10 с на каждый шаг.
2. `verify_client_hello` — см. ниже. Провал → **прозрачный релей на `masking:443`**, предварительно отправив туда уже прочитанный ClientHello (`fake_tls.py:417-463`). Активное зондирование видит настоящий сайт.
3. Успех → `build_server_hello`, `await asyncio.sleep(random.uniform(0.008, 0.024))`, запись, `drain`.
4. Дальше чтение/запись идут через `FakeTlsStream`.
5. Первый байт ≠ `0x16` → `301 Moved Permanently` + `Location: https://{masking}/`, `Content-Length: 0` (`tg_ws_proxy.py:190-200`).

### 3.2 Fake-TLS: проверка ClientHello

`fake_tls.py:84-115`. Смещения: `CLIENT_RANDOM_OFFSET=11`, `CLIENT_RANDOM_LEN=32`, `SESSION_ID_OFFSET=44`, `SESSION_ID_LEN=32`, `TIMESTAMP_TOLERANCE=120`.

```
n >= 43, data[0] == 0x16, data[5] == 0x01
client_random = data[11:43]
zeroed = data с data[11:43] = 0x00 * 32
expected = HMAC-SHA256(secret, zeroed)
require constant_time_eq(expected[0:28], client_random[0:28])
ts = uint32_le(client_random[28:32] XOR expected[28:32])
require |now - ts| <= 120
session_id = data[44:76], если n >= 76 и data[43] == 0x20, иначе 32 нуля
```

### 3.3 Fake-TLS: сборка ServerHello

`fake_tls.py:194-233`. Ровно **три записи**, порядок обязателен:

```
record 1: 16 03 03 len16 | 02 len24 | body
  body = 03 03 | random[32] (позже патчится) | sid_len(1) | sid | cipher(2) | 00 | ext_len(2) | ext
  ext  = 00 2b 00 02 03 04                                  # supported_versions = TLS1.3
       | 00 33 00 (4+klen) group(2) klen(2) key             # key_share
record 2: 14 03 03 00 01 01                                 # CCS
record 3: 17 03 03 len16 | os.urandom(len)                  # ApplicationData
```

`group` = `0x11ec` (X25519MLKEM768) с `key = urandom(1088) || x25519_pub`, если клиент предложил этот group с длиной ровно 1216; иначе `0x001d` (X25519) и 32 B. Cipher — первый из `(0x1301, 0x1303, 0x1302)`, который есть в ClientHello, иначе `0x1301`.

Длина ApplicationData: `_profiled_appdata_len`, зажатая в `[64, 16384]`; если профиля нет — `random.randint(64, 160)` (`fake_tls.py:57-60,188-191,236-241`).

**HMAC всего полёта** (`fake_tls.py:227-233`):

```
hmac_input = client_random || flight_с_обнулённым_server_random
server_random = HMAC-SHA256(secret, hmac_input)
flight[11:43] = server_random
```

Проверяемые размеры: при MLKEM и appdata=64 полёт ровно **1290 байт** (`tests/test_fake_tls.py:241-245`). Арифметика: ServerHello record `5+1210=1215` + CCS `6` + AppData `5+64=69`. Без MLKEM (только X25519) — `127+6+69 = 202`.

### 3.4 FakeTlsStream

`fake_tls.py:319-414`. На запись — нарезка на записи `17 03 03 len16`, payload ≤ `TLS_APPDATA_MAX=16384`. На чтение — читает 5-байтовые заголовки, `0x14` (CCS) пропускает, `0x17` отдаёт payload (с переносом остатка в `_read_left`), любой другой тип → EOF.

### 3.5 «Media only»

Отдельного режима-переключателя нет. Медиа определяется **отрицательным `dc_idx`** в handshake. Дальше:

- маршрут: `ws_domains(dc, is_media=True)` → сначала `kws{dc}-1.web.telegram.org`, затем `kws{dc}.web.telegram.org` (`utils.py:64-69`);
- пул: отдельный ключ `(dc, is_media, is_test_dc)` (`pool.py:37`);
- в relay_init уходит `dc_idx = -dc`;
- CF H2 (`/api`) применяется **только** к медиа и только к CF-маршруту (`bridge.py:164-170`).

### 3.6 Откуда берутся DC IP

Статически, `proxy/utils.py:45-58`:

```python
DC_DEFAULT_IPS = {1:'149.154.175.50', 2:'149.154.167.51', 3:'149.154.175.100',
                  4:'149.154.167.91', 5:'149.154.171.5', 203:'91.105.192.100'}
DC_TEST_IPS    = {1:'149.154.175.10', 2:'149.154.167.40', 3:'149.154.175.117'}
```

Ничего не скачивается и не резолвится. `dc_redirects` (пользовательский) управляет **только** прямым WS-маршрутом; fallback на TCP и CF Worker берёт IP из `DC_DEFAULT_IPS`/`DC_TEST_IPS`. DC, отсутствующий в `dc_redirects`, всё равно обслуживается через CF (`pool.py:426-429`).

Обновляется только список CF-доменов (раз в час, см. 2.5C).

---

## 4. DC IP/порты, IPv4/IPv6

| Что | Значение | Источник |
|---|---|---|
| Прод DC 1..5, 203 | см. таблицу выше | `utils.py:45-52` |
| Тест DC 1..3 | `149.154.175.10`, `149.154.167.40`, `149.154.175.117` | `utils.py:54-58` |
| Порт наружу (WS/TCP/CF) | `443`; `80` при `--no-secure` для CF-плеч | `raw_websocket.py:104-111`, `bridge.py:179` |
| Порт прослушивания | `1443` по умолчанию | `config.py:64` |
| Домены WS | `kws{dc}.web.telegram.org`, медиа — `kws{dc}-1...`; `dc==203 → 2` | `utils.py:64-69` |

**Только IPv4.** `parse_dc_ip_list` валидирует через `inet_pton(AF_INET, ...)` (`config.py:219`) — IPv6 отбрасывается. В UI есть разовое предупреждение, если в системе есть IPv6, с советом отключить IPv6 в настройках прокси Telegram (`tray_common.py:429-456`, `ui/i18n/ru.json: ipv6.warning`). Это прямое следствие: прокси не слушает IPv6 и не умеет `::`.

---

## 5. UI

### 5.1 Трей-меню

`windows.py:499-525`, тексты — `ui/i18n/ru.json`:

| Пункт | Действие |
|---|---|
| «Открыть в Telegram (host:port)» (default) | `webbrowser.open(tg://proxy?...)`; при неудаче — в буфер |
| «Скопировать ссылку» | `pyperclip.copy` |
| «Перезапустить прокси» | `restart_proxy` в отдельном потоке |
| «Настройки...» | CTk-диалог |
| «Открыть логи» | `os.startfile(proxy.log)` |
| «Обновить (cur → new)» | появляется только при `has_update` |
| «Выход» | `quit_ctk()` + форс-`os._exit(0)` через 3 с |

### 5.2 Первый запуск

`ui/ctk_tray_ui.py:74-152`. Показывает: заголовок «Прокси запущен и работает в системном трее», автоматический путь (ПКМ → «Открыть в Telegram»), ссылку `tg://proxy?server=...&port=...&secret=dd{secret}`, ручной путь (Настройки → Продвинутые → Тип подключения → Прокси; MTProto; host:port; «Ключ: dd{secret}»), чекбокс «Открыть прокси в Telegram сейчас». Маркер `.first_run_done_mtproto`.

### 5.3 Форма настроек

`ui/settings_form.py`. Секции: Интерфейс (язык, тема), Соединение MTProto (IP, порт, ключ + кнопка `↺` — `os.urandom(16).hex()`), DC (многострочный `номер:IP`), Cloudflare Proxy (вкл, «Свой домен» + поле + кнопка «Тест», чекбокс H2), Cloudflare Worker (список доменов + «Тест»), Логи (verbose, no_secure, buf_kb, pool_size, log_max_mb), Обновления, Запуск Windows (autostart).

Валидация (`ui/settings.py:37-84`): `inet_aton(host)`, порт 1..65535, `parse_dc_ip_list`, секрет ровно 32 hex. Ключи, меняющие только UI — `appearance, autostart, check_updates, language`; всё остальное требует рестарта прокси (`ui/settings.py:12-24`).

**Тесты связности** (`ui/connectivity.py`): синхронный TCP+TLS+WS-апгрейд с таймаутом 5 с по DC `[1,2,3,4,5,203]`; для CF-proxy — `kws{dc}.{domain}/apiws`, для Worker — `/apiws?dst={ip}&dc={dc}&media=0`. Успех = подстрока «101» в первой строке ответа.

### 5.4 Что генерируется пользователю

**Всегда `dd`.** `utils/tray_common.py:421-426` и `ui/ctk_tray_ui.py:85`:

```python
f"tg://proxy?server={link_host}&port={port}&secret=dd{secret}"
```

`link_host` — `get_link_host(host)`: при `0.0.0.0` определяется через UDP-connect на `8.8.8.8:80` (`utils.py:80-90`). **Fake-TLS (`ee`) в GUI недоступен** — `fake_tls_domain` отсутствует в JSON-конфиге и не выставляется в `apply_proxy_config` (`tray_common.py:343-378`). Если нужен `ee` — только CLI.

---

## 6. Платформенная обвязка

### 6.1 Windows (`windows.py`)

- **Single instance**: именованный мьютекс `Local\TgWsProxy_SingleInstance` (`windows.py:69-84`) + fallback на pid-файл `<pid>.lock` с проверкой `psutil.create_time` (`tray_common.py:129-173`).
- **Автозапуск**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, имя `TgWsProxy`, значение `"C:\...\TgWsProxy.exe"` (`windows.py:298-332`). Только для frozen-сборки (`_supports_autostart`).
- **Трей**: `pystray.Icon(...).run()` в главном потоке; CTk — в отдельном потоке, диалоги через `root.after(0, ...)` + `threading.Event`.
- **Стоп**: `loop.call_soon_threadsafe(stop_ev.set)`, `join(timeout=5)`; при зависании — предупреждение «порт может остаться занятым» (`tray_common.py:400-411`). Выход — принудительный `os._exit(0)` через 3 с.
- **Обновление**: скачать, проверить SHA-256, `exe → *_oldtgws.exe`, подменить, `SetDllDirectoryW(None)`, `Popen(DETACHED_PROCESS|CREATE_NEW_PROCESS_GROUP)`, при ошибке — роллбэк (`utils/windows_update.py:27-54`). Старый exe удаляется фоновым потоком через 3 с (`windows.py:568-577`).

### 6.2 Linux (`linux.py`) / macOS (`macos.py`)

Linux: тот же `tray_common`, `pystray._appindicator`, `xdg-open` для логов, `--smoke-test` проверяет, что GTK/AppIndicator загружены **из бандла** (`/proc/self/maps`, `linux.py:351-385`). macOS: `NSApplicationActivationPolicyAccessory`, `run_detached()`, свой `_ui_queue` с `after(20,...)`, `faulthandler` в `crash.log`.

### 6.3 Права, фаервол, порт

**Админ не нужен. Фаервол не трогается. Правил netsh нет.** Grep по `admin|firewall|netsh|UAC` даёт только Linux-`sudo` в апдейте и classifier в `pyproject.toml`. Причина проста: слушается `127.0.0.1:1443` — непривилегированный порт на loopback, входящих правил не требует.

Единственная диагностика прав — маппинг ошибок bind (`utils/diagnostics.py:15-36`): `WSAEADDRINUSE 10048` → «порт занят», `WSAEACCES 10013` → «доступ запрещён (фаервол/антивирус/права)» с советом взять порт 10000–50000, `WSAEFAULT/WSAEADDRNOTAVAIL` → «некорректный адрес».

---

## 7. Упаковка

PyInstaller **onefile**, `console=False` (`packaging/windows.spec:64-86`). Внутрь:

- `customtkinter/assets` → `customtkinter/assets` (иначе темы/шрифты не найдутся);
- `ui/i18n` → `ui/i18n` (ru/en JSON);
- `certifi` (`collect_data_files`), на Linux дополнительно `certifi/cacert.pem` как DATA;
- `icon.ico`, `packaging/version_info.txt`;
- hiddenimports: `httpx`, `h2`, `pystray._win32` / `._appindicator`, `PIL._tkinter_finder`, `customtkinter`, `cryptography.hazmat.primitives.ciphers[.algorithms|.modes]`, `cryptography.hazmat.backends.openssl`, на Linux — `gi.repository.{GLib,GObject,Gtk,Gdk,DBus,AppIndicator3,AyatanaAppIndicator3}`;
- excludes: `PIL._avif`, `PIL._webp`, `PIL._imagingtk`, `FpxImagePlugin`, `MicImagePlugin`.

Сборки (`.github/workflows/build.yml`): x64 (`python 3.11`, `pyinstaller 6.10.0`, MSVC 14.40, **bootloader собирается из исходников**), ARM64 (`windows-11-arm`, `pyinstaller 6.13.0`, `cryptography` собирается локально из-за отсутствия wheel), Win7 x64/x86 (`python 3.8` + `pyinstaller 5.13.2`), macOS universal2, Linux + `.deb`/`.rpm` через `fpm`.

**Подводные камни, которые они явно обходят:**

1. **Антивирус / ложные срабатывания.** README прямо предупреждает (`docs/README.md:24-34`). В билде — шаг **«Strip Rich PE header»**: находят `b'Rich'`, читают чек-сумму по `rich+4`, вычисляют `0x536E6144 ^ ck` (`DanS`), затирают диапазон `DanS..Rich+8` нулями (`build.yml:88-106`). Это убирает MSVC-подпись и снижает эвристику.
2. `upx=False` на Windows (UPX сам по себе триггерит AV), но `upx=True` на Linux (`linux.spec:115`).
3. Linux: жёсткая проверка полноты GI-бандла — список `libglib/libgobject/libgio/libgtk-3/libappindicator3/libayatana-appindicator3` и typelibs, иначе `RuntimeError` на этапе сборки (`linux.spec:67-86`).
4. Linux smoke-test проверяет, что библиотеки загружены из `sys._MEIPASS`, а не из системы.
5. macOS: свой рендер `.icns` (`macos.py:46-51`), проверка universal2 (наличие обеих архитектур) и валидация DMG.
6. Самообновление: `relaunch_env()` вычищает `_PYI_*`/`_MEIPASS*` и `*_ORIG`, ставит `PYINSTALLER_RESET_ENVIRONMENT=1`, иначе новый процесс подхватит старый распакованный бандл (`utils/update_check.py:142-161`).
7. Обязательный SHA-256 ассета перед установкой (`update_check.py:100-126`).
8. Подпись — SignPath, отдельным шагом после выгрузки unsigned-артефакта.

---

## 8. Тесты: что обязан гарантировать корректный порт

| Файл | Контракт |
|---|---|
| `test_bridge.py::MsgSplitterTest` | abridged: `[words]`/`0x7F+int24`; intermediate/padded: `int32_le & 0x7FFFFFFF`; склейка `''.join(parts) == stream` при произвольном дроблении; неполный пакет буферизуется; `len==0` → splitter отключается; `flush()` отдаёт хвост ровно один раз; quick-ack бит `0x80` (в `packet[0]` для abridged, в `packet[3]` для intermediate) не должен ломать нарезку даже если заголовок разорван на 1/2/3 байта |
| `test_bridge.py::WsReencryptTest` | после ре-шифрования расшифровка relay-ключом даёт **байт-в-байт** исходные пакеты, включая обрезанный хвост; `ws.close()` вызван ровно один раз |
| `test_fake_tls.py` | HMAC покрывает client_random + весь полёт с обнулённым server_random; session_id эхом; cipher выбирается из предложенных; MLKEM зеркалится только при длине 1216, иначе X25519; clamp `[64,16384]`; fallback 64..160; полёт = 1290 B при MLKEM+64; `wrap_tls_record` режет по 16384; `first_appdata_record_len` возвращает `None` (мало данных) / `0` (alert, TLS1.2) / длину |
| `test_fake_tls.py::VerifyClientHelloTest` | отклоняет: чужой секрет, ts ±3600, любой испорченный байт тела, `n<43`, тип ≠ `0x16`, hs ≠ `0x01` |
| `test_raw_websocket.py` | маскирование побайтово верно на всех границах длин (0,1,125,126,127,511..515,65535..65537,1 MiB+3); заголовки `0x82|0x80|len` / `126`+2B / `127`+8B; ре-ассемблирование фрагментов; control-фрейм между фрагментами пропускается; CLOSE → `None` и `_closed`; кадр > `MAX_MESSAGE_LEN` отклоняется **до** чтения payload; `close()` идемпотентен (один CLOSE-фрейм), транспорт закрывается даже при отмене во время `drain`; отмена/ошибка апгрейда закрывает сокет |
| `test_pool.py` | media никогда не берёт обычный домен первым; media пробует `-1`-домен в обоих режимах (fronted/direct), затем обычный; конкурентные промахи стартуют **ровно один** refill; пустой пул восстанавливается сам по backoff без новых запросов; hit не сбрасывает backoff неудачного refill; stale (`expired|eof|exception|closed|closing`) отбрасывается; test DC — отдельный ключ и путь `/apiws_test`; `close()` отменяет задачи и закрывает невостребованные соединения |
| `test_tcp_fallback.py` | backoff 30→60→…→3600 (×2, cap 3600) и **пропуск** попыток в окне; разные IP/порты не блокируются; успех чистит состояние; 8 параллельных вызовов = 1 TCP-попытка; ошибка `drain` закрывает сокет и включает backoff; отмена не помечает IP заблокированным |
| `test_config.py` | `DC:IP` — последний выигрывает; `kind='format'` vs `'invalid'`; `coerce_domain_list` режет по `, ; пробел`, дедуп без учёта регистра; `_is_valid_domain` отклоняет `.lead`, `trail.`, `-bad`, `bad-`, `a..com`, `a.1`, метку >63, домен >253; декодированные дефолтные CF-домены валидны и уникальны |
| `test_h2_routing.py` | **матрица приоритетов**: прямой пул-хит → никогда не открывает H2; промах → сразу fallback; порядок `worker → h2|ws → tcp`; тестовый DC и `cfproxy=false` → только `tcp`; медиа+H2 → `h2` |
| `test_h2_settings.py` | CLI/JSON/GUI дают одинаковый `h2_enabled`; `--no-h2`, `--no-cfproxy`, `--no-secure`, `--force-test-dc` его гасят; остановка и отмена закрывают H2-пул, listener и все задачи без утечек |
| `test_cf_h2.py` | `_read_packet`: abridged extended (`0xFF`+int24), quick-ack `0x80000000`, padded — снятие 0..15 и «расширенного» 0..256 паддинга (Apple/Android), отклонение `declared<24`, невыровненного, паддинга > лимита; `_encode_reply` для `-404` = `\x01`+4B (abridged) / `\x04\x00\x00\x00`+4B; изоляция каналов, backpressure вместо дропа, отсутствие реплея для handshake |
| `test_event_loop.py` | (Windows-only) self-pipe пересоздаётся при EOF/reset, существующие TCP не рвутся, ошибка аллокации ретраится без спина |
| `test_update_check.py` / `test_windows_update.py` / `test_linux_update.py` | выбор ассета по платформе, обязательный SHA-256, роллбэк подмены exe, повышение прав только когда нужно |
| `test_tray_common.py` | `check_updates=false` не стартует поток; при `has_update` колбэк получает `(version, url)` |
| `test_i18n.py` | полнота ru/en словарей |

Минимальный набор для C#-порта, без которого нельзя считать реализацию верной: `MsgSplitter` (все 3 протокола + quick-ack + дробление), ре-шифрование байт-в-байт, Fake-TLS HMAC в обе стороны + 1290, WS-кодек на всех границах длин, матрица маршрутов, backoff.

---

## 9. Производительность и надёжность

### 9.1 Буферы и размеры

| Параметр | Значение | Где |
|---|---|---|
| `buffer_size` (SO_RCVBUF/SO_SNDBUF) | `256 KiB`, минимум `4 KiB` | `config.py:68`, `tg_ws_proxy.py:681` |
| Чтение из клиентского TCP | `65536` | `bridge.py:340` |
| Релей маскировки | `16384` | `fake_tls.py:438` |
| WS `MAX_MESSAGE_LEN` | `16 MiB` | `raw_websocket.py:83` |
| TLS-запись | `16384` | `fake_tls.py:40` |
| H2 `IO_BATCH_SIZE` | `64 KiB` | `h2_transport.py:23` |
| H2 окно стрима / соединения | `256 KiB` / `4 MiB` | `h2_transport.py:21-22` |
| H2 лимиты канала | 8 запросов, 8 MiB | `cf_h2.py:25-26` |
| H2 лимиты lane | 64 запроса, 32 MiB, 32 MiB ответов | `cf_h2.py:27-29` |
| H2 `MAX_PACKET` | `4 MiB`, паддинг ≤ 256 | `cf_h2.py:23-24` |
| Дренаж «плохого» handshake | ≤ 64 KiB / 10 с | `tg_ws_proxy.py:45-46` |

### 9.2 Таймауты

10 с на каждый шаг входного handshake (PROXY-строка, первый байт, TLS-заголовок, TLS-тело, obfs2-init). 7/8 с на WS-коннект (fronted/direct). 10 с на TCP-fallback и на маскировочный релей. H2: `REQUEST_TIMEOUT=65`, `SETUP_TIMEOUT=8`, `DOMAIN_COOLDOWN=30`. `SERVER_HELLO_DELAY=0.008..0.024` — искусственная задержка, чтобы тайминг походил на настоящий сервер.

### 9.3 Пул и переиспользование

- WS: `pool_size=4` на ключ `(dc, is_media, is_test_dc)`. `WS_POOL_MAX_AGE=120 с`, проверка каждые 5 с. Прогрев на старте (`pool.py:200-208`).
- Refill: backoff `1 * 2^(n-1)`, cap 3600, `n ≤ 12`. Ротация — отдельная долгоживущая задача, будит себя по `min(now+5s, ближайший expiry, retry_after)`.
- Промах пула **не ждёт** — сразу `do_fallback`, а refill идёт в фоне. Это осознанное решение: латентность важнее «прогретости».
- CF Worker: `PER_DC_LIMIT=1`, `WS_POOL_MAX_AGE=100 с`.
- TCP-fallback: backoff `30 * 2^(n-1)`, cap 3600, `n ≤ 7`; флаг `_tcp_connecting` защищает от параллельных попыток.
- H2: один TCP/TLS на (DC, домен), до 64 стримов; preflight один раз на lane; ошибка lane → cooldown 30 с и переход на другой домен.

### 9.4 Keepalive

**Явного ping-цикла нет.** PING только отражается PONG'ом (`raw_websocket.py:200-207`). «Живучесть» обеспечивается: (а) переиспользованием только что созданных соединений с max age 120 с, (б) H2 read-timeout с переподключением, если таймаут случился на последнем активном стриме (`h2_transport.py:48-59`), (в) `TCP_NODELAY=1` на listener и на всех сокетах (`tg_ws_proxy.py:425-429`, `raw_websocket.py:62-65`).

### 9.5 Мультиплексирование

- WS-маршрут: **1 WS = 1 клиент**, без мультиплексирования. Пул — это предпрогретые, но ещё не занятые соединения.
- Мультиплексирование есть только в CF-H2: много `_HttpChannel` над одной `_HttpLane` (одно TCP/TLS). Изоляция каналов, приоритет «просроченных» пакетов, реплей шифртекста — `cf_h2.py:535-574`.

### 9.6 Обработка ошибок и реконнект

- `_handle_client` ловит поимённо `TimeoutError`, `IncompleteReadError`, `CancelledError`, `ConnectionResetError`, `OSError` (спец-кейс `winerror==1236` — «connection aborted by local system»), затем общий `Exception` с `exc_info` (`tg_ws_proxy.py:352-373`).
- «Плохой» handshake: инкремент `connections_bad`, дренаж до 64 KiB (чтобы клиент не получил RST раньше времени), закрытие.
- Listener-watchdog: каждые 5 с проверяет `fileno() < 0`; при смерти — закрыть, подождать 1 с, пересоздать, залогировать (`tg_ws_proxy.py:514-558`).
- `event_loop.py` — обход бага CPython #156333: на Windows Proactor пересоздаёт self-pipe при EOF/reset, чтобы `call_soon_threadsafe` не завис (иначе `stop_proxy()` из трея не сработает).
- Реконнект клиента — забота Telegram-клиента; прокси просто закрывает сессию. Восстановление пула — фоновая задача.

### 9.7 Наблюдаемость

`stats.summary()` раз в 60 с: `total/active/ws/h2/h2_tcp/h2_req/h2_err/tcp_fb/cf/front/bad/masked/err/pool/cf_pool/up/down` (`stats.py:25-48`). В DEBUG — `WS FLOW`/`H2 FLOW` раз в 5 с, `WS END` с `first_reply_ms`, `max_rx_gap_ms`, `max_ws_write_ms`, `max_native_write_ms`. `DomainCensorFilter` маскирует домены в логах (кроме `*.telegram.org` и `*.log`), чтобы не палить CF-домены в issue-трекерах (`utils.py:93-116`).

---

## 10. Что реально трудно портировать на C# / .NET 4.8

### 10.1 Хорошая новость: три вещи проще, чем кажется

1. **Настоящий TLS-сервер не нужен.** Fake-TLS — это подделка записей руками; ни `SslStream`, ни сертификаты не участвуют. Весь «TLS» — это 3 записи + HMAC.
2. **X25519 не нужен.** Клиент никогда не выводит ключи из ServerHello. Python в fallback просто берёт `urandom(32)` с обнулённым старшим битом (`fake_tls.py:170-176`). Достаточно `RandomNumberGenerator`.
3. **TLS 1.3-профилирование можно выбросить.** `probe_appdata_len` требует TLS 1.3, которого в .NET 4.8 `SslStream` нет (TLS 1.3 появился в .NET Core 3.0 / .NET 5+). Просто используйте `FALLBACK_APPDATA_RANGE = 64..160` — на функциональность не влияет, только на «реалистичность» размера записи. Если хочется — ClientHello собирается руками (~200 B), а полёт парсится без завершения handshake, ровно как в Python (`MemoryBIO` там используется только для генерации hello).

### 10.2 Что действительно трудно

| Проблема | Почему | Решение |
|---|---|---|
| **AES-CTR** | В BCL нет режима CTR. `AesManaged`/`AesCryptoServiceProvider` дают только CBC/ECB/CFB. | Свой CTR поверх AES-ECB: генерировать блоки счётчика, `TransformBlock`, XOR. **Инкремент — 128-битный big-endian по всему блоку**, не по младшим 32 битам (иначе разойдётся с `cryptography`). Состояние (counter + смещение в keystream) хранить на объект-направление. |
| **Скорость AES** | `AesCryptoServiceProvider.TransformBlock` на 16 байтах — доминирующая стоимость при 100+ Мбит/с. | Шифровать пачками: 256–4096 блоков счётчика за вызов (4–64 KiB), затем XOR. Либо P/Invoke `bcrypt.dll` (`BCryptOpenAlgorithmProvider` + `BCRYPT_CHAIN_MODE_ECB`, `BCryptEncrypt` пачкой) — упирается в AES-NI. Managed-`RijndaelManaged` на net48 медленнее. |
| **HTTP/2 клиент** | Ни `HttpClient` с h2 (в net48 только HTTP/1.1), ни HPACK. Реализовать HPACK + flow control руками — недели. | **Не портировать H2 в v1.** CF-WS-маршрут (`/apiws` через CF-домен) обслуживает медиа и без H2 — просто открывает больше соединений. Потеря: меньше TCP-соединений и меньше латентность на медиа. Архитектурно оставить хук. |
| **WS-сервер** | `System.Net.WebSockets` в BCL — **только клиент** (`ClientWebSocket`). Серверной части нет. | См. 10.3. |
| **Первый байт решает всё** | На одном порту приходят и «сырой» 64-байтовый obfs2-init, и TLS ClientHello (`0x16`), и мусор (→ 301). | Нужен «peek» первого байта до любого парсинга. Это убивает `HttpListener` (см. ниже). |
| **`SslStream` + SNI ≠ Host** | Фронтинг: TCP на IP Telegram, SNI `sprinthost.ru`, `Host: kws2.web.telegram.org`. | `SslStream.AuthenticateAsClient("sprinthost.ru")` задаёт SNI; `RemoteCertificateValidationCallback => true` отключает проверку; `Host` пишете в HTTP-заголовке сами. Работает. Для не-фронтинга — `AuthenticateAsClient("kws2.web.telegram.org")` с штатной проверкой. `SslProtocols.Tls12` указывать явно. |
| **`async`/`await`** | C# 7.3 на net48 — полностью достаточно; ограничение не в языке, а в BCL. | `async Task` везде, где IO. Нельзя: `IAsyncEnumerable`, `ValueTask`, `System.IO.Pipelines`, `ArrayPool<T>`, `Span<T>` (в net48 частично есть через `System.Memory` — но это NuGet). Отмена — `CancellationToken` вместо `asyncio.CancelledError`. |
| **`finally`-семантика** | Python-код агрессивно глушит `BaseException` при закрытии (несколько мест). | В C# аналог — `try { } catch { }` вокруг `Close`/`Dispose`, и **никогда** не бросать из `finally`. Иначе один битый сокет уронит весь цикл. |
| **Один event loop vs N потоков** | Python — кооперативная конкурентность, все состояния гонок «бесплатны». | В C# либо `async` на пуле (эквивалент), либо поток-на-соединение. При 20–50 соединениях поток-на-соединение проще и безопаснее (нет общих `Dictionary` без локов). Пул/балancer требуют `lock` или `ConcurrentDictionary`. |
| **`asyncio.Event`/`Condition` в пуле** | Много ожиданий «пока не появится слот». | `SemaphoreSlim` + `CancellationToken`; `TaskCompletionSource` для пробуждений. |
| **Отсутствие `ArrayPool`** | Аллокации на каждый чанк = GC-давление. | Свой ring/`BufferPool` на соединение: один буфер чтения (64 KiB) и один буфер записи на сессию. |

### 10.3 Конкретные варианты WS-сервера на .NET 4.8 без NuGet

**(A) `TcpListener` + ручной HTTP-апгрейд + ручной кодек кадров — единственный правильный выбор.**

```
TcpListener(IPAddress.Loopback, 1443) → AcceptTcpClientAsync()
  ├─ прочитать 1 байт (peek, не consume)
  ├─ 0x16 → ветка Fake-TLS: 4 B заголовка → тело → verify/HMAC → ServerHello
  ├─ 0xEF/0xEE/0xDD/... → 63 байта → obfs2
  ├─ 'G'/'P'/'O'/'H' → HTTP (для 301/healthcheck)
  └─ иначе → 301 на masking-домен
```

Плюсы: полный контроль над первым байтом; никакого admin; работает на Win7+; один порт на все ветки; тривиально накладывается `PROXY protocol v1` (прочитать строку до `\r\n`). Минусы: ~400 строк на кодек, всё руками. Кодек у вас всё равно будет — он нужен и для исходящего WS-клиента (`raw_websocket.py` уже эталон).

Ключевые детали, которые нельзя потерять: маскирование входящих кадров **проверять не обязательно** (Telegram-клиент сюда не ходит, а свои клиенты — MTProto-клиенты, они вообще не говорят WS); `MAX_MESSAGE_LEN` проверять **до** чтения payload; `PING`→`PONG`; `CLOSE` — эхо 2 байта кода и закрыть транспорт; `TCP_NODELAY` на accept-сокете и на всех исходящих.

**(B) `HttpListener` + `AcceptWebSocketAsync` — не подходит, и вот почему.**

`HttpListenerContext.AcceptWebSocketAsync` в net48 действительно есть (http.sys, Windows 8+), и это был бы самый короткий путь. Но:

1. `HttpListener` отдаёт вам **уже разобранный HTTP-запрос**. Прочитать первые 64 «сырых» байта obfs2 невозможно — http.sys их отвергнет как невалидный запрос. А именно это и есть основной режим (plain-режим без Fake-TLS).
2. http.sys требует URL ACL (`netsh http add urlacl`) для любого префикса, кроме случая запуска от администратора. Ваше требование «без админа» это убивает.
3. Нет контроля над таймингами/буферами сокета (`SO_RCVBUF` не выставить).
4. Нельзя отдать «301 и закрыть» на произвольный мусорный первый байт — http.sys сам ответит 400.

Вывод: `HttpListener` годится только если вы делаете **отдельный** HTTP-порт для чего-то вспомогательного. Для входного порта MTProto-прокси — нет.

**(C) `Socket` + `SocketAsyncEventArgs` (IOCP).** То же, что (A), но без `async`-стейт-машин: один `SAEA` на чтение и один на запись, пул переиспользуемых. Даёт минимальные аллокации и лучший throughput на сотнях соединений. Сложнее в отладке (нет линейного стека). Рекомендую как второй шаг оптимизации, не первый.

**(D) `ClientWebSocket` для исходящих — возможно, но нежелательно.** Он есть в BCL, умеет `RemoteCertificateValidationCallback` (4.7.2+). Но: SNI берётся из URI (фронтинг не сделать чисто), `Host` — restricted header (`TryAddWithoutValidation` может не сработать), маскирование и разбиение на фреймы вам не подконтрольны, доступа к отдельным фреймам нет — только сообщения. Поскольку ручной клиент у вас всё равно появится ради серверного кодека, писать его целиком — дешевле.

**(E) Чужой WS-сервер через P/Invoke.** `websocket.dll`/`WebSocketCompleteAction` — WinHTTP-серверный API, доступен из C++ ; из C# — только P/Invoke, и всё равно требует http.sys. Не стоит.

### 10.4 Чек-лист «не сломать при порте»

1. `SHA256(prekey‖secret)` **только** для клиентского направления; к Telegram — сырой ключ из init.
2. Реверс 48-байтового блока `(prekey‖iv)` для обратного направления — обе стороны.
3. CTR: 128-битный BE инкремент; «пропустить 64 байта» = прогнать 64 нулевых байта через тот же экземпляр шифра, не создавать новый.
4. `dc_idx` — **signed** int16 LE; отрицательный = медиа; при отправке в Telegram знак сохраняется.
5. Fake-TLS HMAC: `client_random ‖ flight(server_random=0)`, патч по смещению **11**, длина 32.
6. Проверка ClientHello: сравнивать **первые 28** байт random с HMAC, а не все 32 (последние 4 — XOR с timestamp).
7. Тайм-штамп — `uint32` LE, допуск ±120 с. Часы пользователя могут врать — это осознанный риск.
8. Порядок записей в ServerHello-полёте жёсткий: `handshake → CCS → appdata`. Telegram проверяет HMAC по всем трём.
9. Нарезка WS-фреймов: один MTProto-пакет = один фрейм. Склеивать пакеты в один фрейм нельзя — Telegram-эндпоинт разбирает по границам фреймов.
10. `Sec-WebSocket-Accept` не проверять (эталон так делает), но `101` проверять обязательно.
11. Всегда `TCP_NODELAY`; `SO_RCVBUF/SO_SNDBUF` — по возможности (в C# `Socket.ReceiveBufferSize`/`SendBufferSize` до `Connect`).
12. Ссылка всегда `dd` + 32 hex; `ee` — только если явно задан `fake_tls_domain`, и тогда `ee + secret + hex(domain)`.

### 10.5 Порядок работ (предложение)

1. `AesCtr` + `MsgSplitter` + ре-шифрование + юнит-тесты, портированные из `test_bridge.py`. Это ядро, и оно тестируется без сети.
2. WS-кодек (клиент) + `RawWebSocket`-эквивалент + тесты из `test_raw_websocket.py`.
3. `TcpListener` + разбор первого байта + plain-режим + прямой WS-маршрут. Уже работает.
4. Пул + backoff + ротация (`test_pool.py`).
5. Fake-TLS (`test_fake_tls.py`) — включая маскировочный релей и 301.
6. CF-proxy (домены + `balancer`) и CF Worker.
7. UI: трей, первый запуск, форма, `tg://`-ссылка, autostart через `HKCU\...\Run`.
8. H2 — отдельным этапом, если вообще.

---

*Все ссылки на файлы даны относительно `refs/tg-ws-proxy/`.*

---

## 15-line summary

1. Точки входа: `proxy/tg_ws_proxy.py:main` (CLI) и `windows|linux|macos.py:main` (трей); прокси всегда живёт в отдельном `threading.Thread` со своим `asyncio`-loop (`utils/tray_common.py:310-341`).
2. Секрет — 16 байт (32 hex); `dd`/`ee` добавляются **только** в `tg://proxy`-ссылку (`tg_ws_proxy.py:433-441`); GUI всегда отдаёт `dd`, Fake-TLS доступен только через `--fake-tls-domain`.
3. Префикса `7` в коде нет; медиа определяется **отрицательным `dc_idx`** в obfuscated2-handshake, и знак сохраняется при отправке в Telegram.
4. Handshake: 64 B = skip(8) + prekey(32) + iv(16) + tag(4)@56 + dc_idx int16LE@60; ключ клиента `SHA256(prekey‖secret)`, к Telegram — сырой `init[8:40]`, обратные направления — реверс 48-байтового блока.
5. Fake-TLS: HMAC-SHA256(secret, ClientHello с обнулённым random), первые 28 байт + XOR-timestamp ±120 с; ответ = ServerHello + CCS + AppData, HMAC по всему полёту патчится в `[11:43]`; 1290 B при MLKEM.
6. WS: ручной RFC 6455, `GET /apiws`, `Sec-WebSocket-Protocol: binary`, `Sec-WebSocket-Accept` **не** проверяется, исходящие фреймы всегда маскированы, лимит 16 MiB.
7. Три маршрута: прямой WS на IP Telegram с SNI/Host `kws{dc}.web.telegram.org`; фронтинг (тот же IP, SNI `sprinthost.ru`, без проверки сертификата); CF-домен `kws{dc}.{domain}` через Cloudflare Flexible.
8. DC-адреса жёстко зашиты (`utils.py:45-58`), ничего не резолвится; обновляется только список CF-доменов с GitHub раз в час (нужно ≥3 валидных).
9. IPv4-only: `inet_pton(AF_INET)` в валидации + разовое предупреждение пользователю про IPv6 в UI.
10. Пул: 4 предпрогретых WS на (DC, media), max age 120 с, refill с backoff 1→3600 с; промах не ждёт, а сразу уходит в fallback (`worker → h2|ws → tcp`).
11. Админ не нужен, фаервол не трогается, netsh-правил нет: слушается `127.0.0.1:1443`; autostart — только `HKCU\...\Run`; single-instance — мьютекс `Local\TgWsProxy_SingleInstance` + pid.lock.
12. Упаковка: PyInstaller onefile с ctk-assets, i18n, certifi, hiddenimports cryptography/h2/pystray; ключевой трюк — затирание `Rich`/`DanS` заголовка PE против антивирусов (`build.yml:88-106`).
13. Тесты задают контракт: байт-в-байт ре-шифрование, нарезка по 3 протоколам с quick-ack, все границы длин WS-фреймов, матрица маршрутов, backoff, идемпотентное закрытие.
14. C#-риски: AES-CTR руками (128-битный BE инкремент, пачки блоков ради скорости), отсутствие HTTP/2 в BCL (H2 лучше не портировать в v1), `SslStream` без TLS 1.3 (профилирование Fake-TLS выбросить), X25519 не нужен вовсе.
15. WS-сервер: только `TcpListener` + ручной апгрейд/кодек, потому что на одном порту надо различать сырой obfs2-байт, `0x16` и мусор — `HttpListener`+`AcceptWebSocketAsync` это не умеет и вдобавок требует URL ACL/админа.

**Файл не создан** — мой режим работы read-only (запрет на создание файлов, включая отчёты). Содержимое выше можно сохранить как есть в `docs/research/tg-ws-proxy.md`.
</subagent>
