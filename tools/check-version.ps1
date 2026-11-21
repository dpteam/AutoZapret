# Сверка версии: csproj против последнего тега.
#
#   powershell -File tools\check-version.ps1
#   powershell -File tools\check-version.ps1 -Strict    # расхождение = провал
#
# Зачем. Версия в exe берётся из csproj при обычной сборке и из тега при релизе.
# Если они разошлись, получается вечный цикл: собран v0.3.0, а exe рапортует
# 0.2.0, самообновление предлагает 0.3.0, ставит и снова видит 0.2.0.
# Ловить надо до тега, а не после публикации.
#
# Правило простое: csproj должен быть НЕ НИЖЕ последнего тега. Выше — нормально:
# тег ставят уже после того, как версию подняли в коде.

param(
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'AutoZapret.csproj'

if (-not (Test-Path $csproj)) {
    Write-Host "[FAIL] нет AutoZapret.csproj"
    exit 1
}

$match = Select-String -Path $csproj -Pattern '<Version>([0-9]+\.[0-9]+\.[0-9]+)' | Select-Object -First 1

if (-not $match) {
    Write-Host "[FAIL] в csproj нет <Version>"
    exit 1
}

$projectVersion = $match.Matches[0].Groups[1].Value

# Последний тег: если git недоступен (например, в архиве без .git), проверять нечего.
$tagVersion = ''

Push-Location $root
try {
    $tag = (git describe --tags --abbrev=0 2>$null | Select-Object -First 1)
} finally { Pop-Location }

if ($tag) {
    $tagVersion = $tag.TrimStart('v', 'V')
}

Write-Host ("csproj:        " + $projectVersion)
Write-Host ("последний тег: " + $(if ($tagVersion) { $tagVersion } else { 'нет' }))
Write-Host ''

if (-not $tagVersion) {
    Write-Host "[ok]   тегов нет - сверять нечего"
    exit 0
}

# Сравниваем как версии, а не как строки: 0.10.0 выше 0.9.0.
function Compare-Version([string]$a, [string]$b) {
    $pa = $a -split '\.'
    $pb = $b -split '\.'

    for ($i = 0; $i -lt 3; $i++) {
        $na = if ($i -lt $pa.Count) { [int]$pa[$i] } else { 0 }
        $nb = if ($i -lt $pb.Count) { [int]$pb[$i] } else { 0 }

        if ($na -gt $nb) { return 1 }
        if ($na -lt $nb) { return -1 }
    }

    return 0
}

$cmp = Compare-Version $projectVersion $tagVersion

if ($cmp -ge 0) {
    Write-Host "[ok]   версия в csproj не ниже тега"
    exit 0
}

Write-Host ("[FAIL] csproj " + $projectVersion + " НИЖЕ тега " + $tagVersion)
Write-Host '       Собранный exe будет рапортовать старую версию, и самообновление'
Write-Host '       зациклится: предлагает новую, ставит, снова видит старую.'
Write-Host '       Поднимите <Version> в AutoZapret.csproj.'

if ($Strict) { exit 1 }

# Без -Strict это предупреждение: локально тег может быть впереди работы в коде.
exit 0
