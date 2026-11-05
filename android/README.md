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

## Чего здесь не будет

* WinDivert и `winws.exe` — это Windows.
* Правки `hosts` — на Android только с root.
* Копий кода ByeByeDPI (GPL-3.0) — только идеи и своя реализация.