using PerformanceMonitor.Headless.Security;
using PerformanceMonitor.SqlConnectivity;

namespace PerformanceMonitor.Headless.Models;

public interface ISqlConnectionProfile
{
    string ConnectionMode { get; }
    string DataSource { get; }
    string InitialCatalog { get; }
    string? UserId { get; }
    string? ProtectedPassword { get; }
    string Encrypt { get; }
    bool TrustServerCertificate { get; }
    string? ConnectionString { get; }
    string? ConnectionStringEnvironmentVariable { get; }
}

public static class SqlConnectionProfile
{
    public static string ResolveConnectionString(this ISqlConnectionProfile profile, string defaultDatabase)
    {
        if (!string.IsNullOrWhiteSpace(profile.ConnectionStringEnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(profile.ConnectionStringEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return Environment.ExpandEnvironmentVariables(fromEnvironment);
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.ConnectionString))
        {
            return Environment.ExpandEnvironmentVariables(profile.ConnectionString);
        }

        if (string.IsNullOrWhiteSpace(profile.DataSource))
        {
            return "";
        }

        var connectionString = SqlConnectionBuilder.BuildConnectionString(
            profile.DataSource.Trim(),
            !string.Equals(profile.ConnectionMode, "Sql", StringComparison.OrdinalIgnoreCase),
            profile.UserId,
            LocalSecretProtector.Unprotect(profile.ProtectedPassword),
            string.IsNullOrWhiteSpace(profile.Encrypt) ? "Optional" : profile.Encrypt.Trim(),
            profile.TrustServerCertificate);

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = string.IsNullOrWhiteSpace(profile.InitialCatalog)
                ? defaultDatabase
                : profile.InitialCatalog.Trim()
        };

        return builder.ConnectionString;
    }
}
