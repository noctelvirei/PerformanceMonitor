using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace PerformanceMonitor.Collectors;

public sealed record SqlRawCollectorContext(
    int? EngineEdition,
    int? SqlMajorVersion,
    IReadOnlyList<string> ExcludedDatabases);

public sealed record SqlRawCollectorDefinition(
    string Name,
    IReadOnlyList<string> KeyColumns,
    Func<SqlRawCollectorContext, string> QueryFactory);

public sealed record SqlRawCollectorRow(
    string? SampleKey,
    string PayloadJson);

public static partial class SqlServerCollectors
{
    private static readonly JsonSerializerOptions s_rawJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryGetRawCollectorDefinition(string collectorName, out SqlRawCollectorDefinition definition)
        => RawCollectorDefinitions.TryGetValue(collectorName, out definition!);

    public static async Task<IReadOnlyList<SqlRawCollectorRow>> CollectRawRowsAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        SqlRawCollectorDefinition definition,
        SqlRawCollectorContext context,
        CancellationToken cancellationToken)
    {
        var rows = new List<SqlRawCollectorRow>();
        await using var command = new SqlCommand(definition.QueryFactory(context), connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = ReadRawRow(reader);
                var sampleKey = BuildSampleKey(values, definition.KeyColumns);
                rows.Add(new SqlRawCollectorRow(sampleKey, JsonSerializer.Serialize(values, s_rawJsonOptions)));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        return rows;
    }

    private static Dictionary<string, object?> ReadRawRow(SqlDataReader reader)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            values[reader.GetName(i)] = NormalizeRawValue(reader, i);
        }

        return values;
    }

    private static object? NormalizeRawValue(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            decimal decimalValue => decimalValue,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    private static string? BuildSampleKey(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> keyColumns)
    {
        if (keyColumns.Count == 0)
        {
            return null;
        }

        var parts = keyColumns
            .Select(column => values.TryGetValue(column, out var value) ? value?.ToString() : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return parts.Count == 0 ? null : string.Join("|", parts);
    }

    private static readonly IReadOnlyDictionary<string, SqlRawCollectorDefinition> RawCollectorDefinitions =
        new Dictionary<string, SqlRawCollectorDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [SqlCollectorNames.MemoryStats] = new(
                SqlCollectorNames.MemoryStats,
                [],
                context => context.EngineEdition == 5 ? AzureMemoryStatsQuery : MemoryStatsQuery),
            [SqlCollectorNames.MemoryClerks] = new(
                SqlCollectorNames.MemoryClerks,
                ["clerk_type"],
                _ => MemoryClerksQuery),
            [SqlCollectorNames.MemoryPressureEvents] = new(
                SqlCollectorNames.MemoryPressureEvents,
                ["sample_time", "memory_notification"],
                _ => MemoryPressureEventsQuery),
            [SqlCollectorNames.FileIoStats] = new(
                SqlCollectorNames.FileIoStats,
                ["database_name", "file_name"],
                context => context.EngineEdition == 5 ? AzureFileIoStatsQuery : FileIoStatsQuery),
            [SqlCollectorNames.TempDbStats] = new(
                SqlCollectorNames.TempDbStats,
                [],
                _ => TempDbStatsQuery),
            [SqlCollectorNames.PerfmonStats] = new(
                SqlCollectorNames.PerfmonStats,
                ["object_name", "counter_name", "instance_name"],
                _ => PerfmonStatsQuery),
            [SqlCollectorNames.MemoryGrantStats] = new(
                SqlCollectorNames.MemoryGrantStats,
                ["session_id", "request_id"],
                _ => MemoryGrantStatsQuery),
            [SqlCollectorNames.SessionStats] = new(
                SqlCollectorNames.SessionStats,
                [],
                _ => SessionStatsQuery),
            [SqlCollectorNames.QuerySnapshots] = new(
                SqlCollectorNames.QuerySnapshots,
                ["session_id", "request_id"],
                _ => QuerySnapshotsQuery),
            [SqlCollectorNames.QueryStats] = new(
                SqlCollectorNames.QueryStats,
                ["query_hash", "plan_hash"],
                _ => QueryStatsQuery),
            [SqlCollectorNames.QueryStore] = new(
                SqlCollectorNames.QueryStore,
                ["database_name", "query_id", "plan_id"],
                _ => QueryStoreQuery),
            [SqlCollectorNames.ProcedureStats] = new(
                SqlCollectorNames.ProcedureStats,
                ["database_name", "schema_name", "procedure_name"],
                _ => ProcedureStatsQuery),
            [SqlCollectorNames.ServerConfig] = new(
                SqlCollectorNames.ServerConfig,
                ["name"],
                _ => ServerConfigQuery),
            [SqlCollectorNames.DatabaseConfig] = new(
                SqlCollectorNames.DatabaseConfig,
                ["database_name"],
                _ => DatabaseConfigQuery),
            [SqlCollectorNames.DatabaseScopedConfig] = new(
                SqlCollectorNames.DatabaseScopedConfig,
                ["database_name", "configuration_name"],
                _ => DatabaseScopedConfigQuery),
            [SqlCollectorNames.TraceFlags] = new(
                SqlCollectorNames.TraceFlags,
                ["TraceFlag"],
                _ => TraceFlagsQuery),
            [SqlCollectorNames.RunningJobs] = new(
                SqlCollectorNames.RunningJobs,
                ["job_id"],
                _ => RunningJobsQuery),
            [SqlCollectorNames.DatabaseSizeStats] = new(
                SqlCollectorNames.DatabaseSizeStats,
                ["database_name"],
                context => context.EngineEdition == 5 ? AzureDatabaseSizeStatsQuery : DatabaseSizeStatsQuery),
            [SqlCollectorNames.Deadlocks] = new(
                SqlCollectorNames.Deadlocks,
                ["deadlock_time"],
                _ => DeadlocksQuery),
            [SqlCollectorNames.BlockedProcessReport] = new(
                SqlCollectorNames.BlockedProcessReport,
                ["event_time", "blocked_spid", "blocking_spid"],
                _ => BlockedProcessReportQuery)
        };

    private const string AzureMemoryStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    total_physical_memory_mb = CONVERT(decimal(18,2), osi.committed_target_kb / 1024.0),
    available_physical_memory_mb = CONVERT(decimal(18,2), (osi.committed_target_kb - osi.committed_kb) / 1024.0),
    total_page_file_mb = CONVERT(decimal(18,2), 0),
    available_page_file_mb = CONVERT(decimal(18,2), 0),
    system_memory_state = N'Available',
    sql_memory_model = N'N/A',
    target_server_memory_mb = CONVERT(decimal(18,2), pc_target.cntr_value / 1024.0),
    total_server_memory_mb = CONVERT(decimal(18,2), pc_total.cntr_value / 1024.0),
    buffer_pool_mb = CONVERT(decimal(18,2), pc_buffer.cntr_value / 1024.0),
    plan_cache_mb = CONVERT(decimal(18,2), pc_plan.cntr_value * 8.0 / 1024.0),
    max_workers_count = osi.max_workers_count,
    current_workers_count = CONVERT(int, NULL)
FROM sys.dm_os_sys_info AS osi
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Target Server Memory (KB)') AS pc_target
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Total Server Memory (KB)') AS pc_total
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Database Cache Memory (KB)') AS pc_buffer
CROSS JOIN (SELECT cntr_value = SUM(cntr_value) FROM sys.dm_os_performance_counters WHERE counter_name = N'Cache Pages' AND object_name LIKE N'%:Plan Cache%') AS pc_plan
OPTION(RECOMPILE);
""";

    private const string MemoryStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    total_physical_memory_mb = CONVERT(decimal(18,2), osm.total_physical_memory_kb / 1024.0),
    available_physical_memory_mb = CONVERT(decimal(18,2), osm.available_physical_memory_kb / 1024.0),
    total_page_file_mb = CONVERT(decimal(18,2), osm.total_page_file_kb / 1024.0),
    available_page_file_mb = CONVERT(decimal(18,2), osm.available_page_file_kb / 1024.0),
    system_memory_state = osm.system_memory_state_desc,
    sql_memory_model = osi.sql_memory_model_desc,
    target_server_memory_mb = CONVERT(decimal(18,2), pc_target.cntr_value / 1024.0),
    total_server_memory_mb = CONVERT(decimal(18,2), pc_total.cntr_value / 1024.0),
    buffer_pool_mb = CONVERT(decimal(18,2), pc_buffer.cntr_value / 1024.0),
    plan_cache_mb = CONVERT(decimal(18,2), pc_plan.cntr_value * 8.0 / 1024.0),
    max_workers_count = osi.max_workers_count,
    current_workers_count = w.current_workers
FROM sys.dm_os_sys_memory AS osm
CROSS JOIN sys.dm_os_sys_info AS osi
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Target Server Memory (KB)') AS pc_target
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Total Server Memory (KB)') AS pc_total
CROSS JOIN (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Database Cache Memory (KB)') AS pc_buffer
CROSS JOIN (SELECT cntr_value = SUM(cntr_value) FROM sys.dm_os_performance_counters WHERE counter_name = N'Cache Pages' AND object_name LIKE N'%:Plan Cache%') AS pc_plan
CROSS JOIN (SELECT current_workers = SUM(active_workers_count) FROM sys.dm_os_schedulers WHERE status = N'VISIBLE ONLINE') AS w
OPTION(RECOMPILE);
""";

    private const string MemoryClerksQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (25)
    clerk_type = mc.type,
    memory_mb = CONVERT(decimal(18,2), SUM(mc.pages_kb) / 1024.0)
FROM sys.dm_os_memory_clerks AS mc
GROUP BY mc.type
HAVING SUM(mc.pages_kb) > 1024
ORDER BY SUM(mc.pages_kb) DESC
OPTION(RECOMPILE);
""";

    private const string MemoryPressureEventsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @ms_ticks bigint, @now datetime2(7) = SYSDATETIME();
SELECT @ms_ticks = dosi.ms_ticks FROM sys.dm_os_sys_info AS dosi;

SELECT TOP (200)
    sample_time = DATEADD(SECOND, -((@ms_ticks - t.timestamp) / 1000), @now),
    memory_notification = t.record.value('(/Record/ResourceMonitor/Notification)[1]', 'nvarchar(100)'),
    memory_indicators_process = t.record.value('(/Record/ResourceMonitor/IndicatorsProcess)[1]', 'integer'),
    memory_indicators_system = t.record.value('(/Record/ResourceMonitor/IndicatorsSystem)[1]', 'integer')
FROM
(
    SELECT dorb.timestamp, record = CONVERT(xml, dorb.record)
    FROM sys.dm_os_ring_buffers AS dorb
    WHERE dorb.ring_buffer_type = N'RING_BUFFER_RESOURCE_MONITOR'
) AS t
ORDER BY t.timestamp DESC
OPTION(RECOMPILE);
""";

    private const string AzureFileIoStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = DB_NAME(),
    file_name = df.name,
    file_type = df.type_desc,
    physical_name = df.physical_name,
    size_mb = CONVERT(decimal(18,2), vfs.size_on_disk_bytes / 1048576.0),
    num_of_reads = vfs.num_of_reads,
    num_of_writes = vfs.num_of_writes,
    read_bytes = vfs.num_of_bytes_read,
    write_bytes = vfs.num_of_bytes_written,
    io_stall_read_ms = vfs.io_stall_read_ms,
    io_stall_write_ms = vfs.io_stall_write_ms,
    io_stall_queued_read_ms = vfs.io_stall_queued_read_ms,
    io_stall_queued_write_ms = vfs.io_stall_queued_write_ms
FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) AS vfs
LEFT JOIN sys.database_files AS df ON df.file_id = vfs.file_id
OPTION(RECOMPILE);
""";

    private const string FileIoStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = ISNULL(d.name, DB_NAME(vfs.database_id)),
    file_name = ISNULL(mf.name, N'File_' + CONVERT(nvarchar(10), vfs.file_id)),
    file_type = ISNULL(mf.type_desc, N'UNKNOWN'),
    physical_name = ISNULL(mf.physical_name, N''),
    size_mb = CONVERT(decimal(18,2), vfs.size_on_disk_bytes / 1048576.0),
    num_of_reads = vfs.num_of_reads,
    num_of_writes = vfs.num_of_writes,
    read_bytes = vfs.num_of_bytes_read,
    write_bytes = vfs.num_of_bytes_written,
    io_stall_read_ms = vfs.io_stall_read_ms,
    io_stall_write_ms = vfs.io_stall_write_ms,
    io_stall_queued_read_ms = vfs.io_stall_queued_read_ms,
    io_stall_queued_write_ms = vfs.io_stall_queued_write_ms
FROM sys.dm_io_virtual_file_stats(NULL, NULL) AS vfs
LEFT JOIN sys.master_files AS mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id
LEFT JOIN sys.databases AS d ON d.database_id = vfs.database_id
WHERE (vfs.database_id > 4 OR vfs.database_id = 2)
AND   vfs.database_id < 32761
AND   vfs.database_id <> ISNULL(DB_ID(N'PerformanceMonitor'), 0)
OPTION(RECOMPILE);
""";

    private const string TempDbStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    user_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count) * 8 / 1024.0),
    internal_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.internal_object_reserved_page_count) * 8 / 1024.0),
    version_store_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.version_store_reserved_page_count) * 8 / 1024.0),
    total_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count + dsu.internal_object_reserved_page_count + dsu.version_store_reserved_page_count) * 8 / 1024.0),
    unallocated_mb = CONVERT(decimal(18,2), SUM(dsu.unallocated_extent_page_count) * 8 / 1024.0),
    top_session_id = top_session.session_id,
    top_session_tempdb_mb = top_session.tempdb_mb,
    active_tempdb_sessions = top_session.total_sessions
