# Performance Monitor Central Repository Package

This package installs the central estate monitor on the monitoring machine. It does not install SQL Agent jobs on monitored SQL Servers.

## Install

Extract the ZIP to the folder where you want the service to live, then run one of the installer launchers as an administrator:

- `InstallCentralRepositoryService.cmd` for SQL authentication or connection strings.
- `InstallCentralRepositoryService-WindowsAuth.cmd` if the monitor should connect to SQL Servers with Windows authentication. It prompts for the Windows account that will run the service.

The installer starts the `PerformanceMonitorCentralRepository` Windows service and opens:

```text
http://localhost:5155
```

Use the website's Settings page to add SQL Servers, set their purpose group, choose Windows or SQL authentication, choose DuckDB or SQL Server repository storage, test connections, and save.

SQL passwords saved from Settings are protected with Windows DPAPI for the Windows account running the service.

For a larger estate, you can run a collector per environment and have those collectors post back to the parent dashboard API. Set an ingest API key in Settings before exposing that endpoint beyond localhost.

## Uninstall

Run `UninstallCentralRepositoryService.cmd` as an administrator. Local settings and collected data are left in place unless you run `uninstall-central-repository-service.ps1 -RemoveLocalData`.
