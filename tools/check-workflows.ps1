# Проверяет, что PowerShell внутри .github/workflows/*.yml синтаксически валиден.
# YAML-валидность этого не гарантирует: опечатка в скрипте вылезет только в CI.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$files = Get-ChildItem (Join-Path $root '.github\workflows') -Filter '*.yml'
$total = 0
$failed = 0

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    $i = 0
    $stepName = '?'

    while ($i -lt $lines.Count) {
        $line = $lines[$i]

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
