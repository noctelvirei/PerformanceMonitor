using PerformanceMonitor.CentralRepository.Models;
using PerformanceMonitor.CentralRepository.Storage;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class CollectionSnapshotIntakeService
{
    private readonly ICentralRepository _repository;

    public CollectionSnapshotIntakeService(ICentralRepository repository)
    {
        _repository = repository;
    }

    public async Task<IngestResultDto> AcceptAsync(CollectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        await _repository.RecordSnapshotAsync(snapshot, cancellationToken);

        return new IngestResultDto(
            true,
            snapshot.ServerProperties is null ? 0 : 1,
            snapshot.WaitStats.Count,
            snapshot.CpuSamples.Count,
            snapshot.WaitingTasks.Count,
            snapshot.CollectorSamples.Count,
            snapshot.Logs.Count);
    }

    public Task<IngestResultDto> AcceptRemoteAsync(IngestSnapshotDto request, CancellationToken cancellationToken)
    {
        var server = new CollectionServerIdentity
        {
            Id = request.Server.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(request.Server.DisplayName) ? request.Server.Id.Trim() : request.Server.DisplayName.Trim(),
            Purpose = string.IsNullOrWhiteSpace(request.Server.Purpose) ? "Unassigned" : request.Server.Purpose.Trim(),
            Enabled = request.Server.Enabled
        };

        var collectionTime = request.CollectionTime ?? DateTime.UtcNow;
        var snapshot = new CollectionSnapshot
        {
            Server = server,
            CollectionTime = collectionTime,
            ServerStatus = request.Status,
            ServerError = request.ErrorMessage,
            ServerProperties = request.ServerProperties,
            WaitStats = request.WaitStats,
            CpuSamples = request.CpuSamples,
            WaitingTasks = request.WaitingTasks,
            CollectorSamples = request.CollectorSamples,
            Logs = request.CollectionLog
                .Select(log => new CollectionLogEntry(
                    log.CollectorName,
                    log.CollectionTime,
                    log.DurationMs,
                    log.Status,
                    log.ErrorMessage,
                    log.RowsCollected,
                    0,
                    0))
                .ToList()
        };

        return AcceptAsync(snapshot, cancellationToken);
    }
}
