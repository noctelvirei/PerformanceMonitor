namespace PerformanceMonitor.Headless.Models;

public sealed class MonitorOptions
{
    public string StorageProvider { get; set; } = "DuckDb";
    public string StoragePath { get; set; } = "data\\headless\\performance-monitor.duckdb";
    public string ArchiveDirectory { get; set; } = "data\\headless\\parquet";
    public RepositoryOptions Repository { get; set; } = new();
    public string? IngestApiKey { get; set; }
    public int CollectionIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentServers { get; set; } = 8;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ArchiveIntervalMinutes { get; set; } = 60;
    public int HotDataDays { get; set; } = 7;
    public List<CollectorScheduleOptions> Collectors { get; set; } = [];
    public List<MonitoredServerOptions> Servers { get; set; } = [];

    public IReadOnlyList<CollectorScheduleOptions> GetEffectiveCollectors()
    {
        if (Collectors.Count > 0)
        {
            return Collectors;
        }

        return CollectorCatalog.DefaultSchedules;
    }
}

public sealed class RepositoryOptions : ISqlConnectionProfile
{
    public string ConnectionMode { get; set; } = "Windows";
    public string DataSource { get; set; } = "";
    public string InitialCatalog { get; set; } = "PerformanceMonitorRepository";
    public string? UserId { get; set; }
    public string? ProtectedPassword { get; set; }
    public string Encrypt { get; set; } = "Optional";
    public bool TrustServerCertificate { get; set; } = true;
    public string? ConnectionString { get; set; }
    public string? ConnectionStringEnvironmentVariable { get; set; }

    public string ResolveConnectionString()
        => this.ResolveConnectionString("PerformanceMonitorRepository");
}
