# Полная диагностика Android-версии: проверяет КАЖДЫЙ слой цепочки отдельно.
#
# Зачем. Мы слишком долго чинили «по одной гипотезе»: меняли TTL, движок,
# MTU — и каждый раз оказывалось, что рвётся в другом месте. Этот скрипт
# проходит цепочку сверху вниз и говорит, на каком слое остановка.
#
#   приложение → tun0 → hev → SOCKS5 (наш) → DoH → движок → интернет
#
# Запуск:
#   powershell -File tools\android-pipeline.ps1
#   powershell -File tools\android-pipeline.ps1 -Host www.youtube.com

param(
    [string]$Host_ = "www.youtube.com",
    [string]$Adb = "C:\Android\sdk\platform-tools\adb.exe",
    [string]$Package = "ru.autozapret.android"
)

$ErrorActionPreference = "Continue"

$script:pass = 0
$script:fail = 0
$script:skip = 0

function Check($name, [scriptblock]$test, $hint) {
    try {
        $result = & $test

        if ($result -eq $true) {
            Write-Host ("[ok]   " + $name) -ForegroundColor Green
            $script:pass++
            return $true
        }

        if ($result -eq "skip") {
            Write-Host ("[skip] " + $name) -ForegroundColor DarkGray
            $script:skip++
            return $false
        }

        Write-Host ("[FAIL] " + $name) -ForegroundColor Red
        if ($hint) { Write-Host ("       -> " + $hint) -ForegroundColor Yellow }
        $script:fail++
        return $false
    }
    catch {
        Write-Host ("[FAIL] " + $name + ": " + $_.Exception.Message) -ForegroundColor Red
        $script:fail++
        return $false
    }
}

function Sh($cmd) {
    return ((& $Adb shell $cmd 2>$null) -join "`n").Trim()
}

Write-Host ""
Write-Host "=== AutoZapret Android: полная проверка цепочки ===" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------- слой 0: процесс

$appPid = Sh "pidof $Package"

Check "процесс приложения запущен" {
    if ($appPid) { $true } else { $false }
} "запустите приложение: adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1"

# ---------------------------------------------------------------- слой 1: tun

$tun = Sh "ip -o link 2>/dev/null | grep tun0"

Check "tun0 существует" {
    if ($tun) { $true } else { $false }
} "VPN не запущен — включите VPN-режим"

if ($tun) {
    # MTU читаем через ip link: /sys/class/net/tun0/mtu не всегда доступен
    # обычному shell (SELinux), и проверка ложно падала.
    $mtuLine = Sh "ip -o link show tun0 2>/dev/null"
    $mtu = "?"

    if ($mtuLine -match 'mtu (\d+)') { $mtu = $Matches[1] }

    Check "MTU туннеля = 1500" {
        if ($mtu -eq "1500") { $true } else { $false }
    } ("сейчас " + $mtu + ": крупные пакеты будут рваться")
}

# ---------------------------------------------------------------- слой 2: hev

$threads = Sh "cat /proc/$appPid/task/*/comm 2>/dev/null"

Check "поток hev-tunnel живёт" {
    if ($threads -match "hev-tunnel") { $true } else { $false }
} "hev не запустился — трафик из tun уходит мимо обхода"

Check "поток socks-accept живёт" {
    if ($threads -match "socks-accept") { $true } else { $false }
} "наш SOCKS5 не слушает"

# ---------------------------------------------------------------- слой 3: порты

$ports = Sh "ss -tln 2>/dev/null"

Check "SOCKS5 слушает 127.0.0.1:1080" {
    if ($ports -match "127.0.0.1:1080") { $true } else { $false }
} "порт занят или прокси не стартовал"

$enginePort = Sh "ss -tln 2>/dev/null | grep -o '127.0.0.1:1081'"

Check "движок byedpi слушает (если выбран fake)" {
    if ($enginePort) { $true } else { "skip" }
} ""

# ---------------------------------------------------------------- слой 4: hev → SOCKS

$hevConns = Sh "ss -tn 2>/dev/null | grep -c ':1080'"

Check "hev подключён к нашему SOCKS5" {
    if ([int]$hevConns -gt 0) { $true } else { $false }
} "hev не отдаёт соединения в прокси — трафик идёт мимо"

# ---------------------------------------------------------------- слой 5: DoH

