# Headless Estate Monitor

The headless host is the central-server version of Performance Monitor. It runs on one monitoring server, connects remotely to SQL Server instances, stores results centrally, and serves the website from the same process.

## Current Thin Slice

The first implementation includes:

- central ASP.NET Core host
- background collector loop
- server inventory from configuration
- Windows auth or SQL auth via normal SQL Server connection strings
- DuckDB and Parquet storage on the monitoring server, or a central SQL Server repository
- HTTP API
- ingest API for child collectors to report to a parent dashboard
- estate overview website with traffic-light server panels, collector log, CPU chart, and top waits
- in-page alert toasts
- optional browser notifications for red/yellow state changes
- initial collectors:
  - `server_properties`
  - `wait_stats`
  - `cpu_utilization`

It does not install SQL Agent jobs on monitored servers.

## Project

```powershell
D:\gitbhub\PerformanceMonitor\Headless\PerformanceMonitor.Headless.csproj
```

Default URL:

```text
http://localhost:5155
```

## Configuration

The browser Settings page is the preferred configuration path. Open the website and choose **Settings** to add servers, set the purpose group, choose Windows or SQL authentication, pick DuckDB or SQL Server repository storage, change collector schedules, and test connections.

Settings are saved to the monitoring server's local `Headless\appsettings.json`, which is ignored by git. SQL passwords entered through Settings are protected with Windows DPAPI for the user or service account running the headless host.

For a dozen or more dev boxes, a single central SQL repository is usually the cleanest shape. For larger estates, run one collector per estate boundary, such as Development, Staging, and Production, and have those collectors post to the parent dashboard API. The monitored SQL Servers still only need normal remote query permissions; they do not need local Performance Monitor databases or Agent jobs.

You can still seed a local config from the example if you are building or debugging the service:

```powershell
Copy-Item D:\gitbhub\PerformanceMonitor\Headless\appsettings.example.json D:\gitbhub\PerformanceMonitor\Headless\appsettings.json
D:\gitbhub\PerformanceMonitor\Headless\appsettings.json
```

Recommended pattern: keep secrets out of JSON and point each server at an environment variable.

```json
{
  "Monitor": {
    "StoragePath": "data\\headless\\performance-monitor.duckdb",
    "ArchiveDirectory": "data\\headless\\parquet",
    "CollectionIntervalSeconds": 60,
    "MaxConcurrentServers": 8,
    "CommandTimeoutSeconds": 30,
    "ArchiveIntervalMinutes": 60,
    "HotDataDays": 7,
    "Servers": [
      {
        "Id": "dev-sql-01",
        "DisplayName": "DEV-SQL-01",
        "Purpose": "Development",
        "ConnectionStringEnvironmentVariable": "PM_DEV_SQL_01",
        "Enabled": true
      }
    ]
  }
}
```

Windows auth example:

```powershell
$env:PM_DEV_SQL_01 = "Server=DEV-SQL-01;Database=master;Integrated Security=true;Encrypt=Optional;TrustServerCertificate=true"
```

SQL auth example:

```powershell
$env:PM_DEV_SQL_02 = "Server=DEV-SQL-02;Database=master;User ID=pm_reader;Password=<password>;Encrypt=Mandatory;TrustServerCertificate=true"
```

For dozens of servers, use stable `Id` values. Those ids become the partition key in DuckDB and API URLs. Set `Purpose` to values such as `Development`, `Staging`, or `Production` so the dashboard can group and filter the estate.

For normal use, avoid hand-editing this file after first launch. Use the Settings page so server changes are written consistently and picked up on the next collection cycle.

## Install Package

For laptop or monitoring-server installs, build the headless package:

```powershell
D:\gitbhub\PerformanceMonitor\package-headless.cmd
```

The package is written to:

```text
D:\gitbhub\PerformanceMonitor\releases\PerformanceMonitorHeadless-<version>-win-x64.zip
```

Extract the ZIP to the folder where you want the monitor to live, then run one of the package launchers as an administrator:

- `InstallHeadlessService.cmd` for SQL authentication or connection strings.
- `InstallHeadlessService-WindowsAuth.cmd` when Windows authentication to SQL Servers should come from a domain/service account.

The installer registers and starts the `PerformanceMonitorHeadless` Windows service, opens the website, and leaves server setup to the browser Settings page. The uninstall launcher removes the Windows service but keeps local settings and collected data by default.

## Run Locally

This is only for development. Use the package above for normal laptop or monitoring-server installs.

Use the workspace-local SDK if the machine does not have a .NET SDK on `PATH`:

```powershell
$env:TEMP = "D:\gitbhub\.tmp"
$env:TMP = "D:\gitbhub\.tmp"
$env:NUGET_PACKAGES = "D:\gitbhub\.nuget"
$env:DOTNET_CLI_HOME = "D:\gitbhub\.dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

D:\gitbhub\.dotnet-sdk\dotnet.exe run --project D:\gitbhub\PerformanceMonitor\Headless\PerformanceMonitor.Headless.csproj --source https://api.nuget.org/v3/index.json
```

Open:

```text
http://localhost:5155
```

Then open **Settings**, add your SQL Servers, test the connection, and save.

## API

```text
GET /api/summary
GET /api/servers
GET /api/alerts
GET /api/settings
PUT /api/settings
POST /api/settings/test-connection
POST /api/settings/test-repository
POST /api/ingest/snapshot
GET /api/storage
GET /api/collection-log?limit=200
GET /api/servers/{serverId}/waits?hours=1&limit=20
GET /api/servers/{serverId}/cpu?hours=1
```

## Traffic Lights And Alerts

The overview cards are intended to work like an estate traffic-light board:

- green: enabled, recently contacted, and no recent collector alerts
- yellow: enabled but not fully healthy yet, for example no successful collection or stale contact
- red: connection failure or any recent alert-worthy collector failure
- disabled: configured but not being monitored

The browser page raises an in-page toast when a server enters red or yellow. If browser notifications are enabled with the button in the header, the same state change also raises a native browser notification.

For the current thin slice, "alert-worthy" means connection failures or collector statuses where the latest run for that server/collector is `ERROR` or `PERMISSIONS`. A later successful collector run clears that alert automatically, so the server panel colour returns to the next-worst current state instead of holding onto stale failures. As more collectors are ported, SQL performance alerts should feed the same red/yellow state so the panel color changes whenever something needs checking.

## Storage

Default local storage:

```powershell
D:\gitbhub\PerformanceMonitor\Headless\data\headless\performance-monitor.duckdb
```

Archived Parquet:

```powershell
D:\gitbhub\PerformanceMonitor\Headless\data\headless\parquet
```

Archival runs in-process. Rows older than `HotDataDays` are copied to Parquet and deleted from the hot DuckDB tables.

SQL Server repository storage can be selected in **Settings**. Point it at one parent SQL database, or use one repository per environment if that better matches the estate. The headless service creates its repository tables on first use.

The ingest API is disabled until `Ingest API Key` is set in **Settings**. Child collectors should send that key as:

```text
X-PerformanceMonitor-Key: <key>
```

## Where This Goes Next

The next ports should come from Lite's existing remote collectors:

- query stats
- query store
- file I/O
- memory stats
- blocking/deadlocks
- database size/capacity
- running SQL Agent jobs as optional `msdb` read telemetry

The website should then grow from a status console into the Redgate-style estate overview: traffic lights, stale data warnings, top pain by server, recent regressions, blocking hotspots, and capacity risk.
