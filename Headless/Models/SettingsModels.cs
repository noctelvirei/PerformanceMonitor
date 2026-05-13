namespace PerformanceMonitor.Headless.Models;

public sealed class HeadlessSettingsDto
{
    public string Urls { get; set; } = "http://localhost:5155";
    public string StorageProvider { get; set; } = "DuckDb";
    public string StoragePath { get; set; } = "data\\headless\\performance-monitor.duckdb";
    public string ArchiveDirectory { get; set; } = "data\\headless\\parquet";
    public RepositorySettingsDto Repository { get; set; } = new();
    public string? IngestApiKey { get; set; }
    public int CollectionIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentServers { get; set; } = 8;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ArchiveIntervalMinutes { get; set; } = 60;
    public int HotDataDays { get; set; } = 7;
    public List<CollectorScheduleOptions> Collectors { get; set; } = [];
    public List<ServerSettingsDto> Servers { get; set; } = [];
}

public sealed class RepositorySettingsDto
{
    public string ConnectionMode { get; set; } = "Windows";
    public string DataSource { get; set; } = "";
    public string InitialCatalog { get; set; } = "PerformanceMonitorRepository";
    public string? UserId { get; set; }
    public string? Password { get; set; }
    public bool HasPassword { get; set; }
    public string Encrypt { get; set; } = "Optional";
    public bool TrustServerCertificate { get; set; } = true;
    public string? ConnectionString { get; set; }
    public string? ConnectionStringEnvironmentVariable { get; set; }
}

public sealed class ServerSettingsDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Purpose { get; set; } = "Development";
    public string ConnectionMode { get; set; } = "Windows";
    public string DataSource { get; set; } = "";
    public string InitialCatalog { get; set; } = "master";
    public string? UserId { get; set; }
    public string? Password { get; set; }
    public bool HasPassword { get; set; }
    public string Encrypt { get; set; } = "Optional";
    public bool TrustServerCertificate { get; set; } = true;
    public string? ConnectionString { get; set; }
    public string? ConnectionStringEnvironmentVariable { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class TestConnectionRequest
{
    public ServerSettingsDto Server { get; set; } = new();
}

public sealed class TestRepositoryRequest
{
    public RepositorySettingsDto Repository { get; set; } = new();
}

public sealed record TestConnectionResult(bool Success, string Message);
