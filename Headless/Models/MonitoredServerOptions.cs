namespace PerformanceMonitor.Headless.Models;

public sealed class MonitoredServerOptions : ISqlConnectionProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Purpose { get; set; } = "Unassigned";
    public string ConnectionMode { get; set; } = "EnvironmentVariable";
    public string DataSource { get; set; } = "";
    public string InitialCatalog { get; set; } = "master";
    public string? UserId { get; set; }
    public string? ProtectedPassword { get; set; }
    public string Encrypt { get; set; } = "Optional";
    public bool TrustServerCertificate { get; set; } = true;
    public string? ConnectionString { get; set; }
    public string? ConnectionStringEnvironmentVariable { get; set; }
    public bool Enabled { get; set; } = true;

    public string ServerNameForStorage => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
    public string PurposeForDisplay => string.IsNullOrWhiteSpace(Purpose) ? "Unassigned" : Purpose.Trim();

    public string ResolveConnectionString()
        => this.ResolveConnectionString("master");
}
