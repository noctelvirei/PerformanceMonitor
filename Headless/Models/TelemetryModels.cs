namespace PerformanceMonitor.Headless.Models;

public sealed record ServerHealthDto(
    string ServerId,
    string DisplayName,
    string Purpose,
    bool IsEnabled,
    DateTime? LastSeenTime,
    string LastStatus,
    string? LastError,
    string? ProductVersion,
    string? Edition,
    int? SqlMajorVersion,
    string HealthState,
    string HealthReason,
    int ActiveAlertCount,
    bool IsAttentionState,
    int? LatestSqlCpuUtilization,
    string? TopWaitType);

public sealed record CollectionLogDto(
    DateTime CollectionTime,
    string ServerId,
    string ServerName,
    string CollectorName,
    string Status,
    int RowsCollected,
    int DurationMs,
    string? ErrorMessage);

public sealed record ActiveAlertDto(
    DateTime RaisedAt,
    string ServerId,
    string ServerName,
    string Source,
    string Severity,
    string Message,
    string TargetTab);

public sealed record TopWaitDto(
    string WaitType,
    long WaitTimeDeltaMs,
    long SignalWaitTimeDeltaMs,
    long WaitingTasksDelta);

public sealed record CpuSampleDto(
    DateTime SampleTime,
    int SqlServerCpuUtilization,
    int OtherProcessCpuUtilization);

public sealed record EstateSummaryDto(
    int ServerCount,
    int GreenCount,
    int YellowCount,
    int RedCount,
    int ErrorCount,
    int DisabledCount,
    DateTime GeneratedAt,
    IReadOnlyList<ServerHealthDto> Servers,
    IReadOnlyList<ActiveAlertDto> ActiveAlerts);

public sealed record StorageInfoDto(
    string Provider,
    string? DuckDbPath,
    string? ParquetDirectory,
    string? SqlDataSource,
    string? SqlDatabase);

public sealed class IngestSnapshotDto
{
    public IngestServerDto Server { get; set; } = new();
    public DateTime? CollectionTime { get; set; }
    public string Status { get; set; } = "ONLINE";
    public string? ErrorMessage { get; set; }
    public ServerPropertiesSnapshot? ServerProperties { get; set; }
    public List<WaitStatSnapshot> WaitStats { get; set; } = [];
    public List<CpuSample> CpuSamples { get; set; } = [];
    public List<CollectionLogDto> CollectionLog { get; set; } = [];
}

public sealed class IngestServerDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Purpose { get; set; } = "Unassigned";
    public bool Enabled { get; set; } = true;
}

public sealed record IngestResultDto(
    bool Success,
    int ServerPropertiesRows,
    int WaitRows,
    int CpuRows,
    int LogRows);

public sealed record ServerPropertiesSnapshot(
    string MachineName,
    string? InstanceName,
    string ProductVersion,
    string ProductLevel,
    string Edition,
    int EngineEdition,
    int SqlMajorVersion,
    int CpuCount,
    long PhysicalMemoryMb,
    DateTime SqlServerStartTime);

public sealed record WaitStatSnapshot(
    string WaitType,
    long WaitingTasksCount,
    long WaitTimeMs,
    long SignalWaitTimeMs);

public sealed record CpuSample(
    DateTime SampleTime,
    int SqlServerCpuUtilization,
    int OtherProcessCpuUtilization);
