using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;

namespace PerformanceMonitor.Headless.Services;

public sealed class SqlInstanceDiscoveryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly ILogger<SqlInstanceDiscoveryService> _logger;

    public SqlInstanceDiscoveryService(
        IOptionsMonitor<MonitorOptions> options,
        ILogger<SqlInstanceDiscoveryService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<SqlInstanceDiscoveryResponse> DiscoverAsync(
        SqlInstanceDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 10, 900);

        try
        {
            var discoveryTypes = SplitList(request.DiscoveryTypes);
            if (discoveryTypes.Length == 0)
            {
                discoveryTypes = ["DomainSPN", "DataSourceEnumeration"];
            }

            var scanTypes = SplitList(request.ScanTypes);
            if (scanTypes.Length == 0)
            {
                scanTypes = ["Browser"];
            }

            var jsonRequest = JsonSerializer.Serialize(new DbatoolsDiscoveryInput(
                SplitList(request.Targets),
                discoveryTypes,
                scanTypes,
                SplitList(request.IpAddresses),
                ParseTcpPorts(request.TcpPorts),
                request.DomainController?.Trim() ?? "",
                string.IsNullOrWhiteSpace(request.MinimumConfidence) ? "Medium" : request.MinimumConfidence.Trim(),
                string.IsNullOrWhiteSpace(request.Purpose) ? "Development" : request.Purpose.Trim()),
                s_jsonOptions);
            var output = await RunDbatoolsDiscoveryAsync(jsonRequest, timeoutSeconds, cancellationToken);
            var rows = ParseDbatoolsOutput(output, request.Purpose);
            return new SqlInstanceDiscoveryResponse(
                true,
                rows.Count == 0 ? "No SQL Server instances found." : $"Found {rows.Count} SQL Server instance(s).",
                rows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SqlInstanceDiscoveryResponse(false, "Discovery cancelled.", []);
        }
        catch (TimeoutException ex)
        {
            return new SqlInstanceDiscoveryResponse(false, ex.Message, []);
        }
        catch (ArgumentException ex)
        {
            return new SqlInstanceDiscoveryResponse(false, ex.Message, []);
        }
        catch (InvalidOperationException ex)
        {
            return new SqlInstanceDiscoveryResponse(false, ex.Message, []);
        }
    }

    private async Task<string> RunDbatoolsDiscoveryAsync(
        string jsonRequest,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var tempDirectory = GetDiscoveryTempDirectory();
        Directory.CreateDirectory(tempDirectory);

        var shell = ResolvePowerShellExecutable(tempDirectory);
        var script = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'

if (-not (Get-Module -ListAvailable -Name dbatools)) {
    throw 'dbatools is not installed for the service account. Install it with: Install-Module dbatools -Scope CurrentUser'
}

Import-Module dbatools -ErrorAction Stop
$request = $env:PM_SQL_DISCOVERY_REQUEST | ConvertFrom-Json
$params = @{
    MinimumConfidence = $request.minimumConfidence
    WarningAction = 'SilentlyContinue'
}

if ($request.targets -and $request.targets.Count -gt 0) {
    $params.ComputerName = @($request.targets)
}
elseif ($request.discoveryTypes -and $request.discoveryTypes.Count -gt 0) {
    $params.DiscoveryType = @($request.discoveryTypes)
}
else {
    $params.DiscoveryType = @('DomainSPN', 'DataSourceEnumeration')
}

if ($request.scanTypes -and $request.scanTypes.Count -gt 0) {
    $params.ScanType = @($request.scanTypes)
}

if ($request.ipAddresses -and $request.ipAddresses.Count -gt 0) {
    $params.IpAddress = @($request.ipAddresses)
}

if ($request.tcpPorts -and $request.tcpPorts.Count -gt 0) {
    $params.TCPPort = @($request.tcpPorts)
}

if (-not [string]::IsNullOrWhiteSpace($request.domainController)) {
    $params.DomainController = $request.domainController
}

Find-DbaInstance @params |
    Select-Object MachineName, ComputerName, InstanceName, SqlInstance, Port, Confidence, Availability, Ping, TcpConnected, SqlConnected |
    ConvertTo-Json -Depth 4 -Compress
""";

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shell.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                ? $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}"
                : $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["PM_SQL_DISCOVERY_REQUEST"] = jsonRequest;
        startInfo.Environment["TEMP"] = tempDirectory;
        startInfo.Environment["TMP"] = tempDirectory;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell for SQL Server discovery.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        var finished = await Task.WhenAny(waitTask, delayTask);
        if (!ReferenceEquals(finished, waitTask) && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Discovery timed out after {timeoutSeconds} seconds.");
        }

        await waitTask;

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("dbatools discovery failed: {Error}", error);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "dbatools discovery failed." : error.Trim());
        }

        return output;
    }

    private List<SqlInstanceDiscoveryResult> ParseDbatoolsOutput(string output, string purpose)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var configured = _options.CurrentValue.Servers
            .SelectMany(static server => new[] { server.DataSource, server.DisplayName, server.Id })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discoveredPurpose = string.IsNullOrWhiteSpace(purpose) ? "Development" : purpose.Trim();
        var rows = JsonSerializer.Deserialize<JsonElement>(output, s_jsonOptions);
        var elements = rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().ToList()
            : [rows];

        return elements
            .Select(element => ToDiscoveryResult(element, discoveredPurpose, configured))
            .Where(static result => !string.IsNullOrWhiteSpace(result.DataSource))
            .GroupBy(static result => result.DataSource, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static result => result.DataSource, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SqlInstanceDiscoveryResult ToDiscoveryResult(
        JsonElement element,
        string purpose,
        HashSet<string> configured)
    {
        var dataSource = GetString(element, "SqlInstance");
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            var machineName = GetString(element, "MachineName") ?? GetString(element, "ComputerName") ?? "";
            var instanceName = GetString(element, "InstanceName");
            var port = GetNullableInt32(element, "Port");
            dataSource = !string.IsNullOrWhiteSpace(instanceName) && !string.Equals(instanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                ? $"{machineName}\\{instanceName}"
                : port.HasValue ? $"{machineName},{port.Value}" : machineName;
        }

        var serverId = BuildServerId(dataSource);
        return new SqlInstanceDiscoveryResult(
            dataSource,
            dataSource,
            serverId,
            purpose,
            GetString(element, "MachineName") ?? GetString(element, "ComputerName"),
            GetString(element, "InstanceName"),
            GetNullableInt32(element, "Port"),
            GetString(element, "Confidence"),
            GetString(element, "Availability"),
            GetNullableBoolean(element, "Ping"),
            GetNullableBoolean(element, "TcpConnected"),
            GetNullableBoolean(element, "SqlConnected"),
            configured.Contains(dataSource) || configured.Contains(serverId));
    }

    private static string ResolvePowerShellExecutable(string tempDirectory)
    {
        if (CanStart("pwsh", tempDirectory))
        {
            return "pwsh";
        }

        if (CanStart("powershell", tempDirectory))
        {
            return "powershell";
        }

        throw new InvalidOperationException("PowerShell was not found. Install PowerShell or Windows PowerShell on the monitoring server.");
    }

    private static bool CanStart(string executable, string tempDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment["TEMP"] = tempDirectory;
            startInfo.Environment["TMP"] = tempDirectory;

            using var process = Process.Start(startInfo);

            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string GetDiscoveryTempDirectory()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "tmp"));

    private static string[] SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int[] ParseTcpPorts(string? value)
    {
        var ports = new List<int>();
        foreach (var token in SplitList(value))
        {
            if (!int.TryParse(token, out var port) || port is < 1 or > 65535)
            {
                throw new ArgumentException("TCP ports must be numbers between 1 and 65535.");
            }

            ports.Add(port);
        }

        return ports.Distinct().ToArray();
    }

    private static string BuildServerId(string dataSource)
    {
        var builder = new StringBuilder(dataSource.Length);
        foreach (var character in dataSource.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var id = builder.ToString().Trim('-');
        while (id.Contains("--", StringComparison.Ordinal))
        {
            id = id.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(id) ? "sql-server" : id;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : null;

    private static int? GetNullableInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? GetNullableBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private sealed record DbatoolsDiscoveryInput(
        string[] Targets,
        string[] DiscoveryTypes,
        string[] ScanTypes,
        string[] IpAddresses,
        int[] TcpPorts,
        string DomainController,
        string MinimumConfidence,
        string Purpose);
}
