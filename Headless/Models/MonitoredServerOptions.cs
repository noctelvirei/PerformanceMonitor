namespace PerformanceMonitor.Headless.Models;

using Microsoft.Data.SqlClient;
using PerformanceMonitor.Headless.Security;

public sealed class MonitoredServerOptions
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
    {
        if (!string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return Environment.ExpandEnvironmentVariables(fromEnvironment);
            }
        }

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return Environment.ExpandEnvironmentVariables(ConnectionString);
        }

        return ResolveTypedConnectionString();
    }

    public string ResolveTypedConnectionString()
    {
        if (string.IsNullOrWhiteSpace(DataSource))
        {
            return "";
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = DataSource.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(InitialCatalog) ? "master" : InitialCatalog.Trim(),
            TrustServerCertificate = TrustServerCertificate
        };

        builder["Encrypt"] = string.IsNullOrWhiteSpace(Encrypt) ? "Optional" : Encrypt.Trim();

        if (string.Equals(ConnectionMode, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = UserId ?? "";
            builder.Password = LocalSecretProtector.Unprotect(ProtectedPassword);
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }
}
