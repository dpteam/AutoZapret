<#
.SYNOPSIS
    Headless-прогон проверок AutoZapret с ОДНИМ запросом UAC.

.DESCRIPTION
    Зачем: половина проверок требует прав администратора (winws.exe и WinDivert),
    а по одному запуску за раз это десяток подтверждений UAC. Здесь создаётся
    временный .cmd со всеми командами подряд, он стартует один раз через runas,
    и весь вывод уходит в отчёт.

    Ничего не нажимается в интерфейсе: только CLI-режимы приложения.

    Наборы:
      safe     - env, diag, preflight, probe, deep, tcp16. Ничего не запускает
                 надолго, обход включается только внутри probe/deep и гасится.
      full     - safe + testall (перебор стратегий, минуты).
      strategy - один прогон конкретной стратегии с замером доступности.

.PARAMETER Set
    safe | full | strategy. По умолчанию safe.

.PARAMETER Strategy
    id стратегии для -Set strategy (например flowseal.general-alt12).

.PARAMETER Strategies
    Сколько стратегий перебирать для -Set full. По умолчанию 6.

.PARAMETER Hold
    Сколько держать стратегию перед замером, секунды. По умолчанию 5.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\headless.ps1
    powershell -ExecutionPolicy Bypass -File tools\headless.ps1 -Set strategy -Strategy flowseal.general-alt12
#>
[CmdletBinding()]
param(
    [ValidateSet('safe', 'full', 'strategy')]
    [string]$Set = 'safe',

    [string]$Strategy = 'flowseal.general-alt12',

    [int]$Strategies = 6,

    [int]$Hold = 5
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'bin\AutoZapret.exe'

if (-not (Test-Path $exe)) {
    Write-Host "[ERROR] нет $exe - сначала build.cmd" -ForegroundColor Red
    exit 1
}

$work = Join-Path $root 'data\headless'
New-Item -ItemType Directory -Path $work -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = Join-Path $work ("report-$stamp.txt")
$cmd = Join-Path $work ("run-$stamp.cmd")

# ---------------------------------------------------------------- набор команд
$commands = New-Object System.Collections.Generic.List[string]

function Add-Step([string]$title, [string]$arguments) {
    $script:commands.Add("echo.")
    $script:commands.Add("echo ===== $title =====")
    # 2>&1 обязателен: часть диагностики пишет в stderr
    $script:commands.Add('"' + $script:exe + '" ' + $arguments + ' 2>&1')
    $script:commands.Add("echo exit=%ERRORLEVEL%")
}

Add-Step 'версия'            '--version'
Add-Step 'проверки окружения' '--diagnose'
Add-Step 'готовность'        '--preflight'
Add-Step 'готовность (стратегия)' ('--preflight ' + $Strategy)

if ($Set -eq 'safe' -or $Set -eq 'full') {
    Add-Step 'базовый замер'  '--probe'
    Add-Step 'тип блокировки' '--deep'
    Add-Step 'обрыв 16-20 КБ' '--tcp16 8'
}

if ($Set -eq 'full') {
    Add-Step ('перебор стратегий (' + $Strategies + ')') ('--test-all ' + $Strategies + ' ' + $Hold)
}

if ($Set -eq 'strategy') {
    Add-Step ('стратегия ' + $Strategy) ('--test-all 1 ' + $Hold + ' --only ' + $Strategy)
}

Add-Step 'итог' '--selftest'

# ---------------------------------------------------------------- .cmd (ASCII + CRLF)
# cmd требует CRLF и не понимает UTF-8: всё содержимое только латиницей.
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('@echo off')
$lines.Add('chcp 65001 >nul')
$lines.Add('pushd "' + (Split-Path -Parent $exe) + '"')
$lines.Add('echo headless run ' + $stamp + ' / set=' + $Set)
$lines.AddRange($commands)
$lines.Add('popd')
$lines.Add('echo.')
$lines.Add('echo done')
$lines.Add('exit /b 0')

[System.IO.File]::WriteAllText($cmd, ($lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- запуск с правами
Write-Host "== headless: набор $Set ==" -ForegroundColor Cyan
Write-Host "   отчёт: $report"
Write-Host "   сейчас будет ОДИН запрос UAC - подтвердите." -ForegroundColor Yellow

$args = '/c ""' + $cmd + '" > "' + $report + '" 2>&1"'

try {
    $p = Start-Process -FilePath 'cmd.exe' -ArgumentList $args -Verb RunAs -Wait -PassThru
    $code = $p.ExitCode
} catch {
    Write-Host "[ERROR] не удалось запустить с правами: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "        (отказ в UAC - это не ошибка программы)" -ForegroundColor Red
    exit 1
}

Write-Host "   код возврата: $code" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $report)) {
    Write-Host "[ERROR] отчёт не создан" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- сводка
$text = Get-Content $report -Encoding UTF8

$fail = @($text | Select-String -Pattern '\[FAIL\]|\[БЛОК\]|RESULT: [^o]')
$warn = @($text | Select-String -Pattern '\[warn\]|WARN ')

Write-Host "== результат ==" -ForegroundColor Cyan
Write-Host ("   строк: {0}, проблем: {1}, предупреждений: {2}" -f $text.Count, $fail.Count, $warn.Count)

if ($fail.Count -gt 0) {
    Write-Host "   ПРОБЛЕМЫ:" -ForegroundColor Red
    $fail | Select-Object -First 12 | ForEach-Object { Write-Host ("   " + $_.Line) -ForegroundColor Red }
}

Write-Host ""
Write-Host "полный отчёт: $report"

# файл .cmd больше не нужен
Remove-Item $cmd -Force -ErrorAction SilentlyContinue

exit $(if ($fail.Count -gt 0) { 1 } else { 0 })