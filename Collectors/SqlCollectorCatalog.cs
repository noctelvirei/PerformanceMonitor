namespace PerformanceMonitor.Collectors;

public static class SqlCollectorNames
{
    public const string ServerProperties = "server_properties";
    public const string WaitStats = "wait_stats";
    public const string CpuUtilization = "cpu_utilization";
    public const string WaitingTasks = "waiting_tasks";
    public const string QueryStats = "query_stats";
    public const string ProcedureStats = "procedure_stats";
    public const string QueryStore = "query_store";
    public const string QuerySnapshots = "query_snapshots";
    public const string FileIoStats = "file_io_stats";
    public const string MemoryStats = "memory_stats";
    public const string MemoryClerks = "memory_clerks";
    public const string MemoryPressureEvents = "memory_pressure_events";
    public const string TempDbStats = "tempdb_stats";
    public const string PerfmonStats = "perfmon_stats";
    public const string MemoryGrantStats = "memory_grant_stats";
    public const string SessionStats = "session_stats";
    public const string ServerConfig = "server_config";
    public const string DatabaseConfig = "database_config";
    public const string DatabaseScopedConfig = "database_scoped_config";
    public const string TraceFlags = "trace_flags";
    public const string RunningJobs = "running_jobs";
    public const string DatabaseSizeStats = "database_size_stats";
    public const string Deadlocks = "deadlocks";
    public const string BlockedProcessReport = "blocked_process_report";
}

public sealed record SqlCollectorScheduleDefinition(
    string Name,
    int DefaultFrequencySeconds,
    int RetentionDays,
    string Description);

public static class SqlCollectorCatalog
{
    public static IReadOnlyList<SqlCollectorScheduleDefinition> DefaultSchedules { get; } =
    [
        new(SqlCollectorNames.ServerProperties, 0, 365, "Server edition, licensing, CPU/memory hardware metadata"),
        new(SqlCollectorNames.WaitStats, 60, 30, "Wait statistics from sys.dm_os_wait_stats"),
        new(SqlCollectorNames.CpuUtilization, 60, 30, "CPU utilization from SQL Server resource samples"),
        new(SqlCollectorNames.WaitingTasks, 60, 7, "Point-in-time waiting tasks from sys.dm_os_waiting_tasks"),
        new(SqlCollectorNames.QueryStats, 60, 30, "Top query statistics from sys.dm_exec_query_stats"),
        new(SqlCollectorNames.ProcedureStats, 60, 30, "Stored procedure statistics from sys.dm_exec_procedure_stats"),
        new(SqlCollectorNames.QueryStore, 300, 30, "Query Store runtime stats from user databases"),
        new(SqlCollectorNames.QuerySnapshots, 60, 7, "Currently running query snapshots"),
        new(SqlCollectorNames.FileIoStats, 60, 30, "File I/O statistics from sys.dm_io_virtual_file_stats"),
        new(SqlCollectorNames.MemoryStats, 60, 30, "Memory statistics from SQL Server DMVs and counters"),
        new(SqlCollectorNames.MemoryClerks, 300, 30, "Top memory clerk allocations"),
        new(SqlCollectorNames.MemoryPressureEvents, 60, 30, "Memory pressure events from resource monitor ring buffers"),
        new(SqlCollectorNames.TempDbStats, 60, 30, "TempDB space usage"),
        new(SqlCollectorNames.PerfmonStats, 60, 30, "Key sys.dm_os_performance_counters values"),
        new(SqlCollectorNames.MemoryGrantStats, 60, 30, "Active memory grant pressure"),
        new(SqlCollectorNames.SessionStats, 60, 30, "Session and request aggregate counts"),
        new(SqlCollectorNames.ServerConfig, 0, 30, "Server-level configuration values"),
        new(SqlCollectorNames.DatabaseConfig, 0, 30, "Database configuration inventory"),
        new(SqlCollectorNames.DatabaseScopedConfig, 0, 30, "Database-scoped configuration inventory"),
        new(SqlCollectorNames.TraceFlags, 0, 30, "Active trace flags"),
        new(SqlCollectorNames.RunningJobs, 300, 7, "Currently running SQL Agent jobs"),
        new(SqlCollectorNames.DatabaseSizeStats, 300, 30, "Database data and log file size inventory"),
        new(SqlCollectorNames.Deadlocks, 60, 30, "Deadlocks from the system_health session"),
        new(SqlCollectorNames.BlockedProcessReport, 60, 30, "Blocked process reports from Extended Events when configured")
    ];
}