FROM tempdb.sys.dm_db_file_space_usage AS dsu
OUTER APPLY
(
    SELECT TOP (1)
        session_id = ssu.session_id,
        tempdb_mb = CONVERT(decimal(18,2), (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) * 8 / 1024.0),
        total_sessions = (SELECT COUNT_BIG(*) FROM sys.dm_db_session_space_usage WHERE user_objects_alloc_page_count + internal_objects_alloc_page_count > 0)
    FROM sys.dm_db_session_space_usage AS ssu
    ORDER BY (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) DESC
) AS top_session
GROUP BY top_session.session_id, top_session.tempdb_mb, top_session.total_sessions
OPTION(RECOMPILE);
""";

    private const string PerfmonStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    object_name = RTRIM(pc.object_name),
    counter_name = RTRIM(pc.counter_name),
    instance_name = RTRIM(pc.instance_name),
    cntr_value = pc.cntr_value
FROM sys.dm_os_performance_counters AS pc
WHERE pc.counter_name IN (
    N'Batch Requests/sec', N'Page reads/sec', N'Page writes/sec',
    N'Checkpoint pages/sec', N'Lazy writes/sec', N'Page life expectancy',
    N'Target Server Memory (KB)', N'Total Server Memory (KB)',
    N'Memory Grants Pending', N'Processes blocked', N'Number of Deadlocks/sec',
    N'Lock Waits/sec', N'Lock Wait Time (ms)', N'Transactions/sec',
    N'Log Flushes/sec', N'Log Bytes Flushed/sec', N'Log Flush Write Time (ms)',
    N'Free Space in tempdb (KB)', N'Version Store Size (KB)',
    N'SQL Compilations/sec', N'SQL Re-Compilations/sec'
)
OPTION(RECOMPILE);
""";

    private const string MemoryGrantStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    session_id = mg.session_id,
    request_id = mg.request_id,
    database_name = DB_NAME(er.database_id),
    requested_memory_mb = CONVERT(decimal(18,2), mg.requested_memory_kb / 1024.0),
    granted_memory_mb = CONVERT(decimal(18,2), mg.granted_memory_kb / 1024.0),
    used_memory_mb = CONVERT(decimal(18,2), mg.used_memory_kb / 1024.0),
    ideal_memory_mb = CONVERT(decimal(18,2), mg.ideal_memory_kb / 1024.0),
    required_memory_mb = CONVERT(decimal(18,2), mg.required_memory_kb / 1024.0),
    request_time = mg.request_time,
    grant_time = mg.grant_time,
    wait_time_ms = mg.wait_time_ms,
    dop = mg.dop,
    queue_id = mg.queue_id,
    sql_text = LEFT(st.text, 4000)
