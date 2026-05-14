using System.Globalization;
using System.Text.Json;
using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Storage;

internal static class CollectorExperienceProjection
{
    public static IReadOnlyList<string> ExperienceCollectorNames { get; } =
    [
        "query_snapshots",
        "query_stats",
        "procedure_stats",
        "query_store",
        "file_io_stats",
        "tempdb_stats",
        "perfmon_stats",
        "session_stats",
        "memory_stats",
        "memory_clerks",
        "memory_pressure_events",
        "memory_grant_stats",
        "running_jobs",
        "database_size_stats",
        "server_config",
        "database_config",
        "database_scoped_config",
        "trace_flags",
        "deadlocks",
        "blocked_process_report"
    ];

    public static IReadOnlyList<string> AlertCollectorNames { get; } =
    [
        "perfmon_stats",
        "query_snapshots",
        "memory_grant_stats",
        "memory_pressure_events",
        "file_io_stats",
        "running_jobs",
        "database_config",
        "deadlocks",
        "blocked_process_report"
    ];

    public static ServerExperienceDto Project(string serverId, IReadOnlyList<CollectorSampleDto> samples, AlertRuleOptions? alertRules = null)
    {
        var rules = alertRules ?? new AlertRuleOptions();
        var rows = LatestRows(ParseSamples(samples)
            .Where(row => string.Equals(row.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new ServerExperienceDto(
            BuildQueryPanels(rows, rules),
            BuildResourcePanels(rows, rules),
            BuildMemoryPanels(rows, rules),
            BuildJobPanels(rows, rules),
            BuildConfigPanels(rows),
            ProjectActiveAlerts(rows, rules));
    }

    public static IReadOnlyList<ActiveAlertDto> ProjectActiveAlerts(IReadOnlyList<CollectorSampleDto> samples, AlertRuleOptions? alertRules = null)
        => ProjectActiveAlerts(LatestRows(ParseSamples(samples)).ToList(), alertRules ?? new AlertRuleOptions());

    private static List<ExperiencePanelDto> BuildQueryPanels(IReadOnlyList<SampleRow> rows, AlertRuleOptions rules)
    {
        var panels = new List<ExperiencePanelDto>();

        var running = RowsFor(rows, "query_snapshots")
            .OrderByDescending(row => Number(row, "total_elapsed_time_ms") ?? 0)
            .Take(10)
            .ToList();
        if (running.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "queries",
                "Running Requests",
                WorstSeverity(running.Select(row => QuerySnapshotSeverity(row, rules))),
                $"{running.Count:n0} active request(s)",
                [Metric("Longest", FormatMs(running.Max(row => Number(row, "total_elapsed_time_ms"))))],
                running.Select(row => Row(
                    QueryLabel(row),
                    $"{Text(row, "database_name") ?? "server"} / session {Text(row, "session_id") ?? "--"} / {Text(row, "status") ?? "running"}",
                    QuerySnapshotSeverity(row, rules),
                    [
                        Metric("Elapsed", FormatMs(Number(row, "total_elapsed_time_ms"))),
                        Metric("CPU", FormatMs(Number(row, "cpu_time_ms"))),
                        Metric("Reads", FormatCount(Number(row, "logical_reads"))),
                        Metric("Wait", Text(row, "wait_type") ?? "--", QuerySnapshotSeverity(row, rules))
                    ])).ToList()));
        }

        var queryStats = RowsFor(rows, "query_stats")
            .OrderByDescending(row => Number(row, "total_worker_time_ms") ?? 0)
            .Take(10)
            .ToList();
        if (queryStats.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "queries",
                "Plan Cache",
                WorstSeverity(queryStats.Select(QueryStatsSeverity)),
                "Highest CPU consumers from the current plan cache",
                [Metric("Cached plans", FormatCount(queryStats.Count))],
                queryStats.Select(row => Row(
                    QueryLabel(row),
                    $"{Text(row, "database_name") ?? "server"} / {Text(row, "query_hash") ?? "query hash"}",
                    QueryStatsSeverity(row),
                    [
                        Metric("CPU", FormatMs(Number(row, "total_worker_time_ms"))),
                        Metric("Elapsed", FormatMs(Number(row, "total_elapsed_time_ms"))),
                        Metric("Executions", FormatCount(Number(row, "execution_count"))),
                        Metric("Reads", FormatCount(Number(row, "total_logical_reads"))),
                        Metric("Spills", FormatCount(Number(row, "total_spills")), QueryStatsSeverity(row))
                    ])).ToList()));
        }

