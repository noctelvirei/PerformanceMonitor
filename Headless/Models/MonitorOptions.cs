namespace PerformanceMonitor.Headless.Models;

using Microsoft.Data.SqlClient;
using PerformanceMonitor.Headless.Security;

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

        return
        [
            new() { Name = "server_properties", FrequencySeconds = 3600 },
            new() { Name = "wait_stats", FrequencySeconds = 60 },
            new() { Name = "cpu_utilization", FrequencySeconds = 60 }
        ];
    }
}

public sealed class RepositoryOptions
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
    {
        if (!string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return Environment.ExpandEnvironmentVariables(fromEnvironment);
            }
        }

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return Environment.ExpandEnvironmentVariables(ConnectionString);
        }

        if (string.IsNullOrWhiteSpace(DataSource))
        {
            return "";
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = DataSource.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(InitialCatalog) ? "PerformanceMonitorRepository" : InitialCatalog.Trim(),
            TrustServerCertificate = TrustServerCertificate
        };

        builder["Encrypt"] = string.IsNullOrWhiteSpace(Encrypt) ? "Optional" : Encrypt.Trim();

        if (string.Equals(ConnectionMode, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = UserId ?? "";
            builder.Password = LocalSecretProtector.Unprotect(ProtectedPassword);
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }
}
