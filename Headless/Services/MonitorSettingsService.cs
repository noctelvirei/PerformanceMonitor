using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Security;

namespace PerformanceMonitor.Headless.Services;

public sealed class MonitorSettingsService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<MonitorOptions> _options;

    public MonitorSettingsService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptionsMonitor<MonitorOptions> options)
    {
        _configuration = configuration;
        _environment = environment;
        _options = options;
    }

    public HeadlessSettingsDto GetSettings()
    {
        var monitor = _options.CurrentValue;
        return new HeadlessSettingsDto
        {
            Urls = _configuration["Urls"] ?? "http://localhost:5155",
            StoragePath = monitor.StoragePath,
            ArchiveDirectory = monitor.ArchiveDirectory,
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
    }

    public async Task SaveSettingsAsync(HeadlessSettingsDto settings, CancellationToken cancellationToken)
    {
        Validate(settings);

        var existingServers = _options.CurrentValue.Servers
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        var root = new JsonObject
        {
            ["Urls"] = string.IsNullOrWhiteSpace(settings.Urls) ? "http://localhost:5155" : settings.Urls.Trim(),
            ["Monitor"] = new JsonObject
            {
                ["StoragePath"] = settings.StoragePath,
                ["ArchiveDirectory"] = settings.ArchiveDirectory,
                ["CollectionIntervalSeconds"] = settings.CollectionIntervalSeconds,
                ["MaxConcurrentServers"] = settings.MaxConcurrentServers,
                ["CommandTimeoutSeconds"] = settings.CommandTimeoutSeconds,
                ["ArchiveIntervalMinutes"] = settings.ArchiveIntervalMinutes,
                ["HotDataDays"] = settings.HotDataDays,
                ["Collectors"] = ToCollectorsJson(settings.Collectors),
                ["Servers"] = ToServersJson(settings.Servers, existingServers)
            }
        };

        var path = Path.Combine(_environment.ContentRootPath, "appsettings.json");
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);

        if (_configuration is IConfigurationRoot rootConfiguration)
        {
            rootConfiguration.Reload();
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(ServerSettingsDto server, CancellationToken cancellationToken)
    {
        var option = ToOption(server, FindExistingServer(server.Id));
        var connectionString = option.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new TestConnectionResult(false, "No connection details.");
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return new TestConnectionResult(true, "Connection OK.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new TestConnectionResult(false, ex.Message);
        }
    }

    private MonitoredServerOptions? FindExistingServer(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        return _options.CurrentValue.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
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

    private static JsonArray ToCollectorsJson(IEnumerable<CollectorScheduleOptions> collectors)
    {
        var array = new JsonArray();
        foreach (var collector in collectors.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            array.Add(new JsonObject
            {
                ["Name"] = collector.Name.Trim(),
                ["Enabled"] = collector.Enabled,
                ["FrequencySeconds"] = Math.Max(1, collector.FrequencySeconds)
            });
        }

        return array;
    }

    private static JsonArray ToServersJson(IEnumerable<ServerSettingsDto> servers, Dictionary<string, MonitoredServerOptions> existingServers)
    {
        var array = new JsonArray();
        foreach (var server in servers.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            existingServers.TryGetValue(server.Id, out var existing);
            var option = ToOption(server, existing);
            array.Add(new JsonObject
            {
                ["Id"] = option.Id,
                ["DisplayName"] = option.DisplayName,
                ["Purpose"] = option.Purpose,
                ["ConnectionMode"] = option.ConnectionMode,
                ["DataSource"] = option.DataSource,
                ["InitialCatalog"] = option.InitialCatalog,
                ["UserId"] = option.UserId,
                ["ProtectedPassword"] = option.ProtectedPassword,
                ["Encrypt"] = option.Encrypt,
                ["TrustServerCertificate"] = option.TrustServerCertificate,
                ["ConnectionString"] = option.ConnectionString,
                ["ConnectionStringEnvironmentVariable"] = option.ConnectionStringEnvironmentVariable,
                ["Enabled"] = option.Enabled
            });
        }

        return array;
    }

    private static MonitoredServerOptions ToOption(ServerSettingsDto server, MonitoredServerOptions? existing)
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

    private static void Validate(HeadlessSettingsDto settings)
    {
        settings.CollectionIntervalSeconds = Math.Clamp(settings.CollectionIntervalSeconds, 10, 86400);
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
}
