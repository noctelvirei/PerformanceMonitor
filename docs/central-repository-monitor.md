# Central Repository Estate Monitor

The central repository service is the central-server version of Performance Monitor. It runs on one monitoring server, connects remotely to SQL Server instances, stores results centrally, and serves the website from the same process.

## Current Central Service

The central service includes:

- central ASP.NET Core host
- background collector loop
- server inventory from configuration
- Windows auth or SQL auth via normal SQL Server connection strings
- DuckDB and Parquet storage on the monitoring server, or a central SQL Server repository
- HTTP API
- ingest API for child collectors to report to a parent dashboard
- estate overview website with traffic-light server panels, alert rail, collector log, CPU chart, waits, query/resource/memory/job/config tabs
- in-page alert toasts
- optional browser notifications for red/yellow state changes
- central-service collectors:
  - `server_properties`
  - `wait_stats`
  - `cpu_utilization`
  - `waiting_tasks`
  - `query_stats`
  - `procedure_stats`
  - `query_store`
  - `query_snapshots`
  - `file_io_stats`
  - `memory_stats`
  - `memory_clerks`
  - `memory_pressure_events`
  - `tempdb_stats`
  - `perfmon_stats`
  - `memory_grant_stats`
  - `session_stats`
  - `server_config`
  - `database_config`
  - `database_scoped_config`
  - `trace_flags`
  - `running_jobs`
  - `database_size_stats`
  - `deadlocks`
  - `blocked_process_report`

It does not install SQL Agent jobs on monitored servers.

## Project

```powershell
D:\gitbhub\PerformanceMonitor\CentralRepository\PerformanceMonitor.CentralRepository.csproj
```

Default URL:

```text
http://localhost:5155
```

## Configuration

The browser Settings page is the preferred configuration path. Open the website and choose **Settings** to add servers, set the purpose group, choose Windows or SQL authentication, pick DuckDB or SQL Server repository storage, change collector schedules, and test connections.

Settings are saved to the monitoring server's local `CentralRepository\appsettings.json`, which is ignored by git. SQL passwords entered through Settings are protected with Windows DPAPI for the user or service account running the central repository service.

Alert thresholds are also configured in Settings. The central defaults are intentionally close to Lite's operator defaults: SQL CPU warning/critical, long-running query warning/critical, memory grant wait, file latency, long-running job, blocking, and deadlock rules all feed the overview traffic lights.

The Settings page can discover SQL Server instances through dbatools' `Find-DbaInstance` command. The central repository service starts PowerShell on the monitoring server, imports dbatools, and runs `Find-DbaInstance` there; it does not install anything or run commands on the monitored SQL Servers. Install dbatools for the same Windows account that runs the central repository service, or install it for all users on the monitoring server. If dbatools is missing, Discovery fails during preflight with an install message. Use **Discovery** to scan named targets, Active Directory SQL SPNs, browser enumeration, or an IP range. Discovery runs as a tracked background job; the page shows the resolved scan plan, PowerShell/dbatools startup, elapsed wait messages, per-phase timeout, and final result instead of leaving the browser silent. Discovered instances are shown as candidates; choose **Add** to turn one into a normal monitored server entry.

Discovery only proves that an instance can be found. After a discovered instance is added, the next collector cycle attempts to log in with the configured Windows or SQL credentials. If SQL Server rejects those credentials, the service records an `AUTH_FAILED` server-connection state, raises a red dashboard alert, and clears it automatically once a later connection succeeds.

Discovery input rules:

- `Discovery Mode`: choose one source first: known servers, registered SQL SPNs, AD Windows servers, AD computers, IP range, or SQL Browser broadcast. The page changes the available scan options to match that source.
- `Known servers`: scans the named computer names or SQL hosts directly.
- `Registered SQL SPNs`: uses Active Directory service principal names to find registered SQL instances. This is usually a sharper shortcut than sweeping an entire IP range.
- `IP range`: uses dbatools-supported IP range syntax such as `10.1.164.0/24` or `10.1.164.1-10.1.164.254`. A bare `10.1.164.0` means that one address only.
- `AD Windows servers` and `AD computers`: require a domain controller, then scan the discovered host list for SQL.
- `SQL Browser broadcast`: uses DataSourceEnumeration without requiring targets, IP ranges, or a domain controller.

For a dozen or more dev boxes, a single central SQL repository is usually the cleanest shape. For larger estates, run one collector per estate boundary, such as Development, Staging, and Production, and have those collectors post to the parent dashboard API. The monitored SQL Servers still only need normal remote query permissions; they do not need local Performance Monitor databases or Agent jobs.

You can still seed a local config from the example if you are building or debugging the service:

