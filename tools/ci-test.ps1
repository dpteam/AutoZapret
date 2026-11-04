# ---------------------------------------------------------------------------
#  ci-test.ps1 - всё, что должно быть зелёным перед коммитом.
#
#  Запускает по порядку:
#    1. синтаксис workflow-файлов (YAML + PowerShell внутри run:)
#    2. синтаксис всех .ps1 в tools\
#    3. сборка (build.cmd)
#    4. selftest (без движка - так проходит на чистом клоне)
#    5. selftest --require-engine, если движок установлен
#
#  Возвращает 0, если всё хорошо, иначе 1.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root

$failures = 0
$started = Get-Date

function Section([string]$title) {
    Write-Host ""
    Write-Host "== $title ==" -ForegroundColor Cyan
}

function Fail([string]$message) {
    Write-Host "[FAIL] $message" -ForegroundColor Red
    $script:failures++
}

function Ok([string]$message) {
    Write-Host "[ok]   $message"
}

# ---------------------------------------------------------------- 1. workflows
Section "workflow-файлы"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-workflows.ps1')
if ($LASTEXITCODE -ne 0) { Fail "workflow-файлы содержат ошибки" } else { Ok "workflow-файлы валидны" }

# ------------------------------------------------------- 1в. кодировки
# .cmd с LF, кириллица в .cmd, .ps1 без BOM, смешанные переводы строк,
# «наполовину UTF-8, наполовину cp1251». Компилятор такое не ловит,
# а ломается оно громко и непонятно.
Section "кодировки"
if (Test-Path (Join-Path $root 'tools\check-encoding.ps1')) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\check-encoding.ps1')
    if ($LASTEXITCODE -ne 0) { Fail "кодировки и переводы строк" } else { Ok "кодировки и переводы строк" }
} else {
    Write-Host "[skip] check-encoding.ps1 не найден"
}

# ------------------------------------------------------- 1б. целостность UI
# Ловит два класса ошибок, которые иначе видны только глазами:
#   * getElementById на id, которого нет в разметке — обработчик в пустоту;
#   * элемент с id, к которому никто не обращается — забытая проводка.
Section "целостность UI"
if (Test-Path (Join-Path $root 'tools\check-ui-ids.js')) {
    & node (Join-Path $root 'tools\check-ui-ids.js')
    if ($LASTEXITCODE -ne 0) { Fail "разметка и app.js расходятся" } else { Ok "разметка и app.js согласованы" }
} else {
    Write-Host "[skip] check-ui-ids.js не найден"
}

# ------------------------------------------------------------------- 2. скрипты
Section "PowerShell-скрипты"
$scripts = Get-ChildItem (Join-Path $root 'tools') -Filter '*.ps1'
$badScripts = 0

foreach ($script in $scripts) {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$errors)

    if ($errors -and $errors.Count -gt 0) {
        $badScripts++
        Write-Host ("[FAIL] {0}" -f $script.Name) -ForegroundColor Red
        foreach ($e in $errors) {
            Write-Host ("       строка {0}: {1}" -f $e.Extent.StartLineNumber, $e.Message) -ForegroundColor Red
        }
    }
}

if ($badScripts -gt 0) { Fail "$badScripts скриптов с ошибками" }
else { Ok ("{0} скриптов валидны" -f $scripts.Count) }

# --------------------------------------------------------------- 3. кодировка
# PowerShell 5.1 читает -File без BOM как ANSI и ломается на кириллице.
Section "кодировка .ps1 (UTF-8 с BOM)"
$noBom = 0
foreach ($script in $scripts) {
    $bytes = [System.IO.File]::ReadAllBytes($script.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (-not $hasBom) {
        $noBom++
        Write-Host ("[FAIL] {0} без BOM" -f $script.Name) -ForegroundColor Red
    }
}
if ($noBom -gt 0) { Fail "$noBom скриптов без BOM" } else { Ok "все с BOM" }

# -------------------------------------------------------------- 4. стратегии
Section "стратегии в репозитории"
foreach ($name in @('strategies.v1.json', 'strategies.v2.json')) {
    $path = Join-Path $root "assets\strategies\$name"
    if (-not (Test-Path $path)) {
        Fail "нет $path (стратегии должны быть закоммичены)"
        continue
    }

    try {
        $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $count = @($json.strategies).Count
        if ($count -lt 20) { Fail "$name содержит всего $count стратегий" }
        else { Ok "$name : $count стратегий" }
    } catch {
        Fail "$name не разбирается: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- 5. лицензии
Section "лицензии"
foreach ($name in @('LICENSE',
                    'licenses\README.md',
                    'licenses\zapret-MIT.txt',
                    'licenses\flowseal-MIT.txt',
                    'licenses\WinDivert-LGPLv3.txt',
                    'licenses\tg-ws-proxy-MIT.txt')) {
    $path = Join-Path $root $name
    if (-not (Test-Path $path)) { Fail "нет $name" }
}
if ($failures -eq 0) { Ok "все на месте" }

# ------------------------------------------------------------------ 6. сборка
Section "сборка"
Push-Location $root
try {
    & cmd /c "build.cmd" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "build.cmd завершился с кодом $LASTEXITCODE" }
    else { Ok "build.cmd" }
} finally { Pop-Location }

$exe = Join-Path $root 'bin\AutoZapret.exe'
if (-not (Test-Path $exe)) {
    Fail "bin\AutoZapret.exe не создан"
} else {
    # -------------------------------------------------------------- 7. selftest
    Section "selftest"

    Push-Location (Join-Path $root 'bin')
    try {
        $p = Start-Process -FilePath '.\AutoZapret.exe' -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
        if (Test-Path 'selftest.txt') {
            Get-Content 'selftest.txt' -Encoding UTF8 | Where-Object { $_ -match 'RESULT|\[FAIL\]' } | Write-Host
        }
        if ($p.ExitCode -ne 0) { Fail "selftest вернул $($p.ExitCode)" }
        else { Ok "selftest" }

        # строгий режим только если движок реально установлен
        $engine = Join-Path $root 'engine\bin\winws.exe'
        if (Test-Path $engine) {
            Section "selftest (строгий, с движком)"
            $s = Start-Process -FilePath '.\AutoZapret.exe' -ArgumentList '--selftest','--require-engine' -Wait -PassThru -NoNewWindow
            if (Test-Path 'selftest.txt') {
                Get-Content 'selftest.txt' -Encoding UTF8 | Where-Object { $_ -match 'RESULT|\[FAIL\]' } | Write-Host
            }
            if ($s.ExitCode -ne 0) { Fail "строгий selftest вернул $($s.ExitCode)" }
            else { Ok "строгий selftest" }
        } else {
            Write-Host "[skip] движок не установлен - строгий selftest пропущен"
        }
    } finally { Pop-Location }
}

# --------------------------------------------------------------------- итог
$elapsed = [int]((Get-Date) - $started).TotalSeconds

Write-Host ""
if ($failures -eq 0) {
    Write-Host ("ВСЁ ХОРОШО ({0} с)" -f $elapsed) -ForegroundColor Green
    exit 0
}

Write-Host ("ПРОВАЛОВ: {0} ({1} с)" -f $failures, $elapsed) -ForegroundColor Red
exit 1
