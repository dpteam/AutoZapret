@echo off
setlocal
rem ===========================================================================
rem  AutoZapret - run from the build output
rem ===========================================================================

set "ROOT=%~dp0"
set "EXE=%ROOT%bin\AutoZapret.exe"

if not exist "%EXE%" (
  echo [ERROR] %EXE% not found. Run build.cmd first.
  exit /b 1
)

start "" "%EXE%" %*
endlocal
exit /b 0
