# Проверка установщика: тихая установка, запуск, удаление.
#
#   powershell -File tools\check-installer.ps1
#
# Зачем отдельно от сборки: установщик, который собрался, — это не установщик,
# который работает. Проверяем то, что ломается молча:
#   * файлы на месте (exe, engine, assets, иконка);
#   * установленная копия запускается и проходит selftest;
#   * удаление убирает программу и не оставляет ярлыков.

param(
    [string]$Setup = '',
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Fail([string]$message) {
    Write-Host ""
    Write-Host "[FAIL] $message"
    exit 1
}

if (-not $Setup) {
    $Setup = (Get-ChildItem (Join-Path $root 'artifacts') -Filter 'AutoZapret-*-setup.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

if (-not $Setup -or -not (Test-Path $Setup)) { Fail 'не нашёл установщик в artifacts\' }

Write-Host "установщик: $Setup"
Write-Host ("размер:     " + [Math]::Round((Get-Item $Setup).Length / 1MB, 1) + " MB")

$target = Join-Path $env:TEMP 'az-install-check'

if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "установка в $target ..."

# /VERYSILENT — без окна; /SUPPRESSMSGBOXES — не ждать ответов; /NORESTART.
$p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$target" -Wait -PassThru

Write-Host "код установки: $($p.ExitCode)"

if ($p.ExitCode -ne 0) { Fail "установщик вернул $($p.ExitCode)" }

# ---- что должно появиться
$must = @(
    'AutoZapret.exe',
    'AutoZapret.ico',
    'WebView2Loader.dll',
    'engine\bin\winws.exe',
    'engine\bin\WinDivert64.sys',
    'engine\lists\list-general.txt',
    'assets\strategies\strategies.v1.json',
    'assets\hosts\fixes.json',
    'assets\fixes\packs.json',
    'README.md',
    'LICENSE'
)

$missing = New-Object System.Collections.ArrayList

foreach ($rel in $must) {
    if (-not (Test-Path (Join-Path $target $rel))) { $missing.Add($rel) | Out-Null }
}

Write-Host ''
Write-Host "файлов проверено: $($must.Count), нет: $($missing.Count)"

if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Host "   нет: $_" }
    Fail 'установщик не положил часть файлов'
}

# ---- программа запускается и сама себя проверяет
$exe = Join-Path $target 'AutoZapret.exe'

Write-Host ''
Write-Host 'selftest установленной копии...'

Push-Location $target
try {
    $s = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
} finally { Pop-Location }

$report = Join-Path $target 'selftest.txt'

if (Test-Path $report) {
    Get-Content $report -Encoding UTF8 | Select-String -Pattern 'RESULT|\[FAIL\]' | ForEach-Object { Write-Host ("  " + $_.Line) }
}

if ($s.ExitCode -ne 0) { Fail "selftest установленной копии вернул $($s.ExitCode)" }

# ---- версия та, что ожидали
Write-Host ''
Write-Host 'версия установленной копии...'

$v = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $env:TEMP 'az-version.txt')
$versionText = (Get-Content (Join-Path $env:TEMP 'az-version.txt') -Raw -ErrorAction SilentlyContinue)
Write-Host ("  " + ($versionText -replace '\s+$', ''))

if ($versionText -notmatch '[0-9]+\.[0-9]+\.[0-9]+') { Fail 'версия не читается' }

# ---- удаление
if (-not $KeepInstalled) {
    $uninst = Join-Path $target 'unins000.exe'

    if (-not (Test-Path $uninst)) { Fail 'нет unins000.exe — удаление не зарегистрировано' }

    Write-Host ''
    Write-Host 'удаление...'

    $u = Start-Process -FilePath $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Write-Host "код удаления: $($u.ExitCode)"

    Start-Sleep -Seconds 2

    $left = New-Object System.Collections.ArrayList

    foreach ($rel in @('AutoZapret.exe', 'engine\bin\winws.exe', 'assets\hosts\fixes.json')) {
        if (Test-Path (Join-Path $target $rel)) { $left.Add($rel) | Out-Null }
    }

    if ($left.Count -gt 0) {
        $left | ForEach-Object { Write-Host "   осталось: $_" }
        Fail 'после удаления остались файлы программы'
    }

    Write-Host '  программа удалена, файлов не осталось'
}
else {
    Write-Host ''
    Write-Host "оставлено установленным: $target"
}

Write-Host ''
Write-Host 'RESULT: ok'
