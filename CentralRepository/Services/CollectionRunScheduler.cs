using System.Collections.Concurrent;
using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class CollectionRunScheduler
{
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string ServerId, string CollectorName), DateTimeOffset> _lastRuns = new();

    public CollectionRunScheduler()
        : this(TimeProvider.System)
    {
    }

    public CollectionRunScheduler(TimeProvider clock)
    {
        _clock = clock;
    }

    public IReadOnlyList<CollectorScheduleOptions> GetDueCollectors(
        string serverId,
        IEnumerable<CollectorScheduleOptions> collectors)
    {
        var now = _clock.GetUtcNow();
        return collectors
            .Where(collector => collector.Enabled)
            .Where(collector => IsDue(serverId, collector, now))
            .ToList();
    }

    public void MarkRun(string serverId, string collectorName)
        => _lastRuns[(serverId, collectorName)] = _clock.GetUtcNow();

    private bool IsDue(
        string serverId,
        CollectorScheduleOptions collector,
        DateTimeOffset now)
    {
        if (collector.FrequencySeconds <= 0)
        {
            return !_lastRuns.ContainsKey((serverId, collector.Name));
        }

        return !_lastRuns.TryGetValue((serverId, collector.Name), out var lastRun)
               || now - lastRun >= TimeSpan.FromSeconds(collector.FrequencySeconds);
    }
}
