using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Security;

namespace PerformanceMonitor.Headless.Services;

internal static class MonitorSettingsMapper
{
    public static HeadlessSettingsDto ToDto(MonitorOptions monitor, string urls)
        => new()
        {
            Urls = string.IsNullOrWhiteSpace(urls) ? "http://localhost:5155" : urls,
            StorageProvider = string.IsNullOrWhiteSpace(monitor.StorageProvider) ? "DuckDb" : monitor.StorageProvider,
            StoragePath = monitor.StoragePath,
            ArchiveDirectory = monitor.ArchiveDirectory,
            Repository = ToRepositoryDto(monitor.Repository),
            IngestApiKey = monitor.IngestApiKey,
            CollectionIntervalSeconds = monitor.CollectionIntervalSeconds,
            MaxConcurrentServers = monitor.MaxConcurrentServers,
            CommandTimeoutSeconds = monitor.CommandTimeoutSeconds,
            ArchiveIntervalMinutes = monitor.ArchiveIntervalMinutes,
            HotDataDays = monitor.HotDataDays,
            Collectors = monitor.GetEffectiveCollectors()
                .Select(c => new CollectorScheduleOptions
                {
                    Name = c.Name,
                    Enabled = c.Enabled,
                    FrequencySeconds = c.FrequencySeconds
                })
                .ToList(),
            Servers = monitor.Servers.Select(ToDto).ToList()
        };

    public static MonitorSettingsDocumentState ToDocumentState(HeadlessSettingsDto settings, MonitorOptions currentOptions)
    {
        var existingServers = currentOptions.Servers
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        return new MonitorSettingsDocumentState(
            string.IsNullOrWhiteSpace(settings.Urls) ? "http://localhost:5155" : settings.Urls.Trim(),
            string.IsNullOrWhiteSpace(settings.StorageProvider) ? "DuckDb" : settings.StorageProvider.Trim(),
            settings.StoragePath,
            settings.ArchiveDirectory,
            ToRepositoryOption(settings.Repository, currentOptions.Repository),
            settings.IngestApiKey,
            settings.CollectionIntervalSeconds,
            settings.MaxConcurrentServers,
            settings.CommandTimeoutSeconds,
            settings.ArchiveIntervalMinutes,
            settings.HotDataDays,
            settings.Collectors
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(ToCollectorOption)
                .ToList(),
            settings.Servers
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .Select(server =>
                {
                    existingServers.TryGetValue(server.Id, out var existing);
                    return ToServerOption(server, existing);
                })
                .ToList());
    }

