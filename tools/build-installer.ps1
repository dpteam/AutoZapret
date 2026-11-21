# Сборка установщика Windows.
#
#   powershell -File tools\build-installer.ps1
#   powershell -File tools\build-installer.ps1 -Version 0.3.0
#   powershell -File tools\build-installer.ps1 -SkipPublish
#
# Что делает:
#   1) версию берёт из тега (если HEAD помечен), иначе из csproj;
#   2) при необходимости запускает publish.cmd — установщик собирается из ТОГО ЖЕ
#      каталога publish\, что и портативный архив, а не из отдельной сборки;
#   3) вызывает ISCC (Inno Setup) и проверяет, что exe появился.
#
# Почему версия из тега, а не из csproj: exe рапортует версию из csproj, а релиз
# называет себя по тегу. Если они разошлись, самообновление зацикливается:
# предлагает новую версию, ставит, снова видит старую.

param(
    [string]$Version = '',
    [switch]$SkipPublish,
    [string]$IsccPath = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

function Fail([string]$message) {
    Write-Host ""
    Write-Host "[FAIL] $message"
    exit 1
}

# ---------------------------------------------------------------- версия

if (-not $Version) {
    # git пишет прогресс и «not a tag» в stderr, а PowerShell при
    # ErrorActionPreference=Stop считает stderr ошибкой и роняет скрипт.
    # Поэтому глушим поток и смотрим только на текст.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    Push-Location $root
    try {
        $tag = (git describe --tags --exact-match 2>&1 | Where-Object { $_ -notmatch '^fatal:' } | Select-Object -First 1)
    } finally { Pop-Location; $ErrorActionPreference = $saved }

    if ($tag -and $tag -match '^v?[0-9]+\.[0-9]+\.[0-9]+') {
        $Version = $tag.TrimStart('v', 'V')
        Write-Host "версия: $Version (из тега $tag)"
    }
    else {
        $tag = ''

        $csproj = Join-Path $root 'AutoZapret.csproj'
        $m = Select-String -Path $csproj -Pattern '<Version>([0-9]+\.[0-9]+\.[0-9]+[^<]*)' | Select-Object -First 1

        if (-not $m) { Fail 'не нашёл <Version> в csproj' }

        $Version = $m.Matches[0].Groups[1].Value
        Write-Host "версия: $Version (из csproj; HEAD не помечен тегом)"
    }
}

# ---------------------------------------------------------------- ISCC

if (-not $IsccPath) {
    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )

    foreach ($c in $candidates) {
        if (Test-Path $c) { $IsccPath = $c; break }
    }
}

if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    Write-Host ""
    Write-Host 'Inno Setup 6 не найден.'
    Write-Host 'Установите его и повторите:'
    Write-Host '    choco install innosetup -y'
    Write-Host 'или скачайте с https://jrsoftware.org/isdl.php'
    exit 1
}

Write-Host "ISCC:    $IsccPath"

# ---------------------------------------------------------------- publish

$publishDir = Join-Path $root 'publish'

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'AutoZapret.exe'))) {
        Fail "нет $publishDir\AutoZapret.exe — без публикации собирать нечего"
    }
    Write-Host 'публикация: пропущена (-SkipPublish)'
}
else {
    Write-Host 'публикация: publish.cmd'

    Push-Location $root
    try {
        & cmd /c publish.cmd
        $code = $LASTEXITCODE
    } finally { Pop-Location }

    if ($code -ne 0) { Fail "publish.cmd вернул $code" }
}

# Движок обязан быть внутри: установщик без engine\ ставит программу, которая
# на первом запуске пишет «движок не установлен». Это выглядит как поломка
# сборки, а не как «скачайте ещё».
if (-not (Test-Path (Join-Path $publishDir 'engine\bin\winws.exe'))) {
    Fail 'в publish\ нет engine\bin\winws.exe — установщик получится без движка. Запустите tools\fetch-engine.ps1'
}

# ---------------------------------------------------------------- ISCC

$env:AUTOZAPRET_VERSION = $Version

$iss = Join-Path $root 'installer\autozapret.iss'

if (-not (Test-Path $iss)) { Fail "нет $iss" }

Write-Host 'сборка установщика...'

Push-Location $root
try {
    & $IsccPath $iss
    $code = $LASTEXITCODE
} finally { Pop-Location }

if ($code -ne 0) { Fail "ISCC вернул $code" }

# ---------------------------------------------------------------- результат

$artifacts = Join-Path $root 'artifacts'
$setup = Get-ChildItem $artifacts -Filter "AutoZapret-$Version-setup.exe" -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $setup) {
    $setup = Get-ChildItem $artifacts -Filter 'AutoZapret-*-setup.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if (-not $setup) { Fail 'ISCC отработал, но установщика нет в artifacts\' }

$mb = [Math]::Round($setup.Length / 1MB, 1)

Write-Host ''
Write-Host 'Готово.'
Write-Host "  установщик: $($setup.FullName)"
Write-Host "  размер:     $mb MB"
Write-Host "  версия:     $Version"
Write-Host ''
Write-Host "  проверка:   `"$($setup.FullName)`" /SILENT /DIR=`"$env:TEMP\az-install-test`""
