# Собирает assets\AutoZapret.ico из assets\logo.svg.
#
# ЗАЧЕМ ОТДЕЛЬНЫЙ ИНСТРУМЕНТ
#   Иконка exe должна быть .ico, а логотип — один векторный исходник. Рисовать
#   одно и то же дважды руками нельзя: расходится при первой же правке.
#
# ПОЧЕМУ ЧЕРЕЗ БРАУЗЕР, И ТОЛЬКО ОДИН РАЗ
#   В .NET нет встроенного рендера SVG. Edge/Chrome есть на каждой Windows 10+
#   и умеет headless-скриншот. Раньше скрипт запускал браузер на КАЖДЫЙ размер,
#   и на 128 px получался пустой файл (386 байт вместо ~2 КБ): headless-окно
#   такого размера иногда отдаёт пустой кадр. Поэтому рендерим один раз
#   крупно, а размеры получаем честным масштабированием System.Drawing.
#
# ФОРМАТ
#   Внутри .ico лежат PNG (Vista+), а не BMP: меньше и без возни с масками.
#   Размеры — 16/24/32/48/64/128/256. 16 и 24 нужны панели задач и списку
#   файлов, 256 — крупным значкам в проводнике.
#
# Запуск: powershell -File tools\make-ico.ps1
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$svg = Join-Path $root 'assets\logo.svg'
$ico = Join-Path $root 'assets\AutoZapret.ico'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('az-ico-' + [Guid]::NewGuid().ToString('N'))

if (-not (Test-Path $svg)) { throw "нет $svg" }

function Find-Browser {
    $candidates = @(
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )

    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    return $null
}

$browser = Find-Browser
if (-not $browser) { throw "не найден Edge или Chrome: без браузера SVG не отрисовать" }

Write-Host "браузер: $browser"

New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# Источник: 1024 px. Прозрачный фон — иконка должна ложиться на любой, а не
# тащить за собой тёмный квадрат (фон знака задан внутри SVG).
$source = 1024

$html = Join-Path $tmp 'icon.html'
$svgUri = 'file:///' + ($svg -replace '\\', '/')
@"
<!doctype html><html><head><meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}
img{display:block;width:${source}px;height:${source}px}</style></head>
<body><img src="$svgUri"></body></html>
"@ | Set-Content -Path $html -Encoding UTF8

$htmlUri = 'file:///' + ($html -replace '\\', '/')
$master = Join-Path $tmp 'master.png'

# Edge пишет диагностику в stderr, а PowerShell 5.1 при ErrorActionPreference=Stop
# считает любую запись в stderr ошибкой и валит скрипт. Глушим поток и смотрим
# только на файл.
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

try {
    & $browser --headless=new --disable-gpu --hide-scrollbars `
        --default-background-color=00000000 `
        --window-size="$source,$source" --screenshot="$master" $htmlUri 2>&1 | Out-Null
} finally {
    $ErrorActionPreference = $prev
}

if (-not (Test-Path $master)) { throw "не удалось отрисовать исходник ${source}px" }

# Проверка, что кадр не пустой: у прозрачной картинки с одним знаком
# PNG всё равно весит килобайты. 1 КБ — заведомо меньше любого осмысленного
# кадра и заведомо больше пустого.
$masterBytes = (Get-Item $master).Length
Write-Host ("  исходник: {0}px, {1} Б" -f $source, $masterBytes)

if ($masterBytes -lt 1024) { throw "исходный кадр подозрительно пуст ($masterBytes Б)" }

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()

$img = [System.Drawing.Image]::FromFile($master)

try {
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)

        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($img, 0, 0, $s, $s)
        } finally {
            $g.Dispose()
        }

        $path = Join-Path $tmp ("icon-$s.png")

        try { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }

        $bytes = [System.IO.File]::ReadAllBytes($path)

        $pngs += [pscustomobject]@{ Size = $s; Bytes = $bytes }
        Write-Host ("  {0}x{0}: {1} Б" -f $s, $bytes.Length)
    }
} finally {
    $img.Dispose()
}

# ---------------------------------------------------------------- упаковка ICO
#
# Заголовок: reserved(2) type(2) count(2).
# Запись на каждую картинку: width(1) height(1) colors(1) reserved(1)
# planes(2) bitcount(2) size(4) offset(4). Ноль в width/height означает 256.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([uint16]0)                  # reserved
$w.Write([uint16]1)                  # 1 = иконка
$w.Write([uint16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count

foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }

    $w.Write([byte]$dim)
    $w.Write([byte]$dim)
    $w.Write([byte]0)                # палитра не используется
    $w.Write([byte]0)
    $w.Write([uint16]1)              # planes
    $w.Write([uint16]32)             # бит на пиксель
    $w.Write([uint32]$p.Bytes.Length)
    $w.Write([uint32]$offset)

    $offset += $p.Bytes.Length
}

foreach ($p in $pngs) { $w.Write($p.Bytes) }

$w.Flush()
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())
$w.Dispose()
$out.Dispose()

Write-Host ("иконка: {0} ({1} Б)" -f $ico, (Get-Item $ico).Length)

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
