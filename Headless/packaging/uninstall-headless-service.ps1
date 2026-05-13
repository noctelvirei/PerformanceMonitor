[CmdletBinding()]
param(
    [string]$ServiceName = "PerformanceMonitorHeadless",
    [switch]$RemoveLocalData
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step {
    param([string]$Message)
    Write-Host "[Performance Monitor] $Message"
}

if (-not (Test-IsAdministrator)) {
    throw "Run this uninstaller as Administrator so it can remove the Windows service."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Write-Step "Stopping service '$ServiceName'."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    Write-Step "Removing service '$ServiceName'."
    & sc.exe delete $ServiceName | Write-Host
}
else {
    Write-Step "Service '$ServiceName' is not installed."
}

if ($RemoveLocalData) {
    $packageRoot = Split-Path -Parent $PSCommandPath
    $appDirectory = Join-Path $packageRoot "app"
    $dataDirectory = Join-Path $appDirectory "data"
    $configPath = Join-Path $appDirectory "appsettings.json"

    if (Test-Path -LiteralPath $dataDirectory) {
        Write-Step "Removing local data."
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $configPath) {
        Write-Step "Removing local settings."
        Remove-Item -LiteralPath $configPath -Force
    }
}

Write-Step "Uninstall complete."
