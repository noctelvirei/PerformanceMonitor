namespace PerformanceMonitor.Headless.Models;

public sealed class MonitorOptions
{
    public string StorageProvider { get; set; } = "DuckDb";
    public string StoragePath { get; set; } = "data\\headless\\performance-monitor.duckdb";
    public string ArchiveDirectory { get; set; } = "data\\headless\\parquet";
    public RepositoryOptions Repository { get; set; } = new();
    public string? IngestApiKey { get; set; }
    public McpAccessOptions McpAccess { get; set; } = new();
    public int CollectionIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentServers { get; set; } = 8;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ArchiveIntervalMinutes { get; set; } = 60;
    public int HotDataDays { get; set; } = 7;
    public AlertRuleOptions AlertRules { get; set; } = new();
    public List<CollectorScheduleOptions> Collectors { get; set; } = [];
    public List<MonitoredServerOptions> Servers { get; set; } = [];

    public IReadOnlyList<CollectorScheduleOptions> GetEffectiveCollectors()
    {
        if (Collectors.Count == 0)
        {
            return CollectorCatalog.DefaultSchedules;
        }

        var configured = Collectors
            .Where(static collector => !string.IsNullOrWhiteSpace(collector.Name))
            .ToDictionary(static collector => collector.Name, StringComparer.OrdinalIgnoreCase);
        var effective = CollectorCatalog.DefaultSchedules
            .Select(defaultCollector => configured.TryGetValue(defaultCollector.Name, out var overrideCollector)
                ? overrideCollector
                : defaultCollector)
            .ToList();

        effective.AddRange(Collectors.Where(collector =>
            !string.IsNullOrWhiteSpace(collector.Name)
            && !CollectorCatalog.DefaultSchedules.Any(defaultCollector =>
                string.Equals(defaultCollector.Name, collector.Name, StringComparison.OrdinalIgnoreCase))));

        return effective;
    }
}

public sealed class McpAccessOptions
{
    public bool Enabled { get; set; } = true;
    public string AuthMode { get; set; } = "None";
    public string PublicBaseUrl { get; set; } = "";
    public string? ProtectedApiKey { get; set; }
    public bool AllowLocalWithoutApiKey { get; set; }
}

public sealed class AlertRuleOptions
{
    public bool Enabled { get; set; } = true;
    public bool CpuEnabled { get; set; } = true;
    public int CpuWarningThreshold { get; set; } = 80;
    public int CpuCriticalThreshold { get; set; } = 90;
    public bool LongRunningQueryEnabled { get; set; } = true;
    public int LongRunningQueryWarningMinutes { get; set; } = 15;
    public int LongRunningQueryCriticalMinutes { get; set; } = 30;
    public bool BlockingEnabled { get; set; } = true;
    public bool DeadlockEnabled { get; set; } = true;
    public bool MemoryGrantEnabled { get; set; } = true;
    public int MemoryGrantWarningSeconds { get; set; } = 5;
    public int MemoryGrantCriticalSeconds { get; set; } = 30;
    public bool FileLatencyEnabled { get; set; } = true;
    public int FileLatencyWarningMs { get; set; } = 50;
    public int FileLatencyCriticalMs { get; set; } = 200;
    public bool LongRunningJobEnabled { get; set; } = true;
    public int LongRunningJobWarningMinutes { get; set; } = 60;
    public int LongRunningJobCriticalMinutes { get; set; } = 240;
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
