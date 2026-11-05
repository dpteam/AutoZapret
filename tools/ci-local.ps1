# Локальный CI: собирает всё, что обычно делает GitHub Actions, и кладёт
# результат в artifacts/. Нужен, когда Actions недоступны (очередь, деградация)
# и просто для быстрой проверки перед коммитом.
#
#   powershell -ExecutionPolicy Bypass -File tools\ci-local.ps1
#   powershell -ExecutionPolicy Bypass -File tools\ci-local.ps1 -Publish
#
# -Publish собирает self-contained single-file (~48 МБ) и кладёт в пакет его.
# Без флага пакет собирается из bin\ (framework-dependent) — быстрее, но
# требует .NET на машине.
#
# Код возврата: 0 — всё зелёное, 1 — есть провал.

[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$SkipEngine,
    [string]$Version = ""
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $artifacts "ci-$stamp.log"

if (-not $Version) {
    try {
        $csproj = [xml](Get-Content (Join-Path $root 'AutoZapret.csproj') -Raw -Encoding UTF8)
        $Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } |
                    Select-Object -First 1).Version
    } catch { }

    if (-not $Version) { $Version = '0.0.0' }
}

$sha = 'nogit'
try {
    $sha = (& git rev-parse --short HEAD 2>$null)
    if (-not $sha) { $sha = 'nogit' }
} catch { }

$results = New-Object System.Collections.Generic.List[string]
$failed = 0

function Section([string]$title) {
    Write-Host ""
    Write-Host "== $title ==" -ForegroundColor Cyan
    "`r`n== $title ==" | Out-File -FilePath $log -Append -Encoding utf8
}

function Record([string]$name, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
    $line = "{0} {1} {2}" -f $mark, $name, $detail

    if ($ok) { Write-Host "  $line" -ForegroundColor Green }
    else { Write-Host "  $line" -ForegroundColor Red }

    $line | Out-File -FilePath $log -Append -Encoding utf8
    $results.Add($line)

    if (-not $ok) { $script:failed++ }
}

# Выполняет шаг и возвращает его код возврата, дублируя вывод в лог.
function Invoke-Step([string]$file, [string[]]$arguments) {
    $output = & $file @arguments 2>&1
    $code = $LASTEXITCODE

    $output | Out-Host
    $output | Out-File -FilePath $log -Append -Encoding utf8

    return $code
}

Write-Host "AutoZapret — локальный CI" -ForegroundColor Cyan
Write-Host "   версия : $Version"
Write-Host "   коммит : $sha"
Write-Host "   лог    : $log"

"AutoZapret local CI $stamp / version $Version / commit $sha" |
    Out-File -FilePath $log -Append -Encoding utf8

# ------------------------------------------------------------------ проверки

Section 'workflow-файлы'
$code = Invoke-Step 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tools\check-workflows.ps1')
Record 'workflow-файлы' ($code -eq 0) ''

Section 'кодировки и переводы строк'
$code = Invoke-Step 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tools\check-encoding.ps1')
Record 'кодировки' ($code -eq 0) ''

Section 'целостность UI'
if (Get-Command node -ErrorAction SilentlyContinue) {
    $code = Invoke-Step 'node' @('tools\check-ui-ids.js')
    Record 'разметка и app.js' ($code -eq 0) ''

    # Переменные, которые используются, но не объявлены в своей функции.
    # Под "use strict" это ReferenceError, и весь обработчик умирает молча:
    # так «Пересоздаю…» висело вечно, а статус DNS не обновлялся.
    $code = Invoke-Step 'node' @('tools\check-ui-vars.js')
    Record 'объявленные переменные UI' ($code -eq 0) ''

    $code = Invoke-Step 'node' @('tools\add-i18n-keys.js')
    Record 'словарь переводов' ($code -eq 0) ''
} else {
    Write-Host '  [skip] node не найден — проверки UI и переводов пропущены'
    Record 'проверки UI' $true 'skip: нет node'
}

# ------------------------------------------------------------------ поставка

Section 'поставка hosts и списков'

# Вшитые наборы hosts обязаны быть hosts, а не HTML-страницей.
#
# Это уже случалось: скачали «hosts» с сайта, а пришла HTML-заглушка,
# и она уехала в поставку. Валидатор в HostsFixes.Download() её бы
# отбросил, но файл в assets кладут руками — значит проверять надо здесь.
$hostsProblems = @()

foreach ($file in Get-ChildItem (Join-Path $root 'assets\hosts') -Filter '*.txt' -ErrorAction SilentlyContinue) {
    $text = [System.IO.File]::ReadAllText($file.FullName)

    if ($text.TrimStart().StartsWith('<')) {
        $hostsProblems += "$($file.Name): HTML вместо hosts"
        continue
    }

    $entries = @($text -split "`n" | Where-Object { $_ -match '^\s*\d{1,3}(\.\d{1,3}){3}\s+\S' })
    if ($entries.Count -lt 3) {
        $hostsProblems += "$($file.Name): записей $($entries.Count), похоже на обрезанный файл"
    }
}

