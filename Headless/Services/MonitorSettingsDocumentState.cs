using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Services;

internal sealed record MonitorSettingsDocumentState(
    string Urls,
    string StorageProvider,
    string StoragePath,
    string ArchiveDirectory,
    RepositoryOptions Repository,
    string? IngestApiKey,
    int CollectionIntervalSeconds,
    int MaxConcurrentServers,
    int CommandTimeoutSeconds,
    int ArchiveIntervalMinutes,
    int HotDataDays,
    IReadOnlyList<CollectorScheduleOptions> Collectors,
    IReadOnlyList<MonitoredServerOptions> Servers);
