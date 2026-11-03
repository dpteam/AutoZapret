<#
.SYNOPSIS
    Качает движок zapret (winws.exe + WinDivert + cygwin1) в engine\.

.DESCRIPTION
    Источник - git-репозитории, а НЕ zip-архивы. Причина: антивирусы регулярно
    блокируют скачивание zip-архива zapret-win-bundle целиком (внутри лежит
    WinDivert64.sys, который флагуется как RiskTool), хотя отдельные файлы
    проходят спокойно. git clone такой проблемы не создаёт.

    Ставим ОБА движка, каждый — из проекта, который его поддерживает:

      v1 (winws.exe)  <- Flowseal/zapret-discord-youtube
                         шипит свой winws.exe (203776 байт), отличный от
                         бандлового, и тестирует 22 стратегии именно с ним.
      v2 (winws2.exe) <- SnAnVl/zapret-discord-youtube (ветка zapret2)
                         шипит свой winws2.exe и СВОЙ lua/ с blobs.lua.
                         Смешивать lua из разных источников нельзя:
                         NFQWS2_COMPAT_VER жёстко связывает exe и скрипты.

      Общее (cygwin1.dll, WinDivert.dll/.sys, фейки .bin) — идентично
      у обоих, берём из Flowseal.
      Дополнения (elevator.exe, killall.exe, windivert.filter/) — из бандла.

    Раскладка на выходе:
      engine\bin\        winws.exe, winws2.exe, dll, sys, *.bin фейки
      engine\lua\        скрипты для winws2 (blobs.lua, zapret-*.lua)
      engine\lists\      list-*.txt, ipset-*.txt
      engine\utils\      game_filter.enabled
      engine\version.txt, engine\version2.txt
      engine\manifest.json  (SHA-256 каждого файла)

.PARAMETER Force
    Перекачать, даже если engine уже на месте.

.PARAMETER BundleRef
    Ветка/тег zapret-win-bundle. По умолчанию master.

.PARAMETER FlowsealRef
    Ветка/тег zapret-discord-youtube (v1). По умолчанию main.

