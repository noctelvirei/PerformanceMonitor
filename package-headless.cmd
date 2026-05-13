@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo ========================================
echo  Packaging Performance Monitor Headless
echo ========================================
echo.

for /f %%a in ('powershell -NoProfile -Command "([xml](Get-Content Headless\PerformanceMonitor.Headless.csproj)).Project.PropertyGroup.Version | Where-Object { $_ }"') do set VERSION=%%a

if "%VERSION%"=="" (
    echo ERROR: Could not determine version from Headless\PerformanceMonitor.Headless.csproj.
    exit /b 1
)

echo Version: %VERSION%
echo.

if not exist ".tmp" mkdir ".tmp"
if not exist ".nuget" mkdir ".nuget"
if not exist ".dotnet-home" mkdir ".dotnet-home"
set TEMP=%CD%\.tmp
set TMP=%CD%\.tmp
set NUGET_PACKAGES=%CD%\.nuget
set DOTNET_CLI_HOME=%CD%\.dotnet-home
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CMD=dotnet
if exist "%CD%\.dotnet-sdk\dotnet.exe" set DOTNET_CMD=%CD%\.dotnet-sdk\dotnet.exe
if exist "%CD%\..\.dotnet-sdk\dotnet.exe" set DOTNET_CMD=%CD%\..\.dotnet-sdk\dotnet.exe

if not exist "releases" mkdir releases
if exist "publish\Headless" rmdir /S /Q "publish\Headless"
mkdir "publish\Headless"

echo Publishing Headless service and website...
"%DOTNET_CMD%" publish Headless\PerformanceMonitor.Headless.csproj -c Release -r win-x64 --self-contained true -o publish\Headless\app --source https://api.nuget.org/v3/index.json

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Headless publish failed!
    exit /b 1
)

echo Copying installer files...
copy "Headless\appsettings.example.json" "publish\Headless\app\appsettings.example.json" >nul
copy "Headless\packaging\install-headless-service.ps1" "publish\Headless\" >nul
copy "Headless\packaging\uninstall-headless-service.ps1" "publish\Headless\" >nul
copy "Headless\packaging\InstallHeadlessService.cmd" "publish\Headless\" >nul
copy "Headless\packaging\InstallHeadlessService-WindowsAuth.cmd" "publish\Headless\" >nul
copy "Headless\packaging\UninstallHeadlessService.cmd" "publish\Headless\" >nul
copy "Headless\packaging\README.md" "publish\Headless\README.md" >nul
if exist LICENSE copy LICENSE "publish\Headless\" >nul
if exist THIRD_PARTY_NOTICES.md copy THIRD_PARTY_NOTICES.md "publish\Headless\" >nul

set ZIPNAME=PerformanceMonitorHeadless-%VERSION%-win-x64.zip
if exist "releases\%ZIPNAME%" del "releases\%ZIPNAME%"

echo Creating ZIP package...
powershell -NoProfile -Command "Compress-Archive -Path 'publish\Headless\*' -DestinationPath 'releases\%ZIPNAME%' -Force"

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: ZIP creation failed!
    exit /b 1
)

echo.
echo ========================================
echo  Headless Package Complete!
echo ========================================
echo.
echo Output: releases\%ZIPNAME%
for %%A in ("releases\%ZIPNAME%") do echo Size: %%~zA bytes
echo.

endlocal
