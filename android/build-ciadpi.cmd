@echo off
rem Build byedpi (ciadpi, MIT) as a shared library for Android and copy the
rem .so files into the app. Run once; the result is cached in
rem android\AutoZapret.Android\libs.
rem
rem WHY A SHARED LIBRARY, NOT AN EXECUTABLE
rem   Android 10+ forbids executing files from the app's data directory
rem   (W^X, ExecuteNoWritableFile): "error=13, Permission denied". Files in the
rem   native library directory are read-only and executable, so the engine is
rem   shipped as libs\<abi>\libciadpi.so.
rem
rem WHY IT IS A NORMAL EXECUTABLE, NOT -shared
rem   The file is named libciadpi.so only to live in nativeLibraryDir (the one
rem   place Android allows exec from). Inside it must be a real ELF executable.
rem   A -shared build has ELF type DYN and no crt _start, so executing it
rem   segfaults immediately - and the app log still says "engine started",
rem   because ProcessBuilder.Start() succeeded. Verified on the device:
rem   running the -shared build gives "Segmentation fault".
rem
rem WHY -Wl,-z,max-page-size=16384
rem   Android 16 requires 16 KB page sizes on some devices. A .so built for
rem   4 KB pages loads but is rejected by the linker on those devices, and the
rem   build warns XA0141 for exactly this reason. The flag aligns segments to
rem   16 KB, which is valid for both 4 KB and 16 KB kernels.
rem
rem Requires: Android NDK. The path is taken from ANDROID_NDK_HOME, or the
rem default C:\Android\sdk\ndk\27.3.13750724.
setlocal
cd /d "%~dp0.."

if "%ANDROID_NDK_HOME%"=="" set "NDK=C:\Android\sdk\ndk\27.3.13750724"
if not "%ANDROID_NDK_HOME%"=="" set "NDK=%ANDROID_NDK_HOME%"

set "TOOLCHAIN=%NDK%\toolchains\llvm\prebuilt\windows-x86_64\bin"
if not exist "%TOOLCHAIN%\aarch64-linux-android29-clang.cmd" (
    echo NDK clang not found in %TOOLCHAIN%
    echo Install it: C:\Android\sdk\cmdline-tools\12.0\bin\sdkmanager.bat "ndk;27.3.13750724"
    exit /b 1
)

set "SRC=refs\byedpi"
if not exist "%SRC%\main.c" (
    echo byedpi source not found. Clone it:
    echo   git clone --depth 1 https://github.com/hufrea/byedpi.git %SRC%
    exit /b 1
)

set "DST=android\AutoZapret.Android\libs"

rem Sources are the same for every ABI; only the compiler prefix changes.
set "SOURCES=packets.c main.c conev.c proxy.c desync.c mpool.c extend.c"
set "CFLAGS=-D_DEFAULT_SOURCE -I. -std=c99 -O2"
set "LDFLAGS=-Wl,-z,max-page-size=16384"

pushd "%SRC%"
set "RESULT=0"

call :build aarch64-linux-android29-clang arm64-v8a
if errorlevel 1 set "RESULT=1"

call :build armv7a-linux-androideabi29-clang armeabi-v7a
if errorlevel 1 set "RESULT=1"

call :build x86_64-linux-android29-clang x86_64
if errorlevel 1 set "RESULT=1"

popd

if not "%RESULT%"=="0" (
    echo ciadpi build failed
    exit /b 1
)

rem VERIFY the engine is a real executable. Two ways this silently breaks:
rem   * no "main" symbol (renamed) - nothing to run;
rem   * built with -shared - no PT_INTERP and no crt _start, segfaults on exec.
rem Both leave ProcessBuilder.Start() succeeding, so the app thinks the engine
rem is running while it is already dead.
rem
rem NOTE on the ELF type: Android executables are PIE, so their type is DYN,
rem exactly like a shared library. The distinguishing mark is PT_INTERP
rem ("Requesting program interpreter: /system/bin/linker64") - a shared library
rem has none.
set "NM=%NDK%\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-nm.exe"
set "RE=%NDK%\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-readelf.exe"
if exist "%NM%" (
    rem no -D: in an executable "main" lives in the regular symbol table,
    rem not in the dynamic one.
    "%NM%" --defined-only "%DST%\arm64-v8a\libciadpi.so" 2>nul | findstr /R /C:" T main" >nul
    if errorlevel 1 (
        echo [FAIL] libciadpi.so has no "main" symbol - it cannot be executed
        exit /b 1
    )
    echo   main present - ok
)
if exist "%RE%" (
    "%RE%" -l "%DST%\arm64-v8a\libciadpi.so" 2>nul | findstr /C:"program interpreter" >nul
    if errorlevel 1 (
        echo [FAIL] libciadpi.so has no PT_INTERP - built with -shared^?
        exit /b 1
    )
    echo   PT_INTERP present - ok
)

echo.
echo Done. Engine libraries:
dir /b /s "%DST%\libciadpi.so"
rem reset the error level left by findstr/dir so the caller sees success
ver >nul
exit /b 0

:build
rem %1 = compiler prefix, %2 = Android ABI directory name
echo Building ciadpi for %2 ...
if not exist "..\..\%DST%\%2" mkdir "..\..\%DST%\%2"
call "%TOOLCHAIN%\%1.cmd" %CFLAGS% %LDFLAGS% %SOURCES% -o "..\..\%DST%\%2\libciadpi.so"
if errorlevel 1 exit /b 1
exit /b 0