.PARAMETER Zapret2Ref
    Ветка snanvl (v2). По умолчанию zapret2.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$BundleRef   = 'master',
    [string]$FlowsealRef = 'main',
    [string]$Zapret2Ref  = 'zapret2'
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

    # ---------------------------------------------------------------- flowseal
    #
    # ВАЖНО: источник бинарников — Flowseal, а не бандл.
    # Причина: Flowseal шипит ПОЛНЫЙ проверенный комплект (winws.exe, cygwin1.dll,
    # WinDivert.dll/.sys и все .bin-фейки) и тестирует свои 22 стратегии именно
    # с этими файлами. Их winws.exe отличается от бандлового (203 КБ против 223 КБ).
    # Смешивать нельзя: получится комбинация, которую никто не проверял.
    #
    # Бандл нужен только для того, чего у Flowseal нет:
    #   elevator.exe, killall.exe, windivert.filter/, lua/ (для zapret2).
    Write-Host "[1/5] конфиги и бинарники Flowseal ($FlowsealRef) ..."
    $flowDir = Join-Path $temp 'flowseal'
    CloneRepo 'https://github.com/Flowseal/zapret-discord-youtube.git' $flowDir $FlowsealRef

    $flowBin = Join-Path $flowDir 'bin'
    $must = @('winws.exe', 'cygwin1.dll', 'WinDivert.dll', 'WinDivert64.sys')
    foreach ($f in $must) {
        $p = Join-Path $flowBin $f
        if (-not (Test-Path $p)) { throw "у Flowseal нет обязательного файла bin\$f" }
        Copy-Item $p (Join-Path $binDir $f) -Force
        Write-Host "      bin\$f"
    }

    # фейки Flowseal
    $fakeCount = 0
    Get-ChildItem $flowBin -File -Filter '*.bin' | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $binDir $_.Name) -Force
        $fakeCount++
    }
    Write-Host "      bin\*.bin ($fakeCount файлов)"

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

    # ---------------------------------------------------------------- zapret2
    #
    # v2 ставим из snanvl: у него свой winws2.exe и СВОЙ lua/ с blobs.lua.
    # lua из бандла НЕ подходит — там нет blobs.lua, и версии zapret-lib.lua
    # отличаются (96453 против 93714 байт). NFQWS2_COMPAT_VER связывает
    # exe и скрипты жёстко, смешивать нельзя.
    Write-Host "[2/5] zapret2 ($Zapret2Ref) ..."
    $z2Dir = Join-Path $temp 'zapret2'
    $z2Ok = $true
    try {
        CloneRepo 'https://github.com/SnAnVl/zapret-discord-youtube.git' $z2Dir $Zapret2Ref
    } catch {
        $z2Ok = $false
        Write-Host "      [warn] zapret2 недоступен — останется только v1" -ForegroundColor Yellow
    }

    $z2Version = ''
    if ($z2Ok) {
        $z2Bin = Join-Path $z2Dir 'bin'
        if (Test-Path (Join-Path $z2Bin 'winws2.exe')) {
            Copy-Item (Join-Path $z2Bin 'winws2.exe') (Join-Path $binDir 'winws2.exe') -Force
            Write-Host "      bin\winws2.exe"
        } else {
            Write-Host "      [warn] у zapret2 нет bin\winws2.exe" -ForegroundColor Yellow
        }

        # lua — целиком из zapret2
        $z2Lua = Join-Path $z2Dir 'lua'
        if (Test-Path $z2Lua) {
            $luaDir = Join-Path $engine 'lua'
            if (Test-Path $luaDir) { Remove-Item $luaDir -Recurse -Force }
            Copy-Item $z2Lua $luaDir -Recurse -Force
            $luaCount = (Get-ChildItem $luaDir -File).Count
            Write-Host "      lua\ ($luaCount файлов)"
        }

        # версия zapret2
        $z2VersionFile = Join-Path $z2Dir '.service\version.txt'
        if (Test-Path $z2VersionFile) {
            $z2Version = (Get-Content $z2VersionFile -Raw).Trim()
        }
    }

    # ---------------------------------------------------------------- bundle
    # Только дополнения, которых нет ни у Flowseal, ни у snanvl.
    Write-Host "[3/5] дополнения из zapret-win-bundle ($BundleRef) ..."
    $bundleDir = Join-Path $temp 'bundle'
    $bundleOk = $true
    try {
        CloneRepo 'https://github.com/bol-van/zapret-win-bundle.git' $bundleDir $BundleRef
    } catch {
        $bundleOk = $false
        Write-Host "      [warn] бандл недоступен — обойдёмся без дополнений" -ForegroundColor Yellow
    }

    if ($bundleOk) {
        $winwsSrc = Join-Path $bundleDir 'zapret-winws'

        foreach ($f in @('elevator.exe', 'killall.exe', 'windivert_delete.cmd', 'enable_timestamps.cmd', '_CMD_ADMIN.cmd')) {
            $p = Join-Path $winwsSrc $f
            if (Test-Path $p) { Copy-Item $p (Join-Path $binDir $f) -Force }
        }

        $filterSrc = Join-Path $winwsSrc 'windivert.filter'
        if (Test-Path $filterSrc) {
            Copy-Item $filterSrc (Join-Path $binDir 'windivert.filter') -Recurse -Force
            Write-Host "      bin\windivert.filter\"
        }

        # фейки бандла — только те, которых нет
        $filesSrc = Join-Path $winwsSrc 'files'
        if (Test-Path $filesSrc) {
            $added = 0
            Get-ChildItem $filesSrc -File | ForEach-Object {
                $dest = Join-Path $binDir $_.Name
                if (-not (Test-Path $dest)) { Copy-Item $_.FullName $dest -Force; $added++ }
            }
            if ($added -gt 0) { Write-Host "      bin\*.bin (+$added из бандла)" }
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
    $z2Commit     = ''
    if ($z2Ok) { $z2Commit = (& git -C $z2Dir rev-parse --short HEAD 2>$null) }

    $manifest = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('o')
        bundle      = @{ repo = 'bol-van/zapret-win-bundle'; ref = $BundleRef; commit = "$bundleCommit" }
        v1          = @{ repo = 'Flowseal/zapret-discord-youtube'; ref = $FlowsealRef; commit = "$flowCommit"; version = $flowVersion }
        v2          = @{ repo = 'SnAnVl/zapret-discord-youtube'; ref = $Zapret2Ref; commit = "$z2Commit"; version = $z2Version }
        fileCount   = $entries.Count
        files       = $entries
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $engine 'manifest.json') -Encoding UTF8

    # ---------------------------------------------------------------- version
    Set-Content -Path (Join-Path $engine 'version.txt') -Value $flowVersion -Encoding ASCII -NoNewline
    if ($z2Version.Length -gt 0) {
        Set-Content -Path (Join-Path $engine 'version2.txt') -Value $z2Version -Encoding ASCII -NoNewline
    }
    Write-Host "[5/5] готово."

    $binSize  = [int](((Get-ChildItem $binDir  -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB)
    $listSize = [int](((Get-ChildItem $listDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB)
    Write-Host ""
    Write-Host "  v1 Flowseal     : $flowVersion ($flowCommit)"
    if ($z2Version.Length -gt 0) {
        Write-Host "  v2 zapret2      : $z2Version ($z2Commit)"
    } else {
        Write-Host "  v2 zapret2      : не установлен"
    }
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
