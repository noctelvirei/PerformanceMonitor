using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class MonitorSettingsConfigurationPersistence
{
    private readonly IHostEnvironment _environment;

    public MonitorSettingsConfigurationPersistence(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task SaveAsync(
        CentralRepositorySettingsDto settings,
        MonitorOptions currentOptions,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_environment.ContentRootPath, "appsettings.json");
        var document = await MonitorSettingsDocument.LoadAsync(path, cancellationToken);
        var documentState = MonitorSettingsMapper.ToDocumentState(settings, currentOptions);

        document.Apply(documentState);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            document.ToJson(),
            cancellationToken);
    }
}
