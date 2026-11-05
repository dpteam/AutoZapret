@echo off
rem Build the Android APK (Debug, signed with the debug key).
rem
rem Requires: dotnet workload install android, Android SDK, JDK 17+.
rem The SDK path is taken from ANDROID_HOME, or the default C:\Android\sdk.
setlocal
cd /d "%~dp0AutoZapret.Android"

if "%ANDROID_HOME%"=="" (
    if exist "C:\Android\sdk" (
        set ANDROID_HOME=C:\Android\sdk
    ) else (
        echo Android SDK not found. Install command-line tools and set ANDROID_HOME.
        exit /b 1
    )
)

echo Using Android SDK: %ANDROID_HOME%
dotnet build -c Debug -p:AndroidSdkDirectory="%ANDROID_HOME%" -v minimal --nologo

if errorlevel 1 exit /b 1

echo.
echo APK: %~dp0AutoZapret.Android\bin\Debug\net9.0-android\ru.autozapret.android-Signed.apk
echo Install: adb install -r "%~dp0AutoZapret.Android\bin\Debug\net9.0-android\ru.autozapret.android-Signed.apk"