# Headless-управление Android-версией через adb: ни одного тапа по экрану.
#
# Зачем: тап по координатам попадает не туда при малейшем сдвиге верстки.
# Сегодня так дважды выбиралась не та техника, и замеры приходилось повторять.
#
# Примеры:
#   powershell -File tools\android-headless.ps1 state
#   powershell -File tools\android-headless.ps1 technique fake8
#   powershell -File tools\android-headless.ps1 vpn on
#   powershell -File tools\android-headless.ps1 check
#   powershell -File tools\android-headless.ps1 probe https://www.youtube.com/
#   powershell -File tools\android-headless.ps1 log

param(
    [Parameter(Position = 0)]
    [string]$Command = "state",

    [Parameter(Position = 1)]
    [string]$Value = "",

    [string]$Adb = "C:\Android\sdk\platform-tools\adb.exe",
    [string]$Package = "ru.autozapret.android",
    [string]$Activity = "ru.autozapret.android/crc64e9b175a717ffbd8c.MainActivity",
    [string]$Service = "ru.autozapret.android/crc64e9b175a717ffbd8c.ZapretVpnService",
    [int]$LocalPort = 19080
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Adb)) {
    Write-Host "adb не найден: $Adb"
    exit 1
}

function Log-Of([string]$filter) {
    & $Adb logcat -d -v brief 2>&1 |
        Select-String -Pattern "AutoZapret\s*\(" |
        ForEach-Object { ($_.Line -split 'AutoZapret\s*\(\s*\d+\):\s*')[-1] } |
        Select-String -Pattern $filter |
        ForEach-Object { $_.Line }
}

function Broadcast([string]$action, [string[]]$extra) {
    $cmd = "am broadcast -a $action -p $Package"
    foreach ($e in $extra) { $cmd += " $e" }
    & $Adb shell $cmd 2>&1 | Select-Object -Last 1
}

switch ($Command.ToLower()) {
    "state" {
        & $Adb logcat -c
        Broadcast "ru.autozapret.android.STATE" @() | Out-Null
        Start-Sleep -Seconds 1
        Log-Of "STATE"
    }

    "technique" {
        if ($Value -eq "") { Write-Host "нужно имя техники: fake8, split1, disorder, oob ..."; exit 1 }
        & $Adb logcat -c
        Broadcast "ru.autozapret.android.SET" @("--es", "technique", $Value) | Out-Null
        Start-Sleep -Seconds 1
        Log-Of "SET"
    }

    "port" {
        if ($Value -eq "") { Write-Host "нужен номер порта"; exit 1 }
        & $Adb logcat -c
        Broadcast "ru.autozapret.android.SET" @("--ei", "port", $Value) | Out-Null
        Start-Sleep -Seconds 1
        Log-Of "SET"
    }

    "quic" {
        if ($Value -eq "") { Write-Host "нужно on или off"; exit 1 }
        $flag = if ($Value -eq "on") { "true" } else { "false" }
        & $Adb logcat -c
        Broadcast "ru.autozapret.android.SET" @("--ez", "blockQuic", $flag) | Out-Null
        Start-Sleep -Seconds 1
        Log-Of "SET"
    }

    "apps" {
        if ($Value -eq "") {
            & $Adb logcat -c
            Broadcast "ru.autozapret.android.APPS" @() | Out-Null
        } else {
            & $Adb logcat -c
            Broadcast "ru.autozapret.android.APPS" @("--es", "mode", $Value) | Out-Null
        }
        Start-Sleep -Seconds 1
        Log-Of "APPS"
    }

    "vpn" {
        if ($Value -eq "on") {
            & $Adb logcat -c
            # Сервис защищён BIND_VPN_SERVICE, поэтому команда идёт через
            # активность приложения — она имеет право его поднять.
            Broadcast "ru.autozapret.android.VPN.ON" @() | Out-Null
            Start-Sleep -Seconds 7
        } else {
            & $Adb logcat -c
            Broadcast "ru.autozapret.android.VPN.OFF" @() | Out-Null
            Start-Sleep -Seconds 3
        }
        Log-Of "VPN|SOCKS5|туннель|UDP|headless"
    }

    "check" {
        & $Adb logcat -c
        Broadcast "ru.autozapret.android.CHECK" @() | Out-Null
        Write-Host "проверяю 6 провайдеров, до 20 с..."
        Start-Sleep -Seconds 18
        Log-Of "CHECK"
    }

    "probe" {
        # Сквозная проверка: поднимаем SOCKS5 приложения на локальный порт и
        # идём через него curl-ом. Так видно, работает ли обход на самом деле,
        # а не только «VPN включён».
        if ($Value -eq "") { Write-Host "нужен URL"; exit 1 }

        # Проверяем не по тексту в логе (кодировка ломается), а по факту:
        # отвечает ли порт 1080 на телефоне.
        & $Adb forward "tcp:$LocalPort" "tcp:1080" 2>&1 | Out-Null

        $alive = $false
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $ar = $tcp.BeginConnect("127.0.0.1", $LocalPort, $null, $null)
            $alive = $ar.AsyncWaitHandle.WaitOne(2000)
            if ($alive) { $tcp.EndConnect($ar) }
            $tcp.Close()
        } catch { $alive = $false }

        if (-not $alive) {
            & $Adb forward --remove "tcp:$LocalPort" 2>&1 | Out-Null
            Write-Host "SOCKS5 не отвечает: сначала 'vpn on'"
            exit 1
        }

        $result = & curl.exe -s -o NUL -w "%{http_code} %{time_total}" --max-time 25 `
            --socks5-hostname "127.0.0.1:$LocalPort" $Value 2>$null

        & $Adb forward --remove "tcp:$LocalPort" 2>&1 | Out-Null

        Write-Host "$Value -> $result"
    }

    "log" {
        Log-Of "."
    }

    "clear" {
        & $Adb logcat -c
        Write-Host "лог очищен"
    }

    default {
        Write-Host "команды: state | technique <id> | port <n> | quic <on|off> | apps [all|only|except] | vpn <on|off> | check | probe <url> | log | clear"
        exit 1
    }
}
