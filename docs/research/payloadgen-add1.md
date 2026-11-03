# `_Add1` — генератор QUIC/TLS payload'ов (`payloadGen`, by Ori)

> Источник: `refs/_Add1` — 3 файла: `payloadGen.go` (6 794 байта), `go.mod` (1 024), `go.sum` (6 395).
> Версия в коде: `const version = "0.8"`.
> Назначение: **снять с реального браузера настоящий TLS ClientHello и QUIC Initial и сохранить их как `.bin`** — ровно те файлы, которые zapret подставляет через `--dpi-desync-fake-tls`, `--dpi-desync-fake-quic`, `--dpi-desync-split-seqovl-pattern`.

---

## 0. Зачем это нужно проекту

Весь смысл zapret-стратегий — подсунуть DPI **правдоподобный** пакет: настоящий ClientHello от Chrome/Firefox, настоящий QUIC Initial. Если фейк «пахнет» (не тот набор шифров, не тот порядок расширений, не тот размер), DPI его отбрасывает и обход не работает.

`zapret-discord-youtube` шипит готовый набор `.bin` (`tls_clienthello_www_google_com.bin`, `quic_initial_www_google_com.bin`, `stun.bin`, `tls_clienthello_4pda_to.bin`, `tls_clienthello_www_sferum_ru.bin`, `ACTIVE_DISCORD_UDP.bin` и т.д. — см. `docs/research/zapret-configs.md`). Но:

1. они **устаревают** (Chrome 131 → 140, Firefox 120 → 135);
2. под конкретного провайдера нужен **свой** SNI (тот, который DPI точно пропускает);
3. под TLS-фейки нужны **разные** hello под разные цели (Discord media, YouTube, игры);
4. в `refs/TLSpack` лежит 1630 готовых `.bin`, но непонятно, каким браузером и когда они сняты.

`_Add1` — это **инструмент, который делает такие файлы сам**, локально, за 10 секунд. Именно поэтому он «ОЧЕНЬ понадобится»: он превращает набор фейков из статического ассета в **генерируемую фичу GUI**.

---

## 1. Что делает программа (пошагово)

Интерактивный CLI, 4 вопроса:

| Шаг | Функция | Вопрос | По умолчанию |
|---|---|---|---|
| 1 | `selectMode()` | `1. TLS Client Hello / 2. QUIC Initial / 3. Both` | Both |
| 2 | `mimicBrowser()` | какой браузер мимикрировать | Firefox (`helloList[0]`) |
| 3 | `selectCrop()` | «At which byte to crop binary» | не резать (`-1`) |
| 4 | `inputSNI()` | SNI для payload | `fonts.google.com` |

Затем — два независимых прогона:

```
TLS:  listenTCP(4343)  → ждёт 1 с → sendRequestU(utls hello)     → ловит ClientHello → пишет .bin
QUIC: listenUDP(4343)  → ждёт 1 с → sendRequestUQUIC(uquic id)   → ловит Initial     → пишет .bin
```

### 1.1 Трюк с loopback

Программа **не** ходит в интернет. Она поднимает listener на `localhost:4343` и заставляет настоящий TLS/HTTP3-стек браузера подключиться к нему. Клиент честно генерирует свой ClientHello (со всеми расширениями, GREASE, key_share, ALPN), а listener просто читает первые байты и сохраняет.

Отсюда `InsecureSkipVerify: true` — сертификат не нужен, handshake всё равно не завершится.

### 1.2 Почему два разных HTTP-транспорта

Комментарий в коде (`payloadGen.go:148-149`) прямо об этом:

> «For some addresses like https://example.com/ http.Transport worked perfectly, while http2.Transport failing. But for some others, like https://www.google.com/ it's vice versa. Dunno why, so gonna leave the code here.»

В коде оставлен закомментированный `http.Transport`-вариант и активный `http2.Transport` с `DialTLSContext`. Практический вывод: **для стабильного снятия hello нужен HTTP/2-путь**, потому что `utls` подменяет только TLS, а `http2.Transport` заставляет клиента предложить `ALPN=h2`, что и делает hello похожим на браузерный.

### 1.3 Список браузеров (жёстко зашит)