    public static MonitoredServerOptions ToServerOption(ServerSettingsDto server, MonitoredServerOptions? existing)
    {
        var mode = string.IsNullOrWhiteSpace(server.ConnectionMode) ? "Windows" : server.ConnectionMode.Trim();
        var protectedPassword = string.IsNullOrWhiteSpace(server.Password)
            ? existing?.ProtectedPassword
            : LocalSecretProtector.Protect(server.Password);

        return new MonitoredServerOptions
        {
            Id = server.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(server.DisplayName) ? server.Id.Trim() : server.DisplayName.Trim(),
            Purpose = string.IsNullOrWhiteSpace(server.Purpose) ? "Unassigned" : server.Purpose.Trim(),
            ConnectionMode = mode,
            DataSource = server.DataSource.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(server.InitialCatalog) ? "master" : server.InitialCatalog.Trim(),
            UserId = string.IsNullOrWhiteSpace(server.UserId) ? null : server.UserId.Trim(),
            ProtectedPassword = protectedPassword,
            Encrypt = string.IsNullOrWhiteSpace(server.Encrypt) ? "Optional" : server.Encrypt.Trim(),
            TrustServerCertificate = server.TrustServerCertificate,
            ConnectionString = mode.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase) ? server.ConnectionString : null,
            ConnectionStringEnvironmentVariable = mode.Equals("EnvironmentVariable", StringComparison.OrdinalIgnoreCase) ? server.ConnectionStringEnvironmentVariable : null,
            Enabled = server.Enabled
        };
    }

    public static RepositoryOptions ToRepositoryOption(RepositorySettingsDto repository, RepositoryOptions? existing)
    {
        var mode = string.IsNullOrWhiteSpace(repository.ConnectionMode) ? "Windows" : repository.ConnectionMode.Trim();
        var protectedPassword = string.IsNullOrWhiteSpace(repository.Password)
            ? existing?.ProtectedPassword
            : LocalSecretProtector.Protect(repository.Password);

        return new RepositoryOptions
        {
            ConnectionMode = mode,
            DataSource = repository.DataSource.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(repository.InitialCatalog) ? "PerformanceMonitorRepository" : repository.InitialCatalog.Trim(),
            UserId = string.IsNullOrWhiteSpace(repository.UserId) ? null : repository.UserId.Trim(),
            ProtectedPassword = protectedPassword,
            Encrypt = string.IsNullOrWhiteSpace(repository.Encrypt) ? "Optional" : repository.Encrypt.Trim(),
            TrustServerCertificate = repository.TrustServerCertificate,
            ConnectionString = mode.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase) ? repository.ConnectionString : null,
            ConnectionStringEnvironmentVariable = mode.Equals("EnvironmentVariable", StringComparison.OrdinalIgnoreCase) ? repository.ConnectionStringEnvironmentVariable : null
        };
    }

    public static void Validate(HeadlessSettingsDto settings)
    {
        settings.CollectionIntervalSeconds = Math.Clamp(settings.CollectionIntervalSeconds, 10, 86400);
        settings.StorageProvider = string.Equals(settings.StorageProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? "SqlServer"
            : "DuckDb";
        settings.MaxConcurrentServers = Math.Clamp(settings.MaxConcurrentServers, 1, 128);
        settings.CommandTimeoutSeconds = Math.Clamp(settings.CommandTimeoutSeconds, 1, 600);
        settings.ArchiveIntervalMinutes = Math.Clamp(settings.ArchiveIntervalMinutes, 0, 10080);
        settings.HotDataDays = Math.Clamp(settings.HotDataDays, 0, 3650);

        foreach (var server in settings.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Id))
            {
                continue;
            }

            server.Id = server.Id.Trim();
            server.DisplayName = string.IsNullOrWhiteSpace(server.DisplayName) ? server.Id : server.DisplayName.Trim();
        }
    }

    private static ServerSettingsDto ToDto(MonitoredServerOptions server)
        => new()
        {
            Id = server.Id,
            DisplayName = server.DisplayName,
            Purpose = server.Purpose,
            ConnectionMode = string.IsNullOrWhiteSpace(server.ConnectionMode) ? InferConnectionMode(server) : server.ConnectionMode,
            DataSource = server.DataSource,
            InitialCatalog = string.IsNullOrWhiteSpace(server.InitialCatalog) ? "master" : server.InitialCatalog,
            UserId = server.UserId,
            HasPassword = !string.IsNullOrWhiteSpace(server.ProtectedPassword),
            Encrypt = string.IsNullOrWhiteSpace(server.Encrypt) ? "Optional" : server.Encrypt,
            TrustServerCertificate = server.TrustServerCertificate,
            ConnectionString = server.ConnectionString,
            ConnectionStringEnvironmentVariable = server.ConnectionStringEnvironmentVariable,
            Enabled = server.Enabled
        };

    private static RepositorySettingsDto ToRepositoryDto(RepositoryOptions repository)
        => new()
        {
            ConnectionMode = string.IsNullOrWhiteSpace(repository.ConnectionMode) ? InferRepositoryConnectionMode(repository) : repository.ConnectionMode,
            DataSource = repository.DataSource,
            InitialCatalog = string.IsNullOrWhiteSpace(repository.InitialCatalog) ? "PerformanceMonitorRepository" : repository.InitialCatalog,
            UserId = repository.UserId,
            HasPassword = !string.IsNullOrWhiteSpace(repository.ProtectedPassword),
            Encrypt = string.IsNullOrWhiteSpace(repository.Encrypt) ? "Optional" : repository.Encrypt,
            TrustServerCertificate = repository.TrustServerCertificate,
            ConnectionString = repository.ConnectionString,
            ConnectionStringEnvironmentVariable = repository.ConnectionStringEnvironmentVariable
        };

    private static string InferConnectionMode(MonitoredServerOptions server)
    {
        if (!string.IsNullOrWhiteSpace(server.ConnectionStringEnvironmentVariable))
        {
            return "EnvironmentVariable";
        }

        if (!string.IsNullOrWhiteSpace(server.ConnectionString))
        {
            return "ConnectionString";
        }

        return string.IsNullOrWhiteSpace(server.UserId) ? "Windows" : "Sql";
    }

    private static string InferRepositoryConnectionMode(RepositoryOptions repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.ConnectionStringEnvironmentVariable))
        {
            return "EnvironmentVariable";
        }

        if (!string.IsNullOrWhiteSpace(repository.ConnectionString))
        {
            return "ConnectionString";
        }

        return string.IsNullOrWhiteSpace(repository.UserId) ? "Windows" : "Sql";
    }

    private static CollectorScheduleOptions ToCollectorOption(CollectorScheduleOptions collector)
        => new()
        {
            Name = collector.Name.Trim(),
            Enabled = collector.Enabled,
            FrequencySeconds = Math.Max(0, collector.FrequencySeconds)
        };
}
