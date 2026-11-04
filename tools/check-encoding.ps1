# Проверка кодировок и переводов строк по всему репозиторию.
#
# Зачем отдельным инструментом: именно эта категория ошибок не видна в диффе
# и не ловится компилятором, но ломает всё разом.
#
#   * .cmd/.bat с LF вместо CRLF — cmd.exe неверно разбирает блоки
#     if (...) else (...): скрипт обрывается на середине, «просто так»,
#     с кодом 255 и без внятного сообщения;
#   * русский текст в .cmd — cmd печатает байты как есть, и в консоли
#     получается «ਥ : ..» (UTF-8, прочитанный как OEM);
#   * .ps1 без BOM — Windows PowerShell 5.1 читает файл как ANSI и падает
#     на кириллице («Неожиданный токен }»);
#   * файл, где часть строк CRLF, а часть LF («наполовину так, наполовину так»);
#   * файл, где часть байтов валидный UTF-8, а часть — cp1251: он выглядит
#     нормально в одном редакторе и мусором в другом.
#
# Код возврата: 0 — всё чисто, 1 — есть проблемы.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$problems = 0
$checked = 0

# Каталоги, которые не проверяем: чужой код, артефакты сборки
$skipDirs = @('bin', 'obj', 'publish', 'refs', 'engine', 'data', '.git', 'stage', 'lib')

$textExtensions = @('.cs', '.js', '.html', '.css', '.json', '.md', '.ps1', '.cmd', '.bat', '.yml', '.yaml', '.txt', '.xml', '.csproj')

function Fail([string]$message) {
    Write-Host "[FAIL] $message" -ForegroundColor Red
    $script:problems++
}

function Ok([string]$message) {
    Write-Host "[ok]   $message"
}

# Строгий UTF-8: бросает, если встречает невалидную последовательность.
# Именно так ловится «наполовину UTF-8, наполовину cp1251».
$utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)

$files = Get-ChildItem -Recurse -File | Where-Object {
    $rel = $_.FullName.Substring($root.Length).TrimStart('\', '/')
    $parts = $rel -split '[\\/]'

    $skip = $false
    foreach ($p in $parts) {
        if ($p -in $skipDirs) { $skip = $true; break }
    }

    -not $skip -and $textExtensions -contains $_.Extension.ToLowerInvariant()
}

Write-Host "== кодировки и переводы строк =="
Write-Host ""

foreach ($file in $files) {
    $checked++
    $rel = $file.FullName.Substring($root.Length).TrimStart('\', '/')
    $ext = $file.Extension.ToLowerInvariant()
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)

    if ($bytes.Length -eq 0) { continue }

    # ---- 1. BOM
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $start = if ($hasBom) { 3 } else { 0 }

    # ---- 2. валидность UTF-8 (без BOM-байтов)
    $payload = New-Object byte[] ($bytes.Length - $start)
    [Array]::Copy($bytes, $start, $payload, 0, $payload.Length)

    $isValidUtf8 = $true
    try { [void]$utf8Strict.GetString($payload) }
    catch { $isValidUtf8 = $false }

    # ---- 3. переводы строк
    $crlf = 0
    $lfOnly = 0

    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -ne 10) { continue }
        if ($i -gt 0 -and $bytes[$i - 1] -eq 13) { $crlf++ } else { $lfOnly++ }
    }

    # ---- 4. не-ASCII байты
    $nonAscii = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) { $nonAscii++; if ($nonAscii -gt 4) { break } }
    }
    $isAscii = $nonAscii -eq 0

    # ---------------------------------------------------------------- правила

    # .cmd / .bat: CRLF обязателен, русский текст запрещён
    if ($ext -eq '.cmd' -or $ext -eq '.bat') {
        if ($lfOnly -gt 0) {
            Fail ("{0}: {1} строк с LF. cmd.exe требует CRLF, иначе блоки if/else разбираются неверно" -f $rel, $lfOnly)
        }

        if ($nonAscii -gt 0) {
            Fail ("{0}: есть не-ASCII байты. cmd печатает их как OEM — в консоли будет мусор. Только латиница" -f $rel)
        }

        # Скобка в echo внутри блока if (...) закрывает блок ДОСРОЧНО.
        # cmd парсит весь блок целиком, даже если ветка не выполняется, —
        # скрипт умирает на разборе с невнятным сообщением и кодом 255.
        $lineNo = 0
        foreach ($line in [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)) {
            $lineNo++
            if ($line -notmatch '^\s*echo\s') { continue }
            if ($line -notmatch '[()]') { continue }

            Fail ("{0}:{1}: скобка в echo. Внутри блока if (...) она закрывает блок досрочно. Экранируйте ^( ^) или перепишите текст" -f $rel, $lineNo)
        }

        continue
    }

    # .ps1: UTF-8 с BOM, если есть кириллица (иначе PS 5.1 читает как ANSI)
    if ($ext -eq '.ps1') {
        if (-not $isValidUtf8) {
            Fail ("{0}: файл не является корректным UTF-8 (cp1251/ANSI?)" -f $rel)
        }
        elseif (-not $isAscii -and -not $hasBom) {
            Fail ("{0}: кириллица без BOM. Windows PowerShell 5.1 прочитает как ANSI и сломается" -f $rel)
        }

        if ($lfOnly -gt 0 -and $crlf -gt 0) {
            Fail ("{0}: смешанные переводы строк (CRLF {1}, LF {2})" -f $rel, $crlf, $lfOnly)
        }

        continue
    }

    # всё остальное: UTF-8 обязателен
    if (-not $isValidUtf8) {
        Fail ("{0}: невалидный UTF-8 — скорее всего часть файла в cp1251" -f $rel)
        continue
    }

    # смешанные переводы строк: оба варианта в заметном количестве
    if ($crlf -gt 0 -and $lfOnly -gt 0) {
        $min = [Math]::Min($crlf, $lfOnly)
        $total = $crlf + $lfOnly
        if ($min -gt 2 -and ($min / $total) -gt 0.05) {
            Fail ("{0}: смешанные переводы строк (CRLF {1}, LF {2})" -f $rel, $crlf, $lfOnly)
        }
    }
}

Write-Host ""
Write-Host ("проверено файлов: {0}, проблем: {1}" -f $checked, $problems)

if ($problems -eq 0) {
    Write-Host "[ok]   кодировки в порядке"
    exit 0
}

exit 1