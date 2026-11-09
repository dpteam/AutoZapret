# Упаковка APK для раздачи: переподпись всеми схемами + контрольная сумма.
#
# Зачем переподписывать после сборки.
#
# 1. .NET подписывает только схемой v3. По спецификации этого достаточно
#    (minSdk 29), но установщики части прошивок (старые MIUI, EMUI, сторонние
#    файловые менеджеры) с v3-only не справляются и выдают «пакет недействителен
#    (например, повреждён)» — человек читает это как «файл битый» и не понимает,
#    что дело в подписи. v1 и v2 не стоят ничего и снимают этот класс отказов.
#
# 2. SHA-256 рядом с файлом. APK на 100 МБ пересылается мессенджерами, и
#    оборванная загрузка выглядит точно так же, как «пакет недействителен».
#    Сверка хеша отвечает на вопрос «файл доехал целым?» за секунду.
#
# Ключ. По умолчанию берём тот же debug-keystore, которым подписывает .NET
# (LOCALAPPDATA\Xamarin\Mono for Android\debug.keystore) — сертификат совпадает,
# поэтому установка поверх предыдущей версии не требует удаления.
# Для релиза задайте AUTOZAPRET_KEYSTORE / AUTOZAPRET_KS_PASS / AUTOZAPRET_KEY_ALIAS.

param(
    [string]$Apk = "",
    [string]$OutDir = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$android = Join-Path $root "android"
$project = Join-Path $android "AutoZapret.Android"

function Fail($message) {
    Write-Host "[FAIL] $message" -ForegroundColor Red
    exit 1
}

function Find-BuildTool($name) {
    $sdk = $env:ANDROID_HOME
    if (-not $sdk -or -not (Test-Path $sdk)) { $sdk = "C:\Android\sdk" }

    $buildTools = Join-Path $sdk "build-tools"
    if (-not (Test-Path $buildTools)) { Fail "build-tools не найден в $sdk" }

    $latest = Get-ChildItem $buildTools -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $path = Join-Path $latest.FullName $name

    if (-not (Test-Path $path)) { Fail "$name не найден в $($latest.FullName)" }

    return $path
}

# ------------------------------------------------------------------ сборка

if (-not $NoBuild) {
    Write-Host "== сборка APK =="
    Push-Location $android
    try {
        & cmd /c "build.cmd" | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "build.cmd завершился с кодом $LASTEXITCODE" }
    } finally { Pop-Location }
}

if (-not $Apk) {
    $Apk = Join-Path $project "bin\Debug\net9.0-android\ru.autozapret.android-Signed.apk"
}

if (-not (Test-Path $Apk)) { Fail "APK не найден: $Apk" }

if (-not $OutDir) { $OutDir = Join-Path $android "dist" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# ------------------------------------------------------------------ подпись

$apksigner = Find-BuildTool "apksigner.bat"

$keystore = $env:AUTOZAPRET_KEYSTORE
$storePass = $env:AUTOZAPRET_KS_PASS
$alias = $env:AUTOZAPRET_KEY_ALIAS

if (-not $keystore) {
    $keystore = Join-Path $env:LOCALAPPDATA "Xamarin\Mono for Android\debug.keystore"
    if (-not $storePass) { $storePass = "android" }
    if (-not $alias) { $alias = "androiddebugkey" }
}

if (-not (Test-Path $keystore)) { Fail "keystore не найден: $keystore" }

$version = (Get-Item $Apk).VersionInfo.FileVersion
if (-not $version) { $version = "dev" }

$out = Join-Path $OutDir "autozapret-android-universal.apk"

Write-Host "== переподпись (v1 + v2 + v3) =="
Write-Host "  keystore: $keystore"
Write-Host "  alias:    $alias"

& $apksigner sign `
    --v1-signing-enabled true `
    --v2-signing-enabled true `
    --v3-signing-enabled true `
    --ks $keystore `
    --ks-key-alias $alias `
    --ks-pass "pass:$storePass" `
    --key-pass "pass:$storePass" `
    --out $out `
    $Apk

if ($LASTEXITCODE -ne 0) { Fail "apksigner завершился с кодом $LASTEXITCODE" }

# ------------------------------------------------------------------ проверка

Write-Host ""
Write-Host "== проверка =="

$verify = & $apksigner verify --verbose $out 2>&1
$text = $verify -join "`n"

if ($text -notmatch "Verifies") { Fail "APK не проходит проверку подписи" }

foreach ($scheme in @("v1", "v2", "v3")) {
    if ($text -match "Verified using $scheme scheme.*: true") {
        Write-Host "  $scheme : ok"
    } else {
        Write-Host "  $scheme : НЕТ" -ForegroundColor Yellow
    }
}

$aapt = Find-BuildTool "aapt2.exe"
$badging = (& $aapt dump badging $out 2>&1) -join "`n"

$native = "?"
if ($badging -match "native-code: '([^']*)'") { $native = $Matches[1] }
$minSdk = "?"
if ($badging -match "minSdkVersion:'(\d+)'") { $minSdk = $Matches[1] }

Write-Host "  ABI      : $native"
Write-Host "  minSdk   : $minSdk"

# Проверяем, что нативная библиотека реально внутри: без неё VPN падает
# на первом вызове hev, и это выглядит как «приложение вылетело».
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($out)
try {
    $libs = @($zip.Entries | Where-Object { $_.FullName -like "*libhev-socks5-tunnel.so" })

    if ($libs.Count -eq 0) { Fail "libhev-socks5-tunnel.so отсутствует внутри APK" }

    $abis = $libs | ForEach-Object { ($_.FullName -split '/')[1] } | Sort-Object -Unique
    Write-Host "  hev      : $($abis -join ', ')"
} finally { $zip.Dispose() }

# ------------------------------------------------------------------ хеш

$hash = (Get-FileHash $out -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $out).Length / 1MB, 1)

$sumFile = Join-Path $OutDir "autozapret-android-universal.apk.sha256"
"$($hash.ToLower())  autozapret-android-universal.apk" |
    Out-File -FilePath $sumFile -Encoding ASCII

Write-Host ""
Write-Host "== готово =="
Write-Host "  файл  : $out"
Write-Host "  размер: $sizeMb МБ"
Write-Host "  sha256: $hash"
Write-Host "  сумма : $sumFile"
