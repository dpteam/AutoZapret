@echo off
rem Strategy audit wrapper: one UAC for all strategies.
rem
rem winws.exe/winws2.exe carry a requireAdministrator manifest, so every single
rem launch from a non-elevated shell shows a UAC prompt - 44 of them for the
rem full audit. audit-strategies.js re-executes itself elevated ONCE and then
rem runs all launches as children of an elevated process (no prompts).
rem
rem Usage: tools\audit.cmd            (v1 + v2 + own)
rem        tools\audit.cmd v2         (only v2)
rem Results are printed in this window and saved to artifacts\audit-strategies.txt.
node "%~dp0audit-strategies.js" %*