FROM sys.dm_exec_query_memory_grants AS mg
LEFT JOIN sys.dm_exec_requests AS er ON er.session_id = mg.session_id AND er.request_id = mg.request_id
OUTER APPLY sys.dm_exec_sql_text(er.sql_handle) AS st
ORDER BY mg.requested_memory_kb DESC
OPTION(RECOMPILE);
""";

    private const string SessionStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    total_sessions = COUNT_BIG(*),
    user_sessions = SUM(CASE WHEN is_user_process = 1 THEN 1 ELSE 0 END),
    active_requests = (SELECT COUNT_BIG(*) FROM sys.dm_exec_requests WHERE session_id <> @@SPID),
    blocked_requests = (SELECT COUNT_BIG(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0),
    sleeping_sessions = SUM(CASE WHEN status = N'sleeping' THEN 1 ELSE 0 END),
    open_transactions = SUM(open_transaction_count)
FROM sys.dm_exec_sessions
OPTION(RECOMPILE);
""";

    private const string QuerySnapshotsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (100)
    session_id = er.session_id,
    request_id = er.request_id,
    database_name = DB_NAME(er.database_id),
    status = er.status,
    command = er.command,
    wait_type = er.wait_type,
    wait_time_ms = er.wait_time,
    blocking_session_id = er.blocking_session_id,
    cpu_time_ms = er.cpu_time,
    total_elapsed_time_ms = er.total_elapsed_time,
    logical_reads = er.logical_reads,
    reads = er.reads,
    writes = er.writes,
    percent_complete = er.percent_complete,
    granted_query_memory_pages = er.granted_query_memory,
    login_name = es.login_name,
    host_name = es.host_name,
    program_name = es.program_name,
    sql_text = LEFT(st.text, 4000)
