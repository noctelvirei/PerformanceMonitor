using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PerformanceMonitor.CentralRepository.Models;
using PerformanceMonitor.CentralRepository.Storage;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class CentralToolService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IEstateTelemetryReader _reader;
    private readonly IOptionsMonitor<MonitorOptions> _options;

    public CentralToolService(IEstateTelemetryReader reader, IOptionsMonitor<MonitorOptions> options)
    {
        _reader = reader;
        _options = options;
    }

    public async Task<string> ListServersAsync(CancellationToken cancellationToken = default)
    {
        var servers = await _reader.GetServersAsync(cancellationToken);
        return ToJson(new
        {
            server_count = servers.Count,
            servers = servers.Select(server => new
            {
                server_id = server.ServerId,
                display_name = server.DisplayName,
                purpose = server.Purpose,
                enabled = server.IsEnabled,
                health = server.HealthState,
                reason = server.HealthReason,
                active_alerts = server.ActiveAlertCount,
                last_seen = server.LastSeenTime?.ToString("o", CultureInfo.InvariantCulture)
            })
        });
    }

    public async Task<string> GetServerSummaryAsync(string? serverName, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var alerts = await _reader.GetEstateActiveAlertsAsync(cancellationToken);
        var waits = await _reader.GetTopWaitsAsync(resolved.ServerId, 1, 5, cancellationToken);
        var cpu = await _reader.GetCpuSamplesAsync(resolved.ServerId, 1, cancellationToken);

        return ToJson(new
        {
            server = ToServerObject(resolved),
            latest_cpu = cpu.Count == 0 ? null : cpu[^1],
            top_waits = waits,
            active_alerts = alerts
                .Where(alert => string.Equals(alert.ServerId, resolved.ServerId, StringComparison.OrdinalIgnoreCase))
                .Take(20)
        });
    }

    public async Task<string> GetCollectionHealthAsync(string? serverName, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var logs = await _reader.GetCollectionLogAsync(1000, cancellationToken);
        var rows = logs
            .Where(log => string.Equals(log.ServerId, resolved.ServerId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(log => log.CollectorName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.OrderByDescending(log => log.CollectionTime).First();
                return new
                {
                    collector = group.Key,
                    latest_status = latest.Status,
                    latest_time = latest.CollectionTime.ToString("o", CultureInfo.InvariantCulture),
                    latest_error = latest.ErrorMessage,
                    latest_rows = latest.RowsCollected,
                    latest_duration_ms = latest.DurationMs,
                    runs = group.Count(),
                    errors = group.Count(log => string.Equals(log.Status, "ERROR", StringComparison.OrdinalIgnoreCase)),
                    permissions = group.Count(log => string.Equals(log.Status, "PERMISSIONS", StringComparison.OrdinalIgnoreCase))
                };
            })
            .OrderBy(row => row.collector)
            .ToList();

        return ToJson(new
        {
            server = ToServerObject(resolved),
            collectors = rows
        });
    }

    public async Task<string> GetAlertsAsync(string? serverName, CancellationToken cancellationToken = default)
    {
        var alerts = await _reader.GetEstateActiveAlertsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var resolved = await ResolveServerAsync(serverName, cancellationToken);
            if (resolved is null)
            {
                return await ServerNotFoundAsync(serverName, cancellationToken);
            }

            alerts = alerts
                .Where(alert => string.Equals(alert.ServerId, resolved.ServerId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return ToJson(new { alerts });
    }

    public Task<string> GetAlertSettingsAsync()
        => Task.FromResult(ToJson(_options.CurrentValue.AlertRules));

    public async Task<string> GetWaitStatsAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var rows = await _reader.GetTopWaitsAsync(resolved.ServerId, ClampHours(hoursBack), ClampLimit(limit), cancellationToken);
        return ToJson(new
        {
            server = ToServerObject(resolved),
            hours_back = ClampHours(hoursBack),
            waits = rows
        });
    }

    public async Task<string> GetWaitingTasksAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var rows = await _reader.GetWaitingTasksAsync(resolved.ServerId, ClampHours(hoursBack), ClampLimit(limit, 500), cancellationToken);
        return ToJson(new
        {
            server = ToServerObject(resolved),
            hours_back = ClampHours(hoursBack),
            waiting_tasks = rows
        });
    }

    public async Task<string> GetCpuUtilizationAsync(string? serverName, int hoursBack, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var rows = await _reader.GetCpuSamplesAsync(resolved.ServerId, ClampHours(hoursBack), cancellationToken);
        return ToJson(new
        {
            server = ToServerObject(resolved),
            hours_back = ClampHours(hoursBack),
            samples = rows.Select(row => new
            {
                sample_time = row.SampleTime.ToString("o", CultureInfo.InvariantCulture),
                sql_server_cpu = row.SqlServerCpuUtilization,
                other_process_cpu = row.OtherProcessCpuUtilization,
                total_cpu = row.SqlServerCpuUtilization + row.OtherProcessCpuUtilization
            })
        });
    }

    public Task<string> GetTopQueriesByCpuAsync(string? serverName, int hoursBack, int top, string? databaseName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "query_stats", hoursBack, top, "total_worker_time_ms", databaseName, cancellationToken);

    public Task<string> GetTopProceduresByCpuAsync(string? serverName, int hoursBack, int top, string? databaseName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "procedure_stats", hoursBack, top, "total_worker_time_ms", databaseName, cancellationToken);

    public Task<string> GetQueryStoreTopAsync(string? serverName, int hoursBack, int top, string? databaseName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "query_store", hoursBack, top, "avg_duration_ms", databaseName, cancellationToken);

    public Task<string> GetActiveQueriesAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "query_snapshots", hoursBack, limit, "total_elapsed_time_ms", null, cancellationToken);

    public Task<string> GetMemoryStatsAsync(string? serverName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "memory_stats", 24, 10, null, null, cancellationToken);

    public Task<string> GetMemoryClerksAsync(string? serverName, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "memory_clerks", 24, limit, "memory_mb", null, cancellationToken);

    public Task<string> GetMemoryGrantsAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "memory_grant_stats", hoursBack, limit, "requested_memory_mb", null, cancellationToken);

    public Task<string> GetMemoryPressureEventsAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "memory_pressure_events", hoursBack, limit, "sample_time", null, cancellationToken);

    public Task<string> GetFileIoStatsAsync(string? serverName, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "file_io_stats", 24, limit, "io_stall_read_ms", null, cancellationToken);

    public Task<string> GetTempDbStatsAsync(string? serverName, int hoursBack, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "tempdb_stats", hoursBack, 200, "collection_time", null, cancellationToken);

    public Task<string> GetPerfmonStatsAsync(string? serverName, int hoursBack, int limit, string? counterName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "perfmon_stats", hoursBack, limit, "counter_name", null, cancellationToken, row =>
            string.IsNullOrWhiteSpace(counterName) || string.Equals(GetText(row, "counter_name"), counterName, StringComparison.OrdinalIgnoreCase));

    public Task<string> GetSessionStatsAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "session_stats", hoursBack, limit, "collection_time", null, cancellationToken);

    public Task<string> GetRunningJobsAsync(string? serverName, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "running_jobs", 24, limit, "run_duration_seconds", null, cancellationToken);

    public async Task<string> GetServerPropertiesAsync(string? serverName, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        return ToJson(new
        {
            server = ToServerObject(resolved),
            note = "Central repository mode projects the latest server properties into the server inventory."
        });
    }

    public Task<string> GetDatabaseSizesAsync(string? serverName, string? databaseName, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "database_size_stats", 720, limit, "database_name", databaseName, cancellationToken);

    public Task<string> GetServerConfigAsync(string? serverName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "server_config", 720, 500, "name", null, cancellationToken);

    public Task<string> GetDatabaseConfigAsync(string? serverName, string? databaseName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "database_config", 720, 1000, "database_name", databaseName, cancellationToken);

    public Task<string> GetDatabaseScopedConfigAsync(string? serverName, string? databaseName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "database_scoped_config", 720, 1000, "database_name", databaseName, cancellationToken);

    public Task<string> GetTraceFlagsAsync(string? serverName, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "trace_flags", 720, 500, "TraceFlag", null, cancellationToken);

    public Task<string> GetDeadlocksAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "deadlocks", hoursBack, limit, "deadlock_time", null, cancellationToken);

    public Task<string> GetBlockedProcessReportsAsync(string? serverName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, "blocked_process_report", hoursBack, limit, "event_time", null, cancellationToken);

    public Task<string> GetCollectorAsync(string? serverName, string collectorName, int hoursBack, int limit, CancellationToken cancellationToken = default)
        => GetCollectorRowsAsync(serverName, collectorName, hoursBack, limit, "collection_time", null, cancellationToken);

    public async Task<string> GetServerExperienceAsync(string? serverName, int hoursBack, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var experience = await _reader.GetServerExperienceAsync(resolved.ServerId, ClampHours(hoursBack), cancellationToken);
        return ToJson(new
        {
            server = ToServerObject(resolved),
            hours_back = ClampHours(hoursBack),
            experience
        });
    }

    private async Task<string> GetCollectorRowsAsync(
        string? serverName,
        string collectorName,
        int hoursBack,
        int limit,
        string? sortKey,
        string? databaseName,
        CancellationToken cancellationToken,
        Func<JsonObject, bool>? predicate = null)
    {
        var resolved = await ResolveServerAsync(serverName, cancellationToken);
        if (resolved is null)
        {
            return await ServerNotFoundAsync(serverName, cancellationToken);
        }

        var rows = await _reader.GetCollectorSamplesAsync(
            resolved.ServerId,
            collectorName,
            ClampHours(hoursBack),
            Math.Max(ClampLimit(limit, 1000), 100),
            cancellationToken);

        var objects = rows
            .Select(ToJsonObject)
            .Where(row => string.IsNullOrWhiteSpace(databaseName)
                || string.Equals(GetText(row, "database_name"), databaseName, StringComparison.OrdinalIgnoreCase))
            .Where(row => predicate?.Invoke(row) ?? true)
            .ToList();

        if (!string.IsNullOrWhiteSpace(sortKey))
        {
            objects = objects
                .OrderByDescending(row => GetSortable(row, sortKey))
                .ThenBy(row => GetText(row, "sample_key"))
                .ToList();
        }

        return ToJson(new
        {
            server = ToServerObject(resolved),
            collector = collectorName,
            hours_back = ClampHours(hoursBack),
            rows = objects.Take(ClampLimit(limit, 1000))
        });
    }

    private async Task<ServerHealthDto?> ResolveServerAsync(string? serverName, CancellationToken cancellationToken)
    {
        var servers = await _reader.GetServersAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return servers.Count == 1 ? servers[0] : null;
        }

        return servers.FirstOrDefault(server =>
                string.Equals(server.ServerId, serverName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(server.DisplayName, serverName, StringComparison.OrdinalIgnoreCase))
            ?? servers.FirstOrDefault(server =>
                server.DisplayName.Contains(serverName, StringComparison.OrdinalIgnoreCase)
                || server.ServerId.Contains(serverName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> ServerNotFoundAsync(string? serverName, CancellationToken cancellationToken)
    {
        var servers = await _reader.GetServersAsync(cancellationToken);
        return ToJson(new
        {
            error = string.IsNullOrWhiteSpace(serverName)
                ? "No server name was supplied and more than one server is configured."
                : $"Could not resolve server '{serverName}'.",
            available_servers = servers.Select(server => new
            {
                server_id = server.ServerId,
                display_name = server.DisplayName,
                purpose = server.Purpose,
                health = server.HealthState
            })
        });
    }

    private static object ToServerObject(ServerHealthDto server)
        => new
        {
            server_id = server.ServerId,
            display_name = server.DisplayName,
            purpose = server.Purpose,
            enabled = server.IsEnabled,
            health = server.HealthState,
            reason = server.HealthReason,
            latest_sql_cpu = server.LatestSqlCpuUtilization,
            top_wait = server.TopWaitType,
            active_alerts = server.ActiveAlertCount,
            edition = server.Edition,
            version = server.ProductVersion,
            last_seen = server.LastSeenTime?.ToString("o", CultureInfo.InvariantCulture)
        };

    private static JsonObject ToJsonObject(CollectorSampleDto row)
    {
        var payload = JsonNode.Parse(row.PayloadJson) as JsonObject ?? [];
        payload["collection_time"] = row.CollectionTime.ToString("o", CultureInfo.InvariantCulture);
        payload["server_id"] = row.ServerId;
        payload["server_name"] = row.ServerName;
        payload["collector_name"] = row.CollectorName;
        payload["sample_key"] = row.SampleKey;
        return payload;
    }

    private static decimal GetSortable(JsonObject row, string key)
    {
        if (TryGetDecimal(row, key, out var decimalValue))
        {
            return decimalValue;
        }

        var text = GetText(row, key);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime))
        {
            return dateTime.Ticks;
        }

        return 0;
    }

    private static bool TryGetDecimal(JsonObject row, string key, out decimal value)
    {
        value = 0;
        if (!row.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<decimal>(out value))
        {
            return true;
        }

        return decimal.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? GetText(JsonObject row, string key)
        => row.TryGetPropertyValue(key, out var node) ? node?.ToString() : null;

    private static int ClampHours(int hoursBack)
        => Math.Clamp(hoursBack <= 0 ? 1 : hoursBack, 1, 720);

    private static int ClampLimit(int limit, int max = 200)
        => Math.Clamp(limit <= 0 ? 20 : limit, 1, max);

    private static string ToJson(object value)
        => JsonSerializer.Serialize(value, JsonOptions);
}
