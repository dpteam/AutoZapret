<#
.SYNOPSIS
    Качает WebView2 SDK (managed DLL) в lib\webview2.

.DESCRIPTION
    Единственная внешняя зависимость проекта. Берём не через NuGet-менеджер,
    а распаковываем .nupkg вручную: build.cmd ссылается на DLL по HintPath.

    Кладём:
      lib\webview2\Microsoft.Web.WebView2.Core.dll
      lib\webview2\Microsoft.Web.WebView2.WinForms.dll
      lib\webview2\WebView2Loader.dll      (x64, рядом с exe)
      lib\webview2\version.txt

.PARAMETER Version
    Версия пакета. По умолчанию — зашитая ниже (проверенная).

.PARAMETER Force
    Перекачать, даже если файлы уже на месте.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.4314.35',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$libDir  = Join-Path $root 'lib\webview2'
$versionFile = Join-Path $libDir 'version.txt'

$coreDll = Join-Path $libDir 'Microsoft.Web.WebView2.Core.dll'
$wfDll   = Join-Path $libDir 'Microsoft.Web.WebView2.WinForms.dll'
$loader  = Join-Path $libDir 'WebView2Loader.dll'

if (-not $Force -and (Test-Path $coreDll) -and (Test-Path $wfDll) -and (Test-Path $loader)) {
    $have = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '?' }
    Write-Host "[ok] WebView2 SDK уже на месте (версия $have) - пропускаю."
    Write-Host "     Перекачать: tools\fetch-webview2.ps1 -Force"
    exit 0
}

if (-not (Test-Path $libDir)) { New-Item -ItemType Directory -Path $libDir -Force | Out-Null }

$url  = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$Version"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("wv2-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$nupkg = Join-Path $temp 'wv2.nupkg'

Write-Host "[1/3] качаю WebView2 SDK $Version ..."
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $wc = New-Object Net.WebClient
    $wc.Headers.Add('User-Agent', 'AutoZapret-build')
    $wc.DownloadFile($url, $nupkg)
} catch {
    Write-Host "[ERROR] не удалось скачать $url" -ForegroundColor Red
    Write-Host "        $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "        Проверьте интернет или укажите версию: -Version 1.0.4314.35" -ForegroundColor Red
    exit 1
}

$size = (Get-Item $nupkg).Length
if ($size -lt 100000) {
    Write-Host "[ERROR] скачался подозрительно маленький файл ($size байт) - вероятно, это страница ошибки." -ForegroundColor Red
    exit 1
}

Write-Host "[2/3] распаковываю ..."
$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    # Путь внутри пакета зависит от версии и целевой платформы.
    # Для .NET 9 (net*-windows) нужен lib_manual/netcoreapp3.0 - вариант,
    # собранный под .NET Core 3.0+, он совместим и с .NET 5+.
    # lib/net462 - для .NET Framework, тоже работает через шим, но это не путь.
    function Find-Entry([string]$fileName) {
        $cands = @($zip.Entries | Where-Object {
            $p = $_.FullName.Replace('\', '/')
            $p.EndsWith('/' + $fileName) -and (
                $p -like "lib_manual/netcoreapp3.0/*" -or
                $p -like "lib/net*/*"
            )
        })
        if ($cands.Count -eq 0) { return $null }

        foreach ($pref in @('lib_manual/netcoreapp3.0/', 'lib/net462/', 'lib/net45/')) {
            $hit = $cands | Where-Object { $_.FullName.Replace('\','/').StartsWith($pref) } | Select-Object -First 1
            if ($hit) { return $hit }
        }
        return ($cands | Sort-Object FullName | Select-Object -First 1)
    }

    $plan = @(
        @{ name = 'Microsoft.Web.WebView2.Core.dll';     dest = $coreDll },
        @{ name = 'Microsoft.Web.WebView2.WinForms.dll'; dest = $wfDll   }
    )
    $found = @{}
    foreach ($item in $plan) {
        $entry = Find-Entry $item.name
        if (-not $entry) {
            Write-Host "[ERROR] в пакете нет '$($item.name)' (искал в lib/net*)" -ForegroundColor Red
            exit 1
        }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $item.dest, $true)
        $found[$item.name] = (Get-Item $item.dest).Length
        Write-Host "      $($item.name) <- $($entry.FullName)"
    }

    $loaderEntry = $zip.Entries | Where-Object {
        $_.FullName.Replace('\','/') -eq 'runtimes/win-x64/native/WebView2Loader.dll'
    } | Select-Object -First 1
    if (-not $loaderEntry) {
        Write-Host "[ERROR] в пакете нет 'runtimes/win-x64/native/WebView2Loader.dll'" -ForegroundColor Red
        exit 1
    }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($loaderEntry, $loader, $true)
    $found['WebView2Loader.dll'] = (Get-Item $loader).Length
    Write-Host "      WebView2Loader.dll <- $($loaderEntry.FullName)"
} finally {
    $zip.Dispose()
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Set-Content -Path $versionFile -Value $Version -Encoding ASCII

Write-Host "[3/3] готово."
Write-Host "      Core.dll     $($found['Microsoft.Web.WebView2.Core.dll']) байт"
Write-Host "      WinForms.dll $($found['Microsoft.Web.WebView2.WinForms.dll']) байт"
Write-Host "      Loader.dll   $($found['WebView2Loader.dll']) байт"
exit 0
