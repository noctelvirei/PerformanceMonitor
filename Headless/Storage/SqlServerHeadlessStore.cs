using System.Diagnostics;
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

    public async Task UpsertConfiguredServersAsync(IEnumerable<CollectionServerIdentity> servers, CancellationToken cancellationToken)
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

    public async Task RecordSnapshotAsync(CollectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var storageWatch = Stopwatch.StartNew();
        await UpsertConfiguredServersAsync([snapshot.Server], cancellationToken);
        await SetServerStatusAsync(snapshot.Server, snapshot.ServerStatus, snapshot.ServerError, snapshot.ServerProperties, cancellationToken);

        if (snapshot.ServerProperties is not null)
        {
            await InsertServerPropertiesAsync(snapshot.Server, snapshot.CollectionTime, snapshot.ServerProperties, cancellationToken);
        }

        await InsertWaitStatsAsync(snapshot.Server, snapshot.CollectionTime, snapshot.WaitStats, cancellationToken);
        await InsertCpuSamplesAsync(snapshot.Server, snapshot.CollectionTime, snapshot.CpuSamples, cancellationToken);
        await InsertWaitingTasksAsync(snapshot.Server, snapshot.CollectionTime, snapshot.WaitingTasks, cancellationToken);
        await InsertCollectorSamplesAsync(snapshot.Server, snapshot.CollectionTime, snapshot.CollectorSamples, cancellationToken);
        storageWatch.Stop();

        foreach (var log in snapshot.Logs)
        {
            var timedLog = WithStorageTiming(log, storageWatch.ElapsedMilliseconds);
            await InsertCollectionLogAsync(
                snapshot.Server,
                timedLog.CollectorName,
                timedLog.CollectionTime,
                timedLog.DurationMs,
                timedLog.Status,
                timedLog.ErrorMessage,
                timedLog.RowsCollected,
                timedLog.SqlDurationMs,
                timedLog.StorageDurationMs,
                cancellationToken);
        }
    }

    private static CollectionLogEntry WithStorageTiming(CollectionLogEntry log, long storageDurationMs)
        => log.StorageDurationMs == 0
            ? log with
            {
                DurationMs = checked(log.DurationMs + (int)Math.Min(int.MaxValue, storageDurationMs)),
                StorageDurationMs = storageDurationMs
            }
            : log;

    public async Task SetServerStatusAsync(
        CollectionServerIdentity server,
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
        CollectionServerIdentity server,
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
        CollectionServerIdentity server,
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
        CollectionServerIdentity server,
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

    public async Task InsertWaitingTasksAsync(
        CollectionServerIdentity server,
        DateTime collectionTime,
        IReadOnlyList<WaitingTaskSnapshot> rows,
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
INSERT dbo.waiting_tasks
    (collection_id, collection_time, server_id, server_name, session_id, wait_type, wait_duration_ms, blocking_session_id, resource_description, database_name)
VALUES
    (@collection_id, @collection_time, @server_id, @server_name, @session_id, @wait_type, @wait_duration_ms, @blocking_session_id, @resource_description, @database_name);
""", connection);
                command.Parameters.AddWithValue("@collection_id", NextId());
                command.Parameters.AddWithValue("@collection_time", collectionTime);
                command.Parameters.AddWithValue("@server_id", server.Id);
                command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
                command.Parameters.AddWithValue("@session_id", row.SessionId);
                command.Parameters.AddWithValue("@wait_type", row.WaitType ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@wait_duration_ms", row.WaitDurationMs);
                command.Parameters.AddWithValue("@blocking_session_id", (object?)row.BlockingSessionId ?? DBNull.Value);
                command.Parameters.AddWithValue("@resource_description", row.ResourceDescription ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@database_name", row.DatabaseName ?? (object)DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task InsertCollectionLogAsync(
        CollectionServerIdentity server,
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

    public async Task InsertCollectorSamplesAsync(
        CollectionServerIdentity server,
        DateTime collectionTime,
        IReadOnlyList<CollectorSampleSnapshot> rows,
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
INSERT dbo.collector_samples
    (collection_id, collection_time, server_id, server_name, collector_name, sample_key, payload_json)
VALUES
    (@collection_id, @collection_time, @server_id, @server_name, @collector_name, @sample_key, @payload_json);
""", connection);
                command.Parameters.AddWithValue("@collection_id", NextId());
                command.Parameters.AddWithValue("@collection_time", collectionTime);
                command.Parameters.AddWithValue("@server_id", server.Id);
                command.Parameters.AddWithValue("@server_name", server.ServerNameForStorage);
                command.Parameters.AddWithValue("@collector_name", row.CollectorName);
                command.Parameters.AddWithValue("@sample_key", row.SampleKey ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@payload_json", row.PayloadJson);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken)
    {
        var servers = new List<ServerHealthDto>();
        var activeAlertsByServer = (await GetActiveOperationalAlertsAsync(cancellationToken))
            .GroupBy(alert => alert.ServerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

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
        var now = DateTime.UtcNow;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var serverId = reader.GetString(0);
            activeAlertsByServer.TryGetValue(serverId, out var activeAlerts);
            var primaryAlert = GetPrimaryAlert(activeAlerts ?? []);
            servers.Add(EstateTelemetryQueryProjection.ToServerHealth(new EstateServerTelemetryRow(
                serverId,
                reader.IsDBNull(1) ? serverId : reader.GetString(1),
                reader.IsDBNull(2) ? "Unassigned" : reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? "UNKNOWN" : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                activeAlerts?.Count ?? 0,
                primaryAlert?.Message,
                primaryAlert?.Severity,
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)),
                _options.CurrentValue.CollectionIntervalSeconds,
                now));
        }

        return servers;
    }

    public async Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken)
    {
        var generatedAt = DateTime.UtcNow;
        var servers = await GetServersAsync(cancellationToken);
        return EstateTelemetryQueryProjection.ToSummary(
            servers,
            await GetActiveOperationalAlertsAsync(cancellationToken),
            generatedAt);
    }

    public async Task<IReadOnlyList<ActiveAlertDto>> GetEstateActiveAlertsAsync(CancellationToken cancellationToken)
    {
        var generatedAt = DateTime.UtcNow;
        var servers = await GetServersAsync(cancellationToken);
        return EstateTelemetryQueryProjection.ToEstateActiveAlerts(
            servers,
            await GetActiveOperationalAlertsAsync(cancellationToken),
            generatedAt);
    }

    private async Task<IReadOnlyList<ActiveAlertDto>> GetActiveOperationalAlertsAsync(CancellationToken cancellationToken)
    {
        var alerts = new List<ActiveAlertDto>();
        alerts.AddRange(await GetActiveCollectorAlertsAsync(cancellationToken));
        alerts.AddRange(await GetActiveCpuAlertsAsync(cancellationToken));
        alerts.AddRange(await GetActiveWaitingTaskAlertsAsync(cancellationToken));
        alerts.AddRange(await GetActiveCollectorSampleAlertsAsync(cancellationToken));
        return alerts;
    }

    private async Task<IReadOnlyList<ActiveAlertDto>> GetActiveCollectorAlertsAsync(CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<ActiveAlertDto>> GetActiveCpuAlertsAsync(CancellationToken cancellationToken)
    {
        var rules = _options.CurrentValue.AlertRules;
        if (!rules.Enabled || !rules.CpuEnabled)
        {
            return [];
        }

        var alerts = new List<ActiveAlertDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
WITH latest_cpu AS
(
    SELECT server_id, sample_time = MAX(sample_time)
    FROM dbo.cpu_utilization_stats
    GROUP BY server_id
)
SELECT
    cu.sample_time,
    cu.server_id,
    COALESCE(NULLIF(s.display_name, N''), cu.server_name) AS server_name,
    cu.sqlserver_cpu_utilization
FROM dbo.cpu_utilization_stats AS cu
JOIN latest_cpu AS latest
  ON latest.server_id = cu.server_id
 AND latest.sample_time = cu.sample_time
LEFT JOIN dbo.servers AS s
  ON s.server_id = cu.server_id
WHERE cu.sample_time >= @since
AND   COALESCE(s.is_enabled, CONVERT(bit, 1)) = CONVERT(bit, 1)
AND   cu.sqlserver_cpu_utilization >= @warning_threshold;
""", connection);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddSeconds(-Math.Max(900, _options.CurrentValue.CollectionIntervalSeconds * 3)));
        command.Parameters.AddWithValue("@warning_threshold", rules.CpuWarningThreshold);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cpu = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            alerts.Add(new ActiveAlertDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                "CPU",
                cpu >= rules.CpuCriticalThreshold ? "red" : "yellow",
                $"SQL CPU is {cpu:n0}%",
                "cpu"));
        }

        return alerts;
    }

    private async Task<IReadOnlyList<ActiveAlertDto>> GetActiveWaitingTaskAlertsAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AlertRules.Enabled || !_options.CurrentValue.AlertRules.BlockingEnabled)
        {
            return [];
        }

        var alerts = new List<ActiveAlertDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
WITH latest_waiting_task_collection AS
(
    SELECT server_id, collection_time
    FROM
    (
        SELECT
            server_id,
            collection_time,
            status,
            ROW_NUMBER() OVER (PARTITION BY server_id ORDER BY collection_time DESC, log_id DESC) AS rn
        FROM dbo.collection_log
        WHERE collector_name = N'waiting_tasks'
    ) AS latest
    WHERE rn = 1
    AND   status = N'SUCCESS'
)
SELECT
    wt.collection_time,
    wt.server_id,
    COALESCE(NULLIF(s.display_name, N''), wt.server_name) AS server_name,
    wt.session_id,
    wt.blocking_session_id,
    wt.wait_type,
    wt.wait_duration_ms
FROM dbo.waiting_tasks AS wt
JOIN latest_waiting_task_collection AS latest
  ON latest.server_id = wt.server_id
 AND latest.collection_time = wt.collection_time
LEFT JOIN dbo.servers AS s
  ON s.server_id = wt.server_id
WHERE COALESCE(wt.blocking_session_id, 0) > 0
AND   COALESCE(s.is_enabled, CONVERT(bit, 1)) = CONVERT(bit, 1)
ORDER BY wt.wait_duration_ms DESC;
""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sessionId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var blockingSessionId = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var waitType = reader.IsDBNull(5) ? "wait" : reader.GetString(5);
            var waitDurationMs = reader.IsDBNull(6) ? 0L : reader.GetInt64(6);

            alerts.Add(new ActiveAlertDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                "Waiting tasks",
                "red",
                $"Session {sessionId} blocked by {blockingSessionId} on {waitType} for {waitDurationMs:n0} ms",
                "stats"));
        }

        return alerts;
    }

    private async Task<IReadOnlyList<ActiveAlertDto>> GetActiveCollectorSampleAlertsAsync(CancellationToken cancellationToken)
    {
        var samples = new List<CollectorSampleDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
WITH latest_sample AS
(
    SELECT server_id, collector_name, collection_time = MAX(collection_time)
    FROM dbo.collector_samples
    WHERE collector_name IN (
        N'perfmon_stats',
        N'query_snapshots',
        N'memory_grant_stats',
        N'memory_pressure_events',
        N'file_io_stats',
        N'running_jobs',
        N'database_config',
        N'deadlocks',
        N'blocked_process_report'
    )
    GROUP BY server_id, collector_name
)
SELECT
    cs.collection_time,
    cs.server_id,
    COALESCE(NULLIF(s.display_name, N''), cs.server_name) AS server_name,
    cs.collector_name,
    cs.sample_key,
    cs.payload_json
FROM dbo.collector_samples AS cs
JOIN latest_sample AS latest
  ON latest.server_id = cs.server_id
 AND latest.collector_name = cs.collector_name
 AND latest.collection_time = cs.collection_time
LEFT JOIN dbo.servers AS s
  ON s.server_id = cs.server_id
WHERE cs.collection_time >= @since
AND   COALESCE(s.is_enabled, CONVERT(bit, 1)) = CONVERT(bit, 1)
ORDER BY cs.collection_time DESC, cs.server_id, cs.collector_name;
""", connection);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddSeconds(-Math.Max(900, _options.CurrentValue.CollectionIntervalSeconds * 3)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new CollectorSampleDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }

        return CollectorExperienceProjection.ProjectActiveAlerts(samples, _options.CurrentValue.AlertRules);
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

    public async Task<IReadOnlyList<WaitingTaskDto>> GetWaitingTasksAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken)
    {
        var tasks = new List<WaitingTaskDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
SELECT TOP (@limit)
    collection_time,
    session_id,
    wait_type,
    wait_duration_ms,
    blocking_session_id,
    resource_description,
    database_name
FROM dbo.waiting_tasks
WHERE server_id = @server_id
AND   collection_time >= @since
ORDER BY collection_time DESC, wait_duration_ms DESC;
""", connection);
        command.Parameters.AddWithValue("@server_id", serverId);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new WaitingTaskDto(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? 0L : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return tasks;
    }

    public async Task<IReadOnlyList<CollectorSampleDto>> GetCollectorSamplesAsync(
        string serverId,
        string collectorName,
        int hoursBack,
        int limit,
        CancellationToken cancellationToken)
    {
        var samples = new List<CollectorSampleDto>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
SELECT TOP (@limit) collection_time, server_id, server_name, collector_name, sample_key, payload_json
FROM dbo.collector_samples
WHERE server_id = @server_id
AND collector_name = @collector_name
AND collection_time >= @since
ORDER BY collection_time DESC, sample_key;
""", connection);
        command.Parameters.AddWithValue("@server_id", serverId);
        command.Parameters.AddWithValue("@collector_name", collectorName);
        command.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new CollectorSampleDto(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5)));
        }

        return samples;
    }

    public async Task<ServerExperienceDto> GetServerExperienceAsync(string serverId, int hoursBack, CancellationToken cancellationToken)
    {
        var samples = new List<CollectorSampleDto>();
        foreach (var collectorName in CollectorExperienceProjection.ExperienceCollectorNames)
        {
            samples.AddRange(await GetCollectorSamplesAsync(serverId, collectorName, hoursBack, 250, cancellationToken));
        }

        return CollectorExperienceProjection.Project(serverId, samples, _options.CurrentValue.AlertRules);
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
                ("dbo.waiting_tasks", "collection_time"),
                ("dbo.collector_samples", "collection_time"),
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

    public Task ApplyRetentionAsync(CancellationToken cancellationToken)
        => ArchiveOldDataAsync(cancellationToken);

    private SqlConnection CreateConnection()
    {
        var connectionString = _options.CurrentValue.Repository.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("SQL Server repository storage is selected, but no repository connection is configured.");
        }

        return new SqlConnection(connectionString);
    }

    private static ActiveAlertDto? GetPrimaryAlert(IReadOnlyList<ActiveAlertDto> alerts)
        => alerts
            .OrderBy(static alert => alert.Severity.Equals("red", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenByDescending(static alert => alert.RaisedAt)
            .FirstOrDefault();

    private static void AddServerParameters(SqlCommand command, CollectionServerIdentity server)
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
        """
        IF OBJECT_ID(N'dbo.waiting_tasks', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waiting_tasks
            (
                collection_id bigint NOT NULL CONSTRAINT PK_waiting_tasks PRIMARY KEY,
                collection_time datetime2(7) NOT NULL,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                session_id int NULL,
                wait_type nvarchar(128) NULL,
                wait_duration_ms bigint NULL,
                blocking_session_id int NULL,
                resource_description nvarchar(3072) NULL,
                database_name nvarchar(256) NULL
            );
        END
        """,
        """
        IF OBJECT_ID(N'dbo.collector_samples', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.collector_samples
            (
                collection_id bigint NOT NULL CONSTRAINT PK_collector_samples PRIMARY KEY,
                collection_time datetime2(7) NOT NULL,
                server_id nvarchar(128) NOT NULL,
                server_name nvarchar(256) NOT NULL,
                collector_name nvarchar(128) NOT NULL,
                sample_key nvarchar(512) NULL,
                payload_json nvarchar(max) NOT NULL
            );
        END
        """,
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_servers_status' AND object_id = OBJECT_ID(N'dbo.servers')) CREATE INDEX IX_servers_status ON dbo.servers(is_enabled, last_status);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_collection_log_time' AND object_id = OBJECT_ID(N'dbo.collection_log')) CREATE INDEX IX_collection_log_time ON dbo.collection_log(collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_collection_log_server_collector_time' AND object_id = OBJECT_ID(N'dbo.collection_log')) CREATE INDEX IX_collection_log_server_collector_time ON dbo.collection_log(server_id, collector_name, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_wait_stats_time' AND object_id = OBJECT_ID(N'dbo.wait_stats')) CREATE INDEX IX_wait_stats_time ON dbo.wait_stats(server_id, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_cpu_time' AND object_id = OBJECT_ID(N'dbo.cpu_utilization_stats')) CREATE INDEX IX_cpu_time ON dbo.cpu_utilization_stats(server_id, sample_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_waiting_tasks_time' AND object_id = OBJECT_ID(N'dbo.waiting_tasks')) CREATE INDEX IX_waiting_tasks_time ON dbo.waiting_tasks(server_id, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_collector_samples_lookup' AND object_id = OBJECT_ID(N'dbo.collector_samples')) CREATE INDEX IX_collector_samples_lookup ON dbo.collector_samples(server_id, collector_name, collection_time);",
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_server_properties_time' AND object_id = OBJECT_ID(N'dbo.server_properties')) CREATE INDEX IX_server_properties_time ON dbo.server_properties(server_id, collection_time);"
    ];
}
