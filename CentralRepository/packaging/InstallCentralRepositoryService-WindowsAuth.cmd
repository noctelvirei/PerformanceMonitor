@echo off
setlocal

net session >nul 2>&1
if %ERRORLEVEL% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-central-repository-service.ps1" -PromptForServiceAccount -OpenBrowser
echo.
pause
