@echo off
rem Build hev-socks5-tunnel (MIT, tun2socks) for Android and copy the .so files
rem into the app. Run once; the result is cached in android\AutoZapret.Android\libs.
rem
rem Requires: Android NDK (installed by sdkmanager "ndk;27.3.13750724").
setlocal
cd /d "%~dp0.."

set "NDK=C:\Android\sdk\ndk\27.3.13750724"
if not exist "%NDK%\ndk-build.cmd" (
    echo NDK not found at %NDK%
    echo Install it: C:\Android\sdk\cmdline-tools\12.0\bin\sdkmanager.bat "ndk;27.3.13750724"
    exit /b 1
)

set "SRC=refs\hev-socks5-tunnel"
if not exist "%SRC%\Android.mk" (
    echo hev-socks5-tunnel not found. Clone it:
    echo   git clone --depth 1 https://github.com/heiher/hev-socks5-tunnel.git %SRC%
    echo   cd %SRC% ^&^& git submodule update --init --recursive --depth 1
    exit /b 1
)

rem submodules are required: yaml, lwip, hev-task-system, src/core
if not exist "%SRC%\third-part\lwip\Android.mk" (
    echo Initializing hev submodules...
    pushd "%SRC%"
    git submodule update --init --recursive --depth 1
    if errorlevel 1 ( popd & exit /b 1 )
    popd
)

echo Building hev-socks5-tunnel for arm64-v8a, armeabi-v7a and x86_64...
pushd "%SRC%"
call "%NDK%\ndk-build.cmd" APP_ABI="arm64-v8a armeabi-v7a x86_64" NDK_PROJECT_PATH=. APP_BUILD_SCRIPT=Android.mk NDK_APPLICATION_MK=Application.mk -j8
set "RESULT=%ERRORLEVEL%"
popd

if not "%RESULT%"=="0" (
    echo ndk-build failed with code %RESULT%
    exit /b %RESULT%
)

rem armeabi-v7a is required: 32-bit Android devices still exist, and without
rem this library the APK either fails to install or crashes on the first hev call.
set "DST=android\AutoZapret.Android\libs"
for %%A in (arm64-v8a armeabi-v7a x86_64) do (
    if not exist "%DST%\%%A" mkdir "%DST%\%%A"
    copy /Y "%SRC%\libs\%%A\libhev-socks5-tunnel.so" "%DST%\%%A\" >nul
    echo   %%A ^<- libhev-socks5-tunnel.so
)

echo Done. Now run android\build.cmd