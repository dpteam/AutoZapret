# Проверяет, что PowerShell внутри .github/workflows/*.yml синтаксически валиден.
# YAML-валидность этого не гарантирует: опечатка в скрипте вылезет только в CI.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$files = Get-ChildItem (Join-Path $root '.github\workflows') -Filter '*.yml'
$total = 0
$failed = 0

# ---------------------------------------------------------------------------
# Структура YAML: работы на верхнем уровне jobs.
#
# Зачем отдельно от синтаксиса скриптов. Дописанная работа с отступом 6 вместо
# 2 делает файл невалидным, и GitHub отвечает «conclusion: failure» с
# `total_count: 0` работ — то есть падает ДО запуска чего-либо, и в логах нет
# ни строчки. Именно так уехал linux-джоб: скрипты внутри были правильные.
#
# Как проверяем. Полноценного YAML-парсера в PowerShell нет, поэтому берём
# якорь, который есть у каждой работы, — `runs-on:`. Внутри jobs он обязан
# быть на отступе 4 (работа на 2 + 2). Если работа уехала на 6, то runs-on
# окажется на 8, и это видно. Первая версия проверки запоминала отступ только
# первой работы и потому пропускала сдвинутую вторую.
# ---------------------------------------------------------------------------
foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    $problems = New-Object System.Collections.ArrayList
    $runsOnCount = 0

    for ($n = 0; $n -lt $lines.Count; $n++) {
        $line = $lines[$n]

        # табы в YAML запрещены
        if ($line -match "`t") {
            [void]$problems.Add(("строка {0}: табуляция (YAML запрещает)" -f ($n + 1)))
            continue
        }

        if ($line -match '^(\s*)runs-on:') {
            $runsOnCount++
            $indent = $Matches[1].Length

            if ($indent -ne 4) {
                [void]$problems.Add(("строка {0}: runs-on на отступе {1}, а нужен 4 — значит работа не на отступе 2, и GitHub файл не разберёт" -f ($n + 1), $indent))
            }
        }
    }

    if ($runsOnCount -eq 0) {
        [void]$problems.Add('ни одной работы с runs-on — файл подозрительный')
    }

    if ($problems.Count -gt 0) {
        $failed++
        Write-Host ("[FAIL] {0} :: структура YAML" -f $file.Name) -ForegroundColor Red
        foreach ($p in $problems) { Write-Host ("       " + $p) -ForegroundColor Red }
    }
    else {
        Write-Host ("[ok]   {0} :: структура YAML ({1} работ)" -f $file.Name, $runsOnCount)
    }
}

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    $i = 0
    $stepName = '?'

    $isLinuxJob = $false

    while ($i -lt $lines.Count) {
        $line = $lines[$i]

        # Какой раннер у текущей работы. Блоки bash нельзя проверять
        # парсером PowerShell: он честно скажет «синтаксическая ошибка»
        # про совершенно правильный `if [ $? -ne 0 ]; then`.
        if ($line -match '^\s*runs-on:\s*(.+)$') {
            $runner = $Matches[1].Trim().ToLowerInvariant()
            $isLinuxJob = ($runner -notmatch 'windows')
        }

        if ($line -match '^\s*-\s+name:\s*(.+)$') { $stepName = $Matches[1].Trim() }

        # начало блока run: | или run: >
        if ($line -match '^(\s*)run:\s*[|>]\s*$') {
            $indent = $Matches[1].Length
            $script = New-Object System.Text.StringBuilder
            $j = $i + 1

            while ($j -lt $lines.Count) {
                $next = $lines[$j]
                if ($next.Trim().Length -eq 0) { [void]$script.AppendLine(); $j++; continue }

                $nextIndent = $next.Length - $next.TrimStart().Length
                if ($nextIndent -le $indent) { break }

                [void]$script.AppendLine($next.Substring([Math]::Min($indent + 2, $next.Length)))
                $j++
            }

            # Ищем shell: у этого шага (он идёт после run: на том же уровне)
            $shellIsBash = $false
            for ($k = $j; $k -lt [Math]::Min($j + 4, $lines.Count); $k++) {
                if ($lines[$k] -match '^\s*shell:\s*bash') { $shellIsBash = $true; break }
                if ($lines[$k] -match '^\s*-\s+name:') { break }
            }

            if ($isLinuxJob -or $shellIsBash) {
                $total++
                Write-Host ("[ok]   {0} :: {1} (bash — парсер PowerShell не применим)" -f $file.Name, $stepName)
                $i = $j
                continue
            }

            $total++
            $text = $script.ToString()

            # Actions подставляет ${{ ... }} ДО запуска PowerShell, поэтому
            # парсер проверки не должен видеть эту конструкцию: для него это
            # `$` + блок `{{ }}`, то есть синтаксическая ошибка. Заменяем на
            # безопасную заглушку — важно проверить структуру самого скрипта.
            $text = [regex]::Replace($text, '\$\{\{[^}]*\}\}', '""')

            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors)

            if ($errors -and $errors.Count -gt 0) {
                $failed++
                Write-Host ("[FAIL] {0} :: {1}" -f $file.Name, $stepName) -ForegroundColor Red
                foreach ($e in $errors) {
                    Write-Host ("       строка {0}: {1}" -f $e.Extent.StartLineNumber, $e.Message) -ForegroundColor Red
                }
            } else {
                Write-Host ("[ok]   {0} :: {1}" -f $file.Name, $stepName)
            }

            $i = $j
            continue
        }

        $i++
    }
}

Write-Host ""
Write-Host ("блоков run: {0}, с ошибками: {1}" -f $total, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
