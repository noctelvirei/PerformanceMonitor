namespace PerformanceMonitor.CentralRepository.Models;

public sealed class CentralRepositorySettingsDto
{
    public string Urls { get; set; } = "http://localhost:5155";
    public string StorageProvider { get; set; } = "DuckDb";
    public string StoragePath { get; set; } = "data\\central-repository\\performance-monitor.duckdb";
    public string ArchiveDirectory { get; set; } = "data\\central-repository\\parquet";
    public RepositorySettingsDto Repository { get; set; } = new();
    public string? IngestApiKey { get; set; }
    public McpAccessSettingsDto McpAccess { get; set; } = new();
    public int CollectionIntervalSeconds { get; set; } = 60;
    public int MaxConcurrentServers { get; set; } = 8;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int ArchiveIntervalMinutes { get; set; } = 60;
    public int HotDataDays { get; set; } = 7;
    public AlertRuleSettingsDto AlertRules { get; set; } = new();
    public List<CollectorScheduleOptions> Collectors { get; set; } = [];
    public List<ServerSettingsDto> Servers { get; set; } = [];
}

public sealed class McpAccessSettingsDto
{
    public bool Enabled { get; set; } = true;
    public string AuthMode { get; set; } = "None";
    public string PublicBaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool HasApiKey { get; set; }
    public bool AllowLocalWithoutApiKey { get; set; }
}

public sealed class AlertRuleSettingsDto
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

public sealed class SqlInstanceDiscoveryRequest
{
    public string Targets { get; set; } = "";
    public string DiscoverySource { get; set; } = "";
    public string DiscoveryTypes { get; set; } = "DomainSPN,DataSourceEnumeration";
    public string ScanTypes { get; set; } = "TCPPort";
    public string IpAddresses { get; set; } = "";
    public string TcpPorts { get; set; } = "1433";
    public string DomainController { get; set; } = "";
    public string RegisteredServerSqlInstance { get; set; } = "";
    public string RegisteredServerGroups { get; set; } = "";
    public string RegisteredServerPatterns { get; set; } = "";
    public bool RegisteredServerIncludeLocal { get; set; }
    public string MinimumConfidence { get; set; } = "Medium";
    public string Purpose { get; set; } = "Development";
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed record SqlInstanceDiscoveryResponse(
    bool Success,
    string Message,
    IReadOnlyList<SqlInstanceDiscoveryResult> Instances);

public sealed record SqlInstanceDiscoveryJobStatus(
    string JobId,
    string Status,
    string Message,
    DateTime StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<string> Events,
    IReadOnlyList<SqlInstanceDiscoveryResult> Instances);

public sealed record SqlInstanceDiscoveryResult(
    string DataSource,
    string DisplayName,
    string ServerId,
    string Purpose,
    string? MachineName,
    string? InstanceName,
    int? Port,
    string? Confidence,
    string? Availability,
    bool? Ping,
    bool? TcpConnected,
    bool? SqlConnected,
    bool IsAlreadyConfigured);
