using System.Data;
using Microsoft.Data.SqlClient;

namespace PerformanceMonitor.Collectors;

public static class SqlServerCollectors
{
    public static async Task<ServerPropertiesTelemetry> CollectServerPropertiesAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string query = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    server_name =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'ServerName')),
    machine_name =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName')),
    instance_name =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'InstanceName')),
    edition =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'Edition')),
    product_version =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')),
    product_level =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductLevel')),
    product_update_level =
        CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductUpdateLevel')),
    engine_edition =
        CONVERT(int, SERVERPROPERTY(N'EngineEdition')),
    cpu_count =
        osi.cpu_count,
    hyperthread_ratio =
        osi.hyperthread_ratio,
    physical_memory_mb =
        osi.physical_memory_kb / 1024,
    socket_count =
        osi.socket_count,
    cores_per_socket =
        osi.cores_per_socket,
    is_hadr_enabled =
        CONVERT(bit, SERVERPROPERTY(N'IsHadrEnabled')),
    is_clustered =
        CONVERT(bit, SERVERPROPERTY(N'IsClustered')),
    service_objective =
        CASE
            WHEN CONVERT(int, SERVERPROPERTY(N'EngineEdition')) = 5
            THEN CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), N'ServiceObjective'))
            ELSE NULL
        END,
    sqlserver_start_time =
        osi.sqlserver_start_time
FROM sys.dm_os_sys_info AS osi
OPTION(RECOMPILE);
""";

        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DataException("Server properties query returned no rows");
        }

        var productVersion = reader.GetString(4);
        var engineEdition = reader.GetInt32(7);
        var serviceObjective = reader.IsDBNull(15) ? null : reader.GetString(15);

        return new ServerPropertiesTelemetry(
            reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            productVersion,
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            engineEdition,
            ParseMajorVersion(productVersion),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.IsDBNull(13) ? null : reader.GetBoolean(13),
            reader.IsDBNull(14) ? null : reader.GetBoolean(14),
            serviceObjective,
            engineEdition == 5 && !string.IsNullOrWhiteSpace(serviceObjective)
                ? ParseVCoreFromServiceObjective(serviceObjective)
                : null,
            reader.GetDateTime(16));
    }

    public static async Task<IReadOnlyList<WaitStatTelemetry>> CollectWaitStatsAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        IReadOnlySet<string> ignoredWaitTypes,
        CancellationToken cancellationToken)
    {
        const string query = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    wait_type = ws.wait_type,
    waiting_tasks_count = ws.waiting_tasks_count,
    wait_time_ms = ws.wait_time_ms,
    signal_wait_time_ms = ws.signal_wait_time_ms
FROM sys.dm_os_wait_stats AS ws
WHERE ws.wait_time_ms > 0
OPTION(RECOMPILE);
""";

        var rows = new List<WaitStatTelemetry>();
        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var waitType = reader.GetString(0);
            if (ignoredWaitTypes.Contains(waitType))
            {
                continue;
            }

            rows.Add(new WaitStatTelemetry(
                waitType,
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    public static async Task<IReadOnlyList<CpuSampleTelemetry>> CollectCpuUtilizationAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        int? engineEdition,
        DateTime? lastSampleTime,
        CancellationToken cancellationToken)
    {
        var resolvedEngineEdition = engineEdition ?? await GetEngineEditionAsync(connection, commandTimeoutSeconds, cancellationToken);
        var isAzureSqlDatabase = resolvedEngineEdition == 5;
        var query = GetCpuQuery(isAzureSqlDatabase, lastSampleTime.HasValue);

        var rows = new List<CpuSampleTelemetry>();
        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        if (isAzureSqlDatabase && lastSampleTime.HasValue)
        {
            command.Parameters.Add(new SqlParameter("@last_sample_time", SqlDbType.DateTime2) { Value = lastSampleTime.Value });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sampleTime = reader.GetDateTime(0);
            if (!isAzureSqlDatabase && lastSampleTime.HasValue && sampleTime <= lastSampleTime.Value)
            {
                continue;
            }

            rows.Add(new CpuSampleTelemetry(
                sampleTime,
                reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));
        }

        return rows;
    }

    public static int? ParseVCoreFromServiceObjective(string serviceObjective)
    {
        var parts = serviceObjective.Split('_');
        return parts.Length >= 3
               && int.TryParse(parts[^1], out var vcores)
               && vcores > 0
            ? vcores
            : null;
    }

    private static async Task<int> GetEngineEditionAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CONVERT(int, SERVERPROPERTY(N'EngineEdition'));", connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static string GetCpuQuery(bool isAzureSqlDatabase, bool hasLastSampleTime)
    {
        if (isAzureSqlDatabase && hasLastSampleTime)
        {
            return """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    sample_time = drs.end_time,
    sqlserver_cpu_utilization = CONVERT(integer, drs.avg_cpu_percent),
    other_process_cpu_utilization = 0
FROM sys.dm_db_resource_stats AS drs
WHERE drs.end_time > @last_sample_time
ORDER BY
    drs.end_time DESC
OPTION(RECOMPILE);
""";
        }

        if (isAzureSqlDatabase)
        {
            return """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (60)
    sample_time = drs.end_time,
    sqlserver_cpu_utilization = CONVERT(integer, drs.avg_cpu_percent),
    other_process_cpu_utilization = 0
FROM sys.dm_db_resource_stats AS drs
ORDER BY
    drs.end_time DESC
OPTION(RECOMPILE);
""";
        }

        return """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE
    @ms_ticks bigint;

SELECT
    @ms_ticks = dosi.ms_ticks
FROM sys.dm_os_sys_info AS dosi;

SELECT TOP (60)
    sample_time = DATEADD(SECOND, -((@ms_ticks - t.timestamp) / 1000), SYSDATETIME()),
    sqlserver_cpu_utilization = t.record.value('(Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'integer'),
    other_process_cpu_utilization =
        CASE
            WHEN (100 - t.record.value('(Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'integer')
                      - t.record.value('(Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'integer')) < 0
            THEN 0
            ELSE 100 - t.record.value('(Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'integer')
                     - t.record.value('(Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'integer')
        END
FROM
(
    SELECT
        dorb.timestamp,
        record = CONVERT(xml, dorb.record)
    FROM sys.dm_os_ring_buffers AS dorb
    WHERE dorb.ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
) AS t
ORDER BY t.timestamp DESC
OPTION(RECOMPILE);
""";
    }

    private static int ParseMajorVersion(string productVersion)
        => int.TryParse(productVersion.Split('.')[0], out var majorVersion) ? majorVersion : 0;
}
