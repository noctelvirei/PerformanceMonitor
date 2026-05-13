/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    private readonly Lazy<HashSet<string>> _ignoredWaitTypes;

    /// <summary>
    /// Loads the set of wait types to ignore during collection.
    /// Thread-safe via Lazy&lt;T&gt; (multiple server tasks call this in parallel).
    /// </summary>
    private HashSet<string> LoadIgnoredWaitTypes()
    {
        var configPath = Path.Combine(App.ConfigDirectory, "ignored_wait_types.json");
        return WaitTypePolicy.LoadFromFileOrDefault(configPath);
    }

    /// <summary>
    /// Collects wait statistics from sys.dm_os_wait_stats.
    /// </summary>
    private async Task<int> CollectWaitStatsAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var ignoredWaits = _ignoredWaitTypes.Value;
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        var waitStats = await SqlServerCollectors.CollectWaitStatsAsync(
            sqlConnection,
            CommandTimeoutSeconds,
            ignoredWaits,
            cancellationToken);
        sqlSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;

        /* Insert into DuckDB with delta calculations using Appender for bulk performance */
        var duckSw = Stopwatch.StartNew();

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender("wait_stats"))
            {
                foreach (var stat in waitStats)
                {
                    var deltaKey = stat.WaitType;
                    var deltaWaitingTasks = _deltaCalculator.CalculateDelta(serverId, "wait_stats_tasks", deltaKey, stat.WaitingTasksCount, baselineOnly: true, collectionTime: collectionTime, maxGapSeconds: 300);
                    var deltaWaitTimeMs = _deltaCalculator.CalculateDelta(serverId, "wait_stats_time", deltaKey, stat.WaitTimeMs, baselineOnly: true, collectionTime: collectionTime, maxGapSeconds: 300);
                    var deltaSignalWaitTimeMs = _deltaCalculator.CalculateDelta(serverId, "wait_stats_signal", deltaKey, stat.SignalWaitTimeMs, baselineOnly: true, collectionTime: collectionTime, maxGapSeconds: 300);

                    var row = appender.CreateRow();
                    row.AppendValue(GenerateCollectionId())    /* collection_id BIGINT */
                       .AppendValue(collectionTime)            /* collection_time TIMESTAMP */
                       .AppendValue(serverId)                  /* server_id INTEGER */
                       .AppendValue(GetServerNameForStorage(server))         /* server_name VARCHAR */
                       .AppendValue(stat.WaitType)             /* wait_type VARCHAR */
                       .AppendValue(stat.WaitingTasksCount)    /* waiting_tasks_count BIGINT */
                       .AppendValue(stat.WaitTimeMs)           /* wait_time_ms BIGINT */
                       .AppendValue(stat.SignalWaitTimeMs)     /* signal_wait_time_ms BIGINT */
                       .AppendValue(deltaWaitingTasks)         /* delta_waiting_tasks BIGINT */
                       .AppendValue(deltaWaitTimeMs)           /* delta_wait_time_ms BIGINT */
                       .AppendValue(deltaSignalWaitTimeMs)     /* delta_signal_wait_time_ms BIGINT */
                       .EndRow();

                    rowsCollected++;
                }
            }
        }

        duckSw.Stop();
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} wait stats for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }
}
