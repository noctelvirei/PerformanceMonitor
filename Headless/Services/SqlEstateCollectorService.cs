using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Storage;

namespace PerformanceMonitor.Headless.Services;

public sealed class SqlEstateCollectorService : BackgroundService
{
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly IHeadlessRepository _store;
    private readonly CollectionSnapshotIntakeService _intake;
    private readonly CollectionRunScheduler _scheduler;
    private readonly SqlCollectorExecutor _collectorExecutor;
    private readonly ILogger<SqlEstateCollectorService> _logger;
    private DateTime _lastArchiveTime = DateTime.UtcNow;

    public SqlEstateCollectorService(
        IOptionsMonitor<MonitorOptions> options,
        IHeadlessRepository store,
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
            await _intake.AcceptAsync(new CollectionSnapshot
            {
                Server = CollectionServerIdentity.FromOptions(server),
                ServerStatus = "ERROR",
                ServerError = "No connection string configured"
            }, cancellationToken);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await _intake.AcceptAsync(new CollectionSnapshot
            {
                Server = CollectionServerIdentity.FromOptions(server),
                ServerStatus = "ONLINE"
            }, cancellationToken);

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
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Connection failed for server {Server}", server.ServerNameForStorage);
            await _intake.AcceptAsync(new CollectionSnapshot
            {
                Server = CollectionServerIdentity.FromOptions(server),
                ServerStatus = "ERROR",
                ServerError = ex.Message
            }, cancellationToken);
        }
    }

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
