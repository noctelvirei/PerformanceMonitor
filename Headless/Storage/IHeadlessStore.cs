using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Storage;

public interface IHeadlessStore
{
    StorageInfoDto GetStorageInfo();
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertConfiguredServersAsync(IEnumerable<MonitoredServerOptions> servers, CancellationToken cancellationToken);
    Task SetServerStatusAsync(MonitoredServerOptions server, string status, string? errorMessage, ServerPropertiesSnapshot? properties, CancellationToken cancellationToken);
    Task InsertServerPropertiesAsync(MonitoredServerOptions server, DateTime collectionTime, ServerPropertiesSnapshot properties, CancellationToken cancellationToken);
    Task InsertWaitStatsAsync(MonitoredServerOptions server, DateTime collectionTime, IReadOnlyList<WaitStatSnapshot> rows, CancellationToken cancellationToken);
    Task<DateTime?> GetLastCpuSampleTimeAsync(string serverId, CancellationToken cancellationToken);
    Task InsertCpuSamplesAsync(MonitoredServerOptions server, DateTime collectionTime, IReadOnlyList<CpuSample> rows, CancellationToken cancellationToken);
    Task InsertCollectionLogAsync(MonitoredServerOptions server, string collectorName, DateTime collectionTime, int durationMs, string status, string? errorMessage, int rowsCollected, long sqlDurationMs, long storageDurationMs, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken);
    Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ActiveAlertDto>> GetActiveAlertsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectionLogDto>> GetCollectionLogAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<TopWaitDto>> GetTopWaitsAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<CpuSampleDto>> GetCpuSamplesAsync(string serverId, int hoursBack, CancellationToken cancellationToken);
    Task ArchiveOldDataAsync(CancellationToken cancellationToken);
}