        var procedures = RowsFor(rows, "procedure_stats")
            .OrderByDescending(row => Number(row, "total_worker_time_ms") ?? 0)
            .Take(10)
            .ToList();
        if (procedures.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "queries",
                "Stored Procedures",
                "green",
                "Highest CPU stored procedures from the current cache",
                [Metric("Procedures", FormatCount(procedures.Count))],
                procedures.Select(row => Row(
                    $"{Text(row, "schema_name") ?? "dbo"}.{Text(row, "procedure_name") ?? "procedure"}",
                    Text(row, "database_name"),
                    "green",
                    [
                        Metric("CPU", FormatMs(Number(row, "total_worker_time_ms"))),
                        Metric("Elapsed", FormatMs(Number(row, "total_elapsed_time_ms"))),
                        Metric("Executions", FormatCount(Number(row, "execution_count"))),
                        Metric("Writes", FormatCount(Number(row, "total_logical_writes")))
                    ])).ToList()));
        }

        var queryStore = RowsFor(rows, "query_store")
            .OrderByDescending(row => Number(row, "avg_duration_ms") ?? 0)
            .Take(10)
            .ToList();
        if (queryStore.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "queries",
                "Query Store",
                WorstSeverity(queryStore.Select(row => DurationSeverity(Number(row, "avg_duration_ms")))),
                "Slowest Query Store plans collected over the Query Store window",
                [Metric("Plans", FormatCount(queryStore.Count))],
                queryStore.Select(row => Row(
                    QueryLabel(row, "query_sql_text"),
                    $"{Text(row, "database_name") ?? "database"} / query {Text(row, "query_id") ?? "--"} / plan {Text(row, "plan_id") ?? "--"}",
                    DurationSeverity(Number(row, "avg_duration_ms")),
                    [
                        Metric("Avg duration", FormatMs(Number(row, "avg_duration_ms"))),
                        Metric("Avg CPU", FormatMs(Number(row, "avg_cpu_time_ms"))),
                        Metric("Executions", FormatCount(Number(row, "count_executions"))),
                        Metric("Avg reads", FormatCount(Number(row, "avg_logical_io_reads")))
                    ])).ToList()));
        }

        return panels;
    }

    private static List<ExperiencePanelDto> BuildResourcePanels(IReadOnlyList<SampleRow> rows, AlertRuleOptions rules)
    {
        var panels = new List<ExperiencePanelDto>();

        var files = RowsFor(rows, "file_io_stats")
            .OrderByDescending(row => Math.Max(ReadLatencyMs(row), WriteLatencyMs(row)))
            .Take(15)
            .ToList();
        if (files.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "resources",
                "File IO",
                WorstSeverity(files.Select(row => FileLatencySeverity(row, rules))),
                "Slowest data and log files by cumulative average latency",
                [
                    Metric("Files", FormatCount(files.Count)),
                    Metric("Worst read", FormatMs(files.Max(ReadLatencyMs))),
                    Metric("Worst write", FormatMs(files.Max(WriteLatencyMs))),
                    Metric("Size", FormatMb(files.Sum(row => Number(row, "size_mb") ?? 0)))
                ],
                files.Select(row => Row(
                    $"{Text(row, "database_name") ?? "database"} / {Text(row, "file_name") ?? "file"}",
                    Text(row, "file_type"),
                    FileLatencySeverity(row, rules),
                    [
                        Metric("Size", FormatMb(Number(row, "size_mb"))),
                        Metric("Read latency", FormatMs(ReadLatencyMs(row)), FileLatencySeverity(row, rules)),
                        Metric("Write latency", FormatMs(WriteLatencyMs(row)), FileLatencySeverity(row, rules)),
                        Metric("Queued", FormatMs((Number(row, "io_stall_queued_read_ms") ?? 0) + (Number(row, "io_stall_queued_write_ms") ?? 0)))
                    ])).ToList()));
        }

        var tempdb = RowsFor(rows, "tempdb_stats").FirstOrDefault();
        if (tempdb is not null)
        {
            var severity = TempDbSeverity(tempdb);
            panels.Add(new ExperiencePanelDto(
                "resources",
                "TempDB",
                severity,
                "Current tempdb allocation snapshot",
                [
                    Metric("Reserved", FormatMb(Number(tempdb, "total_reserved_mb")), severity),
                    Metric("Version store", FormatMb(Number(tempdb, "version_store_reserved_mb")), TempDbVersionSeverity(tempdb)),
                    Metric("Free", FormatMb(Number(tempdb, "unallocated_mb"))),
                    Metric("Sessions", FormatCount(Number(tempdb, "active_tempdb_sessions")))
                ],
                [Row(
                    $"Top session {Text(tempdb, "top_session_id") ?? "--"}",
                    "Highest tempdb allocation in the current sample",
                    severity,
                    [Metric("TempDB", FormatMb(Number(tempdb, "top_session_tempdb_mb")), severity)])]));
        }

        var perfmon = RowsFor(rows, "perfmon_stats")
            .Where(row => InterestingCounter(row))
            .OrderBy(row => PerfmonCounterOrder(Text(row, "counter_name")))
            .ThenBy(row => Text(row, "counter_name"))
            .ToList();
        if (perfmon.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "resources",
                "Counters",
                WorstSeverity(perfmon.Select(PerfmonSeverity)),
                "SQL Server performance counters from the latest collection",
                [Metric("Counters", FormatCount(perfmon.Count))],
                perfmon.Select(row => Row(
                    CounterLabel(row),
                    Text(row, "object_name"),
                    PerfmonSeverity(row),
                    [Metric("Value", FormatCount(Number(row, "cntr_value")), PerfmonSeverity(row))])).ToList()));
        }

        var sessions = RowsFor(rows, "session_stats").FirstOrDefault();
        if (sessions is not null)
        {
            var severity = (Number(sessions, "blocked_requests") ?? 0) > 0 ? "red" : "green";
            panels.Add(new ExperiencePanelDto(
                "resources",
                "Sessions",
                severity,
                "Current session pressure",
                [
                    Metric("Total", FormatCount(Number(sessions, "total_sessions"))),
                    Metric("Users", FormatCount(Number(sessions, "user_sessions"))),
                    Metric("Active", FormatCount(Number(sessions, "active_requests"))),
                    Metric("Blocked", FormatCount(Number(sessions, "blocked_requests")), severity),
                    Metric("Open Tran", FormatCount(Number(sessions, "open_transactions")))
                ],
                []));
        }

        return panels;
    }

    private static List<ExperiencePanelDto> BuildMemoryPanels(IReadOnlyList<SampleRow> rows, AlertRuleOptions rules)
    {
        var panels = new List<ExperiencePanelDto>();

        var memory = RowsFor(rows, "memory_stats").FirstOrDefault();
        if (memory is not null)
        {
            var severity = MemoryStateSeverity(memory);
            panels.Add(new ExperiencePanelDto(
                "memory",
                "Memory",
                severity,
                Text(memory, "system_memory_state") ?? "Latest memory snapshot",
                [
                    Metric("Available", FormatMb(Number(memory, "available_physical_memory_mb")), severity),
                    Metric("SQL total", FormatMb(Number(memory, "total_server_memory_mb"))),
                    Metric("SQL target", FormatMb(Number(memory, "target_server_memory_mb"))),
                    Metric("Buffer pool", FormatMb(Number(memory, "buffer_pool_mb"))),
                    Metric("Plan cache", FormatMb(Number(memory, "plan_cache_mb"))),
                    Metric("Workers", $"{FormatCount(Number(memory, "current_workers_count"))} / {FormatCount(Number(memory, "max_workers_count"))}")
                ],
                []));
        }

        var clerks = RowsFor(rows, "memory_clerks")
            .OrderByDescending(row => Number(row, "memory_mb") ?? 0)
            .Take(12)
            .ToList();
        if (clerks.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "memory",
                "Memory Clerks",
                "green",
                "Largest memory clerk allocations",
                [Metric("Top clerks", FormatCount(clerks.Count))],
                clerks.Select(row => Row(
                    Text(row, "clerk_type") ?? "clerk",
                    null,
                    "green",
                    [Metric("Memory", FormatMb(Number(row, "memory_mb")))])).ToList()));
        }

        var grants = RowsFor(rows, "memory_grant_stats")
            .OrderByDescending(row => Number(row, "requested_memory_mb") ?? 0)
            .Take(12)
            .ToList();
        if (grants.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "memory",
                "Memory Grants",
                WorstSeverity(grants.Select(row => MemoryGrantSeverity(row, rules))),
                "Current query memory grants",
                [
                    Metric("Grants", FormatCount(grants.Count)),
                    Metric("Waiting", FormatCount(grants.Count(row => (Number(row, "wait_time_ms") ?? 0) > 0)), WorstSeverity(grants.Select(row => MemoryGrantSeverity(row, rules))))
                ],
                grants.Select(row => Row(
                    QueryLabel(row),
                    $"{Text(row, "database_name") ?? "server"} / session {Text(row, "session_id") ?? "--"}",
                    MemoryGrantSeverity(row, rules),
                    [
                        Metric("Requested", FormatMb(Number(row, "requested_memory_mb"))),
                        Metric("Granted", FormatMb(Number(row, "granted_memory_mb"))),
                        Metric("Used", FormatMb(Number(row, "used_memory_mb"))),
                        Metric("Wait", FormatMs(Number(row, "wait_time_ms")), MemoryGrantSeverity(row, rules))
                    ])).ToList()));
        }

        var pressure = RowsFor(rows, "memory_pressure_events")
            .Where(row => IsRecent(row, "sample_time", 24))
            .Take(10)
            .ToList();
        if (pressure.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "memory",
                "Pressure Events",
                WorstSeverity(pressure.Select(MemoryPressureSeverity)),
                "Recent resource monitor signals",
                [Metric("Events", FormatCount(pressure.Count), WorstSeverity(pressure.Select(MemoryPressureSeverity)))],
                pressure.Select(row => Row(
                    Text(row, "memory_notification") ?? "memory event",
                    Text(row, "sample_time"),
                    MemoryPressureSeverity(row),
                    [
                        Metric("Process", FormatCount(Number(row, "memory_indicators_process"))),
                        Metric("System", FormatCount(Number(row, "memory_indicators_system")))
                    ])).ToList()));
        }

        return panels;
    }

    private static List<ExperiencePanelDto> BuildJobPanels(IReadOnlyList<SampleRow> rows, AlertRuleOptions rules)
    {
        var panels = new List<ExperiencePanelDto>();

        var jobs = RowsFor(rows, "running_jobs")
            .OrderByDescending(row => Number(row, "run_duration_seconds") ?? 0)
            .Take(15)
            .ToList();
        if (jobs.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "jobs",
                "Running Jobs",
                WorstSeverity(jobs.Select(row => JobSeverity(row, rules))),
                "SQL Agent jobs currently running",
                [
                    Metric("Running", FormatCount(jobs.Count), WorstSeverity(jobs.Select(row => JobSeverity(row, rules)))),
                    Metric("Longest", FormatSeconds(jobs.Max(row => Number(row, "run_duration_seconds"))))
                ],
                jobs.Select(row => Row(
                    Text(row, "job_name") ?? "job",
                    Text(row, "current_step_name"),
                    JobSeverity(row, rules),
                    [
                        Metric("Runtime", FormatSeconds(Number(row, "run_duration_seconds")), JobSeverity(row, rules)),
                        Metric("Step", Text(row, "current_step_id") ?? "--")
                    ])).ToList()));
        }

        var sizes = RowsFor(rows, "database_size_stats")
            .OrderByDescending(row => (Number(row, "data_size_mb") ?? 0) + (Number(row, "log_size_mb") ?? 0))
            .Take(15)
            .ToList();
        if (sizes.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "jobs",
                "Database Sizes",
                WorstSeverity(sizes.Select(DatabaseStateSeverity)),
                "Largest databases in the latest collection",
                [Metric("Databases", FormatCount(sizes.Count))],
                sizes.Select(row => Row(
                    Text(row, "database_name") ?? "database",
                    Text(row, "state_desc") ?? Text(row, "recovery_model_desc"),
                    DatabaseStateSeverity(row),
                    [
                        Metric("Data", FormatMb(Number(row, "data_size_mb"))),
                        Metric("Log", FormatMb(Number(row, "log_size_mb"))),
                        Metric("Files", FormatCount(Number(row, "file_count")))
                    ])).ToList()));
        }

        var events = RowsFor(rows, "deadlocks")
            .Where(row => IsRecent(row, "deadlock_time", 24))
            .Concat(RowsFor(rows, "blocked_process_report").Where(row => IsRecent(row, "event_time", 24)))
            .OrderByDescending(row => EventTime(row) ?? row.CollectionTime)
            .Take(15)
            .ToList();
        if (events.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "jobs",
                "Blocking Events",
                WorstSeverity(events.Select(EventSeverity)),
                "Deadlock and blocked process events",
                [Metric("Events", FormatCount(events.Count), WorstSeverity(events.Select(EventSeverity)))],
                events.Select(row => Row(
                    EventLabel(row),
                    FormatEventTime(EventTime(row)),
                    EventSeverity(row),
                    [
                        Metric("Blocked", Text(row, "blocked_spid") ?? "--"),
                        Metric("Blocking", Text(row, "blocking_spid") ?? "--"),
                        Metric("Wait", FormatMs(Number(row, "wait_time_ms")), EventSeverity(row))
                    ])).ToList()));
        }

        return panels;
    }

    private static List<ExperiencePanelDto> BuildConfigPanels(IReadOnlyList<SampleRow> rows)
    {
        var panels = new List<ExperiencePanelDto>();

        var serverConfig = RowsFor(rows, "server_config")
            .Where(IsInterestingServerConfig)
            .OrderBy(row => Text(row, "name"))
            .Take(50)
            .ToList();
        if (serverConfig.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "config",
                "Server Configuration",
                WorstSeverity(serverConfig.Select(ServerConfigSeverity)),
                "Configuration values worth reviewing",
                [Metric("Options", FormatCount(serverConfig.Count))],
                serverConfig.Select(row => Row(
                    Text(row, "name") ?? "configuration",
                    Text(row, "description"),
                    ServerConfigSeverity(row),
                    [
                        Metric("Value", Text(row, "value") ?? "--"),
                        Metric("In use", Text(row, "value_in_use") ?? "--", ServerConfigSeverity(row)),
                        Metric("Dynamic", Flag(row, "is_dynamic") == true ? "Yes" : "No")
                    ])).ToList()));
        }

        var databaseConfig = RowsFor(rows, "database_config")
            .OrderByDescending(row => DatabaseConfigSeverity(row) == "red" ? 2 : DatabaseConfigSeverity(row) == "yellow" ? 1 : 0)
            .ThenBy(row => Text(row, "database_name"))
            .Take(50)
            .ToList();
        if (databaseConfig.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "config",
                "Databases",
                WorstSeverity(databaseConfig.Select(DatabaseConfigSeverity)),
                "Database settings from sys.databases",
                [Metric("Databases", FormatCount(databaseConfig.Count))],
                databaseConfig.Select(row => Row(
                    Text(row, "database_name") ?? "database",
                    $"{Text(row, "state_desc") ?? "state"} / {Text(row, "recovery_model_desc") ?? "recovery"}",
                    DatabaseConfigSeverity(row),
                    [
                        Metric("Compat", Text(row, "compatibility_level") ?? "--"),
                        Metric("Query Store", Flag(row, "is_query_store_on") == true ? "On" : "Off"),
                        Metric("Auto close", Flag(row, "is_auto_close_on") == true ? "On" : "Off", DatabaseConfigSeverity(row)),
                        Metric("Auto shrink", Flag(row, "is_auto_shrink_on") == true ? "On" : "Off", DatabaseConfigSeverity(row))
                    ])).ToList()));
        }

        var scopedConfig = RowsFor(rows, "database_scoped_config")
            .OrderBy(row => Text(row, "database_name"))
            .ThenBy(row => Text(row, "configuration_name"))
            .Take(50)
            .ToList();
        if (scopedConfig.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "config",
                "Database Scoped Configuration",
                "green",
                "Per-database scoped configuration values",
                [Metric("Values", FormatCount(scopedConfig.Count))],
                scopedConfig.Select(row => Row(
                    Text(row, "configuration_name") ?? "configuration",
                    Text(row, "database_name"),
                    "green",
                    [
                        Metric("Value", Text(row, "value") ?? "--"),
                        Metric("Secondary", Text(row, "value_for_secondary") ?? "--")
                    ])).ToList()));
        }

        var traceFlags = RowsFor(rows, "trace_flags")
            .OrderBy(row => Text(row, "TraceFlag") ?? Text(row, "traceflag"))
            .ToList();
        if (traceFlags.Count > 0)
        {
            panels.Add(new ExperiencePanelDto(
                "config",
                "Trace Flags",
                "yellow",
                "Global trace flags enabled on the server",
                [Metric("Flags", FormatCount(traceFlags.Count), "yellow")],
                traceFlags.Select(row => Row(
                    $"Trace flag {Text(row, "TraceFlag") ?? Text(row, "traceflag") ?? "--"}",
                    $"Global: {Text(row, "Global") ?? Text(row, "global") ?? "--"} / Session: {Text(row, "Session") ?? Text(row, "session") ?? "--"}",
                    "yellow",
                    [])).ToList()));
        }

        return panels;
    }

    private static List<ActiveAlertDto> ProjectActiveAlerts(IReadOnlyList<SampleRow> rows, AlertRuleOptions rules)
    {
        var alerts = new List<ActiveAlertDto>();
        if (!rules.Enabled)
        {
            return alerts;
        }

        foreach (var row in rows)
        {
            switch (row.CollectorName)
            {
                case "perfmon_stats":
                    AddPerfmonAlert(alerts, row, rules);
                    break;
                case "query_snapshots":
                    AddLongRunningQueryAlert(alerts, row, rules);
                    break;
                case "memory_grant_stats":
                    AddMemoryGrantAlert(alerts, row, rules);
                    break;
                case "memory_pressure_events":
                    AddMemoryPressureAlert(alerts, row);
                    break;
                case "file_io_stats":
                    AddFileIoAlert(alerts, row, rules);
                    break;
                case "running_jobs":
                    AddRunningJobAlert(alerts, row, rules);
                    break;
                case "database_config":
                    AddDatabaseConfigAlert(alerts, row);
                    break;
                case "deadlocks":
                    AddDeadlockAlert(alerts, row, rules);
                    break;
                case "blocked_process_report":
                    AddBlockedProcessAlert(alerts, row, rules);
                    break;
            }
        }

        return alerts
            .GroupBy(alert => $"{alert.ServerId}|{alert.Source}|{alert.Message}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(alert => alert.RaisedAt).First())
            .OrderBy(alert => HealthRank(alert.Severity))
            .ThenByDescending(alert => alert.RaisedAt)
            .Take(100)
            .ToList();
    }

    private static void AddPerfmonAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        var counter = Text(row, "counter_name");
        var value = Number(row, "cntr_value") ?? 0;
        if (rules.BlockingEnabled && string.Equals(counter, "Processes blocked", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            alerts.Add(Alert(row, "Counters", "red", $"{FormatCount(value)} blocked process(es)", "resources"));
        }
        else if (rules.MemoryGrantEnabled && string.Equals(counter, "Memory Grants Pending", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            alerts.Add(Alert(row, "Memory", "red", $"{FormatCount(value)} memory grant(s) pending", "memory"));
        }
        else if (rules.DeadlockEnabled && string.Equals(counter, "Number of Deadlocks/sec", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            alerts.Add(Alert(row, "Deadlocks", "red", "Deadlock counter is moving", "jobs"));
        }
        else if (string.Equals(counter, "Page life expectancy", StringComparison.OrdinalIgnoreCase) && value > 0 && value < 300)
        {
            alerts.Add(Alert(row, "Memory", "yellow", $"Page life expectancy is {FormatCount(value)}", "memory"));
        }
    }

    private static void AddLongRunningQueryAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.LongRunningQueryEnabled)
        {
            return;
        }

        var elapsedMs = Number(row, "total_elapsed_time_ms") ?? 0;
        var warningMs = rules.LongRunningQueryWarningMinutes * 60_000m;
        if (elapsedMs < warningMs)
        {
            return;
        }

        var criticalMs = rules.LongRunningQueryCriticalMinutes * 60_000m;
        alerts.Add(Alert(
            row,
            "Queries",
            elapsedMs >= criticalMs ? "red" : "yellow",
            $"Session {Text(row, "session_id") ?? "--"} has been running for {FormatMs(elapsedMs)}",
            "queries"));
    }

    private static void AddMemoryGrantAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.MemoryGrantEnabled)
        {
            return;
        }

        var waitMs = Number(row, "wait_time_ms") ?? 0;
        if (waitMs <= 0)
        {
            return;
        }

        var criticalMs = rules.MemoryGrantCriticalSeconds * 1000m;
        alerts.Add(Alert(
            row,
            "Memory grants",
            waitMs >= criticalMs ? "red" : "yellow",
            $"Session {Text(row, "session_id") ?? "--"} has waited {FormatMs(waitMs)} for a memory grant",
            "memory"));
    }

    private static void AddMemoryPressureAlert(List<ActiveAlertDto> alerts, SampleRow row)
    {
        if (!IsRecent(row, "sample_time", 1) || !MemoryPressureSeverity(row).Equals("red", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        alerts.Add(Alert(row, "Memory pressure", "red", Text(row, "memory_notification") ?? "Memory pressure event", "memory"));
    }

    private static void AddFileIoAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.FileLatencyEnabled)
        {
            return;
        }

        var severity = FileLatencySeverity(row, rules);
        if (severity == "green")
        {
            return;
        }

        var worst = Math.Max(ReadLatencyMs(row), WriteLatencyMs(row));
        alerts.Add(Alert(
            row,
            "File IO",
            severity,
            $"{Text(row, "database_name") ?? "database"} / {Text(row, "file_name") ?? "file"} latency is {FormatMs(worst)}",
            "resources"));
    }

    private static void AddRunningJobAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.LongRunningJobEnabled)
        {
            return;
        }

        var severity = JobSeverity(row, rules);
        if (severity == "green")
        {
            return;
        }

        alerts.Add(Alert(
            row,
            "SQL Agent",
            severity,
            $"{Text(row, "job_name") ?? "Job"} has been running for {FormatSeconds(Number(row, "run_duration_seconds"))}",
            "jobs"));
    }

    private static void AddDatabaseConfigAlert(List<ActiveAlertDto> alerts, SampleRow row)
    {
        if (Flag(row, "is_auto_close_on") == true)
        {
            alerts.Add(Alert(row, "Database config", "yellow", $"{Text(row, "database_name") ?? "Database"} has AUTO_CLOSE on", "config"));
        }

        if (Flag(row, "is_auto_shrink_on") == true)
        {
            alerts.Add(Alert(row, "Database config", "yellow", $"{Text(row, "database_name") ?? "Database"} has AUTO_SHRINK on", "config"));
        }

        if (!string.Equals(Text(row, "state_desc"), "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(Alert(row, "Database state", "red", $"{Text(row, "database_name") ?? "Database"} is {Text(row, "state_desc") ?? "not online"}", "config"));
        }
    }

    private static void AddDeadlockAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.DeadlockEnabled)
        {
            return;
        }

        if (!IsRecent(row, "deadlock_time", 1))
        {
            return;
        }

        alerts.Add(Alert(row, "Deadlock", "red", $"Deadlock captured at {FormatEventTime(DateTimeValue(row, "deadlock_time"))}", "jobs"));
    }

    private static void AddBlockedProcessAlert(List<ActiveAlertDto> alerts, SampleRow row, AlertRuleOptions rules)
    {
        if (!rules.BlockingEnabled)
        {
            return;
        }

        if (!IsRecent(row, "event_time", 1))
        {
            return;
        }

        alerts.Add(Alert(
            row,
            "Blocked process",
            "red",
            $"SPID {Text(row, "blocked_spid") ?? "--"} blocked by {Text(row, "blocking_spid") ?? "--"} for {FormatMs(Number(row, "wait_time_ms"))}",
            "stats"));
    }

    private static IEnumerable<SampleRow> ParseSamples(IEnumerable<CollectorSampleDto> samples)
    {
        foreach (var sample in samples)
        {
            Dictionary<string, JsonElement>? values = null;
            try
            {
                values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(sample.PayloadJson);
            }
            catch (JsonException)
            {
            }

            yield return new SampleRow(
                sample.CollectionTime,
                sample.ServerId,
                sample.ServerName,
                sample.CollectorName,
                sample.SampleKey,
                values ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<SampleRow> LatestRows(IEnumerable<SampleRow> rows)
        => rows
            .GroupBy(row => $"{row.ServerId}\u001f{row.CollectorName}", StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var latest = group.Max(row => row.CollectionTime);
                return group.Where(row => row.CollectionTime == latest);
            });

    private static IEnumerable<SampleRow> RowsFor(IEnumerable<SampleRow> rows, string collectorName)
        => rows.Where(row => string.Equals(row.CollectorName, collectorName, StringComparison.OrdinalIgnoreCase));

    private static ExperienceMetricDto Metric(string label, string value, string? severity = null)
        => new(label, value, severity);

    private static ExperienceMetricDto Metric(string label, decimal? value, string? severity = null)
        => new(label, value.HasValue ? FormatCount(value.Value) : "--", severity);

    private static ExperienceRowDto Row(string label, string? description, string severity, IReadOnlyList<ExperienceMetricDto> metrics)
        => new(Shorten(label, 180), string.IsNullOrWhiteSpace(description) ? null : Shorten(description, 220), severity, metrics);

    private static ActiveAlertDto Alert(SampleRow row, string source, string severity, string message, string targetTab)
        => new(row.CollectionTime, row.ServerId, row.ServerName, source, severity, message, targetTab);

    private static string QueryLabel(SampleRow row, string textColumn = "sql_text")
        => Shorten(Text(row, textColumn) ?? Text(row, "query_sql_text") ?? Text(row, "procedure_name") ?? Text(row, "command") ?? "query", 180);

    private static string CounterLabel(SampleRow row)
    {
        var counter = Text(row, "counter_name") ?? "counter";
        var instance = Text(row, "instance_name");
        return string.IsNullOrWhiteSpace(instance) ? counter : $"{counter} / {instance}";
    }

    private static string EventLabel(SampleRow row)
        => string.Equals(row.CollectorName, "deadlocks", StringComparison.OrdinalIgnoreCase)
            ? "Deadlock"
            : $"Blocked SPID {Text(row, "blocked_spid") ?? "--"}";

    private static DateTime? EventTime(SampleRow row)
        => DateTimeValue(row, "deadlock_time") ?? DateTimeValue(row, "event_time");

    private static string EventSeverity(SampleRow row)
        => string.Equals(row.CollectorName, "deadlocks", StringComparison.OrdinalIgnoreCase)
            ? "red"
            : DurationSeverity(Number(row, "wait_time_ms"));

    private static string QuerySnapshotSeverity(SampleRow row, AlertRuleOptions rules)
    {
        if ((Number(row, "blocking_session_id") ?? 0) > 0)
        {
            return "red";
        }

        var waitMs = Number(row, "wait_time_ms") ?? 0;
        var elapsedMs = Number(row, "total_elapsed_time_ms") ?? 0;
        if (waitMs >= 30000 || elapsedMs >= rules.LongRunningQueryCriticalMinutes * 60_000m)
        {
            return "red";
        }

        if (waitMs >= 5000 || elapsedMs >= rules.LongRunningQueryWarningMinutes * 60_000m)
        {
            return "yellow";
        }

        return "green";
    }

    private static string QueryStatsSeverity(SampleRow row)
        => (Number(row, "total_spills") ?? 0) > 0 ? "yellow" : "green";

    private static string DurationSeverity(decimal? durationMs)
    {
        var ms = durationMs ?? 0;
        if (ms >= 300000)
        {
            return "red";
        }

        return ms >= 30000 ? "yellow" : "green";
    }

    private static decimal ReadLatencyMs(SampleRow row)
    {
        var reads = Number(row, "num_of_reads") ?? 0;
        return reads <= 0 ? 0 : (Number(row, "io_stall_read_ms") ?? 0) / reads;
    }

    private static decimal WriteLatencyMs(SampleRow row)
    {
        var writes = Number(row, "num_of_writes") ?? 0;
        return writes <= 0 ? 0 : (Number(row, "io_stall_write_ms") ?? 0) / writes;
    }

    private static string FileLatencySeverity(SampleRow row, AlertRuleOptions rules)
    {
        var latency = Math.Max(ReadLatencyMs(row), WriteLatencyMs(row));
        if (latency >= rules.FileLatencyCriticalMs)
        {
            return "red";
        }

        return latency >= rules.FileLatencyWarningMs ? "yellow" : "green";
    }

    private static string TempDbSeverity(SampleRow row)
        => WorstSeverity([TempDbVersionSeverity(row), (Number(row, "active_tempdb_sessions") ?? 0) > 50 ? "yellow" : "green"]);

    private static string TempDbVersionSeverity(SampleRow row)
        => (Number(row, "version_store_reserved_mb") ?? 0) >= 1024 ? "yellow" : "green";

    private static bool InterestingCounter(SampleRow row)
        => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Batch Requests/sec",
            "Page life expectancy",
            "Memory Grants Pending",
            "Processes blocked",
            "Number of Deadlocks/sec",
            "Lock Waits/sec",
            "Lock Wait Time (ms)",
            "Page reads/sec",
            "Page writes/sec",
            "SQL Compilations/sec",
            "SQL Re-Compilations/sec"
        }.Contains(Text(row, "counter_name") ?? "");

    private static int PerfmonCounterOrder(string? counter)
        => (counter ?? "").ToLowerInvariant() switch
        {
            "processes blocked" => 1,
            "memory grants pending" => 2,
            "number of deadlocks/sec" => 3,
            "page life expectancy" => 4,
            "batch requests/sec" => 5,
            _ => 99
        };

    private static string PerfmonSeverity(SampleRow row)
    {
        var counter = Text(row, "counter_name");
        var value = Number(row, "cntr_value") ?? 0;
        if (string.Equals(counter, "Processes blocked", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            return "red";
        }

        if (string.Equals(counter, "Memory Grants Pending", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            return "red";
        }

        if (string.Equals(counter, "Number of Deadlocks/sec", StringComparison.OrdinalIgnoreCase) && value > 0)
        {
            return "red";
        }

        if (string.Equals(counter, "Page life expectancy", StringComparison.OrdinalIgnoreCase) && value > 0 && value < 300)
        {
            return "yellow";
        }

        return "green";
    }

    private static string MemoryStateSeverity(SampleRow row)
        => (Text(row, "system_memory_state") ?? "").Contains("Low", StringComparison.OrdinalIgnoreCase)
            ? "red"
            : "green";

    private static string MemoryGrantSeverity(SampleRow row, AlertRuleOptions rules)
    {
        var waitMs = Number(row, "wait_time_ms") ?? 0;
        if (waitMs >= rules.MemoryGrantCriticalSeconds * 1000m)
        {
            return "red";
        }

        return waitMs >= rules.MemoryGrantWarningSeconds * 1000m ? "yellow" : "green";
    }

    private static string MemoryPressureSeverity(SampleRow row)
        => (Text(row, "memory_notification") ?? "").Contains("LOW", StringComparison.OrdinalIgnoreCase)
            ? "red"
            : "green";

    private static string JobSeverity(SampleRow row, AlertRuleOptions rules)
    {
        var seconds = Number(row, "run_duration_seconds") ?? 0;
        if (seconds >= rules.LongRunningJobCriticalMinutes * 60m)
        {
            return "red";
        }

        return seconds >= rules.LongRunningJobWarningMinutes * 60m ? "yellow" : "green";
    }

    private static string DatabaseStateSeverity(SampleRow row)
        => string.Equals(Text(row, "state_desc"), "ONLINE", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Text(row, "state_desc"))
            ? "green"
            : "red";

    private static bool IsInterestingServerConfig(SampleRow row)
    {
        var name = Text(row, "name") ?? "";
        return ServerConfigSeverity(row) != "green"
            || name.Contains("max degree of parallelism", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cost threshold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("max server memory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("min server memory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("backup compression", StringComparison.OrdinalIgnoreCase)
            || name.Contains("optimize for ad hoc", StringComparison.OrdinalIgnoreCase);
    }

    private static string ServerConfigSeverity(SampleRow row)
    {
        var name = Text(row, "name") ?? "";
        var inUse = Number(row, "value_in_use") ?? 0;
        if ((name.Equals("xp_cmdshell", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Ad Hoc Distributed Queries", StringComparison.OrdinalIgnoreCase))
            && inUse != 0)
        {
            return "yellow";
        }

        return "green";
    }

    private static string DatabaseConfigSeverity(SampleRow row)
    {
        if (!string.Equals(Text(row, "state_desc"), "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            return "red";
        }

        return Flag(row, "is_auto_close_on") == true || Flag(row, "is_auto_shrink_on") == true
            ? "yellow"
            : "green";
    }

    private static string WorstSeverity(IEnumerable<string> severities)
    {
        var worst = severities
            .DefaultIfEmpty("green")
            .OrderBy(HealthRank)
            .First();

        return string.IsNullOrWhiteSpace(worst) ? "green" : worst;
    }

    private static int HealthRank(string? severity)
        => (severity ?? "").ToLowerInvariant() switch
        {
            "red" => 1,
            "yellow" => 2,
            "green" => 3,
            _ => 4
        };

    private static bool IsRecent(SampleRow row, string key, int hours)
    {
        var value = DateTimeValue(row, key);
        return (value ?? row.CollectionTime) >= DateTime.UtcNow.AddHours(-hours);
    }

    private static string? Text(SampleRow row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGet(row, key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToString()
            };
        }

        return null;
    }

    private static decimal? Number(SampleRow row, string key)
    {
        if (!TryGet(row, key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static bool? Flag(SampleRow row, string key)
    {
        if (!TryGet(row, key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        var number = Number(row, key);
        if (number.HasValue)
        {
            return number.Value != 0;
        }

        var text = value.GetString();
        return text?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            || text?.Equals("on", StringComparison.OrdinalIgnoreCase) == true
            || text == "1";
    }

    private static DateTime? DateTimeValue(SampleRow row, string key)
    {
        var text = Text(row, key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static bool TryGet(SampleRow row, string key, out JsonElement value)
    {
        if (row.Values.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var item in row.Values)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string FormatCount(decimal? value)
        => value.HasValue ? FormatCount(value.Value) : "--";

    private static string FormatCount(decimal value)
        => value.ToString(value % 1 == 0 ? "n0" : "n2", CultureInfo.InvariantCulture);

    private static string FormatMs(decimal? value)
        => value.HasValue ? FormatMs(value.Value) : "--";

    private static string FormatMs(decimal value)
    {
        if (value >= 3600000)
        {
            return $"{value / 3600000:n1}h";
        }

        if (value >= 60000)
        {
            return $"{value / 60000:n1}m";
        }

        return value >= 1000 ? $"{value / 1000:n1}s" : $"{value:n0}ms";
    }

    private static string FormatSeconds(decimal? value)
        => value.HasValue ? FormatMs(value.Value * 1000) : "--";

    private static string FormatMb(decimal? value)
        => value.HasValue ? FormatMb(value.Value) : "--";

    private static string FormatMb(decimal value)
        => value >= 1024 ? $"{value / 1024:n1} GB" : $"{value:n0} MB";

    private static string FormatEventTime(DateTime? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "--";

    private static string Shorten(string value, int maxLength)
    {
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : $"{text[..Math.Max(0, maxLength - 3)]}...";
    }

    private sealed record SampleRow(
        DateTime CollectionTime,
        string ServerId,
        string ServerName,
        string CollectorName,
        string? SampleKey,
        IReadOnlyDictionary<string, JsonElement> Values);
}
