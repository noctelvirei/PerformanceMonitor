/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects CPU utilization from the ring buffer (on-prem, MI, RDS)
    /// or sys.dm_db_resource_stats (Azure SQL DB).
    /// </summary>
    private async Task<int> CollectCpuUtilizationAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var serverStatus = _serverManager.GetConnectionStatus(server.Id);
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        /* Get the most recent sample_time we already have, to skip duplicates.
           Ring buffer always returns TOP 60 (computed sample_time can't be filtered server-side).
           For Azure SQL DB, we push the filter into the SQL query since end_time is a real column. */
        var lastSampleTime = await GetLastCollectedTimeAsync(
            serverId, "cpu_utilization_stats", "sample_time", cancellationToken);

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        var samples = await SqlServerCollectors.CollectCpuUtilizationAsync(
            sqlConnection,
            CommandTimeoutSeconds,
            serverStatus.SqlEngineEdition,
            lastSampleTime,
            cancellationToken);
        sqlSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;

        /* Insert into DuckDB using Appender for bulk performance */
        var duckSw = Stopwatch.StartNew();

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender("cpu_utilization_stats"))
            {
                foreach (var sample in samples)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(GenerateCollectionId())
                       .AppendValue(collectionTime)
                       .AppendValue(serverId)
                       .AppendValue(GetServerNameForStorage(server))
                       .AppendValue(sample.SampleTime)
                       .AppendValue(sample.SqlServerCpuUtilization)
                       .AppendValue(sample.OtherProcessCpuUtilization)
                       .EndRow();

                    rowsCollected++;
                }
            }
        }

        duckSw.Stop();
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} CPU utilization samples for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }
}
