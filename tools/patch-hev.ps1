# Убирает JNI-обёртку (src/hev-jni.c) из сборки hev-socks5-tunnel.
#
# ЗАЧЕМ
#   hev-jni.c — мост для Java с JNI_OnLoad. Он делает
#   FindClass("hev/htproxy/TProxyService") и падает, если такого класса нет.
#   Мы hev зовём через P/Invoke (hev_socks5_tunnel_main_from_str), Java-класс
#   нам не нужен и его нет — поэтому загрузка библиотеки завершалась аварийно:
#
#     JNI DETECTED ERROR IN APPLICATION: JNI NewGlobalRef called with
#     pending exception java.lang.ClassNotFoundException:
#     Didn't find class "hev.htproxy.TProxyService"
#     -> Fatal signal 6 (SIGABRT)
#
#   Снаружи это «приложение не запускается» без единого намёка на причину.
#
# ПОЧЕМУ ВИДНО НЕ ВСЕГДА
#   CheckJNI включается для debuggable сборок, а Debug-APK именно такая.
#   На релизной сборке тот же баг прошёл бы молча — чинить надо здесь.
#
# ПОЧЕМУ ОТДЕЛЬНЫЙ ФАЙЛ, А НЕ ОДНА СТРОКА В .CMD
#   В .cmd символ % означает переменную: строка вида `$(patsubst $(SRCDIR)/%...`
#   съедалась как `%SRCDIR%`-подобная подстановка, правка не применялась, а
#   ошибка была невидимой. В PowerShell той же беды нет.
#
# Запуск: powershell -File tools\patch-hev.ps1 -Source refs\hev-socks5-tunnel
param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = 'Stop'

$mk = Join-Path $Source 'Android.mk'

if (-not (Test-Path $mk)) { throw "нет файла $mk" }

$old = 'HEV_SOCKS5_TUNNEL_SRC := $(patsubst $(SRCDIR)/%,src/%,$(SRCFILES))'
$new = 'HEV_SOCKS5_TUNNEL_SRC := $(filter-out src/hev-jni.c,$(patsubst $(SRCDIR)/%,src/%,$(SRCFILES)))'

$text = [System.IO.File]::ReadAllText($mk)

if ($text.Contains($new)) {
    Write-Host 'hev-jni.c already excluded'
    exit 0
}

if (-not $text.Contains($old)) {
    # Не «пропустить и поехали»: если строка исчезла, значит upstream поменял
    # сборку, и правка не применится. Тогда лучше упасть здесь, чем выпустить
    # APK, который падает на старте у пользователя.
    Write-Host '[FAIL] Android.mk does not contain the expected source line'
    Write-Host '       upstream changed the build; update tools\patch-hev.ps1'
    exit 1
}

[System.IO.File]::WriteAllText($mk, $text.Replace($old, $new))
Write-Host 'hev-jni.c excluded from build'
exit 0
