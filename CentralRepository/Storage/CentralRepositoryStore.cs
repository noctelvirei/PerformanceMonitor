using DuckDB.NET.Data;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Storage;

public sealed class CentralRepositoryStore : ICentralRepositoryStore
{
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CentralRepositoryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static long s_idCounter = DateTime.UtcNow.Ticks;

    public CentralRepositoryStore(
        IOptionsMonitor<MonitorOptions> options,
        IHostEnvironment environment,
        ILogger<CentralRepositoryStore> logger)
    {
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public string DatabasePath => ResolvePath(_options.CurrentValue.StoragePath);
    public string ArchiveDirectory => ResolvePath(_options.CurrentValue.ArchiveDirectory);

    public StorageInfoDto GetStorageInfo()
        => new("DuckDb", DatabasePath, ArchiveDirectory, null, null);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(ArchiveDirectory);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var sql in SchemaStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public DuckDBConnection CreateConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(ArchiveDirectory);
        return new DuckDBConnection($"Data Source={DatabasePath}");
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
                await using var insert = connection.CreateCommand();
                insert.CommandText = @"
INSERT INTO servers (server_id, server_name, display_name, purpose, is_enabled, last_status)
VALUES ($1, $2, $3, $4, $5, 'UNKNOWN')
ON CONFLICT(server_id) DO UPDATE
SET server_name = excluded.server_name,
    display_name = excluded.display_name,
    purpose = excluded.purpose,
    is_enabled = excluded.is_enabled";
                insert.Parameters.Add(new DuckDBParameter { Value = server.Id });
                insert.Parameters.Add(new DuckDBParameter { Value = server.ServerNameForStorage });
                insert.Parameters.Add(new DuckDBParameter { Value = server.DisplayName });
                insert.Parameters.Add(new DuckDBParameter { Value = server.PurposeForDisplay });
                insert.Parameters.Add(new DuckDBParameter { Value = server.Enabled });
                await insert.ExecuteNonQueryAsync(cancellationToken);
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
            await using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE servers
SET last_seen_time = $2,
    last_status = $3,
    last_error = $4,
    product_version = COALESCE($5, product_version),
    edition = COALESCE($6, edition),
    sql_engine_edition = COALESCE($7, sql_engine_edition),
    sql_major_version = COALESCE($8, sql_major_version)
WHERE server_id = $1";
            command.Parameters.Add(new DuckDBParameter { Value = server.Id });
            command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow });
            command.Parameters.Add(new DuckDBParameter { Value = status });
            command.Parameters.Add(new DuckDBParameter { Value = errorMessage ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDBParameter { Value = properties?.ProductVersion ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDBParameter { Value = properties?.Edition ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDBParameter { Value = properties?.EngineEdition ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDBParameter { Value = properties?.SqlMajorVersion ?? (object)DBNull.Value });
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
            using var appender = connection.CreateAppender("server_properties");
            var row = appender.CreateRow()
                .AppendValue(NextId())
                .AppendValue(collectionTime)
                .AppendValue(server.Id)
                .AppendValue(server.ServerNameForStorage)
                .AppendValue(properties.MachineName);

            if (properties.InstanceName is null)
            {
                row.AppendNullValue();
            }
            else
            {
                row.AppendValue(properties.InstanceName);
            }

            row
                .AppendValue(properties.ProductVersion)
                .AppendValue(properties.ProductLevel)
                .AppendValue(properties.Edition)
                .AppendValue(properties.EngineEdition)
                .AppendValue(properties.SqlMajorVersion)
                .AppendValue(properties.CpuCount)
                .AppendValue(properties.PhysicalMemoryMb)
                .AppendValue(properties.SqlServerStartTime)
                .EndRow();
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
            using var appender = connection.CreateAppender("wait_stats");
            foreach (var row in rows)
            {
                appender.CreateRow()
                    .AppendValue(NextId())
                    .AppendValue(collectionTime)
                    .AppendValue(server.Id)
                    .AppendValue(server.ServerNameForStorage)
                    .AppendValue(row.WaitType)
                    .AppendValue(row.WaitingTasksCount)
                    .AppendValue(row.WaitTimeMs)
                    .AppendValue(row.SignalWaitTimeMs)
                    .EndRow();
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
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(sample_time) FROM cpu_utilization_stats WHERE server_id = $1";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
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
            using var appender = connection.CreateAppender("cpu_utilization_stats");
            foreach (var row in rows)
            {
                appender.CreateRow()
                    .AppendValue(NextId())
                    .AppendValue(collectionTime)
                    .AppendValue(server.Id)
                    .AppendValue(server.ServerNameForStorage)
                    .AppendValue(row.SampleTime)
                    .AppendValue(row.SqlServerCpuUtilization)
                    .AppendValue(row.OtherProcessCpuUtilization)
                    .EndRow();
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
            using var appender = connection.CreateAppender("waiting_tasks");
            foreach (var row in rows)
            {
                appender.CreateRow()
                    .AppendValue(NextId())
                    .AppendValue(collectionTime)
                    .AppendValue(server.Id)
                    .AppendValue(server.ServerNameForStorage)
                    .AppendValue(row.SessionId)
                    .AppendValue(row.WaitType)
                    .AppendValue(row.WaitDurationMs)
                    .AppendValue(row.BlockingSessionId)
                    .AppendValue(row.ResourceDescription)
                    .AppendValue(row.DatabaseName)
                    .EndRow();
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
            await using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO collection_log
    (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, storage_duration_ms)
VALUES
    ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
            command.Parameters.Add(new DuckDBParameter { Value = NextId() });
            command.Parameters.Add(new DuckDBParameter { Value = server.Id });
            command.Parameters.Add(new DuckDBParameter { Value = server.ServerNameForStorage });
            command.Parameters.Add(new DuckDBParameter { Value = collectorName });
            command.Parameters.Add(new DuckDBParameter { Value = collectionTime });
            command.Parameters.Add(new DuckDBParameter { Value = durationMs });
            command.Parameters.Add(new DuckDBParameter { Value = status });
            command.Parameters.Add(new DuckDBParameter { Value = errorMessage ?? (object)DBNull.Value });
            command.Parameters.Add(new DuckDBParameter { Value = rowsCollected });
            command.Parameters.Add(new DuckDBParameter { Value = (int)sqlDurationMs });
            command.Parameters.Add(new DuckDBParameter { Value = (int)storageDurationMs });
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
        var activeAlertsByServer = (await GetActiveOperationalAlertsAsync(cancellationToken))
            .GroupBy(alert => alert.ServerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    s.server_id,
    s.display_name,
    COALESCE(NULLIF(TRIM(s.purpose), ''), 'Unassigned') AS purpose,
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
            FROM collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN ('ERROR', 'AUTH_FAILED', 'PERMISSIONS')
    ) AS active_alert_count,
    (
        SELECT COALESCE(NULLIF(latest.error_message, ''), latest.status)
        FROM
        (
            SELECT
                cl.error_message,
                cl.status,
                cl.collection_time,
                ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
            FROM collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN ('ERROR', 'AUTH_FAILED', 'PERMISSIONS')
        ORDER BY CASE WHEN latest.status IN ('ERROR', 'AUTH_FAILED') THEN 1 ELSE 2 END, latest.collection_time DESC
        LIMIT 1
    ) AS recent_alert,
    (
        SELECT CASE WHEN latest.status IN ('ERROR', 'AUTH_FAILED') THEN 'red' ELSE 'yellow' END
        FROM
        (
            SELECT
                cl.status,
                cl.collection_time,
                ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.collection_time DESC, cl.log_id DESC) AS rn
            FROM collection_log AS cl
            WHERE cl.server_id = s.server_id
        ) AS latest
        WHERE latest.rn = 1
        AND   latest.status IN ('ERROR', 'AUTH_FAILED', 'PERMISSIONS')
        ORDER BY CASE WHEN latest.status IN ('ERROR', 'AUTH_FAILED') THEN 1 ELSE 2 END, latest.collection_time DESC
        LIMIT 1
    ) AS active_alert_severity,
    (
        SELECT cu.sqlserver_cpu_utilization
        FROM cpu_utilization_stats AS cu
        WHERE cu.server_id = s.server_id
        ORDER BY cu.sample_time DESC
        LIMIT 1
    ) AS latest_sql_cpu,
    (
        SELECT ws.wait_type
        FROM wait_stats AS ws
        WHERE ws.server_id = s.server_id
        ORDER BY ws.collection_time DESC, ws.wait_time_ms DESC
        LIMIT 1
    ) AS top_wait_type
FROM servers AS s
ORDER BY
    s.is_enabled DESC,
    CASE LOWER(COALESCE(NULLIF(TRIM(s.purpose), ''), 'unassigned'))
        WHEN 'production' THEN 1
        WHEN 'prod' THEN 1
        WHEN 'staging' THEN 2
        WHEN 'stage' THEN 2
        WHEN 'development' THEN 3
        WHEN 'dev' THEN 3
        WHEN 'test' THEN 4
        ELSE 5
    END,
    s.display_name";
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
            using var appender = connection.CreateAppender("collector_samples");
            foreach (var row in rows)
            {
                appender.CreateRow()
                    .AppendValue(NextId())
                    .AppendValue(collectionTime)
                    .AppendValue(server.Id)
                    .AppendValue(server.ServerNameForStorage)
                    .AppendValue(row.CollectorName)
                    .AppendValue(row.SampleKey)
                    .AppendValue(row.PayloadJson)
                    .EndRow();
            }
        }
        finally
        {
            _writeLock.Release();
        }
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
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
    FROM collection_log AS cl
)
SELECT
    lc.collection_time,
    lc.server_id,
    COALESCE(NULLIF(s.display_name, ''), lc.server_name) AS server_name,
    lc.collector_name,
    CASE WHEN lc.status IN ('ERROR', 'AUTH_FAILED') THEN 'red' ELSE 'yellow' END AS severity,
    COALESCE(NULLIF(lc.error_message, ''), lc.status) AS message,
    CASE WHEN lc.status = 'PERMISSIONS' THEN 'stats' ELSE 'logs' END AS target_tab
FROM latest_collector AS lc
LEFT JOIN servers AS s
    ON s.server_id = lc.server_id
WHERE lc.rn = 1
AND   lc.status IN ('ERROR', 'AUTH_FAILED', 'PERMISSIONS')
AND   COALESCE(s.is_enabled, TRUE) = TRUE
ORDER BY
    CASE WHEN lc.status IN ('ERROR', 'AUTH_FAILED') THEN 1 ELSE 2 END,
    lc.collection_time DESC";
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
WITH latest_cpu AS
(
    SELECT server_id, MAX(sample_time) AS sample_time
    FROM cpu_utilization_stats
    GROUP BY server_id
)
SELECT
    cu.sample_time,
    cu.server_id,
    COALESCE(NULLIF(s.display_name, ''), cu.server_name) AS server_name,
    cu.sqlserver_cpu_utilization
FROM cpu_utilization_stats AS cu
JOIN latest_cpu AS latest
  ON latest.server_id = cu.server_id
 AND latest.sample_time = cu.sample_time
LEFT JOIN servers AS s
  ON s.server_id = cu.server_id
WHERE cu.sample_time >= $1
AND   COALESCE(s.is_enabled, TRUE) = TRUE
AND   cu.sqlserver_cpu_utilization >= $2";
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddSeconds(-Math.Max(900, _options.CurrentValue.CollectionIntervalSeconds * 3)) });
        command.Parameters.Add(new DuckDBParameter { Value = rules.CpuWarningThreshold });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
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
        FROM collection_log
        WHERE collector_name = 'waiting_tasks'
    ) AS latest
    WHERE rn = 1
    AND   status = 'SUCCESS'
)
SELECT
    wt.collection_time,
    wt.server_id,
    COALESCE(NULLIF(s.display_name, ''), wt.server_name) AS server_name,
    wt.session_id,
    wt.blocking_session_id,
    wt.wait_type,
    wt.wait_duration_ms
FROM waiting_tasks AS wt
JOIN latest_waiting_task_collection AS latest
  ON latest.server_id = wt.server_id
 AND latest.collection_time = wt.collection_time
LEFT JOIN servers AS s
  ON s.server_id = wt.server_id
WHERE COALESCE(wt.blocking_session_id, 0) > 0
AND   COALESCE(s.is_enabled, TRUE) = TRUE
ORDER BY wt.wait_duration_ms DESC";
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
WITH latest_sample AS
(
    SELECT server_id, collector_name, MAX(collection_time) AS collection_time
    FROM collector_samples
    WHERE collector_name IN (
        'perfmon_stats',
        'query_snapshots',
        'memory_grant_stats',
        'memory_pressure_events',
        'file_io_stats',
        'running_jobs',
        'database_config',
        'deadlocks',
        'blocked_process_report'
    )
    GROUP BY server_id, collector_name
)
SELECT
    cs.collection_time,
    cs.server_id,
    COALESCE(NULLIF(s.display_name, ''), cs.server_name) AS server_name,
    cs.collector_name,
    cs.sample_key,
    cs.payload_json
FROM collector_samples AS cs
JOIN latest_sample AS latest
  ON latest.server_id = cs.server_id
 AND latest.collector_name = cs.collector_name
 AND latest.collection_time = cs.collection_time
LEFT JOIN servers AS s
  ON s.server_id = cs.server_id
WHERE cs.collection_time >= $1
AND   COALESCE(s.is_enabled, TRUE) = TRUE
ORDER BY cs.collection_time DESC, cs.server_id, cs.collector_name";
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddSeconds(-Math.Max(900, _options.CurrentValue.CollectionIntervalSeconds * 3)) });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT collection_time, server_id, server_name, collector_name, status, rows_collected, duration_ms, error_message
FROM collection_log
ORDER BY collection_time DESC
LIMIT $1";
        command.Parameters.Add(new DuckDBParameter { Value = Math.Clamp(limit, 1, 1000) });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