```powershell
Copy-Item D:\gitbhub\PerformanceMonitor\CentralRepository\appsettings.example.json D:\gitbhub\PerformanceMonitor\CentralRepository\appsettings.json
D:\gitbhub\PerformanceMonitor\CentralRepository\appsettings.json
```

Recommended pattern: keep secrets out of JSON and point each server at an environment variable.

```json
{
  "Monitor": {
    "StoragePath": "data\\central-repository\\performance-monitor.duckdb",
    "ArchiveDirectory": "data\\central-repository\\parquet",
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

For laptop or monitoring-server installs, build the central repository package:

```powershell
D:\gitbhub\PerformanceMonitor\package-central-repository.cmd
```

The package is written to:

```text
D:\gitbhub\PerformanceMonitor\releases\PerformanceMonitorCentralRepository-<version>-win-x64.zip
```

Extract the ZIP to the folder where you want the monitor to live, then run one of the package launchers as an administrator:

- `InstallCentralRepositoryService.cmd` for SQL authentication or connection strings.
- `InstallCentralRepositoryService-WindowsAuth.cmd` when Windows authentication to SQL Servers should come from a domain/service account.

The installer registers and starts the `PerformanceMonitorCentralRepository` Windows service, opens the website, and leaves server setup to the browser Settings page. The uninstall launcher removes the Windows service but keeps local settings and collected data by default. If it finds the earlier `PerformanceMonitorHeadless` service, it updates that legacy service name in place for compatibility.

## Run Locally

This is only for development. Use the package above for normal laptop or monitoring-server installs.

Use the workspace-local SDK if the machine does not have a .NET SDK on `PATH`:

```powershell
$env:TEMP = "D:\gitbhub\.tmp"
$env:TMP = "D:\gitbhub\.tmp"
$env:NUGET_PACKAGES = "D:\gitbhub\.nuget"
$env:DOTNET_CLI_HOME = "D:\gitbhub\.dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

