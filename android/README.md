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
* Telegram: `Настройки → Прокси → Добавить → SOCKS5`, сервер `127.0.0.1`,
  порт из приложения.

Пока приложение на экране (или свёрнуто ненадолго) — прокси работает; постоянную
работу в фоне даст foreground-сервис, это следующий шаг вместе с `VpnService`.

## Чего здесь не будет

* WinDivert и `winws.exe` — это Windows.
* Правки `hosts` — на Android только с root.
* Копий кода ByeByeDPI (GPL-3.0) — только идеи и своя реализация.