using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Storage;
using PerformanceMonitor.SqlConnectivity;

namespace PerformanceMonitor.Headless.Services;

public sealed class MonitorSettingsService
{
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly IHeadlessRepository _repository;
    private readonly MonitorSettingsConfigurationPersistence _persistence;

    public MonitorSettingsService(
        IConfiguration configuration,
        IOptionsMonitor<MonitorOptions> options,
        IHeadlessRepository repository,
        MonitorSettingsConfigurationPersistence persistence)
    {
        _configuration = configuration;
        _options = options;
        _repository = repository;
        _persistence = persistence;
    }

    public HeadlessSettingsDto GetSettings()
    {
        var monitor = _options.CurrentValue;
        return MonitorSettingsMapper.ToDto(monitor, _configuration["Urls"] ?? "http://localhost:5155");
    }

    public async Task SaveSettingsAsync(HeadlessSettingsDto settings, CancellationToken cancellationToken)
    {
        MonitorSettingsMapper.Validate(settings);
        await _persistence.SaveAsync(settings, _options.CurrentValue, cancellationToken);

        if (_configuration is IConfigurationRoot rootConfiguration)
        {
            rootConfiguration.Reload();
        }

        await _repository.InitializeAsync(cancellationToken);
    }

    public async Task<TestConnectionResult> TestRepositoryAsync(RepositorySettingsDto repository, CancellationToken cancellationToken)
    {
        var option = MonitorSettingsMapper.ToRepositoryOption(repository, _options.CurrentValue.Repository);
        var connectionString = option.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new TestConnectionResult(false, "No repository connection details.");
        }

        try
        {
            var info = await SqlConnectionTester.TestConnectionAsync(connectionString, cancellationToken);
            return info.IsConnected
                ? new TestConnectionResult(true, $"Repository connection OK: {info.ServerName}")
                : new TestConnectionResult(false, info.ErrorMessage ?? "Repository connection failed.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new TestConnectionResult(false, ex.Message);
        }
    }

    public async Task<TestConnectionResult> TestConnectionAsync(ServerSettingsDto server, CancellationToken cancellationToken)
    {
        var option = MonitorSettingsMapper.ToServerOption(server, FindExistingServer(server.Id));
        var connectionString = option.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new TestConnectionResult(false, "No connection details.");
        }

        try
        {
            var info = await SqlConnectionTester.TestConnectionAsync(connectionString, cancellationToken);
            return info.IsConnected
                ? new TestConnectionResult(true, $"Connection OK: {info.ServerName}")
                : new TestConnectionResult(false, info.ErrorMessage ?? "Connection failed.");
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
}
