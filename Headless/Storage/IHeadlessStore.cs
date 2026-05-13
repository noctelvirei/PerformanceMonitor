using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Storage;

public interface IHeadlessRepository
{
    StorageInfoDto GetStorageInfo();
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertConfiguredServersAsync(IEnumerable<CollectionServerIdentity> servers, CancellationToken cancellationToken);
    Task RecordSnapshotAsync(CollectionSnapshot snapshot, CancellationToken cancellationToken);
    Task<DateTime?> GetLastCpuSampleTimeAsync(string serverId, CancellationToken cancellationToken);
    Task ApplyRetentionAsync(CancellationToken cancellationToken);
}

public interface IEstateTelemetryReader
{
    Task<IReadOnlyList<ServerHealthDto>> GetServersAsync(CancellationToken cancellationToken);
    Task<EstateSummaryDto> GetEstateSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ActiveAlertDto>> GetEstateActiveAlertsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectionLogDto>> GetCollectionLogAsync(int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<TopWaitDto>> GetTopWaitsAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<CpuSampleDto>> GetCpuSamplesAsync(string serverId, int hoursBack, CancellationToken cancellationToken);
}

public interface IHeadlessStore : IHeadlessRepository, IEstateTelemetryReader;
