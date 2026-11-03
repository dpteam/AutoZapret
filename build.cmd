@echo off
setlocal enabledelayedexpansion
rem ===========================================================================
rem  AutoZapret - build script
rem  .NET Framework 4.8 / x64 / C# 7.3. No NuGet, no MSBuild solution.
rem  The only external dependency is the WebView2 SDK in lib\webview2.
rem ===========================================================================

set "ROOT=%~dp0"
set "OUT=%ROOT%bin"

echo.
echo  AutoZapret build
echo  ------------------------------------------------------------------

rem ---------------------------------------------------------------- checks
if not exist "%ROOT%AutoZapret.csproj" (
  echo [ERROR] AutoZapret.csproj not found. Run from the repo root.
  exit /b 1
)

rem CS0016 / MSB3027: the exe is locked by a running instance. Kill our own process.
tasklist /fi "imagename eq AutoZapret.exe" 2>nul | find /i "AutoZapret.exe" >nul
if not errorlevel 1 (
  echo [1/3] AutoZapret.exe is running - stopping it so the build can replace the file
  taskkill /im AutoZapret.exe /f >nul 2>&1
  ping -n 2 127.0.0.1 >nul
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet SDK not found in PATH.
  echo         Install .NET SDK 8 or newer: https://dotnet.microsoft.com/download
  echo         The RESULT still targets .NET Framework 4.8 - the SDK is only the compiler.
  exit /b 1
)

if not exist "%ROOT%lib\webview2\Microsoft.Web.WebView2.Core.dll" (
  echo [1/3] WebView2 SDK missing - fetching...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%tools\fetch-webview2.ps1"
  if errorlevel 1 (
    echo [ERROR] WebView2 SDK could not be fetched.
    exit /b 1
  )
) else (
  echo [1/3] WebView2 SDK present
)

rem ---------------------------------------------------------------- build
echo [2/3] compiling...
pushd "%ROOT%"
dotnet build AutoZapret.csproj -c Release -v minimal --nologo
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
  echo.
  echo [ERROR] build failed.
  echo         If you see CS0016: AutoZapret.exe is running - close it and retry.
  exit /b 1
)

rem ---------------------------------------------------------------- layout
echo [3/3] checking output...
if not exist "%OUT%\AutoZapret.exe" (
  echo [ERROR] %OUT%\AutoZapret.exe was not produced.
  exit /b 1
)
if not exist "%OUT%\WebView2Loader.dll" (
  echo [WARN] WebView2Loader.dll is missing next to the exe - the window will not start.
)

if not exist "%OUT%\engine" (
  echo.
  echo [note] engine\ is not installed yet. The GUI will start, but DPI bypass needs it:
  echo        powershell -File tools\fetch-engine.ps1
)

echo.
echo  Done.
echo    exe      : %OUT%\AutoZapret.exe
echo    selftest : %OUT%\AutoZapret.exe --selftest
echo    run      : run.cmd
echo.
endlocal
exit /b 0
