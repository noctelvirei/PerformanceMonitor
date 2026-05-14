using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Storage;

internal sealed record EstateServerTelemetryRow(
    string ServerId,
    string DisplayName,
    string Purpose,
    bool IsEnabled,
    DateTime? LastSeenTime,
    string LastStatus,
    string? LastError,
    string? ProductVersion,
    string? Edition,
    int? SqlMajorVersion,
    int ActiveCollectorAlertCount,
    string? RecentCollectorAlert,
    string? ActiveCollectorAlertSeverity,
    int? LatestSqlCpuUtilization,
    string? TopWaitType);

internal static class EstateTelemetryQueryProjection
{
    public static ServerHealthDto ToServerHealth(
        EstateServerTelemetryRow row,
        int collectionIntervalSeconds,
        DateTime now)
    {
        var health = EstateHealthProjection.ProjectServerHealth(
            row.IsEnabled,
            row.LastSeenTime,
            row.LastStatus,
            row.LastError,
            row.ActiveCollectorAlertCount,
            row.RecentCollectorAlert,
            row.ActiveCollectorAlertSeverity,
            collectionIntervalSeconds,
            now);

        return new ServerHealthDto(
            row.ServerId,
            row.DisplayName,
            row.Purpose,
            row.IsEnabled,
            row.LastSeenTime,
            row.LastStatus,
            row.RecentCollectorAlert ?? row.LastError,
            row.ProductVersion,
            row.Edition,
            row.SqlMajorVersion,
            health.HealthState,
            health.HealthReason,
            row.ActiveCollectorAlertCount,
            health.IsAttentionState,
            row.LatestSqlCpuUtilization,
            row.TopWaitType);
    }

    public static EstateSummaryDto ToSummary(
        IReadOnlyList<ServerHealthDto> servers,
        IReadOnlyList<ActiveAlertDto> activeCollectorAlerts,
        DateTime generatedAt)
    {
        var estateActiveAlerts = EstateHealthProjection.BuildEstateVisibleActiveAlerts(
            servers,
            activeCollectorAlerts,
            generatedAt);

        return new EstateSummaryDto(
            servers.Count,
            servers.Count(s => string.Equals(s.HealthState, "green", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => string.Equals(s.HealthState, "yellow", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => string.Equals(s.HealthState, "red", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => s.IsEnabled && string.Equals(s.LastStatus, "ERROR", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => !s.IsEnabled),
            generatedAt,
            servers,
            estateActiveAlerts);
    }

    public static IReadOnlyList<ActiveAlertDto> ToEstateActiveAlerts(
        IReadOnlyList<ServerHealthDto> servers,
        IReadOnlyList<ActiveAlertDto> activeCollectorAlerts,
        DateTime generatedAt)
        => EstateHealthProjection.BuildEstateVisibleActiveAlerts(servers, activeCollectorAlerts, generatedAt);
}
