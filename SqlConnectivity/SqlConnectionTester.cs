using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace PerformanceMonitor.SqlConnectivity;

public static class SqlConnectionTester
{
    public static async Task<SqlServerConnectionInfo> TestConnectionAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var info = new SqlServerConnectionInfo();
        var watch = Stopwatch.StartNew();

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            info.IsConnected = true;
            info.ElapsedMilliseconds = watch.ElapsedMilliseconds;

            await using var command = new SqlCommand("""
                SELECT
                    @@VERSION,
                    SERVERPROPERTY('Edition'),
                    @@SERVERNAME,
                    CONVERT(int, SERVERPROPERTY('EngineEdition')),
                    SERVERPROPERTY('ProductMajorVersion');
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                info.SqlServerVersion = reader.GetString(0);
                info.SqlServerEdition = reader.GetString(1);
                info.ServerName = reader.GetString(2);
                info.EngineEdition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                info.ProductMajorVersion = reader.IsDBNull(4) ? 0 : int.TryParse(reader.GetValue(4).ToString(), out var version) ? version : 0;
            }
        }
        catch (Exception ex)
        {
            info.IsConnected = false;
            info.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            info.ErrorMessage = ex.Message;
            if (ex.InnerException is not null)
            {
                info.ErrorMessage += $"\n{ex.InnerException.Message}";
            }
        }

        return info;
    }
}
