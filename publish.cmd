@echo off
setlocal
rem ===========================================================================
rem  AutoZapret - publish script
rem  Self-contained single-file: пользователю НЕ нужен установленный .NET.
rem
rem  Отличия от build.cmd:
rem    build.cmd   - быстрая сборка для разработки (нужен .NET 9 на машине)
rem    publish.cmd - готовый дистрибутив (~50-60 МБ, .NET внутри)
rem ===========================================================================

set "ROOT=%~dp0"
set "OUT=%ROOT%publish"

echo.
echo  AutoZapret publish (self-contained, single-file)
echo  ------------------------------------------------------------------

if not exist "%ROOT%AutoZapret.csproj" (
  echo [ERROR] AutoZapret.csproj not found. Run from the repo root.
  exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet SDK not found in PATH.
  echo         Install .NET SDK 9 or newer: https://dotnet.microsoft.com/download
  exit /b 1
)

rem Запущенный exe держит файл - гасим свой процесс
tasklist /fi "imagename eq AutoZapret.exe" 2>nul | find /i "AutoZapret.exe" >nul
if not errorlevel 1 (
  echo [1/3] AutoZapret.exe is running - stopping it
  taskkill /im AutoZapret.exe /f >nul 2>&1
  ping -n 2 127.0.0.1 >nul
)

if not exist "%ROOT%lib\webview2\Microsoft.Web.WebView2.Core.dll" (
  echo [1/3] WebView2 SDK missing - fetching...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\fetch-webview2.ps1"
  if errorlevel 1 exit /b 1
) else (
  echo [1/3] WebView2 SDK present
)

if exist "%OUT%" rmdir /s /q "%OUT%"

echo [2/3] publishing (это занимает до минуты)...
pushd "%ROOT%"
dotnet publish AutoZapret.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -v minimal --nologo ^
  -o "%OUT%"
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo.
  echo [ERROR] publish failed.
  exit /b 1
)

echo [3/3] laying out engine and assets...

rem Движок рядом с exe: приложение ищет <exe dir>\engine
if exist "%ROOT%engine\bin\winws.exe" (
  if not exist "%OUT%\engine" mkdir "%OUT%\engine"
  robocopy "%ROOT%engine" "%OUT%\engine" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
  if errorlevel 8 (
    echo [WARN] engine copy failed - is winws.exe running?
  ) else (
    echo       engine -^> %OUT%\engine
  )
) else (
  echo       [note] engine\ не установлен - движок скачается при первом запуске
)

if exist "%ROOT%assets" (
  if not exist "%OUT%\assets" mkdir "%OUT%\assets"
  robocopy "%ROOT%assets" "%OUT%\assets" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)

rem ---------------------------------------------------------------------------
rem  Стратегии. В репозитории их нет (они импортируются из .bat-файлов
rem  апстрима, которые лежат в refs\ и не шипятся). Для релиза генерируем
rem  JSON-кэш здесь: он компактнее .bat-набора и уже проверен.
rem ---------------------------------------------------------------------------
if not exist "%OUT%\assets\strategies" mkdir "%OUT%\assets\strategies"

if exist "%ROOT%bin\assets\strategies\strategies.v1.json" (
  robocopy "%ROOT%bin\assets\strategies" "%OUT%\assets\strategies" *.json /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)

rem Если кэша нет (чистая сборка) — просим приложение его создать.
if not exist "%OUT%\assets\strategies\strategies.v1.json" (
  echo       генерирую кэш стратегий...
  pushd "%OUT%"
  ".\AutoZapret.exe" --strategies >nul 2>&1
  popd
  if exist "%OUT%\assets\strategies\strategies.v1.json" (
    echo       стратегии: v1 есть
  ) else (
    echo [WARN] не удалось создать кэш стратегий.
    echo        Нужны исходные .bat в refs\zapret-discord-youtube и refs\snanvl-zapret2
    echo        и установленный engine\ (tools\fetch-engine.ps1).
    echo        Без кэша приложение на машине пользователя останется без стратегий.
  )
)

if exist "%OUT%\assets\strategies\strategies.v2.json" (
  echo       стратегии: v1 + v2
) else (
  if exist "%OUT%\assets\strategies\strategies.v1.json" (
    echo       стратегии: только v1 (zapret2 не установлен?)
  )
)
if exist "%ROOT%licenses" (
  robocopy "%ROOT%licenses" "%OUT%\licenses" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)
if exist "%ROOT%README.md" copy /y "%ROOT%README.md" "%OUT%\" >nul

if not exist "%OUT%\AutoZapret.exe" (
  echo [ERROR] %OUT%\AutoZapret.exe was not produced.
  exit /b 1
)

rem WebView2Loader.dll - нативная библиотека WebView2. В single-file она НЕ
rem встраивается (это не managed-сборка), поэтому копируем явно. Без неё
rem окно не запускается: DllNotFoundException при создании окружения.
if exist "%ROOT%lib\webview2\WebView2Loader.dll" (
  copy /y "%ROOT%lib\webview2\WebView2Loader.dll" "%OUT%\" >nul
  echo       WebView2Loader.dll
) else (
  echo [ERROR] lib\webview2\WebView2Loader.dll не найден - окно не запустится.
  exit /b 1
)

for %%F in ("%OUT%\AutoZapret.exe") do set "SIZE=%%~zF"
set /a SIZE_MB=%SIZE% / 1048576

echo.
echo  Done.
echo    папка   : %OUT%
echo    exe     : %OUT%\AutoZapret.exe  (%SIZE_MB% МБ)
echo    .NET    : не требуется (вшит в exe)
echo    проверить: "%OUT%\AutoZapret.exe" --selftest
echo.
echo  Раскладка: exe + WebView2Loader.dll + engine\ + assets\
echo  Раздать можно папкой или заархивировать целиком.
echo.
endlocal
exit /b 0
