<#
.SYNOPSIS
    Качает движок zapret (winws.exe + WinDivert + cygwin1) в engine\.

.DESCRIPTION
    Источник - git-репозитории, а НЕ zip-архивы. Причина: антивирусы регулярно
    блокируют скачивание zip-архива zapret-win-bundle целиком (внутри лежит
    WinDivert64.sys, который флагуется как RiskTool), хотя отдельные файлы
    проходят спокойно. git clone такой проблемы не создаёт.

    Берём только нужное:
      zapret-win-bundle\zapret-winws\  -> winws.exe, dll, sys, files\, windivert.filter\
      Flowseal\lists\                  -> списки доменов и ipset
      Flowseal\bin\                    -> фейки (.bin), свежее бандловых
      Flowseal\utils\                  -> game_filter.enabled, targets.txt

    Раскладка на выходе:
      engine\bin\        winws.exe + dll + sys + *.bin фейки
      engine\lists\      list-*.txt, ipset-*.txt
      engine\utils\      game_filter.enabled
      engine\version.txt
      engine\manifest.json  (SHA-256 каждого файла)

.PARAMETER Force
    Перекачать, даже если engine уже на месте.

.PARAMETER BundleRef
    Ветка/тег zapret-win-bundle. По умолчанию master.

.PARAMETER FlowsealRef
    Ветка/тег zapret-discord-youtube. По умолчанию main.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$BundleRef   = 'master',
    [string]$FlowsealRef = 'main'
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$engine  = Join-Path $root 'engine'
$binDir  = Join-Path $engine 'bin'
$listDir = Join-Path $engine 'lists'
$utilDir = Join-Path $engine 'utils'

if (-not $Force -and (Test-Path (Join-Path $binDir 'winws.exe'))) {
    $v = if (Test-Path (Join-Path $engine 'version.txt')) { (Get-Content (Join-Path $engine 'version.txt') -Raw).Trim() } else { '?' }
    Write-Host "[ok] engine уже установлен (версия $v) - пропускаю."
    Write-Host "     Перекачать: tools\fetch-engine.ps1 -Force"
    exit 0
}

# ---------------------------------------------------------------- prerequisites
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Host "[ERROR] git не найден в PATH." -ForegroundColor Red
    Write-Host "        Скачайте: https://git-scm.com/download/win" -ForegroundColor Red
    Write-Host "        (или задайте GIT_BIN вручную)" -ForegroundColor Red
    exit 1
}