D:\gitbhub\.dotnet-sdk\dotnet.exe run --project D:\gitbhub\PerformanceMonitor\CentralRepository\PerformanceMonitor.CentralRepository.csproj --source https://api.nuget.org/v3/index.json
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
POST /api/settings/discover-servers
POST /api/ingest/snapshot
GET /api/storage
GET /api/collection-log?limit=200
GET /api/servers/{serverId}/waits?hours=1&limit=20
GET /api/servers/{serverId}/cpu?hours=1
GET /api/servers/{serverId}/waiting-tasks?hours=1&limit=50
GET /api/servers/{serverId}/experience?hours=1
GET /api/servers/{serverId}/collectors/{collectorName}/samples?hours=1&limit=100
```

## Central Tooling / MCP

The central service also exposes a read-only MCP server over the same ASP.NET Core host. It reads through the central telemetry interface, so tools work the same whether storage is DuckDB or a central SQL Server repository.

```text
http://localhost:5155/mcp
```

The central tools use the same familiar names for data the central service now collects: `list_servers`, `get_server_summary`, `get_collection_health`, `get_alerts`, `get_wait_stats`, `get_waiting_tasks`, `get_cpu_utilization`, `get_top_queries_by_cpu`, `get_top_procedures_by_cpu`, `get_query_store_top`, `get_active_queries`, `get_memory_stats`, `get_memory_clerks`, `get_memory_grants`, `get_file_io_stats`, `get_tempdb_trend`, `get_perfmon_stats`, `get_session_stats`, `get_running_jobs`, `get_server_config`, `get_database_config`, `get_database_scoped_config`, `get_trace_flags`, `get_deadlocks`, `get_blocked_process_reports`, `get_database_sizes`, and `get_collector_samples`.

These tools do not connect directly to monitored SQL Servers and do not execute arbitrary SQL. They return only data already gathered into the central repository.

OpenAI, Codex, ChatGPT, Claude Code, and Anthropic API callers can all use the central MCP tools, but the MCP server itself is model-less. The model, subscription, API key, or user-account sign-in belongs to the client that is calling `/mcp`.

Configure **MCP Endpoint** in Settings:

- `Trusted Network`: no MCP auth, intended only for isolated internal testing.
- `API Key`: accepts `Authorization: Bearer <token>` or `X-PerformanceMonitor-MCP-Key: <token>`. This is just endpoint access for PerformanceMonitor, not OpenAI or Anthropic model sign-in.

For a server install, use HTTPS for the public base URL and prefer API-key access unless the endpoint is genuinely isolated.

Typical connection paths:

- Codex: sign in to Codex on your laptop, then add the monitor's MCP server URL from that laptop. The Settings page shows the URL and a copyable `codex mcp add performance-monitor --url ...` command.
- ChatGPT / Claude Code: sign in inside those clients and add the monitor's MCP server URL there. PerformanceMonitor does not sign in to the model provider for them.
- OpenAI Responses API: authenticate to OpenAI as usual, configure an MCP tool with the monitor `server_url`, and include an `authorization` value only when this monitor requires one.
- Anthropic Messages API: authenticate to Anthropic as usual, configure `mcp_servers[].url`, and include `authorization_token` only when this monitor requires one.

If the dashboard later hosts its own embedded assistant, that is a separate feature from exposing `/mcp`. In that mode the monitoring server would become a model caller and would need provider credentials or per-user provider sessions of its own.

## Traffic Lights And Alerts

The overview cards are intended to work like an estate traffic-light board:

- green: enabled, recently contacted, and no recent collector alerts
- yellow: enabled but not fully healthy yet, for example no successful collection or stale contact
- red: connection failure or any recent alert-worthy collector failure
- disabled: configured but not being monitored

The browser page raises an in-page toast when a server enters red or yellow. If browser notifications are enabled with the button in the header, the same state change also raises a native browser notification.

Alert-worthy means connection failures, login failures (`AUTH_FAILED`), collector statuses where the latest run for that server/collector is `ERROR` or `PERMISSIONS`, currently blocked sessions from the latest `waiting_tasks` snapshot, and red/yellow conditions projected from the latest central collector samples. Current sample-based rules include blocked process counters, pending memory grants, recent memory pressure, high file latency, long-running SQL Agent jobs, recent deadlock or blocked process events, non-online databases, and AUTO_CLOSE/AUTO_SHRINK database settings. A later successful collector run with a clean latest snapshot clears the relevant alert automatically, so the server panel colour returns to the next-worst current state instead of holding onto stale failures.

## Full Parity Map

Central service mode should grow by reusing existing Full/Lite code, not by copying UI-specific logic into a second implementation.

| Area | Full/Lite source | Central service status | Reuse path |
| --- | --- | --- | --- |
| Server metadata, waits, CPU | Shared `Collectors` project | Available | Already extracted and called by Lite and Central Repository |
| Waiting tasks/blocking snapshot | Lite `RemoteCollectorService.WaitingTasks` | Available | SQL collection moved into shared `Collectors`; Lite and Central Repository call it |
| Memory stats, clerks, pressure | Lite `RemoteCollectorService.Memory` | Available as central experience panels and samples | Shared `Collectors` row definitions feed central storage, alert projection, API, and web tabs |
| Query stats, procedure stats, active requests, Query Store | Lite remote collectors and Full `DatabaseService.QueryPerformance` | Available as central experience panels and samples | Shared `Collectors` row definitions feed central storage, API, and web tabs |
| File I/O, tempdb, perfmon, sessions | Lite remote collectors and Full resource metrics services | Available as central experience panels and samples | Shared `Collectors` row definitions feed central storage, alert projection, API, and web tabs |
| Deadlocks and blocked process reports | Lite remote collectors and Full blocking/deadlock services | Available when target permissions/XE sources exist | Reads system_health and available blocked-process XE ring buffers centrally |
| SQL Agent jobs and database size | Lite remote collectors | Available as central experience panels and samples | SQL Agent data is naturally permission-dependent because `msdb` access varies by estate |
| Full MCP toolset | `Dashboard\Mcp` and `Lite\Mcp` | Core central tools available | Central MCP tools read through `IEstateTelemetryReader`; deeper plan analysis and inference graph parity still need extraction into shared services |
| Full SQL Agent/install repository mode | `install\*.sql`, `Installer.Core`, Dashboard `DatabaseService` | Supported only as central storage today | If using a Full repository, call existing procs/views instead of reimplementing them |
| SQL instance discovery | dbatools `Find-DbaInstance` | Available in Settings | Calls dbatools from the monitoring server and imports selected candidates |

## Storage

Default local storage:

```powershell
D:\gitbhub\PerformanceMonitor\CentralRepository\data\central-repository\performance-monitor.duckdb
```

Archived Parquet:

```powershell
D:\gitbhub\PerformanceMonitor\CentralRepository\data\central-repository\parquet
```

Archival runs in-process. Rows older than `HotDataDays` are copied to Parquet and deleted from the hot DuckDB tables.

SQL Server repository storage can be selected in **Settings**. Point it at one parent SQL database, or use one repository per environment if that better matches the estate. The central repository service creates its repository tables on first use.

The ingest API is disabled until `Ingest API Key` is set in **Settings**. Child collectors should send that key as:

```text
X-PerformanceMonitor-Key: <key>
```

## Where This Goes Next

The remaining parity work is now less about whether the central service can collect the Full/Lite families and more about first-class presentation and tooling: typed read models for each collector family, richer web screens, MCP bindings over the central API, and deeper Full repository interop where an existing Erik-style repository already exists.
