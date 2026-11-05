# AutoZapret для Android

Отдельное приложение на **.NET 9 Android** (`net9.0-android`). Не часть
десктопной сборки и не влияет на `tools\ci-local.ps1`.

## Почему .NET, а не Kotlin

Telegram-прокси (`MtProtoHandshake`, `AesCtr`, `WsClient`, `WsTunnel`,
`MtProtoSplitter`) — чистый BCL: сокеты, `SslStream`, AES. Он переносится на
Android без переписывания. Kotlin-версия означала бы второй источник правды для
уже отлаженного протокола.

UI — те же файлы из `../web/` (`ui.html`, `app.js`, `theme.css`). Мост
`chrome.webview.postMessage` подменяется тонким шимом на `@JavascriptInterface`,
поэтому HTML не форкается.

## Тулчейн

Нужно один раз:

```
dotnet workload install android
```

(JDK 17+ и Android SDK ставит сам workload. `adb` нужен для установки на телефон.)

## Сборка

Один раз:

```
dotnet workload install android
dotnet build -t:InstallAndroidDependencies -f net9.0-android ^
    -p:AndroidSdkDirectory="C:\Android\sdk" -p:AcceptAndroidSDKLicenses=True
```

Дальше — `android\build.cmd` (сам подхватит `ANDROID_HOME` или `C:\Android\sdk`).

APK:

```
android\AutoZapret.Android\bin\Debug\net9.0-android\ru.autozapret.android-Signed.apk
```

Установка на подключённый телефон:

```
adb install -r android\AutoZapret.Android\bin\Debug\net9.0-android\ru.autozapret.android-Signed.apk
```

## Что уже работает

* Экран приложения (WebView, наша палитра): включение/выключение, порт,
  позиция разреза, пауза между частями, счётчики, лог.
* Локальный **SOCKS5 с разрезом TLS ClientHello**. Проверено на десктопе тем же
  кодом движка: получатель видит `Read → 5 байт`, `Read → 59 байт` вместо одного
  куска, байты не искажаются.
* **VPN-режим** (кнопка «VPN-режим: весь телефон»): `VpnService` создаёт tun,
  **hev-socks5-tunnel** (MIT) превращает его в TCP/UDP и ведёт в наш SOCKS5,
  где и делается обход. Архитектура как у ByeByeDPI, но без JNI: hev вызывается
  из C# через P/Invoke (`Engine/HevTunnel.cs`).
* **Выбор приложений** (`AppSelection.cs` + карточка в интерфейсе): весь телефон,
  «только выбранные», «все, кроме выбранных». Список — только приложения из
  лончера (`ACTION_MAIN` + `CATEGORY_LAUNCHER`), без системной мелочи.
  Зачем: банки и госуслуги ругаются на активный VPN, а заворачивать нужно не всё.
* **UDP в SOCKS5** (`UDP ASSOCIATE`, RFC 1928). Без него в VPN-режиме не было ни
  DNS, ни голоса: hev отдаёт сюда всё, что не TCP. На каждый ответ мы возвращаем
  заголовок с адресом отправителя, поэтому голос (много собеседников сразу)
  работает через один сокет.
* Telegram: `Настройки → Прокси → Добавить → SOCKS5`, сервер `127.0.0.1`,
  порт из приложения. В VPN-режиме то же самое, но для всех приложений.

Пока приложение на экране (или свёрнуто ненадолго) — прокси работает; VPN-режим
живёт в foreground-сервисе с уведомлением.

## Сборка tun2socks

Один раз, до первой сборки APK:

```
android\build-hev.cmd
```

Скрипт соберёт `refs\hev-socks5-tunnel` через NDK (`arm64-v8a` + `x86_64`) и
положит `.so` в `AutoZapret.Android\libs\`. Готовые библиотеки в git не лежат.

Минимальная версия Android — **10 (API 29)**: под неё собрана hev.

## Проверено на живом телефоне

Тест на Android 16 (API 36), arm64:

* **SOCKS5**: `curl --socks5-hostname` через `adb forward` → `https://example.com`
  отвечает 200 за 0,34 с.
* **VPN**: tun0 `10.10.10.10/32` поднимается, браузер открывает сайты, в приложении
  счётчики растут (58 соединений, 34 КБ вверх / 41 КБ вниз), лог показывает
  `VPN включён: tun → SOCKS5 127.0.0.1:1080`.
* Остановка VPN убирает tun0 и возвращает сеть — проверено.

Что ломалось на первом запуске и как вылечено (чтобы не наступить снова):

| Симптом | Причина |
|---|---|
| `No assemblies found in .../.__override__` | Fast Deployment: Debug-APK был «оболочкой», сборки доливала IDE. `EmbedAssembliesIntoApk=true` |
| `SecurityException` при старте FGS | targetSdk 35 требует `FOREGROUND_SERVICE_SYSTEM_EXEMPTED` **и** одно из exact-alarm разрешений. У ByeByeDPI targetSdk 34, поэтому им хватает одного |
| SIGSEGV в `hev_task_new` | `hev_socks5_tunnel_init` нельзя звать без `hev_task_system_init` + `lwip_init`. Правильная точка входа одна — `hev_socks5_tunnel_main_from_str` |
| SOCKS: `host unreachable` по имени, по IP работает | `TcpClient.BeginConnect(host, …)` шёл в IPv6 первым и висел до таймаута ОС. Теперь имя разбираем сами: IPv4 первым, 6 с на адрес |
| В VPN-режиме «сайты не открываются», хотя соединения есть | В SOCKS5 не было `UDP ASSOCIATE`: DNS-запросы (это UDP) получали отказ, и браузер не мог разрезолвить имя. Голос в Discord — по той же причине |
| `PackageInfoFlags.MainActivity` не компилируется | Такого значения в биндинге нет. Список приложений берём через `QueryIntentActivities` с `Intent.ActionMain` + `CategoryLauncher` |
| Список приложений почти пустой (Android 11+) | Ограничение видимости пакетов. Нужен `QUERY_ALL_PACKAGES` в манифесте — он не спрашивается у человека, просто объявляется |

## Чего здесь не будет

* WinDivert и `winws.exe` — это Windows.
* Правки `hosts` — на Android только с root.
* Копий кода ByeByeDPI (GPL-3.0) — только идеи и своя реализация.