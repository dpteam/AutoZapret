@echo off
rem Strategy audit: v1 + v2 + own against the real engine binaries.
rem
rem The script tries the engine directly first. Only if the engine cannot start
rem at all without elevation (exit code 2 from the script) does this wrapper ask
rem for admin rights ONCE and run the whole audit elevated - instead of 44
rem separate UAC prompts, one per strategy launch.
rem
rem Usage: tools\audit.cmd            (all engines)
rem        tools\audit.cmd v2         (only v2)
setlocal
cd /d "%~dp0.."

if /i "%~1"=="--elevated" goto run

node "%~dp0audit-strategies.js" %*
if not errorlevel 2 goto end

echo.
echo Asking for admin rights once...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '--elevated' -Wait"
if exist artifacts\audit-strategies.txt type artifacts\audit-strategies.txt
goto end

:run
node "%~dp0audit-strategies.js"
:end