foreach ($p in $hostsProblems) { Write-Host "  $p" -ForegroundColor Red }

Record 'поставка hosts' ($hostsProblems.Count -eq 0) ("$($hostsProblems.Count) проблем")

# --------------------------------------------------------------------- сборка

Section 'сборка'
$buildOutput = & cmd /c 'build.cmd' 2>&1
$code = $LASTEXITCODE
$buildOutput | Out-Host
$buildOutput | Out-File -FilePath $log -Append -Encoding utf8

$warnings = @($buildOutput | Select-String -Pattern 'warning CS|warning CA|warning IL' |
              ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)

Record 'build.cmd' ($code -eq 0) ''
Record 'без предупреждений' ($warnings.Count -eq 0) ("$($warnings.Count) шт.")

if ($code -ne 0) {
    Write-Host ""
    Write-Host 'Сборка не прошла — дальше проверять нечего.' -ForegroundColor Red
    $results | Out-File -FilePath (Join-Path $artifacts 'last-run.txt') -Encoding utf8
    exit 1
}

# ------------------------------------------------------------------- selftest

$exe = Join-Path $root 'bin\AutoZapret.exe'
$engineReady = Test-Path (Join-Path $root 'engine\bin\winws.exe')

Section 'selftest'
Push-Location (Join-Path $root 'bin')
try {
    & $exe '--selftest' | Out-Null
    $code = $LASTEXITCODE
} finally { Pop-Location }

$selftestFile = Join-Path $root 'bin\selftest.txt'
if (Test-Path $selftestFile) {
    Get-Content $selftestFile -Encoding UTF8 | Out-File -FilePath $log -Append -Encoding utf8
    Get-Content $selftestFile -Encoding UTF8 | Select-String -Pattern 'RESULT|\[FAIL\]' | ForEach-Object { Write-Host ("  " + $_.Line) }
}
Record 'selftest' ($code -eq 0) ''

# Режим службы: тело не должно зависать. Если `--service` (без действия) не
# выйдет за пару секунд, SCM убьёт службу с ошибкой 1053 — и человек увидит
# «служба не запустилась» без причины. Проверяем здесь, а не на живой машине.
Section 'режим службы'
$svcCode = -1
$svcWatch = [System.Diagnostics.Stopwatch]::StartNew()

Push-Location (Join-Path $root 'bin')
try {
    & $exe '--service' | Out-Null
    $svcCode = $LASTEXITCODE
} finally { Pop-Location }

$svcWatch.Stop()

# 5 = «это тело службы, запускайте через SCM» — штатный ответ при ручном запуске
$svcOk = ($svcCode -eq 5) -and ($svcWatch.Elapsed.TotalSeconds -lt 10)
Record 'режим службы' $svcOk ("код $svcCode, $([int]$svcWatch.Elapsed.TotalSeconds) с")

Push-Location (Join-Path $root 'bin')
try {
    & $exe '--service' 'status' | Out-Null
    $svcStatus = $LASTEXITCODE
} finally { Pop-Location }

Record 'служба: состояние' ($svcStatus -eq 0) ''

if ($engineReady -and -not $SkipEngine) {
    Section 'selftest (строгий, с движком)'
    Push-Location (Join-Path $root 'bin')
    try {
        & $exe '--selftest' '--require-engine' | Out-Null
        $code = $LASTEXITCODE
    } finally { Pop-Location }

    if (Test-Path $selftestFile) {
        Get-Content $selftestFile -Encoding UTF8 | Out-File -FilePath $log -Append -Encoding utf8
        Get-Content $selftestFile -Encoding UTF8 | Select-String -Pattern 'RESULT|\[FAIL\]' | ForEach-Object { Write-Host ("  " + $_.Line) }
    }
    Record 'selftest (строгий)' ($code -eq 0) ''
} else {
    Record 'selftest (строгий)' $true 'skip: движок не установлен'
}

# --------------------------------------------------------------------- отчёт

Section 'расширенный отчёт'
$diagDir = Join-Path $artifacts 'diag'
New-Item -ItemType Directory -Path $diagDir -Force | Out-Null

Push-Location (Join-Path $root 'bin')
try {
    $diagOutput = & $exe '--diag-zip' '--deep' $diagDir 2>&1
    $code = $LASTEXITCODE
} finally { Pop-Location }

$diagOutput | Out-Host
$diagOutput | Out-File -FilePath $log -Append -Encoding utf8
Record 'расширенный отчёт' ($code -eq 0) ''

# --------------------------------------------------------------------- пакет

Section 'пакет'

$stageDir = Join-Path $artifacts "stage-$stamp"

