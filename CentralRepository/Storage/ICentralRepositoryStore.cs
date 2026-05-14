using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Storage;

public interface ICentralRepository
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
    Task<IReadOnlyList<WaitingTaskDto>> GetWaitingTasksAsync(string serverId, int hoursBack, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectorSampleDto>> GetCollectorSamplesAsync(string serverId, string collectorName, int hoursBack, int limit, CancellationToken cancellationToken);
    Task<ServerExperienceDto> GetServerExperienceAsync(string serverId, int hoursBack, CancellationToken cancellationToken);
}

public interface ICentralRepositoryStore : ICentralRepository, IEstateTelemetryReader;
