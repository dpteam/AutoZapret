@echo off
setlocal
rem ===========================================================================
rem  AutoZapret - publish script
rem  Self-contained single-file: the user does NOT need .NET installed.
rem
rem  Differences from build.cmd:
rem    build.cmd   - fast dev build (needs .NET 9 on the machine)
rem    publish.cmd - ready-to-ship package (~50-60 MB, .NET inside)
rem ===========================================================================

set "ROOT=%~dp0"
set "OUT=%ROOT%publish"

echo.
echo  AutoZapret publish - self-contained, single-file
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

rem A running exe holds the file - stop our own process
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

echo [2/3] publishing - takes up to a minute...
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

rem Engine next to the exe: the app looks for <exe dir>\engine
if exist "%ROOT%engine\bin\winws.exe" (
  if not exist "%OUT%\engine" mkdir "%OUT%\engine"
  robocopy "%ROOT%engine" "%OUT%\engine" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
  if errorlevel 8 (
    echo [WARN] engine copy failed - is winws.exe running?
  ) else (
    echo       engine -^> %OUT%\engine
  )
) else (
  echo       [note] engine\ is not installed - the app downloads it on first run
)

if exist "%ROOT%assets" (
  if not exist "%OUT%\assets" mkdir "%OUT%\assets"
  robocopy "%ROOT%assets" "%OUT%\assets" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)

rem ---------------------------------------------------------------------------
rem  Strategies. They are not in the repo (imported from the upstream .bat
rem  files under refs\, which are not shipped). For a release we generate
rem  the JSON cache here: it is smaller than the .bat set and already proven.
rem ---------------------------------------------------------------------------
if not exist "%OUT%\assets\strategies" mkdir "%OUT%\assets\strategies"

if exist "%ROOT%bin\assets\strategies\strategies.v1.json" (
  robocopy "%ROOT%bin\assets\strategies" "%OUT%\assets\strategies" *.json /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)

rem No cache (clean build) - ask the app to create it.
if not exist "%OUT%\assets\strategies\strategies.v1.json" (
  echo       generating strategy cache...
  pushd "%OUT%"
  ".\AutoZapret.exe" --strategies >nul 2>&1
  popd
  if exist "%OUT%\assets\strategies\strategies.v1.json" (
    echo       strategies: v1 present
  ) else (
    echo [WARN] could not create the strategy cache.
    echo        Needs the source .bat files in refs\zapret-discord-youtube and refs\snanvl-zapret2
    echo        and an installed engine\ from tools\fetch-engine.ps1.
    echo        Without the cache the app on a user machine will have no strategies.
  )
)

if exist "%OUT%\assets\strategies\strategies.v2.json" (
  echo       strategies: v1 + v2
) else (
  if exist "%OUT%\assets\strategies\strategies.v1.json" (
    echo       strategies: v1 only - zapret2 not installed?
  )
)
if exist "%ROOT%licenses" (
  robocopy "%ROOT%licenses" "%OUT%\licenses" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)

rem tools\ next to the exe: fetch-engine.ps1 and the check-*.ps1 diagnostics.
rem The README refers to them, so shipping without them sends people nowhere.
if exist "%ROOT%tools" (
  robocopy "%ROOT%tools" "%OUT%\tools" /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 >nul 2>&1
)
if exist "%ROOT%README.md" copy /y "%ROOT%README.md" "%OUT%\" >nul

if not exist "%OUT%\AutoZapret.exe" (
  echo [ERROR] %OUT%\AutoZapret.exe was not produced.
  exit /b 1
)

rem WebView2Loader.dll - native WebView2 library. In single-file it is NOT
rem embedded (it is not a managed assembly), so copy it explicitly. Without it
rem the window does not start: DllNotFoundException on environment creation.
if exist "%ROOT%lib\webview2\WebView2Loader.dll" (
  copy /y "%ROOT%lib\webview2\WebView2Loader.dll" "%OUT%\" >nul
  echo       WebView2Loader.dll
) else (
  echo [ERROR] lib\webview2\WebView2Loader.dll not found - the window will not start.
  exit /b 1
)

for %%F in ("%OUT%\AutoZapret.exe") do set "SIZE=%%~zF"
set /a SIZE_MB=%SIZE% / 1048576

echo.
echo  Done.
echo    folder  : %OUT%
echo    exe     : %OUT%\AutoZapret.exe  - %SIZE_MB% MB
echo    .NET    : not required - bundled in the exe
echo    selftest: "%OUT%\AutoZapret.exe" --selftest
echo.
echo  Layout: exe + WebView2Loader.dll + engine\ + assets\ + tools\
echo  Ships as a folder or zipped as a whole.
echo.
endlocal
exit /b 0