FROM sys.dm_exec_requests AS er
LEFT JOIN sys.dm_exec_sessions AS es ON es.session_id = er.session_id
OUTER APPLY sys.dm_exec_sql_text(er.sql_handle) AS st
WHERE er.session_id <> @@SPID
ORDER BY er.total_elapsed_time DESC
OPTION(RECOMPILE);
""";

    private const string QueryStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (100)
    database_name = DB_NAME(CONVERT(int, pa.value)),
    query_hash = CONVERT(varchar(34), qs.query_hash, 1),
    plan_hash = CONVERT(varchar(34), qs.query_plan_hash, 1),
    execution_count = qs.execution_count,
    total_worker_time_ms = qs.total_worker_time / 1000,
    total_elapsed_time_ms = qs.total_elapsed_time / 1000,
    total_logical_reads = qs.total_logical_reads,
    total_logical_writes = qs.total_logical_writes,
    total_physical_reads = qs.total_physical_reads,
    total_spills = qs.total_spills,
    last_execution_time = qs.last_execution_time,
    sql_text = LEFT(st.text, 4000)
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
OUTER APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
WHERE pa.attribute = N'dbid'
ORDER BY qs.total_worker_time DESC
OPTION(RECOMPILE);
""";

    private const string ProcedureStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (100)
    database_name = DB_NAME(ps.database_id),
    schema_name = OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id),
    procedure_name = OBJECT_NAME(ps.object_id, ps.database_id),
    execution_count = ps.execution_count,
    total_worker_time_ms = ps.total_worker_time / 1000,
    total_elapsed_time_ms = ps.total_elapsed_time / 1000,
    total_logical_reads = ps.total_logical_reads,
    total_logical_writes = ps.total_logical_writes,
    cached_time = ps.cached_time,
    last_execution_time = ps.last_execution_time
