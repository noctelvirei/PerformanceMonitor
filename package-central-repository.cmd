@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo.
echo ========================================
echo  Packaging Performance Monitor Central Repository
echo ========================================
echo.

for /f %%a in ('powershell -NoProfile -Command "([xml](Get-Content CentralRepository\PerformanceMonitor.CentralRepository.csproj)).Project.PropertyGroup.Version | Where-Object { $_ }"') do set VERSION=%%a

if "%VERSION%"=="" (
    echo ERROR: Could not determine version from CentralRepository\PerformanceMonitor.CentralRepository.csproj.
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
if exist "publish\CentralRepository" rmdir /S /Q "publish\CentralRepository"
mkdir "publish\CentralRepository"

echo Publishing Central Repository service and website...
"%DOTNET_CMD%" publish CentralRepository\PerformanceMonitor.CentralRepository.csproj -c Release -r win-x64 --self-contained true -o publish\CentralRepository\app --source https://api.nuget.org/v3/index.json

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: Central Repository publish failed!
    exit /b 1
)

echo Copying installer files...
copy "CentralRepository\appsettings.example.json" "publish\CentralRepository\app\appsettings.example.json" >nul
copy "CentralRepository\packaging\install-central-repository-service.ps1" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\uninstall-central-repository-service.ps1" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\InstallCentralRepositoryService.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\InstallCentralRepositoryService-WindowsAuth.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\UninstallCentralRepositoryService.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\InstallHeadlessService.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\InstallHeadlessService-WindowsAuth.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\UninstallHeadlessService.cmd" "publish\CentralRepository\" >nul
copy "CentralRepository\packaging\README.md" "publish\CentralRepository\README.md" >nul
if exist LICENSE copy LICENSE "publish\CentralRepository\" >nul
if exist THIRD_PARTY_NOTICES.md copy THIRD_PARTY_NOTICES.md "publish\CentralRepository\" >nul

set ZIPNAME=PerformanceMonitorCentralRepository-%VERSION%-win-x64.zip
if exist "releases\%ZIPNAME%" del "releases\%ZIPNAME%"

echo Creating ZIP package...
powershell -NoProfile -Command "Compress-Archive -Path 'publish\CentralRepository\*' -DestinationPath 'releases\%ZIPNAME%' -Force"

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: ZIP creation failed!
    exit /b 1
)

echo.
echo ========================================
echo  Central Repository Package Complete!
echo ========================================
echo.
echo Output: releases\%ZIPNAME%
for %%A in ("releases\%ZIPNAME%") do echo Size: %%~zA bytes
echo.

endlocal