```go
helloList = []hello{
    {"Firefox", &utls.HelloFirefox_120, &uquic.QUICFirefox_116C},
    {"Chrome",  &utls.HelloChrome_131,  &uquic.QUICChrome_115},
}
```

Только два профиля и только в этой версии. `utls` поддерживает десятки (`HelloChrome_120`, `HelloSafari_16_0`, `HelloEdge_106`, `HelloIOS_14`, `HelloAndroid_11_OkHttp`, `HelloRandomizedALPN`…), но здесь не подключены. **Это первое, что надо расширить.**

### 1.4 Обрезка (crop)

`selectCrop()` → число 1..65533. Обрезка делается **после** чтения, простым срезом буфера:

```go
if cropAt <= 0 { buf = buf[:n] } else { buf = buf[:cropAt] }
```

Зачем: DPI смотрит на первые N байт; иногда выгодно послать фейк **короче** настоящего hello (меньше трафика, та же реакция). Плюс некоторые стратегии (`--dpi-desync-split-seqovl=N`) требуют паттерн ровно N байт.

### 1.5 Имя файла

```go
t := time.Now().Format("2006-01-02_15-04-05")
filename := fmt.Sprintf("%s_%s_%s.bin", marker, strings.NewReplacer(".", "_").Replace(sni), t)
// → TLS_ClientHello_fonts_google_com_2026-11-04_12-30-00.bin
// → QUIC_Initial_fonts_google_com_2026-11-04_12-30-00.bin
```

Формат имени **не совпадает** с Flowseal (`tls_clienthello_www_google_com.bin`, нижний регистр, без даты). Для совместимости с `--dpi-desync-fake-tls` имя не важно (путь передаётся явно), но для «человеческой» библиотеки фейков лучше привести к стилю Flowseal.

### 1.6 Права на файл

```go
os.WriteFile(filename, b, 0200)   // только запись, без чтения (!)
```

`0200` — это `--w-------`. На Windows Go транслирует в read-only атрибут; файл потом **нельзя перезаписать** без снятия атрибута. Это баг (скорее всего опечатка вместо `0644`). В порте делать `0644`.

---

## 2. Зависимости и их смысл

`go.mod`: `go 1.24.1`

| Пакет | Версия | Зачем |
|---|---|---|
| `github.com/refraction-networking/utls` | v1.7.3 | TLS-клиент с **подменяемым** ClientHello (форк crypto/tls) |
| `github.com/refraction-networking/uquic` | v0.0.6 | то же для QUIC (форк quic-go) |
| `github.com/refraction-networking/uquic/http3` | — | HTTP/3 поверх uquic |
| `golang.org/x/net` | v0.38.0 | `http2.Transport` |
| `github.com/gaukas/godicttls` | v0.0.4 | словари TLS-фингерпринтов (для utls) |
| `github.com/gaukas/clienthellod` | v0.4.2 | сервер, который «вытягивает» ClientHello |
| `github.com/cloudflare/circl` | v1.5.0 | пост-квантовые группы (X25519MLKEM768) |
| `github.com/quic-go/qpack` | v0.5.1 | QPACK для HTTP/3 |
| `github.com/google/gopacket` | v1.1.19 | разбор пакетов (транзитивно) |

**Ключевое:** всё это Go-специфично. На C# **нет** прямого эквивалента `utls`/`uquic` — в .NET нельзя «подменить» ClientHello внутри `SslStream`. Значит порт «в лоб» невозможен, и это надо решать иначе (см. §5).

---

## 3. Что именно получается на выходе

### 3.1 TLS ClientHello

Сырая TLS-запись:

```
16 03 01 LL LL   ← handshake type 0x16, версия 0x0301 (legacy), длина
01 LL LL         ← ClientHello
03 03            ← client version
RR×32            ← client random
SS               ← session_id len (0x20 у Chrome/Firefox)
...
```

Размер: ~500-520 байт для Chrome/Firefox с MLKEM, ~300-350 без него. В `refs/TLSpack/steam/*.bin` — ровно **517 байт**, что совпадает с Chrome-подобным hello.

### 3.2 QUIC Initial

UDP-датаграмма:

```
C0 | 00000001 | DCID_len(8) | DCID(8) | SCID_len(0) | Token_len(0) | Length(varint) | PN(1..4) | payload...
```