FROM sys.dm_exec_procedure_stats AS ps
ORDER BY ps.total_worker_time DESC
OPTION(RECOMPILE);
""";

    private const string QueryStoreQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

CREATE TABLE #query_store
(
    database_name sysname NOT NULL,
    query_id bigint NOT NULL,
    plan_id bigint NOT NULL,
    count_executions bigint NULL,
    avg_duration_ms decimal(18,2) NULL,
    avg_cpu_time_ms decimal(18,2) NULL,
    avg_logical_io_reads decimal(18,2) NULL,
    last_execution_time datetime2 NULL,
    query_sql_text nvarchar(max) NULL
);

DECLARE @database_name sysname;
DECLARE @sql nvarchar(max);

DECLARE database_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT name
FROM sys.databases
WHERE state_desc = N'ONLINE'
AND is_query_store_on = 1
AND database_id > 4
AND database_id < 32761;

OPEN database_cursor;
FETCH NEXT FROM database_cursor INTO @database_name;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'
USE ' + QUOTENAME(@database_name) + N';
INSERT #query_store
(
    database_name, query_id, plan_id, count_executions, avg_duration_ms,
    avg_cpu_time_ms, avg_logical_io_reads, last_execution_time, query_sql_text
)
SELECT TOP (50)
    database_name = DB_NAME(),
    q.query_id,
    p.plan_id,
    count_executions = SUM(rs.count_executions),
    avg_duration_ms = CONVERT(decimal(18,2), AVG(rs.avg_duration) / 1000.0),
    avg_cpu_time_ms = CONVERT(decimal(18,2), AVG(rs.avg_cpu_time) / 1000.0),
    avg_logical_io_reads = CONVERT(decimal(18,2), AVG(rs.avg_logical_io_reads)),
    last_execution_time = MAX(rs.last_execution_time),
    query_sql_text = LEFT(MAX(qt.query_sql_text), 4000)
FROM sys.query_store_runtime_stats AS rs
JOIN sys.query_store_runtime_stats_interval AS rsi
  ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
JOIN sys.query_store_plan AS p
  ON p.plan_id = rs.plan_id
JOIN sys.query_store_query AS q
  ON q.query_id = p.query_id
JOIN sys.query_store_query_text AS qt
  ON qt.query_text_id = q.query_text_id
WHERE rsi.end_time >= DATEADD(HOUR, -24, SYSUTCDATETIME())
GROUP BY q.query_id, p.plan_id
ORDER BY AVG(rs.avg_duration) DESC;';

    BEGIN TRY
        EXEC sys.sp_executesql @sql;
    END TRY
    BEGIN CATCH
    END CATCH;

    FETCH NEXT FROM database_cursor INTO @database_name;
END

CLOSE database_cursor;
DEALLOCATE database_cursor;

SELECT TOP (100)
    database_name,
    query_id,
    plan_id,
    count_executions,
    avg_duration_ms,
    avg_cpu_time_ms,
    avg_logical_io_reads,
    last_execution_time,
    query_sql_text
FROM #query_store
ORDER BY avg_duration_ms DESC;
""";

    private const string ServerConfigQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    name,
    value,
    value_in_use,
    minimum,
    maximum,
    is_dynamic,
    is_advanced,
    description
