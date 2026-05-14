using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PerformanceMonitor.CentralRepository.Models;
using PerformanceMonitor.CentralRepository.Storage;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class SqlEstateCollectorService : BackgroundService
{
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly ICentralRepository _store;
    private readonly CollectionSnapshotIntakeService _intake;
    private readonly CollectionRunScheduler _scheduler;
    private readonly SqlCollectorExecutor _collectorExecutor;
    private readonly ILogger<SqlEstateCollectorService> _logger;
    private DateTime _lastArchiveTime = DateTime.UtcNow;

    public SqlEstateCollectorService(
        IOptionsMonitor<MonitorOptions> options,
        ICentralRepository store,
        CollectionSnapshotIntakeService intake,
        CollectionRunScheduler scheduler,
        SqlCollectorExecutor collectorExecutor,
        ILogger<SqlEstateCollectorService> logger)
    {
        _options = options;
        _store = store;
        _intake = intake;
        _scheduler = scheduler;
        _collectorExecutor = collectorExecutor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken);
        await _store.UpsertConfiguredServersAsync(_options.CurrentValue.Servers.Select(CollectionServerIdentity.FromOptions), stoppingToken);

        var storage = _store.GetStorageInfo();
        _logger.LogInformation(
            "Central repository monitor started. Storage={Provider}; DuckDB={DuckDbPath}; SQL={SqlDataSource}/{SqlDatabase}",
            storage.Provider,
            storage.DuckDbPath,
            storage.SqlDataSource,
            storage.SqlDatabase);

        await RunCollectionCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, _options.CurrentValue.CollectionIntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunCollectionCycleAsync(stoppingToken);
        }
    }

    private async Task RunCollectionCycleAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        await _store.InitializeAsync(cancellationToken);
        await _store.UpsertConfiguredServersAsync(options.Servers.Select(CollectionServerIdentity.FromOptions), cancellationToken);

        var enabledServers = options.Servers
            .Where(s => s.Enabled)
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToList();

        if (enabledServers.Count == 0)
        {
            _logger.LogDebug("No enabled servers configured");
            return;
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentServers));
        var tasks = enabledServers.Select(async server =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                await CollectServerAsync(server, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        await ArchiveIfDueAsync(cancellationToken);
    }

    private async Task CollectServerAsync(MonitoredServerOptions server, CancellationToken cancellationToken)
    {
        var connectionString = server.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await RecordServerConnectionAsync(
                server,
                CollectorCatalog.StatusError,
                CollectorCatalog.StatusError,
                "No connection string configured",
                DateTime.UtcNow,
                0,
                cancellationToken);
            return;
        }

        var connectionStartTime = DateTime.UtcNow;
        var connectionWatch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            connectionWatch.Stop();

            await RecordServerConnectionAsync(
                server,
                CollectorCatalog.StatusOnline,
                CollectorCatalog.StatusSuccess,
                null,
                connectionStartTime,
                ToDurationMilliseconds(connectionWatch.ElapsedMilliseconds),
                cancellationToken);

            var commandTimeoutSeconds = _options.CurrentValue.CommandTimeoutSeconds;
            foreach (var collector in _scheduler.GetDueCollectors(
                server.Id,
                _options.CurrentValue.GetEffectiveCollectors()))
            {
                await _collectorExecutor.ExecuteAsync(
                    server,
                    connection,
                    collector.Name,
                    commandTimeoutSeconds,
                    cancellationToken);
                _scheduler.MarkRun(server.Id, collector.Name);
            }
        }
        catch (SqlException ex) when (CollectorCatalog.IsAuthenticationError(ex))
        {
            connectionWatch.Stop();
            var message = BuildAuthenticationFailureMessage(ex);
            _logger.LogWarning(ex, "Authentication failed for server {Server}", server.ServerNameForStorage);
            await RecordServerConnectionAsync(
                server,
                CollectorCatalog.StatusAuthenticationFailed,
                CollectorCatalog.StatusAuthenticationFailed,
                message,
                connectionStartTime,
                ToDurationMilliseconds(connectionWatch.ElapsedMilliseconds),
                cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            connectionWatch.Stop();
            _logger.LogWarning(ex, "Connection failed for server {Server}", server.ServerNameForStorage);
            await RecordServerConnectionAsync(
                server,
                CollectorCatalog.StatusError,
                CollectorCatalog.StatusError,
                ex.Message,
                connectionStartTime,
                ToDurationMilliseconds(connectionWatch.ElapsedMilliseconds),
                cancellationToken);
        }
    }

    private Task<IngestResultDto> RecordServerConnectionAsync(
        MonitoredServerOptions server,
        string serverStatus,
        string connectionStatus,
        string? message,
        DateTime collectionTime,
        int durationMs,
        CancellationToken cancellationToken)
        => _intake.AcceptAsync(new CollectionSnapshot
        {
            Server = CollectionServerIdentity.FromOptions(server),
            CollectionTime = collectionTime,
            ServerStatus = serverStatus,
            ServerError = message,
            Logs =
            [
                new CollectionLogEntry(
                    CollectorCatalog.ServerConnection,
                    collectionTime,
                    durationMs,
                    connectionStatus,
                    message,
                    0,
                    durationMs,
                    0)
            ]
        }, cancellationToken);

    private static string BuildAuthenticationFailureMessage(SqlException exception)
    {
        var errorNumber = exception.Errors.Cast<SqlError>()
            .Select(error => error.Number)
            .FirstOrDefault(number => number != 0);
        var prefix = errorNumber == 0
            ? "Cannot log in with the configured SQL credentials"
            : $"Cannot log in with the configured SQL credentials (SQL {errorNumber})";

        return $"{prefix}: {exception.Message}";
    }

    private static int ToDurationMilliseconds(long elapsedMilliseconds)
        => checked((int)Math.Min(int.MaxValue, elapsedMilliseconds));

    private async Task ArchiveIfDueAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (options.ArchiveIntervalMinutes <= 0)
        {
            return;
        }

        if (DateTime.UtcNow - _lastArchiveTime < TimeSpan.FromMinutes(options.ArchiveIntervalMinutes))
        {
            return;
        }

        try
        {
            await _store.ApplyRetentionAsync(cancellationToken);
            _lastArchiveTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention/archive cycle failed");
        }
    }

}
