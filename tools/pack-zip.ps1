# Упаковка каталога в zip с ПРАВИЛЬНЫМИ разделителями.
#
# ЗАЧЕМ НЕ Compress-Archive
#   PowerShell 5.1 (им работает ci-local.ps1) пишет в архив имена с ОБРАТНЫМИ
#   слешами: `assets\fixes\packs.json`. Спецификация zip требует прямые, и
#   это не придирка: 7-Zip и WinRAR считают обратный слеш частью ИМЕНИ файла,
#   а не разделителем. После распаковки получается один файл со странным
#   именем вместо дерева каталогов, и программа не находит ни исправления, ни
#   hosts, ни фейки. Снаружи это «в паке пусто».
#
#   Проверено на PS 5.1.20348: и `-Path (dir\*)`, и `-Path dir` дают
#   `assets\fixes\packs.json`. `[ZipFile]::CreateFromDirectory` — тоже.
#   Исправлено только в PowerShell 7, поэтому полагаться на него нельзя.
#
# ЧТО ДЕЛАЕМ
#   Собираем записи вручную через System.IO.Compression и сами пишем имя с
#   прямыми слешами. Работает одинаково на PS 5.1 и 7.
#
# ЗАПУСК
#   powershell -File tools\pack-zip.ps1 -Source stage -Destination out.zip
#   powershell -File tools\pack-zip.ps1 -Source stage -Destination out.zip -Prefix AutoZapret
#
# -Prefix кладёт всё внутрь папки: распаковка не рассыпает файлы по текущему
# каталогу, а создаёт один подкаталог.

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$Prefix = ""
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $Source)) { throw "pack-zip: нет каталога $Source" }

$sourceFull = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\', '/')

# Пустой каталог — это ошибка, а не пустой архив: такой пакет бесполезен,
# но выглядит как успешной сборкой.
#
# Имена файлов берём относительно текущего каталога (Push-Location +
# Resolve-Path -Relative), а не вычитанием длины префикса: `Resolve-Path`
# может вернуть короткий путь (C:\Users\DARTPO~1\...), а `Get-ChildItem` —
# длинный (C:\Users\DartPower\...), и вычитание строк даёт мусор вида
# `e/assets/...`.
Push-Location -LiteralPath $sourceFull

try {
    $relative = @(Get-ChildItem -Recurse -File | ForEach-Object {
        $r = Resolve-Path -LiteralPath $_.FullName -Relative
        $r -replace '^\.\\', '' -replace '^\./', ''
    })
}
finally {
    Pop-Location
}

if ($relative.Count -eq 0) { throw "pack-zip: $Source пуст — упаковывать нечего" }

$files = @(Get-ChildItem -LiteralPath $sourceFull -Recurse -File)

$destFull = [System.IO.Path]::GetFullPath($Destination)

if (Test-Path $destFull) { Remove-Item $destFull -Force }

# Каталог назначения должен существовать: ZipFile.Open не создаёт его.
$destDir = [System.IO.Path]::GetDirectoryName($destFull)
if ($destDir -and -not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

$zip = [System.IO.Compression.ZipFile]::Open($destFull, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    for ($i = 0; $i -lt $files.Count; $i++) {
        $file = $files[$i]

        # ВСЁ, ради чего этот файл: прямые слеши в имени записи.
        $entryName = $relative[$i].Replace('\', '/')

        if ($Prefix) { $entryName = $Prefix.Trim('/') + '/' + $entryName }

        $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)

        $stream = $entry.Open()
        try {
            $input = [System.IO.File]::OpenRead($file.FullName)
            try { $input.CopyTo($stream) } finally { $input.Dispose() }
        } finally {
            $stream.Dispose()
        }
    }
} finally {
    $zip.Dispose()
}

# ---- проверка результата
#
# Проверяем то, что легко сломать снова: разделители и непустоту. Архив с
# обратными слешами внешне выглядит нормальным, а распаковывается в мусор,
# поэтому проверка обязательна здесь же, а не «когда-нибудь в CI».
$check = [System.IO.Compression.ZipFile]::OpenRead($destFull)
try {
    $bad = 0
    $total = 0

    foreach ($entry in $check.Entries) {
        $total++
        if ($entry.FullName.Contains('\')) { $bad++ }
    }

    if ($bad -gt 0) {
        throw "pack-zip: $bad из $total записей с обратными слешами — такой архив распакуется в мусор"
    }

    Write-Host ("pack-zip: $total файлов, разделители в порядке -> " + (Split-Path -Leaf $destFull))
} finally {
    $check.Dispose()
}