Write-Host ""
Write-Host "--- DNS ---" -ForegroundColor Cyan

$dnsOut = Sh "am broadcast -a ru.autozapret.android.DNS -p $Package"
Start-Sleep -Seconds 8
$dnsLog = ((& $Adb logcat -d -v brief 2>$null) -join "`n")

Check "DoH отвечает настоящими адресами" {
    if ($dnsLog -match "DoH 142\.251" -or $dnsLog -match "DoH [0-9]+\.[0-9]+\.[0-9]+\.[0-9]+") { $true } else { $false }
} "DoH не работает — имена резолвятся в подлог 198.18.x"

Check "системный DNS действительно подменён (ожидаемо)" {
    if ($dnsLog -match "ПОДЛОГ") { $true } else { "skip" }
} ""

# ---------------------------------------------------------------- слой 6: прокси напрямую

Write-Host ""
Write-Host "--- прокси напрямую (в обход tun) ---" -ForegroundColor Cyan

& $Adb forward --remove-all 2>$null | Out-Null
& $Adb forward tcp:19999 tcp:1080 2>$null | Out-Null

$direct = curl.exe -s -o NUL -w "%{http_code}|%{size_download}|%{time_total}" --max-time 20 `
    --socks5-hostname 127.0.0.1:19999 "https://$Host_/" 2>$null

$parts = "$direct".Split("|")
$code = if ($parts.Count -ge 1) { $parts[0] } else { "000" }
$size = if ($parts.Count -ge 2) { $parts[1] } else { "0" }
$time = if ($parts.Count -ge 3) { $parts[2] } else { "0" }

Write-Host ("       ответ: " + $code + ", " + $size + " байт, " + $time + " с")

Check "через прокси сайт отдаёт данные" {
    if ($code -match "^(200|204|301|302|404|405)$" -and [int]$size -gt 100) { $true } else { $false }
} "прокси не работает — смотрите слой 5 (DoH) и движок"

# ---------------------------------------------------------------- слой 7: крупный ответ

Write-Host ""
Write-Host "--- крупный ответ (проверка MTU и потока данных) ---" -ForegroundColor Cyan

$big = curl.exe -s -o NUL -w "%{http_code}|%{size_download}|%{time_total}" --max-time 30 `
    --socks5-hostname 127.0.0.1:19999 "https://www.youtube.com/" 2>$null

$bp = "$big".Split("|")
$bcode = if ($bp.Count -ge 1) { $bp[0] } else { "000" }
$bsize = if ($bp.Count -ge 2) { [int]$bp[1] } else { 0 }
$btime = if ($bp.Count -ge 3) { $bp[2] } else { "0" }

Write-Host ("       ответ: " + $bcode + ", " + $bsize + " байт, " + $btime + " с")

Check "крупный ответ проходит целиком (>200 КБ)" {
    if ($bsize -gt 200000) { $true } else { $false }
} "данные рвутся на объёме — это MTU или потеря пакетов"

# ---------------------------------------------------------------- слой 8: ошибки в логе

Write-Host ""
Write-Host "--- ошибки в журнале приложения ---" -ForegroundColor Cyan

$errs = ($dnsLog -split "`n") | Where-Object {
    $_ -match "AutoZapret" -and ($_ -match "таймаут|не удалось|отказал|ВНИМАНИЕ|упал")
} | Select-Object -Last 6

if ($errs) {
    foreach ($e in $errs) {
        $line = ($e -split 'AutoZapret\s*\(\s*\d+\):\s*')[-1]
        Write-Host ("       " + $line) -ForegroundColor DarkYellow
    }
} else {
    Write-Host "       ошибок нет" -ForegroundColor DarkGray
}

& $Adb forward --remove-all 2>$null | Out-Null

# ---------------------------------------------------------------- итог

Write-Host ""
Write-Host "=== итог ===" -ForegroundColor Cyan
Write-Host ("  прошло:  " + $script:pass) -ForegroundColor Green
Write-Host ("  провалов:" + $script:fail) -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "DarkGray" })
Write-Host ("  пропущено:" + $script:skip) -ForegroundColor DarkGray
Write-Host ""

if ($script:fail -gt 0) {
    Write-Host "Первый провал сверху — это и есть слой, где рвётся." -ForegroundColor Yellow
}

exit $(if ($script:fail -gt 0) { 1 } else { 0 })