if ($Publish) {
    Write-Host '  publish.cmd (self-contained, до минуты)…'
    $publishOutput = & cmd /c 'publish.cmd' 2>&1
    $code = $LASTEXITCODE
    $publishOutput | Out-Host
    $publishOutput | Out-File -FilePath $log -Append -Encoding utf8
    Record 'publish.cmd' ($code -eq 0) ''
    $source = Join-Path $root 'publish'
} else {
    $source = Join-Path $root 'bin'
    Write-Host '  без -Publish: пакет из bin\ (нужен установленный .NET)'
}

# Раскладываем только то, что нужно пользователю. Списком, а не «всё из bin»:
# в bin\ лежат obj-хвосты, логи и тестовые файлы, которым в пакете не место.
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

function Copy-IfExists([string]$relative, [string]$target = '') {
    $from = Join-Path $source $relative
    if (-not (Test-Path $from)) { return $false }

    $to = if ($target) { Join-Path $stageDir $target } else { Join-Path $stageDir $relative }
    $parent = Split-Path -Parent $to
    if ($parent -and -not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    Copy-Item $from $to -Recurse -Force
    return $true
}

$packaged = @()
if (Copy-IfExists 'AutoZapret.exe') { $packaged += 'AutoZapret.exe' }
if (Copy-IfExists 'WebView2Loader.dll') { $packaged += 'WebView2Loader.dll' }

# framework-dependent сборка без этих файлов не запустится
foreach ($f in @('AutoZapret.dll', 'AutoZapret.runtimeconfig.json', 'AutoZapret.deps.json')) {
    if (Copy-IfExists $f) { $packaged += $f }
}

foreach ($f in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll')) {
    if (Copy-IfExists $f) { $packaged += $f }
}

if (Copy-IfExists 'assets' 'assets') { $packaged += 'assets\' }

# tools\, licenses\, README и LICENSE лежат в корне репозитория, а не в bin\,
# поэтому копируем их не из $source. Иначе пакет уезжает без fetch-engine.ps1,
# на который ссылается README.
foreach ($name in @('tools', 'licenses')) {
    $from = Join-Path $root $name
    if (-not (Test-Path $from)) { continue }

    Copy-Item $from (Join-Path $stageDir $name) -Recurse -Force
    $packaged += "$name\"
}
if (-not $SkipEngine -and (Copy-IfExists 'engine' 'engine')) { $packaged += 'engine\' }

if (Test-Path (Join-Path $root 'README.md')) {
    Copy-Item (Join-Path $root 'README.md') $stageDir -Force
    $packaged += 'README.md'
}

if (Test-Path (Join-Path $root 'LICENSE')) {
    Copy-Item (Join-Path $root 'LICENSE') $stageDir -Force
    $packaged += 'LICENSE'
}

# Мусор от апдейтеров списков: .bak, .backup, .download.bak, .upd.
# Пользователю это не нужно, а в пакете выглядит как «аварийное состояние».
$junk = @(Get-ChildItem $stageDir -Recurse -File |
          Where-Object { $_.Name -match '\.(bak|backup|upd)$' -or $_.Name -match '\.download\.bak$' })

foreach ($f in $junk) {
    Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
}

if ($junk.Count -gt 0) {
    Write-Host ("  убрано мусорных файлов: " + $junk.Count)
    "  убрано мусорных файлов: $($junk.Count)" | Out-File -FilePath $log -Append -Encoding utf8
}

$zipName = "autozapret-$Version-$sha.zip"
$zipPath = Join-Path $artifacts $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue

$zipMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ("  $zipName  ($zipMb МБ)")
"  состав: " + ($packaged -join ', ') | Out-File -FilePath $log -Append -Encoding utf8

Record 'пакет' $true ("$zipName, $zipMb МБ")

# проверяем сам пакет: движок внутри, мусора нет
if (Get-Command node -ErrorAction SilentlyContinue) {
    Section 'содержимое пакета'
    $code = Invoke-Step 'node' @('tools\check-artifact.js')
    Record 'содержимое пакета' ($code -eq 0) ''
}

# selftest-отчёт и лог кладём рядом с пакетом
if (Test-Path $selftestFile) {
    Copy-Item $selftestFile (Join-Path $artifacts "selftest-$stamp.txt") -Force
}

# --------------------------------------------------------------------- итог

Section 'итог'

$summary = Join-Path $artifacts 'last-run.txt'
$header = "AutoZapret CI $stamp / $Version / $sha / publish=$Publish"
$header | Out-File -FilePath $summary -Encoding utf8
$results | Out-File -FilePath $summary -Append -Encoding utf8

Write-Host ""
foreach ($line in $results) {
    $color = if ($line.StartsWith('ok')) { 'Green' } else { 'Red' }
    Write-Host "  $line" -ForegroundColor $color
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "ВСЁ ЗЕЛЁНОЕ ($($results.Count) проверок)" -ForegroundColor Green
    Write-Host "   пакет: $zipPath"
    Write-Host "   лог  : $log"
    exit 0
}

Write-Host "ПРОВАЛОВ: $failed из $($results.Count)" -ForegroundColor Red
Write-Host "   лог: $log"
exit 1