WITH wait_window AS
(
    SELECT
        wait_type,
        MAX(wait_time_ms) - MIN(wait_time_ms) AS wait_time_delta_ms,
        MAX(signal_wait_time_ms) - MIN(signal_wait_time_ms) AS signal_wait_time_delta_ms,
        MAX(waiting_tasks_count) - MIN(waiting_tasks_count) AS waiting_tasks_delta
    FROM wait_stats
    WHERE server_id = $1
    AND   collection_time >= $2
    GROUP BY wait_type
)
SELECT wait_type, wait_time_delta_ms, signal_wait_time_delta_ms, waiting_tasks_delta
FROM wait_window
WHERE wait_time_delta_ms > 0
ORDER BY wait_time_delta_ms DESC
LIMIT $3";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)) });
        command.Parameters.Add(new DuckDBParameter { Value = Math.Clamp(limit, 1, 100) });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization
FROM cpu_utilization_stats
WHERE server_id = $1
AND   sample_time >= $2
ORDER BY sample_time";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)) });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT collection_time, session_id, wait_type, wait_duration_ms, blocking_session_id, resource_description, database_name
FROM waiting_tasks
WHERE server_id = $1
AND   collection_time >= $2
ORDER BY collection_time DESC, wait_duration_ms DESC
LIMIT $3";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)) });
        command.Parameters.Add(new DuckDBParameter { Value = Math.Clamp(limit, 1, 500) });
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT collection_time, server_id, server_name, collector_name, sample_key, payload_json
FROM collector_samples
WHERE server_id = $1
AND collector_name = $2
AND collection_time >= $3
ORDER BY collection_time DESC, sample_key
LIMIT $4";
        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = collectorName });
        command.Parameters.Add(new DuckDBParameter { Value = DateTime.UtcNow.AddHours(-Math.Clamp(hoursBack, 1, 720)) });
        command.Parameters.Add(new DuckDBParameter { Value = Math.Clamp(limit, 1, 1000) });
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
        var tables = new[] { "wait_stats", "cpu_utilization_stats", "waiting_tasks", "collector_samples", "server_properties", "collection_log" };

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            foreach (var table in tables)
            {
                var timeColumn = table == "cpu_utilization_stats" ? "sample_time" : "collection_time";
                await using var countCommand = connection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {timeColumn} < $1";
                countCommand.Parameters.Add(new DuckDBParameter { Value = cutoff });
                var count = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken));
                if (count == 0)
                {
                    continue;
                }

                var tableArchiveDirectory = Path.Combine(ArchiveDirectory, table);
                Directory.CreateDirectory(tableArchiveDirectory);
                var archiveFile = Path.Combine(tableArchiveDirectory, $"{table}_{DateTime.UtcNow:yyyyMMddTHHmmss}.parquet");
                var archiveFileSql = archiveFile.Replace("\\", "/").Replace("'", "''");
                var cutoffSql = cutoff.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

                await using var copyCommand = connection.CreateCommand();
                copyCommand.CommandText = $@"
