namespace PerformanceMonitor.SqlConnectivity;

public sealed class SqlServerConnectionInfo
{
    public string ServerName { get; set; } = string.Empty;
    public string SqlServerVersion { get; set; } = string.Empty;
    public string SqlServerEdition { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public string? ErrorMessage { get; set; }
    public int EngineEdition { get; set; }
    public int ProductMajorVersion { get; set; }
    public long ElapsedMilliseconds { get; set; }

    public bool IsSupportedVersion =>
        EngineEdition is 8 || ProductMajorVersion >= 13;
}
