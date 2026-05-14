namespace PerformanceMonitor.CentralRepository.Models;

public sealed record ServerHealthProjection(
    string HealthState,
    string HealthReason,
    bool IsAttentionState);

public static class EstateHealthProjection
{
    public static ServerHealthProjection ProjectServerHealth(
        bool isEnabled,
        DateTime? lastSeenTime,
        string lastStatus,
        string? lastError,
        int activeAlertCount,
        string? recentAlert,
        string? activeAlertSeverity,
        int collectionIntervalSeconds,
        DateTime now)
    {
        if (!isEnabled)
        {
            return new ServerHealthProjection("disabled", "Monitoring disabled", false);
        }

        if (string.Equals(lastStatus, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return new ServerHealthProjection("red", lastError ?? "Connection failed", true);
        }

        if (activeAlertCount > 0)
        {
            var severity = string.Equals(activeAlertSeverity, "yellow", StringComparison.OrdinalIgnoreCase) ? "yellow" : "red";
            return new ServerHealthProjection(severity, recentAlert ?? $"{activeAlertCount} active collector alert(s)", true);
        }

        if (!lastSeenTime.HasValue)
        {
            return new ServerHealthProjection("yellow", "No successful collection yet", true);
        }

        var staleAfter = TimeSpan.FromSeconds(Math.Max(180, collectionIntervalSeconds * 3));
        if (now - lastSeenTime.Value > staleAfter)
        {
            return new ServerHealthProjection("yellow", $"No server contact for {now - lastSeenTime.Value:g}", true);
        }

        return new ServerHealthProjection("green", "All good", false);
    }

    public static IReadOnlyList<ActiveAlertDto> BuildEstateVisibleActiveAlerts(
        IReadOnlyList<ServerHealthDto> servers,
        IReadOnlyList<ActiveAlertDto> activeCollectorAlerts,
        DateTime now)
    {
        var alerts = new List<ActiveAlertDto>(activeCollectorAlerts);
        var collectorAlertServers = activeCollectorAlerts
            .Select(alert => alert.ServerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            var hasCollectorAlert = collectorAlertServers.Contains(server.ServerId);
            var hasConnectionError = string.Equals(server.LastStatus, "ERROR", StringComparison.OrdinalIgnoreCase);
            if (!server.IsEnabled || !server.IsAttentionState || (hasCollectorAlert && !hasConnectionError))
            {
                continue;
            }

            alerts.Add(new ActiveAlertDto(
                server.LastSeenTime ?? now,
                server.ServerId,
                server.DisplayName,
                "Server",
                server.HealthState,
                server.HealthReason,
                "stats"));
        }

        return alerts
            .OrderBy(alert => HealthRank(alert.Severity))
            .ThenByDescending(alert => alert.RaisedAt)
            .Take(30)
            .ToList();
    }

    private static int HealthRank(string health)
        => health.ToLowerInvariant() switch
        {
            "red" => 1,
            "yellow" => 2,
            "green" => 3,
            "disabled" => 4,
            _ => 5
        };
}
