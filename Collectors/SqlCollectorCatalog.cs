namespace PerformanceMonitor.Collectors;

public static class SqlCollectorNames
{
    public const string ServerProperties = "server_properties";
    public const string WaitStats = "wait_stats";
    public const string CpuUtilization = "cpu_utilization";
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
        new(SqlCollectorNames.CpuUtilization, 60, 30, "CPU utilization from SQL Server resource samples")
    ];
}
