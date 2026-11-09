# Отрисовка PNG-иконок приложения из того же знака, что и вектор.
#
# Зачем это нужно, если есть adaptive-icon. Адаптивная иконка (mipmap-anydpi-v26)
# применяется на Android 8+, но PNG-фолбэки в mipmap-mdpi..xxxhdpi остаются в
# APK и используются частью системы: списком приложений в настройках, экраном
# недавних задач, некоторыми лаунчерами. В шаблоне .NET там лежит робот, и
# именно его видит человек, хотя в лончере уже наш значок.
#
# Рисуем тот же знак: стрелка проходит сквозь разбитую стену.

param(
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$res = Join-Path $root "android\AutoZapret.Android\Resources"

if (-not $OutDir) { $OutDir = $res }

# Цвета — ровно те же, что в ic_launcher_foreground.xml и в палитре приложения.
$bg = [System.Drawing.Color]::FromArgb(255, 0x13, 0x1A, 0x2E)
$wall = [System.Drawing.Color]::FromArgb(255, 0x4A, 0x58, 0x78)
$arrow = [System.Drawing.Color]::FromArgb(255, 0xF0, 0xB4, 0x29)

function New-Icon([int]$size, [bool]$withBackground, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    if ($withBackground) {
        $g.Clear($bg)
    } else {
        $g.Clear([System.Drawing.Color]::Transparent)
    }

    # Координаты заданы в viewport 108 и масштабируются под размер картинки.
    $k = $size / 108.0

    $wallBrush = New-Object System.Drawing.SolidBrush($wall)
    $arrowBrush = New-Object System.Drawing.SolidBrush($arrow)

    # верхний обломок стены
    $g.FillPolygon($wallBrush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF((41 * $k), (26 * $k))),
        (New-Object System.Drawing.PointF((50 * $k), (26 * $k))),
        (New-Object System.Drawing.PointF((50 * $k), (41 * $k))),
        (New-Object System.Drawing.PointF((41 * $k), (46 * $k)))
    ))

    # нижний обломок
    $g.FillPolygon($wallBrush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF((41 * $k), (62 * $k))),
        (New-Object System.Drawing.PointF((50 * $k), (57 * $k))),
        (New-Object System.Drawing.PointF((50 * $k), (82 * $k))),
        (New-Object System.Drawing.PointF((41 * $k), (82 * $k)))
    ))

    # древко стрелки
    $g.FillRectangle($arrowBrush, (19 * $k), (49 * $k), (48 * $k), (10 * $k))

    # наконечник
    $g.FillPolygon($arrowBrush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF((65 * $k), (39 * $k))),
        (New-Object System.Drawing.PointF((91 * $k), (54 * $k))),
        (New-Object System.Drawing.PointF((65 * $k), (69 * $k)))
    ))

    $wallBrush.Dispose()
    $arrowBrush.Dispose()
    $g.Dispose()

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# Плотность -> (размер обычной иконки, размер foreground для adaptive)
$densities = @{
    "mdpi"    = @(48, 108)
    "hdpi"    = @(72, 162)
    "xhdpi"   = @(96, 216)
    "xxhdpi"  = @(144, 324)
    "xxxhdpi" = @(192, 432)
}

$made = 0

foreach ($name in $densities.Keys) {
    $dir = Join-Path $OutDir "mipmap-$name"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null

    $sizes = $densities[$name]

    # legacy-иконка: с фоном, скругления добавит лаунчер
    New-Icon $sizes[0] $true (Join-Path $dir "appicon.png")

    # foreground для adaptive: только знак, фон задан цветом в resources
    New-Icon $sizes[1] $false (Join-Path $dir "appicon_foreground.png")

    # round-иконка: тот же знак с фоном
    New-Icon $sizes[0] $true (Join-Path $dir "appicon_round.png")

    # appicon_background.png больше не нужен: фон задан цветом. Удаляем
    # робота, который остался от шаблона .NET.
    $old = Join-Path $dir "appicon_background.png"
    if (Test-Path $old) { Remove-Item $old -Force }

    $made++
}

Write-Host "готово: $made плотностей"
Write-Host "  каталог: $OutDir"

# Контроль: в APK не должно остаться ни одного файла робота.
Get-ChildItem $OutDir -Recurse -Filter "appicon_background.png" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  ОСТАЛОСЬ: $($_.FullName)" }
