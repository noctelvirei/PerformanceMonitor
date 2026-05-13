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
    /// Collects server edition, version, CPU/memory hardware metadata for
    /// license audit and FinOps cost attribution. On-load only collector.
    /// </summary>
    private async Task<int> CollectServerPropertiesAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        var properties = await SqlServerCollectors.CollectServerPropertiesAsync(
            sqlConnection,
            CommandTimeoutSeconds,
            cancellationToken);
        sqlSw.Stop();

        var duckSw = Stopwatch.StartNew();

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender("server_properties"))
            {
                var row = appender.CreateRow();
                row.AppendValue(GenerateCollectionId())
                   .AppendValue(collectionTime)
                   .AppendValue(serverId)
                   .AppendValue(GetServerNameForStorage(server))
                   .AppendValue(properties.Edition)
                   .AppendValue(properties.ProductVersion)
                   .AppendValue(properties.ProductLevel)
                   .AppendValue(properties.ProductUpdateLevel)
                   .AppendValue(properties.EngineEdition)
                   .AppendValue(properties.CpuCount)
                   .AppendValue(properties.HyperthreadRatio)
                   .AppendValue(properties.PhysicalMemoryMb)
                   .AppendValue(properties.SocketCount)
                   .AppendValue(properties.CoresPerSocket)
                   .AppendValue(properties.IsHadrEnabled)
                   .AppendValue(properties.IsClustered)
                   .AppendValue((string?)null) // enterprise_features - not collected in Lite (requires cross-database cursor)
                   .AppendValue(properties.ServiceObjective)
                   .AppendValue(properties.VCoreCount)
                   .EndRow();
                rowsCollected++;
            }
        }

        duckSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} server properties row(s) for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }
}