Размер всегда **1200 байт** — это требование RFC 9000 (клиентский Initial обязан быть ≥1200 байт для прохождения через DPI/PMTUD). Все файлы в `refs/TLSpack/**/quic*/*.bin` — ровно 1200 байт, что подтверждает.

Внутри payload — CRYPTO-фрейм с ClientHello, зашифрованный Initial-ключами (публичный DCID). То есть DPI видит **только** заголовок и длину; содержимое для него — шум. Именно поэтому QUIC-фейк работает почти всегда, и его можно просто копировать.

---

## 4. Как это связано с `refs/TLSpack`

`refs/TLSpack` — 1630 файлов, 1.37 МБ, структура:

```
epicgames/     10 файлов   (8 КБ)
microsoft/     12 файлов   (10 КБ)
random/        12 файлов   (10 КБ)
steam/          8 файлов   (7 КБ)
whitelist/
  tls1.2/quic_inital/     269 файлов
  tls1.2/tls_clienthello/ 268 файлов
  tls1.3/quic/            547 файлов
  tls1.3/tls_clienthello/ 546 файлов
```

Все `quic_initial_*.bin` — 1200 байт, все `tls_clienthello_*.bin` — переменного размера. Имена: `<тип>_<домен>_<timestamp>.bin`, timestamp вида `20260430T080152` — то есть **это и есть вывод такой же утилиты**, снятый пачкой по списку доменов.

Практический вывод: `TLSpack` — «готовая библиотека фейков по доменам», а `_Add1` — «станок, который её производит». В GUI нужны **оба**: библиотека как дефолт + станок как кнопка «снять свежие фейки».

Отдельно ценно: `whitelist/` — это домены, которые DPI **точно пропускает** (банки, госуслуги, яндекс, VK, Ozon). Подставлять их SNI в фейк — самая рабочая тактика (см. `hostfakesplit host=ozon.ru` в стратегиях Zapret-Manager).

---

## 5. Порт на C#: что реально возможно

### 5.1 Чего нельзя

`SslStream` в .NET Framework 4.8 **не позволяет** задать произвольный ClientHello: ни порядок расширений, ни GREASE, ни набор групп, ни `key_share` для MLKEM. Максимум — `SslProtocols`, `CipherSuitesPolicy` (только .NET Core 3.0+), `TargetHost` (SNI). Поэтому «снять настоящий Chrome-hello» через `SslStream` невозможно в принципе.

### 5.2 Три рабочиих варианта

**(A) Оставить Go-бинарь и вызывать его из GUI.** Самый дешёвый и самый честный путь.

- `payloadgen.exe` (x64, ~8-12 МБ после `-ldflags="-s -w"`) лежит в `tools/`.
- GUI запускает его с аргументами (`--mode tls --browser chrome --sni www.google.com --out <dir>`), ждёт файл, читает.
- Требует: переписать `main()` на `flag`-парсинг вместо `fmt.Scanln` (30 строк работы), добавить `--json` вывод.
- Минус: +12 МБ в поставке и ещё один бинарь, который может флагаться AV (Go-бинари часто эвристически подозрительны).

**(B) Вшить **эталонные** hello как ресурсы, а не генерировать.** 

- Взять `refs/TLSpack` + `refs/zapret-discord-youtube/bin/*.bin` (≈200-300 файлов, ~400 КБ), нормализовать имена, положить в `assets/fakes/` с манифестом (`fakes.json`: имя, тип, SNI, размер, браузер, дата, источник).
- GUI даёт **выбор** фейка по цели (Discord / YouTube / игра / «свой домен») и подставляет путь в аргументы.
- Минус: устаревает; плюс: ноль зависимостей, ноль AV-риска, работает офлайн.
- **Это то, что нужно сделать в v1.**

**(C) Собрать ClientHello руками на C#.** 

Технически реально: ClientHello — это простой TLV-формат, и `utls`-словари (`godicttls`) содержат готовые байтовые шаблоны для Chrome/Firefox/Safari. Но:

- нужно поддерживать актуальность шаблонов (новый Chrome = новые расширения);
- нужен валидный `key_share` (X25519 — 32 байта, генерируется `ECDiffieHellman`; MLKEM768 — 1184 байта, в .NET 4.8 **нет**, только .NET 9+);
- нужен `GREASE` (случайные зарезервированные значения);
- нужен padding до нужного размера.

Итог: **собрать «похожий» hello — 1-2 дня работы; собрать неотличимый — недели и вечная погоня.** Для v1 не нужно.

### 5.3 Рекомендация

| Версия | Подход |
|---|---|
| v1 | (B) — библиотека `.bin` из TLSpack + Flowseal, манифест, выбор в UI |
| v2 | (A) — `payloadgen.exe` как «станок»: кнопка «снять свежие фейки» с прогрессом |
| v3 | (C) — свой генератор для тех, кто не хочет Go-бинарь; только X25519, без MLKEM |

---

## 6. Что доработать в самом `payloadGen`, если берём (A)

1. **Аргументы вместо интерактива.** `--mode tls|quic|both --browser firefox|chrome --sni <domain> --crop <n> --out <dir> --format json`.
2. **Пакетный режим.** `--sni-file domains.txt` → по файлу на каждый домен. Иначе снимать 100 доменов = 100 запусков.
3. **Расширить `helloList`.** Добавить Safari, Edge, iOS, Android OkHttp — по одному на актуальную версию; хранить в JSON, а не в коде.
4. **Починить права файла** (`0200` → `0644`).
5. **Имя файла в стиле Flowseal**: `tls_clienthello_<sni с _>.bin`, без даты (дата — в манифест).
6. **Валидация результата**: проверить, что поймано ≥ 43 байт и `[0]==0x16` для TLS / `≥1200` для QUIC. Сейчас `check(err)` падает только на ошибке чтения, а не на «поймали мусор».
7. **Таймаут listener'а.** Сейчас `Accept()`/`Read()` висят вечно, если клиент не подключился → программа зависает без объяснений.
8. **Снять ограничение одного соединения.** `listenTCP` принимает **ровно одно** соединение и умирает; при ретрае браузера — зависание.
9. **Порт 4343 фиксирован.** Сделать `--port 0` (эфемерный) — иначе конфликт с чем угодно.
10. **`defer os.Exit(0)`** в конце `main` — мёртвый код (`defer` не выполняется при `os.Exit`), но и вреда нет; убрать для чистоты.

---

## 7. Проверка «фейк валиден»

Мини-валидатор, который стоит встроить в GUI перед подстановкой файла в аргументы:

**TLS ClientHello** (`tls_clienthello_*.bin`):
```
len >= 43
b[0] == 0x16                      // handshake
b[5] == 0x01                      // ClientHello
be16(b[3..5]) + 5 == len          // длина записи сходится
b[9..11] == 0x0303                // legacy version
```

**QUIC Initial** (`quic_initial_*.bin`):
```
len == 1200                       // требование RFC 9000
(b[0] & 0x80) != 0                // long header
(b[0] & 0x40) == 0                // Initial, не 0-RTT
b[1..5] == 00 00 00 01            // version 1
```

**STUN/fake-udp** (`stun.bin`, `ACTIVE_DISCORD_UDP.bin`):
```
len >= 20
be16(b[0]) & 0xC000 == 0          // top 2 bits zero
be16(b[0]) % 4 == 0               // STUN message length кратен 4
b[4..8] == 21 12 A4 42            // magic cookie
```

Файл, не прошедший проверку, не должен попадать в `--dpi-desync-fake-tls`: winws либо ругнётся, либо (хуже) будет молча слать мусор, и пользователь решит, что «стратегия не работает».

---

## 8. Итог в трёх строках

1. `_Add1` — Go-утилита, которая снимает настоящий TLS ClientHello и QUIC Initial с реального браузера через loopback-listener на `localhost:4343` и сохраняет их в `.bin` — ровно те файлы, что нужны zapret.
2. `refs/TLSpack` — уже готовый вывод такой же утилиты: 1630 фейков по доменам, включая `whitelist/` (домены, которые DPI точно пропускает).
3. Для AutoZapret: **v1 — шипить библиотеку `.bin` + манифест**; **v2 — прикрутить `payloadgen.exe` как «станок» с батч-режимом**; порт на чистом C# — только если очень нужно, и только X25519.
