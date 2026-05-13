using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Headless.Models;

public static class CollectorCatalog
{
    public const string ServerProperties = SqlCollectorNames.ServerProperties;
    public const string WaitStats = SqlCollectorNames.WaitStats;
    public const string CpuUtilization = SqlCollectorNames.CpuUtilization;
    public const string ServerConnection = "server_connection";

    public static IReadOnlyList<CollectorScheduleOptions> DefaultSchedules =>
        SqlCollectorCatalog.DefaultSchedules
            .Select(schedule => new CollectorScheduleOptions
            {
                Name = schedule.Name,
                FrequencySeconds = schedule.DefaultFrequencySeconds
            })
            .ToList();

    public static bool IsPermissionError(Microsoft.Data.SqlClient.SqlException exception)
    {
        foreach (Microsoft.Data.SqlClient.SqlError error in exception.Errors)
        {
            if (error.Number is 229 or 297 or 300)
            {
                return true;
            }
        }

        return false;
    }
}
