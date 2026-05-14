using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerformanceMonitor.CentralRepository.Models;

namespace PerformanceMonitor.CentralRepository.Services;

public sealed class SqlInstanceDiscoveryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOptionsMonitor<MonitorOptions> _options;
    private readonly ILogger<SqlInstanceDiscoveryService> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ConcurrentDictionary<string, DiscoveryJobState> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public SqlInstanceDiscoveryService(
        IOptionsMonitor<MonitorOptions> options,
        ILogger<SqlInstanceDiscoveryService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _options = options;
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public SqlInstanceDiscoveryJobStatus StartDiscovery(SqlInstanceDiscoveryRequest request)
    {
        TrimCompletedJobs();

        var job = new DiscoveryJobState(Guid.NewGuid().ToString("n"), DateTime.UtcNow);
        _jobs[job.JobId] = job;
        job.Report("Queued SQL Server discovery scan.");

        _ = Task.Run(() => RunDiscoveryJobAsync(job, request), CancellationToken.None);
        return job.ToStatus();
    }

    public SqlInstanceDiscoveryJobStatus? GetDiscoveryJob(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job.ToStatus() : null;

    public async Task<SqlInstanceDiscoveryResponse> DiscoverAsync(
        SqlInstanceDiscoveryRequest request,
        CancellationToken cancellationToken)
        => await DiscoverAsync(request, null, cancellationToken);

    private async Task<SqlInstanceDiscoveryResponse> DiscoverAsync(
        SqlInstanceDiscoveryRequest request,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        void Report(string message) => progress?.Invoke(message);

        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 10, 900);
        Report($"Preparing discovery scan with a {timeoutSeconds} second per-phase timeout.");

        try
        {
            var requestedDiscoveryTypes = SplitList(request.DiscoveryTypes);
            var discoveryTypes = requestedDiscoveryTypes;
            if (discoveryTypes.Length == 0)
            {
                discoveryTypes = ["DomainSPN", "DataSourceEnumeration"];
            }

            var scanTypes = SplitList(request.ScanTypes);
            if (scanTypes.Length == 0)
            {
                scanTypes = ["Browser"];
            }

            var targets = SplitList(request.Targets);
            var ipAddresses = SplitList(request.IpAddresses);
            if (targets.Length == 0 && ipAddresses.Length > 0 && IsDefaultDiscoveryTypes(requestedDiscoveryTypes))
            {
                discoveryTypes = ["IPRange"];
            }
            else if (targets.Length == 0
                     && ipAddresses.Length > 0
                     && !discoveryTypes.Contains("IPRange", StringComparer.OrdinalIgnoreCase))
            {
                discoveryTypes = ["IPRange", .. discoveryTypes];
            }

            var tcpPorts = ParseTcpPorts(request.TcpPorts);
            ValidateDiscoveryRequest(targets, discoveryTypes, ipAddresses);
            var phases = BuildDiscoveryPhases(
                targets,
                discoveryTypes,
                scanTypes,
                ipAddresses,
                tcpPorts,
                request.DomainController?.Trim() ?? "",
                string.IsNullOrWhiteSpace(request.MinimumConfidence) ? "Medium" : request.MinimumConfidence.Trim(),
                string.IsNullOrWhiteSpace(request.Purpose) ? "Development" : request.Purpose.Trim());
            Report($"Scan plan: {phases.Count} phase(s). Timeout is per phase, not for the whole scan.");

            var outputs = new List<string>();
            var phaseProblems = new List<string>();
            for (var index = 0; index < phases.Count; index++)
            {
                var phase = phases[index];
                Report($"Phase {index + 1}/{phases.Count}: {phase.Name}.");
                try
                {
                    var output = await RunDbatoolsDiscoveryAsync(phase, timeoutSeconds, Report, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        outputs.Add(output);
                    }
                }
                catch (TimeoutException ex)
                {
                    phaseProblems.Add(ex.Message);
                    Report(ex.Message);
                }
                catch (InvalidOperationException ex) when (!IsFatalDiscoveryError(ex))
                {
                    var phaseFailure = $"Phase {index + 1}/{phases.Count} failed: {phase.Name}. {ex.Message}";
                    phaseProblems.Add(phaseFailure);
                    Report(phaseFailure);
                }
            }

            if (outputs.Count == 0 && phaseProblems.Count > 0)
            {
                return new SqlInstanceDiscoveryResponse(
                    false,
                    $"Discovery did not complete any phase. Last issue: {phaseProblems[^1]}",
                    []);
            }

            Report("Parsing dbatools discovery output.");
            var rows = ParseDbatoolsOutput(outputs, request.Purpose);
            Report(rows.Count == 0 ? "dbatools completed; no SQL Server instances matched." : $"dbatools completed; found {rows.Count} SQL Server instance(s).");
            var message = rows.Count == 0 ? "No SQL Server instances found." : $"Found {rows.Count} SQL Server instance(s).";
            if (phaseProblems.Count > 0)
            {
                message = $"{message} {phaseProblems.Count} phase(s) failed or timed out; see the discovery log for the exact phase.";
            }

            return new SqlInstanceDiscoveryResponse(
                true,
                message,
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

    private async Task RunDiscoveryJobAsync(DiscoveryJobState job, SqlInstanceDiscoveryRequest request)
    {
        try
        {
            job.MarkRunning("Starting SQL Server discovery scan.");
            var result = await DiscoverAsync(request, job.Report, _appLifetime.ApplicationStopping);
            if (result.Success)
            {
                job.MarkSucceeded(result.Message, result.Instances);
                return;
            }

            job.MarkFailed(result.Message);
        }
        catch (OperationCanceledException) when (_appLifetime.ApplicationStopping.IsCancellationRequested)
        {
            job.MarkFailed("Discovery stopped because the central repository service is shutting down.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discovery job {JobId} failed.", job.JobId);
            job.MarkFailed(ex.Message);
        }
    }

    private async Task<string> RunDbatoolsDiscoveryAsync(
        DiscoveryPhase phase,
        int timeoutSeconds,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var tempDirectory = GetDiscoveryTempDirectory();
        Directory.CreateDirectory(tempDirectory);
        progress($"Using discovery temp folder {tempDirectory}.");

        var shell = ResolvePowerShellExecutable(tempDirectory);
        progress($"Using {shell} to run dbatools Find-DbaInstance.");
        var script = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'

if (-not (Get-Module -ListAvailable -Name dbatools)) {
    throw 'dbatools is not installed for the account running this service. Install it on the monitoring server for that account, or install it for all users with: Install-Module dbatools -Scope AllUsers'
}

Import-Module dbatools -ErrorAction Stop
$request = $env:PM_SQL_DISCOVERY_REQUEST | ConvertFrom-Json
$params = @{
    MinimumConfidence = $request.minimumConfidence
    WarningAction = 'SilentlyContinue'
}

$hasTargets = $request.targets -and $request.targets.Count -gt 0

if ($hasTargets) {
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

if (-not $hasTargets -and $request.ipAddresses -and $request.ipAddresses.Count -gt 0) {
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

        var jsonRequest = JsonSerializer.Serialize(phase.Input, s_jsonOptions);
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

        progress($"Starting Find-DbaInstance for {phase.Name}. Parameters: {phase.Plan}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell for SQL Server discovery.");
        var processStartTime = DateTime.UtcNow;
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

        while (!waitTask.IsCompleted)
        {
            var elapsed = DateTime.UtcNow - processStartTime;
            if (elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw new TimeoutException($"Phase timed out after {timeoutSeconds} seconds: {phase.Name}. Parameters: {phase.Plan}");
            }

            var pollDelay = TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, timeoutSeconds - elapsed.TotalSeconds)));
            var finished = await Task.WhenAny(waitTask, Task.Delay(pollDelay, cancellationToken));
            if (ReferenceEquals(finished, waitTask))
            {
                break;
            }

            elapsed = DateTime.UtcNow - processStartTime;
            progress($"Still waiting after {elapsed.TotalSeconds:n0}s of {timeoutSeconds}s for {phase.Name}. Parameters: {phase.Plan}");
        }

        await waitTask;

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("dbatools discovery failed: {Error}", error);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "dbatools discovery failed." : error.Trim());
        }

        progress($"Find-DbaInstance finished for {phase.Name}; collecting returned candidates.");
        return output;
    }

    private List<SqlInstanceDiscoveryResult> ParseDbatoolsOutput(IReadOnlyList<string> outputs, string purpose)
    {
        if (outputs.Count == 0)
        {
            return [];
        }

        var configured = _options.CurrentValue.Servers
            .SelectMany(static server => new[] { server.DataSource, server.DisplayName, server.Id })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var discoveredPurpose = string.IsNullOrWhiteSpace(purpose) ? "Development" : purpose.Trim();
        var elements = new List<JsonElement>();
        foreach (var output in outputs.Where(static output => !string.IsNullOrWhiteSpace(output)))
        {
            var rows = JsonSerializer.Deserialize<JsonElement>(output, s_jsonOptions);
            if (rows.ValueKind == JsonValueKind.Array)
            {
                elements.AddRange(rows.EnumerateArray());
            }
            else
            {
                elements.Add(rows);
            }
        }

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

    private static bool IsDefaultDiscoveryTypes(string[] discoveryTypes)
    {
        if (discoveryTypes.Length == 0)
        {
            return true;
        }

        var normalized = discoveryTypes
            .Select(static type => type.Trim())
            .Where(static type => type.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalized.SetEquals(["DomainSPN", "DataSourceEnumeration"]);
    }

    private static void ValidateDiscoveryRequest(
        string[] targets,
        string[] discoveryTypes,
        string[] ipAddresses)
    {
        if (targets.Length > 0)
        {
            return;
        }

        var hasIpRangeDiscovery = discoveryTypes.Any(static type => string.Equals(type, "IPRange", StringComparison.OrdinalIgnoreCase));
        if (hasIpRangeDiscovery && ipAddresses.Length == 0)
        {
            throw new ArgumentException("IPRange discovery needs an IP range. Add a CIDR/range such as 10.1.164.0/24, or remove IPRange from Discovery Type.");
        }

        if (ipAddresses.Length > 0)
        {
            return;
        }

        if (discoveryTypes.Length == 0)
        {
            throw new ArgumentException("Choose a discovery source before scanning.");
        }
    }

    private static List<DiscoveryPhase> BuildDiscoveryPhases(
        string[] targets,
        string[] discoveryTypes,
        string[] scanTypes,
        string[] ipAddresses,
        int[] tcpPorts,
        string domainController,
        string minimumConfidence,
        string purpose)
    {
        var phases = new List<DiscoveryPhase>();
        var scanVariants = BuildScanVariants(scanTypes, tcpPorts);

        if (targets.Length > 0)
        {
            foreach (var target in targets)
            {
                foreach (var scan in scanVariants)
                {
                    var input = new DbatoolsDiscoveryInput(
                        [target],
                        [],
                        scan.ScanTypes,
                        [],
                        scan.TcpPorts,
                        domainController,
                        minimumConfidence,
                        purpose);
                    phases.Add(new DiscoveryPhase($"target {target} / {scan.Name}", DescribePlan(input), input));
                }
            }

            return phases;
        }

        foreach (var ipAddress in ipAddresses)
        {
            foreach (var scan in scanVariants)
            {
                var input = new DbatoolsDiscoveryInput(
                    [],
                    ["IPRange"],
                    scan.ScanTypes,
                    [ipAddress],
                    scan.TcpPorts,
                    domainController,
                    minimumConfidence,
                    purpose);
                phases.Add(new DiscoveryPhase($"IP range {ipAddress} / {scan.Name}", DescribePlan(input), input));
            }
        }

        foreach (var discoveryType in discoveryTypes.Where(static type => !string.Equals(type, "IPRange", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var scan in scanVariants)
            {
                var input = new DbatoolsDiscoveryInput(
                    [],
                    [discoveryType],
                    scan.ScanTypes,
                    [],
                    scan.TcpPorts,
                    domainController,
                    minimumConfidence,
                    purpose);
                phases.Add(new DiscoveryPhase($"discovery {discoveryType} / {scan.Name}", DescribePlan(input), input));
            }
        }

        if (phases.Count == 0)
        {
            foreach (var scan in scanVariants)
            {
                var input = new DbatoolsDiscoveryInput(
                    [],
                    discoveryTypes,
                    scan.ScanTypes,
                    [],
                    scan.TcpPorts,
                    domainController,
                    minimumConfidence,
                    purpose);
                phases.Add(new DiscoveryPhase($"discovery {FormatList(discoveryTypes, "default")} / {scan.Name}", DescribePlan(input), input));
            }
        }

        return phases;
    }

    private static List<ScanVariant> BuildScanVariants(string[] scanTypes, int[] tcpPorts)
    {
        var variants = new List<ScanVariant>();
        foreach (var scanType in scanTypes)
        {
            if (string.Equals(scanType, "TCPPort", StringComparison.OrdinalIgnoreCase) && tcpPorts.Length > 0)
            {
                variants.AddRange(tcpPorts.Select(port => new ScanVariant($"TCPPort {port}", [scanType], [port])));
                continue;
            }

            variants.Add(new ScanVariant(scanType, [scanType], []));
        }

        return variants.Count == 0 ? [new ScanVariant("Browser", ["Browser"], [])] : variants;
    }

    private static string DescribePlan(
        string[] targets,
        string[] discoveryTypes,
        string[] scanTypes,
        string[] ipAddresses,
        int[] tcpPorts,
        string? domainController)
    {
        var parts = new List<string>
        {
            $"targets={FormatList(targets, "none")}",
            $"discovery={FormatList(discoveryTypes, "default")}",
            $"scan={FormatList(scanTypes, "default")}",
            $"ip={FormatList(ipAddresses, "none")}",
            $"ports={FormatList(tcpPorts.Select(static port => port.ToString()).ToArray(), "default")}"
        };

        if (!string.IsNullOrWhiteSpace(domainController))
        {
            parts.Add($"domain controller={domainController.Trim()}");
        }

        return string.Join("; ", parts);
    }

    private static string DescribePlan(DbatoolsDiscoveryInput input)
        => DescribePlan(input.Targets, input.DiscoveryTypes, input.ScanTypes, input.IpAddresses, input.TcpPorts, input.DomainController);

    private static string FormatList(string[] values, string emptyValue)
        => values.Length == 0 ? emptyValue : string.Join(",", values);

    private static bool IsFatalDiscoveryError(InvalidOperationException exception)
        => exception.Message.Contains("dbatools is not installed", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("PowerShell was not found", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Could not start PowerShell", StringComparison.OrdinalIgnoreCase);

    private void TrimCompletedJobs()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var item in _jobs)
        {
            if (item.Value.CompletedAt is { } completedAt && completedAt < cutoff)
            {
                _jobs.TryRemove(item.Key, out _);
            }
        }
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

    private sealed record DiscoveryPhase(string Name, string Plan, DbatoolsDiscoveryInput Input);

    private sealed record ScanVariant(string Name, string[] ScanTypes, int[] TcpPorts);

    private sealed class DiscoveryJobState
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private IReadOnlyList<SqlInstanceDiscoveryResult> _instances = [];

        public DiscoveryJobState(string jobId, DateTime startedAt)
        {
            JobId = jobId;
            StartedAt = startedAt;
        }

        public string JobId { get; }
        public DateTime StartedAt { get; }
        public DateTime? CompletedAt { get; private set; }
        private string Status { get; set; } = "queued";
        private string Message { get; set; } = "Queued SQL Server discovery scan.";

        public void MarkRunning(string message)
        {
            lock (_gate)
            {
                Status = "running";
            }

            Report(message);
        }

        public void MarkSucceeded(string message, IReadOnlyList<SqlInstanceDiscoveryResult> instances)
        {
            lock (_gate)
            {
                Status = "succeeded";
                Message = message;
                CompletedAt = DateTime.UtcNow;
                _instances = instances;
                AddEventLocked(message);
            }
        }

        public void MarkFailed(string message)
        {
            lock (_gate)
            {
                Status = "failed";
                Message = message;
                CompletedAt = DateTime.UtcNow;
                AddEventLocked(message);
            }
        }

        public void Report(string message)
        {
            lock (_gate)
            {
                Message = message;
                AddEventLocked(message);
            }
        }

        public SqlInstanceDiscoveryJobStatus ToStatus()
        {
            lock (_gate)
            {
                return new SqlInstanceDiscoveryJobStatus(
                    JobId,
                    Status,
                    Message,
                    StartedAt,
                    CompletedAt,
                    _events.ToList(),
                    _instances);
            }
        }

        private void AddEventLocked(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _events.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_events.Count > 50)
            {
                _events.RemoveRange(0, _events.Count - 50);
            }
        }
    }
}
