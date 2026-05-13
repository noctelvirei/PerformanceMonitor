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

    public Task UpsertConfiguredServersAsync(IEnumerable<CollectionServerIdentity> servers, CancellationToken cancellationToken)
        => CurrentStore.UpsertConfiguredServersAsync(servers, cancellationToken);

    public Task RecordSnapshotAsync(CollectionSnapshot snapshot, CancellationToken cancellationToken)
        => CurrentStore.RecordSnapshotAsync(snapshot, cancellationToken);

    public Task<DateTime?> GetLastCpuSampleTimeAsync(string serverId, CancellationToken cancellationToken)
        => CurrentStore.GetLastCpuSampleTimeAsync(serverId, cancellationToken);

    public Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken)
        => CurrentStore.GetServersAsync(cancellationToken);

    public Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken)
        => CurrentStore.GetEstateSummaryAsync(cancellationToken);

    public Task<IReadOnlyList<ActiveAlertDto>> GetEstateActiveAlertsAsync(CancellationToken cancellationToken)
        => CurrentStore.GetEstateActiveAlertsAsync(cancellationToken);

    public Task<IReadOnlyList<CollectionLogDto>> GetCollectionLogAsync(int limit, CancellationToken cancellationToken)
        => CurrentStore.GetCollectionLogAsync(limit, cancellationToken);

    public Task<IReadOnlyList<TopWaitDto>> GetTopWaitsAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken)
        => CurrentStore.GetTopWaitsAsync(serverId, hoursBack, limit, cancellationToken);

    public Task<IReadOnlyList<CpuSampleDto>> GetCpuSamplesAsync(string serverId, int hoursBack, CancellationToken cancellationToken)
        => CurrentStore.GetCpuSamplesAsync(serverId, hoursBack, cancellationToken);

    public Task ApplyRetentionAsync(CancellationToken cancellationToken)
        => CurrentStore.ApplyRetentionAsync(cancellationToken);

    private IHeadlessStore CurrentStore
        => string.Equals(_options.CurrentValue.StorageProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? _sqlServerStore
            : _duckDbStore;
}
