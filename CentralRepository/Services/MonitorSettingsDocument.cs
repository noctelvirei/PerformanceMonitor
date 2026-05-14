using System.Text.Json;
using System.Text.Json.Nodes;
using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Services;

internal sealed class MonitorSettingsDocument
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly JsonObject _root;

    private MonitorSettingsDocument(JsonObject root)
    {
        _root = root;
    }

    public static async Task<MonitorSettingsDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new MonitorSettingsDocument(new JsonObject());
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MonitorSettingsDocument(new JsonObject());
        }

        return new MonitorSettingsDocument(JsonNode.Parse(json) as JsonObject ?? new JsonObject());
    }

    public void Apply(MonitorSettingsDocumentState settings)
    {
        _root["Urls"] = settings.Urls;

        var monitor = GetOrCreateObject(_root, "Monitor");
        monitor["StorageProvider"] = settings.StorageProvider;
        monitor["StoragePath"] = settings.StoragePath;
        monitor["ArchiveDirectory"] = settings.ArchiveDirectory;
        monitor["Repository"] = ToRepositoryJson(settings.Repository);
        monitor["IngestApiKey"] = settings.IngestApiKey;
        monitor["McpAccess"] = ToMcpAccessJson(settings.McpAccess);
        monitor["CollectionIntervalSeconds"] = settings.CollectionIntervalSeconds;
        monitor["MaxConcurrentServers"] = settings.MaxConcurrentServers;
        monitor["CommandTimeoutSeconds"] = settings.CommandTimeoutSeconds;
        monitor["ArchiveIntervalMinutes"] = settings.ArchiveIntervalMinutes;
        monitor["HotDataDays"] = settings.HotDataDays;
        monitor["AlertRules"] = ToAlertRulesJson(settings.AlertRules);
        monitor["Collectors"] = ToCollectorsJson(settings.Collectors);
        monitor["Servers"] = ToServersJson(settings.Servers);
    }

    public string ToJson()
        => _root.ToJsonString(SerializerOptions);

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject child)
        {
            return child;
        }

        child = new JsonObject();
        parent[propertyName] = child;
        return child;
    }

    private static JsonObject ToRepositoryJson(RepositoryOptions repository)
        => new()
        {
            ["ConnectionMode"] = repository.ConnectionMode,
            ["DataSource"] = repository.DataSource,
            ["InitialCatalog"] = repository.InitialCatalog,
            ["UserId"] = repository.UserId,
            ["ProtectedPassword"] = repository.ProtectedPassword,
            ["Encrypt"] = repository.Encrypt,
            ["TrustServerCertificate"] = repository.TrustServerCertificate,
            ["ConnectionString"] = repository.ConnectionString,
            ["ConnectionStringEnvironmentVariable"] = repository.ConnectionStringEnvironmentVariable
        };

    private static JsonObject ToMcpAccessJson(McpAccessOptions mcpAccess)
        => new()
        {
            ["Enabled"] = mcpAccess.Enabled,
            ["AuthMode"] = mcpAccess.AuthMode,
            ["PublicBaseUrl"] = mcpAccess.PublicBaseUrl,
            ["ProtectedApiKey"] = mcpAccess.ProtectedApiKey,
            ["AllowLocalWithoutApiKey"] = mcpAccess.AllowLocalWithoutApiKey
        };

    private static JsonObject ToAlertRulesJson(AlertRuleOptions alertRules)
        => new()
        {
            ["Enabled"] = alertRules.Enabled,
            ["CpuEnabled"] = alertRules.CpuEnabled,
            ["CpuWarningThreshold"] = alertRules.CpuWarningThreshold,
            ["CpuCriticalThreshold"] = alertRules.CpuCriticalThreshold,
            ["LongRunningQueryEnabled"] = alertRules.LongRunningQueryEnabled,
            ["LongRunningQueryWarningMinutes"] = alertRules.LongRunningQueryWarningMinutes,
            ["LongRunningQueryCriticalMinutes"] = alertRules.LongRunningQueryCriticalMinutes,
            ["BlockingEnabled"] = alertRules.BlockingEnabled,
            ["DeadlockEnabled"] = alertRules.DeadlockEnabled,
            ["MemoryGrantEnabled"] = alertRules.MemoryGrantEnabled,
            ["MemoryGrantWarningSeconds"] = alertRules.MemoryGrantWarningSeconds,
            ["MemoryGrantCriticalSeconds"] = alertRules.MemoryGrantCriticalSeconds,
            ["FileLatencyEnabled"] = alertRules.FileLatencyEnabled,
            ["FileLatencyWarningMs"] = alertRules.FileLatencyWarningMs,
            ["FileLatencyCriticalMs"] = alertRules.FileLatencyCriticalMs,
            ["LongRunningJobEnabled"] = alertRules.LongRunningJobEnabled,
            ["LongRunningJobWarningMinutes"] = alertRules.LongRunningJobWarningMinutes,
            ["LongRunningJobCriticalMinutes"] = alertRules.LongRunningJobCriticalMinutes
        };

    private static JsonArray ToCollectorsJson(IEnumerable<CollectorScheduleOptions> collectors)
    {
        var array = new JsonArray();
        foreach (var collector in collectors)
        {
            array.Add(new JsonObject
            {
                ["Name"] = collector.Name,
                ["Enabled"] = collector.Enabled,
                ["FrequencySeconds"] = collector.FrequencySeconds
            });
        }

        return array;
    }

    private static JsonArray ToServersJson(IEnumerable<MonitoredServerOptions> servers)
    {
        var array = new JsonArray();
        foreach (var server in servers)
        {
            array.Add(new JsonObject
            {
                ["Id"] = server.Id,
                ["DisplayName"] = server.DisplayName,
                ["Purpose"] = server.Purpose,
                ["ConnectionMode"] = server.ConnectionMode,
                ["DataSource"] = server.DataSource,
                ["InitialCatalog"] = server.InitialCatalog,
                ["UserId"] = server.UserId,
                ["ProtectedPassword"] = server.ProtectedPassword,
                ["Encrypt"] = server.Encrypt,
                ["TrustServerCertificate"] = server.TrustServerCertificate,
                ["ConnectionString"] = server.ConnectionString,
                ["ConnectionStringEnvironmentVariable"] = server.ConnectionStringEnvironmentVariable,
                ["Enabled"] = server.Enabled
            });
        }

        return array;
    }
}
