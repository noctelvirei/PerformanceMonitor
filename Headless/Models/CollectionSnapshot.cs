namespace PerformanceMonitor.Headless.Models;

public sealed record CollectionSnapshot
{
    public CollectionServerIdentity Server { get; init; } = new();
    public DateTime CollectionTime { get; init; } = DateTime.UtcNow;
    public string ServerStatus { get; init; } = "ONLINE";
    public string? ServerError { get; init; }
    public ServerPropertiesSnapshot? ServerProperties { get; init; }
    public IReadOnlyList<WaitStatSnapshot> WaitStats { get; init; } = [];
    public IReadOnlyList<CpuSample> CpuSamples { get; init; } = [];
    public IReadOnlyList<CollectionLogEntry> Logs { get; init; } = [];
}

public sealed record CollectionServerIdentity
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Purpose { get; init; } = "Unassigned";
    public bool Enabled { get; init; } = true;

    public string ServerNameForStorage => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
    public string PurposeForDisplay => string.IsNullOrWhiteSpace(Purpose) ? "Unassigned" : Purpose.Trim();

    public static CollectionServerIdentity FromOptions(MonitoredServerOptions server)
        => new()
        {
            Id = server.Id,
            DisplayName = server.DisplayName,
            Purpose = server.Purpose,
            Enabled = server.Enabled
        };
}

public sealed record CollectionLogEntry(
    string CollectorName,
    DateTime CollectionTime,
    int DurationMs,
    string Status,
    string? ErrorMessage,
    int RowsCollected,
    long SqlDurationMs,
    long StorageDurationMs);
