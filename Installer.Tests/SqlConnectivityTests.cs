using Installer.Core;
using Microsoft.Data.SqlClient;

namespace Installer.Tests;

public class SqlConnectivityTests
{
    [Fact]
    public void InstallationService_BuildConnectionString_KeepsWindowsAuthShape()
    {
        var connectionString = InstallationService.BuildConnectionString(
            "dev-sql",
            useWindowsAuth: true,
            encryption: "Optional",
            trustCertificate: true);

        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal("dev-sql", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void InstallationService_BuildConnectionString_KeepsSqlAuthShape()
    {
        var connectionString = InstallationService.BuildConnectionString(
            "dev-sql",
            useWindowsAuth: false,
            username: "monitor",
            password: "secret",
            encryption: "Mandatory",
            trustCertificate: false);

        var builder = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal("dev-sql", builder.DataSource);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal("monitor", builder.UserID);
        Assert.Equal("secret", builder.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
    }
}
