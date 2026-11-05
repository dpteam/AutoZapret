@echo off
rem Strategy audit: v1 + v2 + own against the real engine binaries.
rem
rem WHY THIS WRAPPER EXISTS
rem   winws.exe / winws2.exe carry a requireAdministrator manifest. Launching
rem   them from a NON-elevated console makes Windows ask for consent on EVERY
rem   launch - 44 prompts for 44 strategies. This wrapper asks ONCE, then runs
rem   the whole audit inside the elevated process, and shows the saved report.
rem
rem   In CI nothing here is used: the workflow calls
rem   "node tools\audit-strategies.js" directly and never elevates.
rem
rem Usage: tools\audit.cmd            (all engines)
rem        tools\audit.cmd v2         (only v2)
setlocal
cd /d "%~dp0.."

for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)"`) do set "ADMIN=%%i"

if /i "%ADMIN%"=="True" goto run

echo Asking for administrator rights once - the engine requires it, and this way
echo it is ONE prompt instead of one per strategy.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs -Wait"

if exist artifacts\audit-strategies.txt type artifacts\audit-strategies.txt
goto end

:run
node "%~dp0audit-strategies.js" %*
:end