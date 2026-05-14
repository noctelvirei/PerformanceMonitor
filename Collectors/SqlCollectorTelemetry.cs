namespace PerformanceMonitor.Collectors;

public sealed record ServerPropertiesTelemetry(
    string MachineName,
    string? InstanceName,
    string Edition,
    string ProductVersion,
    string ProductLevel,
    string? ProductUpdateLevel,
    int EngineEdition,
    int SqlMajorVersion,
    int CpuCount,
    int HyperthreadRatio,
    long PhysicalMemoryMb,
    int? SocketCount,
    int? CoresPerSocket,
    bool? IsHadrEnabled,
    bool? IsClustered,
    string? ServiceObjective,
    int? VCoreCount,
    DateTime SqlServerStartTime);

public sealed record WaitStatTelemetry(
    string WaitType,
    long WaitingTasksCount,
    long WaitTimeMs,
    long SignalWaitTimeMs);

public sealed record CpuSampleTelemetry(
    DateTime SampleTime,
    int SqlServerCpuUtilization,
    int OtherProcessCpuUtilization);

public sealed record WaitingTaskTelemetry(
    int SessionId,
    string? WaitType,
    long WaitDurationMs,
    int? BlockingSessionId,
    string? ResourceDescription,
    string? DatabaseName);
