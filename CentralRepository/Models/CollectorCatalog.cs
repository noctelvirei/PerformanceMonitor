using Microsoft.Data.SqlClient;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.CentralRepository.Models;

public static class CollectorCatalog
{
    public const string ServerProperties = SqlCollectorNames.ServerProperties;
    public const string WaitStats = SqlCollectorNames.WaitStats;
    public const string CpuUtilization = SqlCollectorNames.CpuUtilization;
    public const string WaitingTasks = SqlCollectorNames.WaitingTasks;
    public const string QueryStats = SqlCollectorNames.QueryStats;
    public const string ProcedureStats = SqlCollectorNames.ProcedureStats;
    public const string QueryStore = SqlCollectorNames.QueryStore;
    public const string QuerySnapshots = SqlCollectorNames.QuerySnapshots;
    public const string FileIoStats = SqlCollectorNames.FileIoStats;
    public const string MemoryStats = SqlCollectorNames.MemoryStats;
    public const string MemoryClerks = SqlCollectorNames.MemoryClerks;
    public const string MemoryPressureEvents = SqlCollectorNames.MemoryPressureEvents;
    public const string TempDbStats = SqlCollectorNames.TempDbStats;
    public const string PerfmonStats = SqlCollectorNames.PerfmonStats;
    public const string MemoryGrantStats = SqlCollectorNames.MemoryGrantStats;
    public const string SessionStats = SqlCollectorNames.SessionStats;
    public const string ServerConfig = SqlCollectorNames.ServerConfig;
    public const string DatabaseConfig = SqlCollectorNames.DatabaseConfig;
    public const string DatabaseScopedConfig = SqlCollectorNames.DatabaseScopedConfig;
    public const string TraceFlags = SqlCollectorNames.TraceFlags;
    public const string RunningJobs = SqlCollectorNames.RunningJobs;
    public const string DatabaseSizeStats = SqlCollectorNames.DatabaseSizeStats;
    public const string Deadlocks = SqlCollectorNames.Deadlocks;
    public const string BlockedProcessReport = SqlCollectorNames.BlockedProcessReport;
    public const string ServerConnection = "server_connection";
    public const string StatusOnline = "ONLINE";
    public const string StatusSuccess = "SUCCESS";
    public const string StatusError = "ERROR";
    public const string StatusPermissions = "PERMISSIONS";
    public const string StatusAuthenticationFailed = "AUTH_FAILED";

    public static IReadOnlyList<CollectorScheduleOptions> DefaultSchedules =>
        SqlCollectorCatalog.DefaultSchedules
            .Select(schedule => new CollectorScheduleOptions
            {
                Name = schedule.Name,
                FrequencySeconds = schedule.DefaultFrequencySeconds
            })
            .ToList();

    public static bool IsPermissionError(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is 229 or 297 or 300)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsAuthenticationError(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is 18452 or 18456 or 18470 or 18487 or 18488 or 4060)
            {
                return true;
            }
        }

        return exception.Message.Contains("Login failed", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsServerStatusError(string? status)
        => string.Equals(status, StatusError, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StatusAuthenticationFailed, StringComparison.OrdinalIgnoreCase);
}