FROM sys.configurations
ORDER BY name
OPTION(RECOMPILE);
""";

    private const string DatabaseConfigQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = name,
    database_id,
    state_desc,
    recovery_model_desc,
    compatibility_level,
    collation_name,
    snapshot_isolation_state_desc,
    is_read_committed_snapshot_on,
    is_auto_close_on,
    is_auto_shrink_on,
    is_query_store_on,
    page_verify_option_desc,
    create_date
FROM sys.databases
WHERE database_id < 32761
ORDER BY name
OPTION(RECOMPILE);
""";

    private const string DatabaseScopedConfigQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @sql nvarchar(max) = N'';

SELECT @sql += N'
USE ' + QUOTENAME(name) + N';
SELECT database_name = DB_NAME(), configuration_name = name, value, value_for_secondary
FROM sys.database_scoped_configurations;'
FROM sys.databases
WHERE state_desc = N'ONLINE'
AND database_id < 32761;

EXEC sys.sp_executesql @sql;
""";

    private const string TraceFlagsQuery = """
DBCC TRACESTATUS(-1) WITH NO_INFOMSGS;
""";

    private const string RunningJobsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    job_id = CONVERT(nvarchar(36), j.job_id),
    job_name = j.name,
    start_execution_date = ja.start_execution_date,
    run_duration_seconds = DATEDIFF(SECOND, ja.start_execution_date, SYSDATETIME()),
    current_step_id = ja.last_executed_step_id,
    current_step_name = js.step_name
FROM msdb.dbo.sysjobactivity AS ja
JOIN msdb.dbo.sysjobs AS j ON j.job_id = ja.job_id
LEFT JOIN msdb.dbo.sysjobsteps AS js ON js.job_id = ja.job_id AND js.step_id = ja.last_executed_step_id
WHERE ja.start_execution_date IS NOT NULL
AND ja.stop_execution_date IS NULL
ORDER BY ja.start_execution_date
OPTION(RECOMPILE);
""";

    private const string AzureDatabaseSizeStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = DB_NAME(),
    data_size_mb = CONVERT(decimal(18,2), SUM(CASE WHEN type_desc = N'ROWS' THEN size ELSE 0 END) * 8 / 1024.0),
    log_size_mb = CONVERT(decimal(18,2), SUM(CASE WHEN type_desc = N'LOG' THEN size ELSE 0 END) * 8 / 1024.0),
    file_count = COUNT_BIG(*)
