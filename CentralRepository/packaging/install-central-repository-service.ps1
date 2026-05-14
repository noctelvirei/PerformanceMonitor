[CmdletBinding()]
param(
    [string]$ServiceName = "PerformanceMonitorCentralRepository",
    [string]$LegacyServiceName = "PerformanceMonitorHeadless",
    [string]$DisplayName = "Performance Monitor Central Repository",
    [string]$Url = "http://localhost:5155",
    [switch]$PromptForServiceAccount,
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$OpenBrowser
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
    throw "Run this installer as Administrator so it can register the Windows service."
}

if ($PromptForServiceAccount -and -not $Credential) {
    $Credential = Get-Credential -Message "Account to run the Performance Monitor Central Repository service"
}

$packageRoot = Split-Path -Parent $PSCommandPath
$appDirectory = Join-Path $packageRoot "app"
$exePath = Join-Path $appDirectory "PerformanceMonitor.CentralRepository.exe"
$configPath = Join-Path $appDirectory "appsettings.json"
$exampleConfigPath = Join-Path $appDirectory "appsettings.example.json"

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Could not find $exePath. Keep the installer scripts next to the packaged app folder."
}

if (-not (Test-Path -LiteralPath $configPath)) {
    if (Test-Path -LiteralPath $exampleConfigPath) {
        Copy-Item -LiteralPath $exampleConfigPath -Destination $configPath
    }
    else {
        @{
            Urls = $Url
            Monitor = @{
                StorageProvider = "DuckDb"
                StoragePath = "data\central-repository\performance-monitor.duckdb"
                ArchiveDirectory = "data\central-repository\parquet"
                Repository = @{
                    ConnectionMode = "Windows"
                    DataSource = ""
                    InitialCatalog = "PerformanceMonitorRepository"
                    Encrypt = "Optional"
                    TrustServerCertificate = $true
                }
                IngestApiKey = ""
                CollectionIntervalSeconds = 60
                MaxConcurrentServers = 8
                CommandTimeoutSeconds = 30
                ArchiveIntervalMinutes = 60
                HotDataDays = 7
                McpAccess = @{
                    Enabled = $true
                    AuthMode = "None"
                    PublicBaseUrl = $Url
                    AllowLocalWithoutApiKey = $false
                }
                AlertRules = @{
                    Enabled = $true
                    CpuEnabled = $true
                    CpuWarningThreshold = 80
                    CpuCriticalThreshold = 90
                    LongRunningQueryEnabled = $true
                    LongRunningQueryWarningMinutes = 15
                    LongRunningQueryCriticalMinutes = 30
                    BlockingEnabled = $true
                    DeadlockEnabled = $true
                    MemoryGrantEnabled = $true
                    MemoryGrantWarningSeconds = 5
                    MemoryGrantCriticalSeconds = 30
                    FileLatencyEnabled = $true
                    FileLatencyWarningMs = 50
                    FileLatencyCriticalMs = 200
                    LongRunningJobEnabled = $true
                    LongRunningJobWarningMinutes = 60
                    LongRunningJobCriticalMinutes = 240
                }
                Collectors = @()
                Servers = @()
            }
        } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
    }
}

$settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$settings | Add-Member -MemberType NoteProperty -Name "Urls" -Value $Url -Force
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path $appDirectory "data\central-repository") -Force | Out-Null

$binaryPath = "`"$exePath`" --urls `"$Url`""
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existingService -and $LegacyServiceName) {
    $legacyService = Get-Service -Name $LegacyServiceName -ErrorAction SilentlyContinue
    if ($legacyService) {
        Write-Step "Found legacy service '$LegacyServiceName'; updating it for compatibility."
        $ServiceName = $LegacyServiceName
        $existingService = $legacyService
    }
}

if ($existingService) {
    Write-Step "Updating existing service '$ServiceName'."
    if ($existingService.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    & sc.exe config $ServiceName binPath= $binaryPath DisplayName= $DisplayName start= delayed-auto | Write-Host
}
else {
    Write-Step "Creating service '$ServiceName'."
    $serviceParameters = @{
        Name = $ServiceName
        DisplayName = $DisplayName
        BinaryPathName = $binaryPath
        StartupType = "Automatic"
    }

    if ($Credential) {
        $serviceParameters.Credential = $Credential
    }

    New-Service @serviceParameters | Out-Null
    & sc.exe config $ServiceName start= delayed-auto | Write-Host
}

& sc.exe description $ServiceName "Central SQL Server Performance Monitor estate collector and website." | Write-Host

if ($Credential -and $existingService) {
    $networkCredential = $Credential.GetNetworkCredential()
    & sc.exe config $ServiceName obj= $Credential.UserName password= $networkCredential.Password | Write-Host
}

Write-Step "Starting service."
Start-Service -Name $ServiceName

Write-Step "Installed. Open $Url and use Settings to add SQL Servers."

if ($OpenBrowser) {
    Start-Process $Url
}