COPY
(
    SELECT *
    FROM {table}
    WHERE {timeColumn} < TIMESTAMP '{cutoffSql}'
)
TO '{archiveFileSql}'
(FORMAT PARQUET)";
                await copyCommand.ExecuteNonQueryAsync(cancellationToken);

                await using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = $"DELETE FROM {table} WHERE {timeColumn} < $1";
                deleteCommand.Parameters.Add(new DuckDBParameter { Value = cutoff });
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Archived {RowCount} rows from {Table} to {File}", count, table, archiveFile);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task ApplyRetentionAsync(CancellationToken cancellationToken)
        => ArchiveOldDataAsync(cancellationToken);

    private static ActiveAlertDto? GetPrimaryAlert(IReadOnlyList<ActiveAlertDto> alerts)
        => alerts
            .OrderBy(static alert => alert.Severity.Equals("red", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenByDescending(static alert => alert.RaisedAt)
            .FirstOrDefault();

    private string ResolvePath(string configuredPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
        if (!Path.IsPathRooted(expanded))
        {
            expanded = Path.Combine(_environment.ContentRootPath, expanded);
        }

        return Path.GetFullPath(expanded);
    }

    private static long NextId() => Interlocked.Increment(ref s_idCounter);

    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS servers (
            server_id VARCHAR PRIMARY KEY,
            server_name VARCHAR NOT NULL,
            display_name VARCHAR,
            purpose VARCHAR NOT NULL DEFAULT 'Unassigned',
            is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
            last_seen_time TIMESTAMP,
            last_status VARCHAR NOT NULL DEFAULT 'UNKNOWN',
            last_error VARCHAR,
            product_version VARCHAR,
            edition VARCHAR,
            sql_engine_edition INTEGER,
            sql_major_version INTEGER,
            created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
        """,
        "ALTER TABLE servers ADD COLUMN IF NOT EXISTS purpose VARCHAR DEFAULT 'Unassigned'",
        "UPDATE servers SET purpose = 'Unassigned' WHERE purpose IS NULL OR TRIM(purpose) = ''",
        """
        CREATE TABLE IF NOT EXISTS collection_log (
            log_id BIGINT PRIMARY KEY,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            collector_name VARCHAR NOT NULL,
            collection_time TIMESTAMP NOT NULL,
            duration_ms INTEGER,
            status VARCHAR NOT NULL,
            error_message VARCHAR,
            rows_collected INTEGER,
            sql_duration_ms INTEGER,
            storage_duration_ms INTEGER
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS server_properties (
            collection_id BIGINT PRIMARY KEY,
            collection_time TIMESTAMP NOT NULL,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            machine_name VARCHAR,
            instance_name VARCHAR,
            product_version VARCHAR,
            product_level VARCHAR,
            edition VARCHAR,
            engine_edition INTEGER,
            sql_major_version INTEGER,
            cpu_count INTEGER,
            physical_memory_mb BIGINT,
            sqlserver_start_time TIMESTAMP
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS wait_stats (
            collection_id BIGINT PRIMARY KEY,
            collection_time TIMESTAMP NOT NULL,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            wait_type VARCHAR NOT NULL,
            waiting_tasks_count BIGINT,
            wait_time_ms BIGINT,
            signal_wait_time_ms BIGINT
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS cpu_utilization_stats (
            collection_id BIGINT PRIMARY KEY,
            collection_time TIMESTAMP NOT NULL,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            sample_time TIMESTAMP NOT NULL,
            sqlserver_cpu_utilization INTEGER,
            other_process_cpu_utilization INTEGER
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS waiting_tasks (
            collection_id BIGINT PRIMARY KEY,
            collection_time TIMESTAMP NOT NULL,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            session_id INTEGER,
            wait_type VARCHAR,
            wait_duration_ms BIGINT,
            blocking_session_id INTEGER,
            resource_description VARCHAR,
            database_name VARCHAR
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS collector_samples (
            collection_id BIGINT PRIMARY KEY,
            collection_time TIMESTAMP NOT NULL,
            server_id VARCHAR NOT NULL,
            server_name VARCHAR NOT NULL,
            collector_name VARCHAR NOT NULL,
            sample_key VARCHAR,
            payload_json VARCHAR NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_servers_status ON servers(is_enabled, last_status)",
        "CREATE INDEX IF NOT EXISTS idx_collection_log_time ON collection_log(collection_time)",
        "CREATE INDEX IF NOT EXISTS idx_collection_log_server_collector_time ON collection_log(server_id, collector_name, collection_time)",
        "CREATE INDEX IF NOT EXISTS idx_wait_stats_time ON wait_stats(server_id, collection_time)",
        "CREATE INDEX IF NOT EXISTS idx_cpu_time ON cpu_utilization_stats(server_id, sample_time)",
        "CREATE INDEX IF NOT EXISTS idx_waiting_tasks_time ON waiting_tasks(server_id, collection_time)",
        "CREATE INDEX IF NOT EXISTS idx_collector_samples_lookup ON collector_samples(server_id, collector_name, collection_time)",
        "CREATE INDEX IF NOT EXISTS idx_server_properties_time ON server_properties(server_id, collection_time)"
    ];
}
