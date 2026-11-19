# Локальная проверка шага "Assemble portable package" из release.yml.
#
# ЗАЧЕМ ОТДЕЛЬНЫЙ СКРИПТ
#   Первый релиз упал именно здесь: `Compress-Archive -Path "$stage/*"`
#   раскладывает содержимое папки в корень архива, а проверка ждёт префикс
#   `AutoZapret/`. Пакет был собран (47 МБ), но распаковался бы «во все
#   стороны», и проверка это поймала — уже на CI, после тега.
#
#   Повторять такое на каждом релизе дорого: тег уже отправлен, релиз не
#   создан. Поэтому раскладка и упаковка прогоняются локально до тега.
#
# Запуск: powershell -File tools\check-release-package.ps1
# Требует: publish\ (создаётся publish.cmd)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$publish = Join-Path $root 'publish'

if (-not (Test-Path (Join-Path $publish 'AutoZapret.exe'))) {
    throw "нет publish\AutoZapret.exe - сначала запустите publish.cmd"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('az-rel-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'stage\AutoZapret'

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Copy-Item (Join-Path $publish 'AutoZapret.exe')     $stage
    Copy-Item (Join-Path $publish 'WebView2Loader.dll') $stage
    Copy-Item (Join-Path $publish 'assets')             $stage -Recurse -Force

    if (Test-Path (Join-Path $publish 'engine\bin\winws.exe')) {
        New-Item -ItemType Directory -Path (Join-Path $stage 'engine') -Force | Out-Null
        robocopy (Join-Path $publish 'engine') (Join-Path $stage 'engine') /E /XO /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "engine copy failed with $LASTEXITCODE" }
        $global:LASTEXITCODE = 0
    } else {
        throw "нет publish\engine\bin\winws.exe - пакет без движка бесполезен"
    }

    New-Item -ItemType Directory -Path (Join-Path $stage 'tools') -Force | Out-Null
    Copy-Item 'tools\fetch-engine.ps1' (Join-Path $stage 'tools') -Force
    Copy-Item 'LICENSE'  $stage -Force
    Copy-Item 'licenses' $stage -Recurse -Force
    Copy-Item 'README.md' $stage -Force

    # Имя файла инструкции — латиницей: кириллица в имени внутри zip ломается
    # на части распаковщиков, а «прочти меня» нужно прочитать обязательно.
    $readme = @(
        'AutoZapret - portable',
        '',
        '1. Распакуйте в папку без пробелов (например C:\AutoZapret).',
        '2. Правый клик по AutoZapret.exe -> Запуск от имени администратора.',
        '3. Нажмите "Включить обход".',
        '',
        '.NET устанавливать не нужно: он внутри exe.',
        'Нужен Microsoft Edge WebView2 Runtime (в Windows 11 есть, для Windows 10:',
        'https://go.microsoft.com/fwlink/p/?LinkId=2124703).'
    ) -join "`r`n"

    Set-Content -Path (Join-Path $stage 'READ-ME-FIRST.txt') -Value $readme -Encoding UTF8

    $zipPath = Join-Path $work 'AutoZapret-portable.zip'

    # ВАЖНО: -Path $stage (папка целиком), а не "$stage/*".
    # Со звёздочкой содержимое ложится в корень архива и проверка ниже падает.
    Compress-Archive -Path $stage -DestinationPath $zipPath -Force

    $mb = [int]((Get-Item $zipPath).Length / 1MB)
    Write-Host "package: $mb MB"
    if ($mb -lt 20) { throw "package too small ($mb MB)" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)

    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }

        $need = @(
            'AutoZapret/AutoZapret.exe',
            'AutoZapret/WebView2Loader.dll',
            'AutoZapret/assets/strategies/strategies.v1.json',
            'AutoZapret/assets/fakes/fakes.json',
            'AutoZapret/engine/bin/winws.exe',
            'AutoZapret/engine/bin/WinDivert64.sys',
            'AutoZapret/tools/fetch-engine.ps1',
            'AutoZapret/LICENSE',
            'AutoZapret/licenses/zapret-MIT.txt',
            'AutoZapret/licenses/zapret-custom-binaries-MIT.txt',
            'AutoZapret/licenses/WinDivert-LGPLv3.txt'
        )

        $missing = @()

        foreach ($n in $need) {
            if (-not ($names -contains $n)) { $missing += $n }
        }

        if ($missing.Count -gt 0) {
            Write-Host '[FAIL] в архиве нет:'
            $missing | ForEach-Object { Write-Host "       $_" }
            throw "package layout is wrong"
        }

        Write-Host "содержимое проверено: $($names.Count) файлов, все обязательные на месте"
    } finally { $zip.Dispose() }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