function EnsureDir([string]$path) {
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('azengine-' + [Guid]::NewGuid().ToString('N'))

function CloneRepo([string]$url, [string]$dest, [string]$ref) {
    Write-Host "      git clone --depth 1 -b $ref $url"
    # git пишет прогресс в stderr, а PowerShell 5.1 при ErrorActionPreference=Stop
    # считает любую запись в stderr ошибкой. Поэтому глушим stderr и смотрим код возврата.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & git clone --depth 1 --single-branch --branch $ref --quiet $url $dest 2>$null | Out-Null
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    if ($code -ne 0) { throw "git clone не удался: $url ($ref), код $code" }
}

try {
    EnsureDir $engine
    EnsureDir $binDir
    EnsureDir $listDir
    EnsureDir $utilDir
    EnsureDir $temp

    # ---------------------------------------------------------------- bundle
    Write-Host "[1/5] zapret-win-bundle ($BundleRef) ..."
    $bundleDir = Join-Path $temp 'bundle'
    CloneRepo 'https://github.com/bol-van/zapret-win-bundle.git' $bundleDir $BundleRef

    $winwsSrc = Join-Path $bundleDir 'zapret-winws'
    if (-not (Test-Path $winwsSrc)) { throw 'в бандле нет каталога zapret-winws' }

    # то, без чего winws не запустится
    $must = @('winws.exe', 'cygwin1.dll', 'WinDivert.dll', 'WinDivert64.sys')
    foreach ($f in $must) {
        $p = Join-Path $winwsSrc $f
        if (-not (Test-Path $p)) { throw "в бандле нет обязательного файла: $f" }
        Copy-Item $p (Join-Path $binDir $f) -Force
        Write-Host "      bin\$f"
    }

    # необязательное, но полезное
    foreach ($f in @('elevator.exe', 'killall.exe', 'windivert_delete.cmd', 'enable_timestamps.cmd', '_CMD_ADMIN.cmd')) {
        $p = Join-Path $winwsSrc $f
        if (Test-Path $p) { Copy-Item $p (Join-Path $binDir $f) -Force }
    }

    # фейки и kernel-фильтры из бандла
    $filesSrc = Join-Path $winwsSrc 'files'
    if (Test-Path $filesSrc) {
        $n = 0
        Get-ChildItem $filesSrc -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $binDir $_.Name) -Force; $n++ }
        Write-Host "      bin\*.bin ($n файлов)"
    }

    $filterSrc = Join-Path $winwsSrc 'windivert.filter'
    if (Test-Path $filterSrc) {
        Copy-Item $filterSrc (Join-Path $binDir 'windivert.filter') -Recurse -Force
        Write-Host "      bin\windivert.filter\"
    }

    # ---------------------------------------------------------------- flowseal
    Write-Host "[2/5] конфиги Flowseal ($FlowsealRef) ..."
    $flowDir = Join-Path $temp 'flowseal'
    CloneRepo 'https://github.com/Flowseal/zapret-discord-youtube.git' $flowDir $FlowsealRef

    # списки
    $listsSrc = Join-Path $flowDir 'lists'
    $listCount = 0
    if (Test-Path $listsSrc) {
        Get-ChildItem $listsSrc -File -Filter '*.txt' | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $listDir $_.Name) -Force
            $listCount++
        }
    }
    Write-Host "      lists\*.txt ($listCount файлов)"

    # фейки Flowseal (свежее бандловых, перекрывают их)
    $binSrc = Join-Path $flowDir 'bin'
    $fakeCount = 0
    if (Test-Path $binSrc) {
        Get-ChildItem $binSrc -File -Filter '*.bin' | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $binDir $_.Name) -Force
            $fakeCount++
        }
    }
    Write-Host "      bin\*.bin ($fakeCount файлов)"

    # версия Flowseal
    $flowVersion = 'unknown'
    $versionFile = Join-Path $flowDir '.service\version.txt'
    if (Test-Path $versionFile) {
        $flowVersion = (Get-Content $versionFile -Raw).Trim()
    } else {
        $serviceBat = Join-Path $flowDir 'service.bat'
        if (Test-Path $serviceBat) {
            $m = Select-String -Path $serviceBat -Pattern 'LOCAL_VERSION=([\d\.]+)' | Select-Object -First 1
            if ($m) { $flowVersion = $m.Matches[0].Groups[1].Value }
        }
    }

    # ---------------------------------------------------------------- utils
    Write-Host "[3/5] служебные файлы ..."
    $utilsSrc = Join-Path $flowDir 'utils'
    if (Test-Path $utilsSrc) {
        Get-ChildItem $utilsSrc -File | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $utilDir $_.Name) -Force
        }
    }

    # игровой фильтр по умолчанию выключен
    $gf = Join-Path $utilDir 'game_filter.enabled'
    if (-not (Test-Path $gf)) { Set-Content -Path $gf -Value 'off' -Encoding ASCII -NoNewline }

    # пользовательские списки создаём пустыми, чтобы winws не ругался на отсутствие
    foreach ($name in @('list-general-user.txt', 'list-exclude-user.txt', 'ipset-exclude-user.txt')) {
        $p = Join-Path $listDir $name
        if (-not (Test-Path $p)) { Set-Content -Path $p -Value '' -Encoding ASCII }
    }

    # ---------------------------------------------------------------- manifest
    Write-Host "[4/5] считаю SHA-256 ..."
    $entries = @()
    Get-ChildItem $engine -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' } | ForEach-Object {
        $rel = $_.FullName.Substring($engine.Length).TrimStart('\')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $entries += [pscustomobject]@{ path = $rel; size = $_.Length; sha256 = $hash }
    }

    $bundleCommit = (& git -C $bundleDir rev-parse --short HEAD 2>$null)
    $flowCommit   = (& git -C $flowDir   rev-parse --short HEAD 2>$null)

    $manifest = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        bundle      = @{ repo = 'bol-van/zapret-win-bundle'; ref = $BundleRef; commit = "$bundleCommit" }
        flowseal    = @{ repo = 'Flowseal/zapret-discord-youtube'; ref = $FlowsealRef; commit = "$flowCommit"; version = $flowVersion }
        fileCount   = $entries.Count
        files       = $entries
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $engine 'manifest.json') -Encoding UTF8

    # ---------------------------------------------------------------- version
    Set-Content -Path (Join-Path $engine 'version.txt') -Value $flowVersion -Encoding ASCII -NoNewline
    Write-Host "[5/5] готово."

    $binSize  = [int](((Get-ChildItem $binDir  -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
    $listSize = [int](((Get-ChildItem $listDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)
    Write-Host ""
    Write-Host "  версия Flowseal : $flowVersion ($flowCommit)"
    Write-Host "  bundle commit   : $bundleCommit"
    Write-Host "  bin\            : $binSize МБ"
    Write-Host "  lists\          : $listSize КБ"
    Write-Host "  файлов в манифесте: $($entries.Count)"
    Write-Host ""
    exit 0
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
