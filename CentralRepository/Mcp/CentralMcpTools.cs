using System.ComponentModel;
using ModelContextProtocol.Server;
using PerformanceMonitor.CentralRepository.Services;

namespace PerformanceMonitor.CentralRepository.Mcp;

#pragma warning disable CA1707 // MCP clients expect snake_case tool parameter names.

[McpServerToolType]
public sealed class CentralMcpTools
{
    [McpServerTool(Name = "list_servers"), Description("Lists all monitored SQL Server instances with their current health, purpose, alert count, and last contact time.")]
    public static Task<string> ListServers(CentralToolService tools)
        => tools.ListServersAsync();

    [McpServerTool(Name = "get_server_summary"), Description("Gets a quick central-repository health summary for a SQL Server instance, including latest CPU, top waits, and active alerts.")]
    public static Task<string> GetServerSummary(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetServerSummaryAsync(server_name);

    [McpServerTool(Name = "get_collection_health"), Description("Shows collector health for a server: latest run status, errors, row counts, and duration by collector.")]
    public static Task<string> GetCollectionHealth(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetCollectionHealthAsync(server_name);

    [McpServerTool(Name = "get_alerts"), Description("Gets current central dashboard alerts. Filter to a server to see only alerts attached to that server.")]
    public static Task<string> GetAlerts(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Leave blank for all active alerts.")] string? server_name = null)
        => tools.GetAlertsAsync(server_name);

    [McpServerTool(Name = "get_alert_settings"), Description("Gets the central service alert threshold settings used to colour dashboard cards and generate active alerts.")]
    public static Task<string> GetAlertSettings(CentralToolService tools)
        => tools.GetAlertSettingsAsync();

    [McpServerTool(Name = "get_wait_stats"), Description("Gets the top SQL Server wait types aggregated over a time period from the central repository.")]
    public static Task<string> GetWaitStats(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of wait types to return. Default 20.")] int limit = 20)
        => tools.GetWaitStatsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_waiting_tasks"), Description("Gets recently captured waiting tasks, including wait type, duration, blocker, resource, and database where available.")]
    public static Task<string> GetWaitingTasks(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of waiting task rows. Default 50.")] int limit = 50)
        => tools.GetWaitingTasksAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_cpu_utilization"), Description("Gets SQL Server CPU and non-SQL CPU utilization samples over time from the central repository.")]
    public static Task<string> GetCpuUtilization(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1)
        => tools.GetCpuUtilizationAsync(server_name, hours_back);

    [McpServerTool(Name = "get_top_queries_by_cpu"), Description("Gets expensive cached queries collected from sys.dm_exec_query_stats, ranked by total CPU.")]
    public static Task<string> GetTopQueriesByCpu(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 20.")] int top = 20,
        [Description("Optional database name filter.")] string? database_name = null)
        => tools.GetTopQueriesByCpuAsync(server_name, hours_back, top, database_name);

    [McpServerTool(Name = "get_top_procedures_by_cpu"), Description("Gets expensive stored procedures collected from sys.dm_exec_procedure_stats, ranked by total CPU.")]
    public static Task<string> GetTopProceduresByCpu(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 20.")] int top = 20,
        [Description("Optional database name filter.")] string? database_name = null)
        => tools.GetTopProceduresByCpuAsync(server_name, hours_back, top, database_name);

    [McpServerTool(Name = "get_query_store_top"), Description("Gets expensive Query Store queries collected centrally from databases where Query Store is enabled.")]
    public static Task<string> GetQueryStoreTop(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 20.")] int top = 20,
        [Description("Optional database name filter.")] string? database_name = null)
        => tools.GetQueryStoreTopAsync(server_name, hours_back, top, database_name);

    [McpServerTool(Name = "get_active_queries"), Description("Gets active query snapshots captured centrally from current requests.")]
    public static Task<string> GetActiveQueries(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetActiveQueriesAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_server_properties"), Description("Gets the latest server inventory properties projected by central collection: version, edition, CPU, memory, and health metadata.")]
    public static Task<string> GetServerProperties(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetServerPropertiesAsync(server_name);

    [McpServerTool(Name = "get_database_sizes"), Description("Gets centrally collected database size inventory rows.")]
    public static Task<string> GetDatabaseSizes(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Optional database name filter.")] string? database_name = null,
        [Description("Number of rows. Default 100.")] int limit = 100)
        => tools.GetDatabaseSizesAsync(server_name, database_name, limit);

    [McpServerTool(Name = "get_memory_stats"), Description("Gets recent memory statistics snapshots: physical memory, target memory, total memory, buffer pool, plan cache, and worker counts.")]
    public static Task<string> GetMemoryStats(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetMemoryStatsAsync(server_name);

    [McpServerTool(Name = "get_memory_clerks"), Description("Gets top memory clerks by allocated MB from central collection.")]
    public static Task<string> GetMemoryClerks(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Number of rows. Default 25.")] int limit = 25)
        => tools.GetMemoryClerksAsync(server_name, limit);

    [McpServerTool(Name = "get_memory_pressure_events"), Description("Gets recent memory pressure notifications collected from SQL Server ring buffers.")]
    public static Task<string> GetMemoryPressureEvents(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetMemoryPressureEventsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_memory_grants"), Description("Gets active query memory grants and waiter information from central collection.")]
    public static Task<string> GetMemoryGrants(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetMemoryGrantsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_resource_semaphore"), Description("Alias for get_memory_grants. Shows memory grant pressure affecting query execution.")]
    public static Task<string> GetResourceSemaphore(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetMemoryGrantsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_file_io_stats"), Description("Gets database file I/O statistics and latency counters collected centrally.")]
    public static Task<string> GetFileIoStats(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Number of rows. Default 100.")] int limit = 100)
        => tools.GetFileIoStatsAsync(server_name, limit);

    [McpServerTool(Name = "get_tempdb_trend"), Description("Gets TempDB space usage over time, including user objects, internal objects, version store, and top TempDB consumer.")]
    public static Task<string> GetTempDbTrend(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24)
        => tools.GetTempDbStatsAsync(server_name, hours_back);

    [McpServerTool(Name = "get_perfmon_stats"), Description("Gets centrally collected SQL Server performance counters such as batch requests/sec, page life expectancy, memory grants pending, and blocked processes.")]
    public static Task<string> GetPerfmonStats(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of rows. Default 100.")] int limit = 100,
        [Description("Optional counter name filter.")] string? counter_name = null)
        => tools.GetPerfmonStatsAsync(server_name, hours_back, limit, counter_name);

    [McpServerTool(Name = "get_session_stats"), Description("Gets session and request aggregate counts from central collection.")]
    public static Task<string> GetSessionStats(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetSessionStatsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_running_jobs"), Description("Gets currently running SQL Agent jobs collected centrally, including current duration where SQL Agent metadata is accessible.")]
    public static Task<string> GetRunningJobs(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetRunningJobsAsync(server_name, limit);

    [McpServerTool(Name = "get_server_config"), Description("Gets server-level sys.configurations rows collected centrally.")]
    public static Task<string> GetServerConfig(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetServerConfigAsync(server_name);

    [McpServerTool(Name = "get_database_config"), Description("Gets database-level configuration inventory collected centrally.")]
    public static Task<string> GetDatabaseConfig(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Optional database name filter.")] string? database_name = null)
        => tools.GetDatabaseConfigAsync(server_name, database_name);

    [McpServerTool(Name = "get_database_scoped_config"), Description("Gets database-scoped configuration rows collected centrally.")]
    public static Task<string> GetDatabaseScopedConfig(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Optional database name filter.")] string? database_name = null)
        => tools.GetDatabaseScopedConfigAsync(server_name, database_name);

    [McpServerTool(Name = "get_trace_flags"), Description("Gets active trace flags collected centrally.")]
    public static Task<string> GetTraceFlags(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null)
        => tools.GetTraceFlagsAsync(server_name);

    [McpServerTool(Name = "get_deadlocks"), Description("Gets recent deadlock events collected centrally from the system_health session.")]
    public static Task<string> GetDeadlocks(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetDeadlocksAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_blocked_process_reports"), Description("Gets blocked process report rows collected centrally when a blocked-process Extended Events source is available.")]
    public static Task<string> GetBlockedProcessReports(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetBlockedProcessReportsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_blocking"), Description("Alias for get_blocked_process_reports for compatibility with the dashboard MCP toolset.")]
    public static Task<string> GetBlocking(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 24.")] int hours_back = 24,
        [Description("Number of rows. Default 50.")] int limit = 50)
        => tools.GetBlockedProcessReportsAsync(server_name, hours_back, limit);

    [McpServerTool(Name = "get_server_experience"), Description("Gets the same grouped central repository read model used by the server detail website tabs.")]
    public static Task<string> GetServerExperience(
        CentralToolService tools,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1)
        => tools.GetServerExperienceAsync(server_name, hours_back);

    [McpServerTool(Name = "get_collector_samples"), Description("Gets raw central collector samples by collector name for cases where no typed tool exists yet.")]
    public static Task<string> GetCollectorSamples(
        CentralToolService tools,
        [Description("Collector name, for example query_stats, memory_stats, file_io_stats, or database_size_stats.")] string collector_name,
        [Description("Server id, SQL Server name, or display name. Optional when only one server is configured.")] string? server_name = null,
        [Description("Hours of history. Default 1.")] int hours_back = 1,
        [Description("Number of rows. Default 100.")] int limit = 100)
        => tools.GetCollectorAsync(server_name, collector_name, hours_back, limit);
}

#pragma warning restore CA1707
