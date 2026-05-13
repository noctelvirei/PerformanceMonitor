using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Storage;

namespace PerformanceMonitor.Headless.Services;

public sealed class SqlCollectorExecutor
{
    private readonly IReadOnlySet<string> _ignoredWaitTypes = WaitTypePolicy.LoadDefaultIgnoredWaitTypes();
    private readonly IHeadlessRepository _repository;
    private readonly CollectionSnapshotIntakeService _intake;
    private readonly ILogger<SqlCollectorExecutor> _logger;

    public SqlCollectorExecutor(
        IHeadlessRepository repository,
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

                default:
                    status = "SKIPPED";
                    errorMessage = $"Unknown collector '{collectorName}'";
                    _logger.LogWarning("Unknown collector {Collector}", collectorName);
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
}