FROM sys.database_files
OPTION(RECOMPILE);
""";

    private const string DatabaseSizeStatsQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = d.name,
    data_size_mb = CONVERT(decimal(18,2), SUM(CASE WHEN mf.type_desc = N'ROWS' THEN mf.size ELSE 0 END) * 8 / 1024.0),
    log_size_mb = CONVERT(decimal(18,2), SUM(CASE WHEN mf.type_desc = N'LOG' THEN mf.size ELSE 0 END) * 8 / 1024.0),
    file_count = COUNT_BIG(*),
    state_desc = d.state_desc,
    recovery_model_desc = d.recovery_model_desc
FROM sys.databases AS d
JOIN sys.master_files AS mf ON mf.database_id = d.database_id
WHERE d.database_id < 32761
GROUP BY d.name, d.state_desc, d.recovery_model_desc
ORDER BY d.name
OPTION(RECOMPILE);
""";

    private const string DeadlocksQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (50)
    deadlock_time = xed.event_data.value('(/event/@timestamp)[1]', 'datetime2'),
    deadlock_graph_xml = CONVERT(nvarchar(max), xed.event_data.query('/event/data/value/deadlock'))
FROM
(
    SELECT event_data = CONVERT(xml, event_data)
    FROM sys.fn_xe_file_target_read_file(N'system_health*.xel', NULL, NULL, NULL)
    WHERE object_name = N'xml_deadlock_report'
) AS xed
ORDER BY deadlock_time DESC
OPTION(RECOMPILE);
""";

    private const string BlockedProcessReportQuery = """
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (100)
    event_time = event_node.value('(@timestamp)[1]', 'datetime2'),
    blocked_spid = event_node.value('(data/value/blocked-process-report/blocked-process/process/@spid)[1]', 'int'),
    blocking_spid = event_node.value('(data/value/blocked-process-report/blocking-process/process/@spid)[1]', 'int'),
    wait_time_ms = event_node.value('(data/value/blocked-process-report/blocked-process/process/@waittime)[1]', 'bigint'),
    lock_mode = event_node.value('(data/value/blocked-process-report/blocked-process/process/@lockMode)[1]', 'nvarchar(32)'),
    blocked_sql_text = event_node.value('(data/value/blocked-process-report/blocked-process/process/inputbuf/text())[1]', 'nvarchar(max)'),
    blocking_sql_text = event_node.value('(data/value/blocked-process-report/blocking-process/process/inputbuf/text())[1]', 'nvarchar(max)'),
    blocked_process_report_xml = CONVERT(nvarchar(max), event_node.query('.'))
FROM sys.dm_xe_sessions AS s
JOIN sys.dm_xe_session_targets AS t
  ON t.event_session_address = s.address
CROSS APPLY (SELECT target_xml = CONVERT(xml, t.target_data)) AS tx
CROSS APPLY tx.target_xml.nodes('//event[@name="blocked_process_report"]') AS events(event_node)
WHERE t.target_name = N'ring_buffer'
ORDER BY event_time DESC
OPTION(RECOMPILE);
""";
}
