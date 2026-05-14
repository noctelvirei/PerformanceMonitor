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
    /// Collects point-in-time waiting task information from sys.dm_os_waiting_tasks.
    /// </summary>
    private async Task<int> CollectWaitingTasksAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        var waitingTasks = await SqlServerCollectors.CollectWaitingTasksAsync(
            sqlConnection,
            CommandTimeoutSeconds,
            server.ExcludedDatabases,
            cancellationToken);
        sqlSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;

        var duckSw = Stopwatch.StartNew();

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender("waiting_tasks"))
            {
                foreach (var waitingTask in waitingTasks)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(GenerateCollectionId())
                       .AppendValue(collectionTime)
                       .AppendValue(serverId)
                       .AppendValue(GetServerNameForStorage(server))
                       .AppendValue(waitingTask.SessionId)
                       .AppendValue(waitingTask.WaitType)
                       .AppendValue(waitingTask.WaitDurationMs)
                       .AppendValue(waitingTask.BlockingSessionId)
                       .AppendValue(waitingTask.ResourceDescription)
                       .AppendValue(waitingTask.DatabaseName)
                       .EndRow();

                    rowsCollected++;
                }
            }
        }

        duckSw.Stop();
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} waiting task records for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }
}
