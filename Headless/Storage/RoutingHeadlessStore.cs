using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Storage;

public sealed class RoutingHeadlessStore : IHeadlessStore
{
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly HeadlessStore _duckDbStore;
    private readonly SqlServerHeadlessStore _sqlServerStore;

    public RoutingHeadlessStore(
        IOptionsMonitor<MonitorOptions> options,
        HeadlessStore duckDbStore,
        SqlServerHeadlessStore sqlServerStore)
    {
        _options = options;
        _duckDbStore = duckDbStore;
        _sqlServerStore = sqlServerStore;
    }

    public StorageInfoDto GetStorageInfo()
        => CurrentStore.GetStorageInfo();

    public Task InitializeAsync(CancellationToken cancellationToken)
        => CurrentStore.InitializeAsync(cancellationToken);

    public Task UpsertConfiguredServersAsync(IEnumerable<MonitoredServerOptions> servers, CancellationToken cancellationToken)
        => CurrentStore.UpsertConfiguredServersAsync(servers, cancellationToken);

    public Task SetServerStatusAsync(MonitoredServerOptions server, string status, string? errorMessage, ServerPropertiesSnapshot? properties, CancellationToken cancellationToken)
        => CurrentStore.SetServerStatusAsync(server, status, errorMessage, properties, cancellationToken);

    public Task InsertServerPropertiesAsync(MonitoredServerOptions server, DateTime collectionTime, ServerPropertiesSnapshot properties, CancellationToken cancellationToken)
        => CurrentStore.InsertServerPropertiesAsync(server, collectionTime, properties, cancellationToken);

    public Task InsertWaitStatsAsync(MonitoredServerOptions server, DateTime collectionTime, IReadOnlyList<WaitStatSnapshot> rows, CancellationToken cancellationToken)
        => CurrentStore.InsertWaitStatsAsync(server, collectionTime, rows, cancellationToken);

    public Task<DateTime?> GetLastCpuSampleTimeAsync(string serverId, CancellationToken cancellationToken)
        => CurrentStore.GetLastCpuSampleTimeAsync(serverId, cancellationToken);

    public Task InsertCpuSamplesAsync(MonitoredServerOptions server, DateTime collectionTime, IReadOnlyList<CpuSample> rows, CancellationToken cancellationToken)
        => CurrentStore.InsertCpuSamplesAsync(server, collectionTime, rows, cancellationToken);

    public Task InsertCollectionLogAsync(MonitoredServerOptions server, string collectorName, DateTime collectionTime, int durationMs, string status, string? errorMessage, int rowsCollected, long sqlDurationMs, long storageDurationMs, CancellationToken cancellationToken)
        => CurrentStore.InsertCollectionLogAsync(server, collectorName, collectionTime, durationMs, status, errorMessage, rowsCollected, sqlDurationMs, storageDurationMs, cancellationToken);

    public Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken)
        => CurrentStore.GetServersAsync(cancellationToken);

    public Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken)
        => CurrentStore.GetEstateSummaryAsync(cancellationToken);

    public Task<IReadOnlyList<ActiveAlertDto>> GetActiveAlertsAsync(CancellationToken cancellationToken)
        => CurrentStore.GetActiveAlertsAsync(cancellationToken);

    public Task<IReadOnlyList<CollectionLogDto>> GetCollectionLogAsync(int limit, CancellationToken cancellationToken)
        => CurrentStore.GetCollectionLogAsync(limit, cancellationToken);

    public Task<IReadOnlyList<TopWaitDto>> GetTopWaitsAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken)
        => CurrentStore.GetTopWaitsAsync(serverId, hoursBack, limit, cancellationToken);

    public Task<IReadOnlyList<CpuSampleDto>> GetCpuSamplesAsync(string serverId, int hoursBack, CancellationToken cancellationToken)
        => CurrentStore.GetCpuSamplesAsync(serverId, hoursBack, cancellationToken);

    public Task ArchiveOldDataAsync(CancellationToken cancellationToken)
        => CurrentStore.ArchiveOldDataAsync(cancellationToken);

    private IHeadlessStore CurrentStore
        => string.Equals(_options.CurrentValue.StorageProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? _sqlServerStore
            : _duckDbStore;
}
