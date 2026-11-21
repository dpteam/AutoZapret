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

    # ---------------------------------------------------------------- Linux
    #
    # Установщик для Linux — это единственный путь, которым человек получает
    # программу, и он качает архив САМ. Поэтому проверяем то, что ломается
    # молча: определение версии и работоспособность скрипта.
    #
    # Первый прогон нашёл здесь настоящий баг: `/releases/latest` для проекта,
    # где все релизы помечены пререлизом, редиректит на общий список, а не на
    # тег. Установка у пользователя упала бы на «не смог узнать версию» либо
    # пошла бы качать `vlocation: https://...`.
    Write-Host ''
    Write-Host '== Linux: install.sh =='

    $installer = Join-Path $root 'linux\install.sh'

    if (-not (Test-Path $installer)) {
        throw 'нет linux\install.sh'
    }

    $text = Get-Content $installer -Raw

    # Только код, без комментариев: в комментарии `/releases/latest` упомянут
    # именно как неправильный, и проверка «есть ли строка» краснела бы на
    # объяснении. Это уже второй раз за день, когда поиск слов вместо
    # конструкций даёт ложное срабатывание.
    $code = ($text -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"

    if ($code -match '/releases/latest') {
        throw "linux\install.sh использует /releases/latest: для пререлизов (все релизы до 1.0.0) он не даёт тег. Нужен releases.atom"
    }

    if ($text -match "`r") {
        throw "linux\install.sh с CRLF: bash скажет `"`$'\r': command not found`""
    }

    Write-Host '  нет /releases/latest, переводы строк LF'

    # Синтаксис — только настоящим bash. `bash` из PATH на Windows — это
    # лончер WSL, он передаёт путь в Linux, где диска E: нет, и проверка
    # падала бы с «No such file» на правильном файле. Git for Windows несёт
    # обычный bash, который понимает пути Windows.
    $bashCandidates = @(
        (Join-Path $env:ProgramFiles 'Git\bin\bash.exe'),
        (Join-Path $env:ProgramFiles 'Git\usr\bin\bash.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe')
    )

    $bash = $bashCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($bash) {
        # Путь — в прямых слешах: для bash обратный слеш это escape.
        & $bash -n $installer.Replace('\', '/')

        if ($LASTEXITCODE -ne 0) { throw 'linux\install.sh: синтаксическая ошибка' }

        Write-Host "  синтаксис ok ($([System.IO.Path]::GetFileName($bash)))"
    } else {
        Write-Host '  [skip] Git for Windows не найден, синтаксис проверит CI на Linux'
    }

    # Определение версии — тем же кодом, что в скрипте, и по-настоящему.
    # Сеть может отсутствовать: это предупреждение, а не провал, потому что
    # пакет к этому моменту уже собран.
    Write-Host ''
    Write-Host '== Linux: определение последней версии =='

    try {
        $atom = Invoke-WebRequest -Uri 'https://github.com/dpteam/AutoZapret/releases.atom' `
                                  -UseBasicParsing -TimeoutSec 20

        $m = [regex]::Match($atom.Content, '/releases/tag/(v[0-9]+\.[0-9]+\.[0-9]+)')

        if (-not $m.Success) { throw 'в фиде нет ни одного тега версии' }

        Write-Host ("  последняя версия: " + $m.Groups[1].Value)

        # Версия в csproj не должна отставать от последнего тега — иначе
        # самообновление зациклится (это уже случалось, см. check-version.ps1).
        $csproj = Get-Content (Join-Path $root 'AutoZapret.csproj') -Raw
        $declared = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
        $tagged = $m.Groups[1].Value.TrimStart('v')

        if ([version]$declared -lt [version]$tagged) {
            throw "версия в csproj ($declared) ниже последнего тега ($tagged): самообновление зациклится"
        }

        Write-Host "  версия в csproj: $declared (не ниже тега)"
    } catch {
        Write-Host ("  [warn] сеть или GitHub недоступны: " + $_.Exception.Message)
    }

    # ---------------------------------------------------------------- сборка Linux
    #
    # Linux-пакет обязан собираться на ubuntu, а не на windows-раннере.
    #
    # Это не стилистика: `chmod +x` в Git for Windows эмулируется и в архив
    # не попадает. Релиз 0.3.1 вышел с правами 0644 на AutoZapret и nfqws —
    # человек распаковал и получил «Permission denied». Установщик это
    # маскировал (chmod после распаковки), но `--status` на распакованной
    # копии не работал.
    Write-Host ''
    Write-Host '== Linux: сборка пакета =='

    $workflowPath = Join-Path $root '.github\workflows\release.yml'
    $workflow = Get-Content $workflowPath -Raw

    if ($workflow -notmatch '(?m)^  linux:\s*$') {
        throw 'в release.yml нет отдельной работы linux'
    }

    if ($workflow -match '(?m)^\s+runs-on:\s*ubuntu' -and $workflow -notmatch '(?s)linux:\s*\n.*?runs-on:\s*ubuntu') {
        throw 'работа linux не на ubuntu: chmod +x там не работает'
    }

    if ($workflow -notmatch 'not executable after extraction') {
        throw 'в release.yml нет проверки бита исполнения внутри архива'
    }

    Write-Host '  отдельная работа linux на ubuntu, права внутри архива проверяются'
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
