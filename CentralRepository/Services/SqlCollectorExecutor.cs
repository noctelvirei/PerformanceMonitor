using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.CentralRepository.Models;
using PerformanceMonitor.CentralRepository.Storage;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class SqlCollectorExecutor
{
    private readonly IReadOnlySet<string> _ignoredWaitTypes = WaitTypePolicy.LoadDefaultIgnoredWaitTypes();
    private readonly ICentralRepository _repository;
    private readonly CollectionSnapshotIntakeService _intake;
    private readonly ILogger<SqlCollectorExecutor> _logger;

    public SqlCollectorExecutor(
        ICentralRepository repository,
        CollectionSnapshotIntakeService intake,
        ILogger<SqlCollectorExecutor> logger)
    {
        _repository = repository;
        _intake = intake;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        MonitoredServerOptions server,
        SqlConnection connection,
        string collectorName,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var totalWatch = Stopwatch.StartNew();
        var sqlWatch = new Stopwatch();
        var rowsCollected = 0;
        var status = "SUCCESS";
        string? errorMessage = null;
        ServerPropertiesSnapshot? properties = null;
        IReadOnlyList<WaitStatSnapshot> waitStats = [];
        IReadOnlyList<CpuSample> cpuSamples = [];
        IReadOnlyList<WaitingTaskSnapshot> waitingTasks = [];
        IReadOnlyList<CollectorSampleSnapshot> collectorSamples = [];

        try
        {
            switch (collectorName)
            {
                case CollectorCatalog.ServerProperties:
                    sqlWatch.Start();
                    var collectedProperties = await SqlServerCollectors.CollectServerPropertiesAsync(
                        connection,
                        commandTimeoutSeconds,
                        cancellationToken);
                    sqlWatch.Stop();
                    properties = ToSnapshot(collectedProperties);
                    rowsCollected = 1;
                    break;

                case CollectorCatalog.WaitStats:
                    sqlWatch.Start();
                    var collectedWaits = await SqlServerCollectors.CollectWaitStatsAsync(
                        connection,
                        commandTimeoutSeconds,
                        _ignoredWaitTypes,
                        cancellationToken);
                    sqlWatch.Stop();
                    waitStats = collectedWaits
                        .Select(ToSnapshot)
                        .ToList();
                    rowsCollected = waitStats.Count;
                    break;

                case CollectorCatalog.CpuUtilization:
                    var lastSampleTime = await _repository.GetLastCpuSampleTimeAsync(server.Id, cancellationToken);
                    sqlWatch.Start();
                    var collectedCpu = await SqlServerCollectors.CollectCpuUtilizationAsync(
                        connection,
                        commandTimeoutSeconds,
                        properties?.EngineEdition,
                        lastSampleTime,
                        cancellationToken);
                    sqlWatch.Stop();
                    cpuSamples = collectedCpu
                        .Select(ToSnapshot)
                        .ToList();
                    rowsCollected = cpuSamples.Count;
                    break;

                case CollectorCatalog.WaitingTasks:
                    sqlWatch.Start();
                    var collectedWaitingTasks = await SqlServerCollectors.CollectWaitingTasksAsync(
                        connection,
                        commandTimeoutSeconds,
                        [],
                        cancellationToken);
                    sqlWatch.Stop();
                    waitingTasks = collectedWaitingTasks
                        .Select(ToSnapshot)
                        .ToList();
                    rowsCollected = waitingTasks.Count;
                    break;

                default:
                    if (SqlServerCollectors.TryGetRawCollectorDefinition(collectorName, out var rawDefinition))
                    {
                        sqlWatch.Start();
                        var rawRows = await SqlServerCollectors.CollectRawRowsAsync(
                            connection,
                            commandTimeoutSeconds,
                            rawDefinition,
                            await BuildRawCollectorContextAsync(connection, commandTimeoutSeconds, cancellationToken),
                            cancellationToken);
                        sqlWatch.Stop();
                        collectorSamples = rawRows
                            .Select(row => new CollectorSampleSnapshot(collectorName, row.SampleKey, row.PayloadJson))
                            .ToList();
                        rowsCollected = collectorSamples.Count;
                    }
                    else
                    {
                        status = "SKIPPED";
                        errorMessage = $"Unknown collector '{collectorName}'";
                        _logger.LogWarning("Unknown collector {Collector}", collectorName);
                    }
                    break;
            }
        }
        catch (SqlException ex) when (CollectorCatalog.IsPermissionError(ex))
        {
            status = "PERMISSIONS";
            errorMessage = $"SQL Error #{ex.Number}: {ex.Message}";
            _logger.LogWarning("Collector {Collector} permission denied for {Server}: {Message}",
                collectorName, server.ServerNameForStorage, ex.Message);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or DataException)
        {
            status = "ERROR";
            errorMessage = ex.Message;
            _logger.LogWarning(ex, "Collector {Collector} failed for {Server}",
                collectorName, server.ServerNameForStorage);
        }
        finally
        {
            totalWatch.Stop();
            await _intake.AcceptAsync(new CollectionSnapshot
            {
                Server = CollectionServerIdentity.FromOptions(server),
                CollectionTime = startTime,
                ServerStatus = "ONLINE",
                ServerProperties = properties,
                WaitStats = waitStats,
                CpuSamples = cpuSamples,
                WaitingTasks = waitingTasks,
                CollectorSamples = collectorSamples,
                Logs =
                [
                    new CollectionLogEntry(
                        collectorName,
                        startTime,
                        (int)totalWatch.ElapsedMilliseconds,
                        status,
                        errorMessage,
                        rowsCollected,
                        sqlWatch.ElapsedMilliseconds,
                        0)
                ]
            }, cancellationToken);
        }
    }

    private static ServerPropertiesSnapshot ToSnapshot(ServerPropertiesTelemetry properties)
        => new(
            properties.MachineName,
            properties.InstanceName,
            properties.ProductVersion,
            properties.ProductLevel,
            properties.Edition,
            properties.EngineEdition,
            properties.SqlMajorVersion,
            properties.CpuCount,
            properties.PhysicalMemoryMb,
            properties.SqlServerStartTime);

    private static WaitStatSnapshot ToSnapshot(WaitStatTelemetry wait)
        => new(
            wait.WaitType,
            wait.WaitingTasksCount,
            wait.WaitTimeMs,
            wait.SignalWaitTimeMs);

    private static CpuSample ToSnapshot(CpuSampleTelemetry sample)
        => new(
            sample.SampleTime,
            sample.SqlServerCpuUtilization,
            sample.OtherProcessCpuUtilization);

    private static WaitingTaskSnapshot ToSnapshot(WaitingTaskTelemetry task)
        => new(
            task.SessionId,
            task.WaitType,
            task.WaitDurationMs,
            task.BlockingSessionId,
            task.ResourceDescription,
            task.DatabaseName);

    private static async Task<SqlRawCollectorContext> BuildRawCollectorContextAsync(
        SqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
SELECT
    engine_edition = CONVERT(int, SERVERPROPERTY(N'EngineEdition')),
    product_version = CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion'));
""", connection);
        command.CommandTimeout = Math.Max(1, commandTimeoutSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SqlRawCollectorContext(null, null, []);
        }

        var engineEdition = reader.IsDBNull(0) ? null : (int?)reader.GetInt32(0);
        var productVersion = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var majorVersion = int.TryParse(productVersion.Split('.')[0], out var parsed) ? parsed : (int?)null;
        return new SqlRawCollectorContext(engineEdition, majorVersion, []);
    }
}
