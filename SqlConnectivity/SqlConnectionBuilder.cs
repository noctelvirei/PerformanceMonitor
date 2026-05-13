using Microsoft.Data.SqlClient;

namespace PerformanceMonitor.SqlConnectivity;

public static class SqlConnectionBuilder
{
    public static string BuildConnectionString(
        string server,
        bool useWindowsAuth,
        string? username = null,
        string? password = null,
        string encryption = "Mandatory",
        bool trustCertificate = false,
        bool useEntraAuth = false)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            TrustServerCertificate = trustCertificate
        };

        builder.Encrypt = encryption switch
        {
            "Optional" => SqlConnectionEncryptOption.Optional,
            "Mandatory" => SqlConnectionEncryptOption.Mandatory,
            "Strict" => SqlConnectionEncryptOption.Strict,
            _ => SqlConnectionEncryptOption.Mandatory
        };

        if (useEntraAuth)
        {
            builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
            builder.UserID = username;
        }
        else if (useWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = username;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }
}
