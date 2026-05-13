using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Storage;

public sealed class SqlServerHeadlessStore : IHeadlessStore
{
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly ILogger<SqlServerHeadlessStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static long s_idCounter = DateTime.UtcNow.Ticks;

    public SqlServerHeadlessStore(
        IOptionsMonitor<MonitorOptions> options,
        ILogger<SqlServerHeadlessStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public StorageInfoDto GetStorageInfo()
    {
        var repository = _options.CurrentValue.Repository;
        return new StorageInfoDto(
            "SqlServer",
            null,
            null,
            repository.DataSource,
            string.IsNullOrWhiteSpace(repository.InitialCatalog) ? "PerformanceMonitorRepository" : repository.InitialCatalog);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var sql in SchemaStatements)
        {
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertConfiguredServersAsync(IEnumerable<MonitoredServerOptions> servers, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            foreach (var server in servers)
            {
                await using var command = new SqlCommand("""
IF EXISTS (SELECT 1 FROM dbo.servers WHERE server_id = @server_id)
BEGIN
    UPDATE dbo.servers
    SET server_name = @server_name,
        display_name = @display_name,
        purpose = @purpose,
        is_enabled = @is_enabled
    WHERE server_id = @server_id;
END
ELSE
BEGIN
    INSERT dbo.servers (server_id, server_name, display_name, purpose, is_enabled, last_status)
    VALUES (@server_id, @server_name, @display_name, @purpose, @is_enabled, N'UNKNOWN');
END
""", connection);
                AddServerParameters(command, server);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetServerStatusAsync(
        MonitoredServerOptions server,
        string status,
        string? errorMessage,
        ServerPropertiesSnapshot? properties,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
UPDATE dbo.servers
SET last_seen_time = @last_seen_time,
    last_status = @last_status,
    last_error = @last_error,
    product_version = COALESCE(@product_version, product_version),
    edition = COALESCE(@edition, edition),
    sql_engine_edition = COALESCE(@sql_engine_edition, sql_engine_edition),
    sql_major_version = COALESCE(@sql_major_version, sql_major_version)
WHERE server_id = @server_id;

IF @@ROWCOUNT = 0
BEGIN
    INSERT dbo.servers
        (server_id, server_name, display_name, purpose, is_enabled, last_seen_time, last_status, last_error, product_version, edition, sql_engine_edition, sql_major_version)
    VALUES
        (@server_id, @server_name, @display_name, @purpose, @is_enabled, @last_seen_time, @last_status, @last_error, @product_version, @edition, @sql_engine_edition, @sql_major_version);
END
""", connection);
            AddServerParameters(command, server);
            command.Parameters.AddWithValue("@last_seen_time", DateTime.UtcNow);
            command.Parameters.AddWithValue("@last_status", status);
            command.Parameters.AddWithValue("@last_error", errorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@product_version", properties?.ProductVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@edition", properties?.Edition ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sql_engine_edition", properties?.EngineEdition ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sql_major_version", properties?.SqlMajorVersion ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertServerPropertiesAsync(
        MonitoredServerOptions server,
        DateTime collectionTime,
        ServerPropertiesSnapshot properties,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
INSERT dbo.server_properties
    (collection_id, collection_time, server_id, server_name, machine_name, instance_name, product_version, product_level, edition, engine_edition, sql_major_version, cpu_count, physical_memory_mb, sqlserver_start_time)
VALUES
    (@collection_id, @collection_time, @server_id, @server_name, @machine_name, @instance_name, @product_version, @product_level, @edition, @engine_edition, @sql_major_version, @cpu_count, @physical_memory_mb, @sqlserver_start_time);
""", connection);
            command.Parameters.AddWithValue("@collection_id", NextId());
            command.Parameters.AddWithValue("@collection_time", collectionTime);
            command.Parameters.AddWithValue("@server_id", server.Id);
            command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
            command.Parameters.AddWithValue("@machine_name", properties.MachineName);
            command.Parameters.AddWithValue("@instance_name", properties.InstanceName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@product_version", properties.ProductVersion);
            command.Parameters.AddWithValue("@product_level", properties.ProductLevel);
            command.Parameters.AddWithValue("@edition", properties.Edition);
            command.Parameters.AddWithValue("@engine_edition", properties.EngineEdition);
            command.Parameters.AddWithValue("@sql_major_version", properties.SqlMajorVersion);
            command.Parameters.AddWithValue("@cpu_count", properties.CpuCount);
            command.Parameters.AddWithValue("@physical_memory_mb", properties.PhysicalMemoryMb);
            command.Parameters.AddWithValue("@sqlserver_start_time", properties.SqlServerStartTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertWaitStatsAsync(
        MonitoredServerOptions server,
        DateTime collectionTime,
        IReadOnlyList<WaitStatSnapshot> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var row in rows)
            {
                await using var command = new SqlCommand("""
INSERT dbo.wait_stats
    (collection_id, collection_time, server_id, server_name, wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms)
VALUES
    (@collection_id, @collection_time, @server_id, @server_name, @wait_type, @waiting_tasks_count, @wait_time_ms, @signal_wait_time_ms);
""", connection);
                command.Parameters.AddWithValue("@collection_id", NextId());
                command.Parameters.AddWithValue("@collection_time", collectionTime);
                command.Parameters.AddWithValue("@server_id", server.Id);
                command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
                command.Parameters.AddWithValue("@wait_type", row.WaitType);
                command.Parameters.AddWithValue("@waiting_tasks_count", row.WaitingTasksCount);
                command.Parameters.AddWithValue("@wait_time_ms", row.WaitTimeMs);
                command.Parameters.AddWithValue("@signal_wait_time_ms", row.SignalWaitTimeMs);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<DateTime?> GetLastCpuSampleTimeAsync(string serverId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT MAX(sample_time) FROM dbo.cpu_utilization_stats WHERE server_id = @server_id;",
            connection);
        command.Parameters.AddWithValue("@server_id", serverId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTime dateTime ? dateTime : null;
    }

    public async Task InsertCpuSamplesAsync(
        MonitoredServerOptions server,
        DateTime collectionTime,
        IReadOnlyList<CpuSample> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var row in rows)
            {
                await using var command = new SqlCommand("""
INSERT dbo.cpu_utilization_stats
    (collection_id, collection_time, server_id, server_name, sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization)
VALUES
    (@collection_id, @collection_time, @server_id, @server_name, @sample_time, @sqlserver_cpu_utilization, @other_process_cpu_utilization);
""", connection);
                command.Parameters.AddWithValue("@collection_id", NextId());
                command.Parameters.AddWithValue("@collection_time", collectionTime);
                command.Parameters.AddWithValue("@server_id", server.Id);
                command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
                command.Parameters.AddWithValue("@sample_time", row.SampleTime);
                command.Parameters.AddWithValue("@sqlserver_cpu_utilization", row.SqlServerCpuUtilization);
                command.Parameters.AddWithValue("@other_process_cpu_utilization", row.OtherProcessCpuUtilization);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertCollectionLogAsync(
        MonitoredServerOptions server,
        string collectorName,
        DateTime collectionTime,
        int durationMs,
        string status,
        string? errorMessage,
        int rowsCollected,
        long sqlDurationMs,
        long storageDurationMs,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("""
INSERT dbo.collection_log
    (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, storage_duration_ms)
VALUES
    (@log_id, @server_id, @server_name, @collector_name, @collection_time, @duration_ms, @status, @error_message, @rows_collected, @sql_duration_ms, @storage_duration_ms);
""", connection);
            command.Parameters.AddWithValue("@log_id", NextId());
            command.Parameters.AddWithValue("@server_id", server.Id);
            command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
            command.Parameters.AddWithValue("@collector_name", collectorName);
            command.Parameters.AddWithValue("@collection_time", collectionTime);
            command.Parameters.AddWithValue("@duration_ms", durationMs);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@error_message", errorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@rows_collected", rowsCollected);
            command.Parameters.AddWithValue("@sql_duration_ms", sqlDurationMs);
            command.Parameters.AddWithValue("@storage_duration_ms", storageDurationMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken)
    {
        var servers = new List<ServerHealthDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
SELECT
    s.server_id,
    s.display_name,
    COALESCE(NULLIF(LTRIM(RTRIM(s.purpose)), N''), N'Unassigned') AS purpose,
    s.is_enabled,
    s.last_seen_time,
    s.last_status,
    s.last_error,
    s.product_version,
    s.edition,
    s.sql_major_version,
    (
        SELECT COUNT(*)
        FROM
        (
            SELECT
                cl.status,
                ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
            FROM dbo.collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN (N'ERROR', N'PERMISSIONS')
    ) AS active_alert_count,
    (
        SELECT TOP (1) COALESCE(NULLIF(latest.error_message, N''), latest.status)
        FROM
        (
            SELECT
                cl.error_message,
                cl.status,
                cl.collection_time,
                ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
            FROM dbo.collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN (N'ERROR', N'PERMISSIONS')
        ORDER BY CASE WHEN latest.status = N'ERROR' THEN 1 ELSE 2 END, latest.collection_time DESC
    ) AS recent_alert,
    (
        SELECT TOP (1) CASE WHEN latest.status = N'ERROR' THEN N'red' ELSE N'yellow' END
        FROM
        (
            SELECT
                cl.status,
                cl.collection_time,
                ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
            FROM dbo.collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN (N'ERROR', N'PERMISSIONS')
        ORDER BY CASE WHEN latest.status = N'ERROR' THEN 1 ELSE 2 END, latest.collection_time DESC
    ) AS active_alert_severity,
    (
        SELECT TOP (1) cu.sqlserver_cpu_utilization
        FROM dbo.cpu_utilization_stats AS cu
        WHERE cu.server_id = s.server_id
        ORDER BY cu.sample_time DESC
    ) AS latest_sql_cpu,
    (
        SELECT TOP (1) ws.wait_type
        FROM dbo.wait_stats AS ws
        WHERE ws.server_id = s.server_id
        ORDER BY ws.collection_time DESC, ws.wait_time_ms DESC
    ) AS top_wait_type
FROM dbo.servers AS s
ORDER BY
    s.is_enabled DESC,
    CASE LOWER(COALESCE(NULLIF(LTRIM(RTRIM(s.purpose)), N''), N'unassigned'))
        WHEN N'production' THEN 1
        WHEN N'prod' THEN 1
        WHEN N'staging' THEN 2
        WHEN N'stage' THEN 2
        WHEN N'development' THEN 3
        WHEN N'dev' THEN 3
        WHEN N'test' THEN 4
        ELSE 5
    END,
    s.display_name;
""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var serverId = reader.GetString(0);
            var displayName = reader.IsDBNull(1) ? serverId : reader.GetString(1);
            var purpose = reader.IsDBNull(2) ? "Unassigned" : reader.GetString(2);
            var isEnabled = reader.GetBoolean(3);
            var lastSeenTime = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
            var lastStatus = reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5);
            var lastError = reader.IsDBNull(6) ? null : reader.GetString(6);
            var productVersion = reader.IsDBNull(7) ? null : reader.GetString(7);
            var edition = reader.IsDBNull(8) ? null : reader.GetString(8);
            var sqlMajorVersion = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);
            var activeAlertCount = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10));
            var recentAlert = reader.IsDBNull(11) ? null : reader.GetString(11);
            var activeAlertSeverity = reader.IsDBNull(12) ? null : reader.GetString(12);
            var latestSqlCpu = reader.IsDBNull(13) ? (int?)null : reader.GetInt32(13);
            var topWaitType = reader.IsDBNull(14) ? null : reader.GetString(14);
            var (healthState, healthReason) = ComputeHealth(isEnabled, lastSeenTime, lastStatus, lastError, activeAlertCount, recentAlert, activeAlertSeverity);

            servers.Add(new ServerHealthDto(
                serverId,
                displayName,
                purpose,
                isEnabled,
                lastSeenTime,
                lastStatus,
                recentAlert ?? lastError,
                productVersion,
                edition,
                sqlMajorVersion,
                healthState,
                healthReason,
                activeAlertCount,
                latestSqlCpu,
                topWaitType));
        }

        return servers;
    }

    public async Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken)
    {
        var servers = await GetServersAsync(cancellationToken);
        var activeAlerts = await GetActiveAlertsAsync(cancellationToken);
        return new EstateSummaryDto(
            servers.Count,
            servers.Count(s => string.Equals(s.HealthState, "green", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => string.Equals(s.HealthState, "yellow", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => string.Equals(s.HealthState, "red", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => s.IsEnabled && string.Equals(s.LastStatus, "ERROR", StringComparison.OrdinalIgnoreCase)),
            servers.Count(s => !s.IsEnabled),
            DateTime.UtcNow,
            servers,
            activeAlerts);
    }

    public async Task<IReadOnlyList<ActiveAlertDto>> GetActiveAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = new List<ActiveAlertDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
WITH latest_collector AS
(
    SELECT
        cl.collection_time,
        cl.server_id,
        cl.server_name,
        cl.collector_name,
        cl.status,
        cl.error_message,
        ROW_NUMBER() OVER (PARTITION BY cl.server_id, cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
    FROM dbo.collection_log AS cl
)
SELECT
    lc.collection_time,
    lc.server_id,
    COALESCE(NULLIF(s.display_name, N''), lc.server_name) AS server_name,
    lc.collector_name,
    CASE WHEN lc.status = N'ERROR' THEN N'red' ELSE N'yellow' END AS severity,
    COALESCE(NULLIF(lc.error_message, N''), lc.status) AS message,
    CASE WHEN lc.status = N'PERMISSIONS' THEN N'stats' ELSE N'logs' END AS target_tab
FROM latest_collector AS lc
LEFT JOIN dbo.servers AS s
    ON s.server_id = lc.server_id
WHERE lc.rn = 1
AND   lc.status IN (N'ERROR', N'PERMISSIONS')
AND   COALESCE(s.is_enabled, CONVERT(bit, 1)) = CONVERT(bit, 1)
ORDER BY
    CASE WHEN lc.status = N'ERROR' THEN 1 ELSE 2 END,
    lc.collection_time DESC;
""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new ActiveAlertDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return alerts;
    }

    public async Task<IReadOnlyList<CollectionLogDto>> GetCollectionLogAsync(int limit, CancellationToken cancellationToken)
    {
        var logs = new List<CollectionLogDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
SELECT TOP (@limit) collection_time, server_id, server_name, collector_name, status, rows_collected, duration_ms, error_message
FROM dbo.collection_log
ORDER BY collection_time DESC;
""", connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logs.Add(new CollectionLogDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return logs;
    }

    public async Task<IReadOnlyList<TopWaitDto>> GetTopWaitsAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken)
    {
        var waits = new List<TopWaitDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
WITH wait_window AS
(
    SELECT
        wait_type,
        wait_time_delta_ms = MAX(wait_time_ms) - MIN(wait_time_ms),
        signal_wait_time_delta_ms = MAX(signal_wait_time_ms) - MIN(signal_wait_time_ms),
        waiting_tasks_delta = MAX(waiting_tasks_count) - MIN(waiting_tasks_count)
    FROM dbo.wait_stats
    WHERE server_id = @server_id
    AND   collection_time >= @since
    GROUP BY wait_type
)
SELECT TOP (@limit) wait_type, wait_time_delta_ms, signal_wait_time_delta_ms, waiting_tasks_delta
FROM wait_window
WHERE wait_time_delta_ms > 0
ORDER BY wait_time_delta_ms DESC;
""", connection);
        command.Parameters.AddWithValue("@server_id", serverId);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            waits.Add(new TopWaitDto(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return waits;
    }

    public async Task<IReadOnlyList<CpuSampleDto>> GetCpuSamplesAsync(string serverId, int hoursBack, CancellationToken cancellationToken)
    {
        var samples = new List<CpuSampleDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
SELECT sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization
FROM dbo.cpu_utilization_stats
WHERE server_id = @server_id
AND   sample_time >= @since
ORDER BY sample_time;
""", connection);
        command.Parameters.AddWithValue("@server_id", serverId);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new CpuSampleDto(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
        }

        return samples;
    }

    public async Task ArchiveOldDataAsync(CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.HotDataDays <= 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_options.CurrentValue.HotDataDays);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            foreach (var (tableName, timeColumn) in new[]
            {
                ("dbo.wait_stats", "collection_time"),
                ("dbo.cpu_utilization_stats", "sample_time"),
                ("dbo.server_properties", "collection_time"),
                ("dbo.collection_log", "collection_time")
            })
            {
                await using var command = new SqlCommand($"DELETE FROM {tableName} WHERE {timeColumn} < @cutoff;", connection);
                command.Parameters.AddWithValue("@cutoff", cutoff);
                var rows = await command.ExecuteNonQueryAsync(cancellationToken);
                if (rows > 0)
                {
                    _logger.LogInformation("Purged {RowCount} old rows from {Table}", rows, tableName);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = _options.CurrentValue.Repository.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("SQL Server repository storage is selected, but no repository connection is configured.");
        }

        return new SqlConnection(connectionString);
    }

    private (string HealthState, string HealthReason) ComputeHealth(
        bool isEnabled,
        DateTime? lastSeenTime,
        string lastStatus,
        string? lastError,
        int activeAlertCount,
        string? recentAlert,
        string? activeAlertSeverity)
    {
        if (!isEnabled)
        {
            return ("disabled", "Monitoring disabled");
        }

        if (string.Equals(lastStatus, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return ("red", lastError ?? "Connection failed");
        }

        if (activeAlertCount > 0)
        {
            var severity = string.Equals(activeAlertSeverity, "yellow", StringComparison.OrdinalIgnoreCase) ? "yellow" : "red";
            return (severity, recentAlert ?? $"{activeAlertCount} active collector alert(s)");
        }

        if (!lastSeenTime.HasValue)
        {
            return ("yellow", "No successful collection yet");
        }

        var staleAfter = TimeSpan.FromSeconds(Math.Max(180, _options.CurrentValue.CollectionIntervalSeconds * 3));
        if (DateTime.UtcNow - lastSeenTime.Value > staleAfter)
        {
            return ("yellow", $"No server contact for {DateTime.UtcNow - lastSeenTime.Value:g}");
        }

        return ("green", "All good");
    }

    private static void AddServerParameters(SqlCommand command, MonitoredServerOptions server)
    {
        command.Parameters.AddWithValue("@server_id", server.Id);
        command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
        command.Parameters.AddWithValue("@display_name", server.DisplayName);
        command.Parameters.AddWithValue("@purpose", server.PurposeForDisplay);
        command.Parameters.AddWithValue("@is_enabled", server.Enabled);
    }

    private static long NextId() => Interlocked.Increment(ref s_idCounter);

    private static readonly string[] SchemaStatements =
    [
        """
        IF OBJECT_ID(N'dbo.servers', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.servers
            (
                server_id nvarchar(128) NOT NULL CONSTRAINT PK_servers PRIMARY KEY,
                server_name nvarchar(256) NOT NULL,
                display_name nvarchar(256) NULL,
                purpose nvarchar(128) NOT NULL CONSTRAINT DF_servers_purpose DEFAULT N'Unassigned',
                is_enabled bit NOT NULL CONSTRAINT DF_servers_is_enabled DEFAULT 1,
                last_seen_time datetime2(7) NULL,
                last_status nvarchar(32) NOT NULL CONSTRAINT DF_servers_last_status DEFAULT N'UNKNOWN',
                last_error nvarchar(max) NULL,
                product_version nvarchar(128) NULL,
                edition nvarchar(256) NULL,
                sql_engine_edition int NULL,
                sql_major_version int NULL,
                created_date datetime2(7) NOT NULL CONSTRAINT DF_servers_created_date DEFAULT SYSUTCDATETIME()
            );
        END
        """,
        """
        IF OBJECT_ID(N'dbo.collection_log', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.collection_log
            (
                log_id bigint NOT NULL CONSTRAINT PK_collection_log PRIMARY KEY,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                collector_name nvarchar(128) NOT NULL,
                collection_time datetime2(7) NOT NULL,
                duration_ms int NULL,
                status nvarchar(32) NOT NULL,
                error_message nvarchar(max) NULL,
                rows_collected int NULL,
                sql_duration_ms bigint NULL,
                storage_duration_ms bigint NULL
            );
        END
        """,
        """
        IF OBJECT_ID(N'dbo.server_properties', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.server_properties
            (
                collection_id bigint NOT NULL CONSTRAINT PK_server_properties PRIMARY KEY,
                collection_time datetime2(7) NOT NULL,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                machine_name nvarchar(128) NULL,
                instance_name nvarchar(128) NULL,
                product_version nvarchar(128) NULL,
                product_level nvarchar(128) NULL,
                edition nvarchar(256) NULL,
                engine_edition int NULL,
                sql_major_version int NULL,
                cpu_count int NULL,
                physical_memory_mb bigint NULL,
                sqlserver_start_time datetime2(7) NULL
            );
        END
        """,
        """
        IF OBJECT_ID(N'dbo.wait_stats', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.wait_stats
            (
                collection_id bigint NOT NULL CONSTRAINT PK_wait_stats PRIMARY KEY,
                collection_time datetime2(7) NOT NULL,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                wait_type nvarchar(128) NOT NULL,
                waiting_tasks_count bigint NULL,
                wait_time_ms bigint NULL,
                signal_wait_time_ms bigint NULL
            );
        END
        """,
        """
        IF OBJECT_ID(N'dbo.cpu_utilization_stats', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.cpu_utilization_stats
            (
                collection_id bigint NOT NULL CONSTRAINT PK_cpu_utilization_stats PRIMARY KEY,
                collection_time datetime2(7) NOT NULL,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                sample_time datetime2(7) NOT NULL,
                sqlserver_cpu_utilization int NULL,
                other_process_cpu_utilization int NULL
            );
        END
        """,
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_servers_status' AND object_id = OBJECT_ID(N'dbo.servers')) CREATE INDEX IX_servers_status ON dbo.servers(is_enabled, last_status);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_collection_log_time' AND object_id = OBJECT_ID(N'dbo.collection_log')) CREATE INDEX IX_collection_log_time ON dbo.collection_log(collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_collection_log_server_collector_time' AND object_id = OBJECT_ID(N'dbo.collection_log')) CREATE INDEX IX_collection_log_server_collector_time ON dbo.collection_log(server_id, collector_name, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_wait_stats_time' AND object_id = OBJECT_ID(N'dbo.wait_stats')) CREATE INDEX IX_wait_stats_time ON dbo.wait_stats(server_id, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_cpu_time' AND object_id = OBJECT_ID(N'dbo.cpu_utilization_stats')) CREATE INDEX IX_cpu_time ON dbo.cpu_utilization_stats(server_id, sample_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_server_properties_time' AND object_id = OBJECT_ID(N'dbo.server_properties')) CREATE INDEX IX_server_properties_time ON dbo.server_properties(server_id, collection_time);"
    ];
}
