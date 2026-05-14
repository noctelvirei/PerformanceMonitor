[CmdletBinding()]
param(
    [string]$ServiceName = "PerformanceMonitorCentralRepository",
    [string]$LegacyServiceName = "PerformanceMonitorHeadless",
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

$serviceNames = @($ServiceName)
if ($LegacyServiceName -and $LegacyServiceName -ne $ServiceName) {
    $serviceNames += $LegacyServiceName
}

foreach ($name in $serviceNames) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne "Stopped") {
            Write-Step "Stopping service '$name'."
            Stop-Service -Name $name -Force -ErrorAction Stop
        }

        Write-Step "Removing service '$name'."
        & sc.exe delete $name | Write-Host
    }
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
