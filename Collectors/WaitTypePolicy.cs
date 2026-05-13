using System.Reflection;
using System.Text.Json;

namespace PerformanceMonitor.Collectors;

public static class WaitTypePolicy
{
    private const string ResourceName = "PerformanceMonitor.Collectors.ignored_wait_types.json";

    public static HashSet<string> LoadDefaultIgnoredWaitTypes()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? [] : LoadFromStream(stream);
    }

    public static HashSet<string> LoadFromFileOrDefault(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                return LoadFromStream(stream);
            }
            catch (IOException)
            {
                return LoadDefaultIgnoredWaitTypes();
            }
            catch (JsonException)
            {
                return LoadDefaultIgnoredWaitTypes();
            }
        }

        return LoadDefaultIgnoredWaitTypes();
    }

    private static HashSet<string> LoadFromStream(Stream stream)
    {
        var waits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("ignored_waits", out var waitsArray))
        {
            return waits;
        }

        foreach (var wait in waitsArray.EnumerateArray())
        {
            var waitType = wait.GetString();
            if (!string.IsNullOrWhiteSpace(waitType))
            {
                waits.Add(waitType);
            }
        }

        return waits;
    }
}
