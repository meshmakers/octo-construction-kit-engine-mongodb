using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dapper;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Polly;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// Client for interacting with the stream data database.
/// </summary>
internal class CrateDatabaseClient : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient,
    IStreamDataHealthCheckClient
{
    /// <summary>
    /// AB#4278: maximum single-row inserts executed under one resilience (Polly) envelope. A bulk
    /// insert of millions of rows (the <c>import_archive_data</c> path) is split into sub-batches of
    /// this size, each opening its own connection and running inside its own per-attempt timeout, so
    /// no single attempt spans more inserts than can complete within the timeout — and a transient
    /// retry replays only the failed sub-batch instead of the whole input. Sized well below the number
    /// of single-row round-trips that fit in the 30s per-attempt budget.
    /// </summary>
    private const int MaxRowsPerInsertBatch = 1000;

    private readonly ILogger<CrateDatabaseClient> _logger;
    private readonly ICrateDbConnectionAccess _connectionAccess;
    private readonly StreamDataConfiguration _configuration;
    private readonly ResiliencePipeline _resilience;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="connectionAccess"></param>
    /// <param name="configuration"></param>
    /// <param name="resilienceOptions">Tunables for the timeout/retry/circuit-breaker pipeline (concept §8 T13).</param>
    public CrateDatabaseClient(ILogger<CrateDatabaseClient> logger, ICrateDbConnectionAccess connectionAccess,
        IOptions<StreamDataConfiguration> configuration,
        IOptions<CrateResilienceOptions> resilienceOptions)
    {
        _logger = logger;
        _connectionAccess = connectionAccess;
        _configuration = configuration.Value;
        _resilience = CrateResiliencePipeline.Build(resilienceOptions.Value);

        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, object>>());
        SqlMapper.AddTypeHandler(new CkIdTypeHandler());
        SqlMapper.AddTypeHandler(new OctoIdTypeHandler());
    }

    public async Task<List<DataPointDto>> GetDataAsync(string tenantId, string query)
    {
        return await _resilience.ExecuteAsync(async _ => await GetDataInternalAsync(tenantId, query));
    }

    private async Task<List<DataPointDto>> GetDataInternalAsync(string tenantId, string query)
    {
        await using var lease = await LeaseConnectionAsync(tenantId);
        var connection = lease.Connection;

        var queryResult = await connection.QueryAsync(query);

        var dataPointDtos = new List<DataPointDto>();

        foreach (var entry in queryResult)
        {
            if (entry is not IDictionary<string, object?> result)
            {
                continue;
            }

            var dp = new DataPointDto(result.ToDictionary());

            if (result.TryGetValue(Constants.Timestamp, out var timestamp))
            {
                dp.Timestamp = (DateTime)timestamp!;
            }
            else if (result.TryGetValue("T", out var ts))
            {
                dp.Timestamp = (DateTime)ts!;
            }
            else if (result.TryGetValue(Constants.WindowEnd, out var winEnd))
            {
                // Windowed-storage rows (rollup / time-range) — the query usually aliases
                // window_end as "timestamp" (see CrateQueryBuilder.UseWindowedTimeAxis) so the
                // first branch already catches it. This fallback covers queries that select
                // window_end directly without aliasing.
                dp.Timestamp = (DateTime)winEnd!;
            }

            if (result.TryGetValue(Constants.RtId, out var rtIdValue) &&
                OctoObjectId.TryParse(rtIdValue as string ?? "", out var octoRtId))
            {
                dp.RtId = octoRtId;
            }

            if (result.TryGetValue(Constants.CkTypeId, out var ckTypeIdValue))
            {
                var typeId = new RtCkId<CkTypeId>(ckTypeIdValue as string ?? "");
                dp.CkTypeId = typeId;
            }
            
            if(result.TryGetValue(Constants.RtWellKnownName, out var rtWellKnownName))
            {
                dp.RtWellKnownName = rtWellKnownName as string;
            }
            
            if(result.TryGetValue(Constants.RtChangedDateTime, out var rtChangedDateTime))
            {
                dp.RtChangedDateTime = (DateTime)rtChangedDateTime!;
            }
            
            if(result.TryGetValue(Constants.RtCreationDateTime, out var rtCreationDateTime))
            {
                dp.RtCreationDateTime = (DateTime)rtCreationDateTime!;
            }

            dataPointDtos.Add(dp);
        }

        return dataPointDtos;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Unbuffered enumeration: Dapper hands back rows one at a time off the open data reader so a
        // multi-GB archive export never materialises the whole table. The connection stays open for
        // the life of the enumeration (held by `await using`), which is why this is a per-page query
        // in the caller's keyset loop rather than one giant cursor — a page is small and bounded.
        await using var lease = await LeaseConnectionAsync(tenantId, cancellationToken);
        var connection = lease.Connection;
        var rows = connection.QueryUnbufferedAsync(query);
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (row is not IDictionary<string, object?> dict)
            {
                continue;
            }

            // Copy into a plain dictionary so the value survives past the reader's row lifetime.
            yield return new Dictionary<string, object?>(dict, StringComparer.Ordinal);
        }
    }

    public async Task<long> GetCountAsync(string tenantId, string countQuery)
    {
        return await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId);
            var connection = lease.Connection;
            return await connection.ExecuteScalarAsync<long>(countQuery);
        });
    }

    public async Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<DataPointDto> datapoints)
    {
        var d = datapoints.ToArray();
        if (d.Length == 0) return;

        // Per-archive tables have a typed schema (one CrateDB column per archive column with the
        // CK-derived primitive type), so the legacy `unnest(@arr,...)` bulk path no longer fits:
        // it required `NpgsqlDbType.Array|Json` for the dynamic `data` blob, but typed columns
        // need their own native param types and silently drop values when forced through JSON.
        // Looping single-row inserts trades raw throughput for correct typing across heterogeneous
        // column sets — fine for the volumes the pipeline produces; revisit if profiling flags
        // this as a hot spot.
        var sql = BuildSingleRowInsertSql(qualifiedTable, userColumnNames);

        // AB#4278: chunk into bounded sub-batches so one resilience attempt (and its timeout) never
        // spans more inserts than can complete in the budget; a retry replays only the sub-batch.
        for (var offset = 0; offset < d.Length; offset += MaxRowsPerInsertBatch)
        {
            var start = offset;
            var end = Math.Min(offset + MaxRowsPerInsertBatch, d.Length);
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var lease = await LeaseConnectionAsync(tenantId);
                var connection = lease.Connection;
                for (var i = start; i < end; i++)
                {
                    await ExecuteSingleInsertAsync(connection, sql, d[i], userColumnNames);
                }
            });
        }
    }

    public async Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, DataPointDto datapoint)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId);
            var connection = lease.Connection;
            var sql = BuildSingleRowInsertSql(qualifiedTable, userColumnNames);
            await ExecuteSingleInsertAsync(connection, sql, datapoint, userColumnNames);
        });
    }

    public async Task InsertTimeRangeDataAsync(
        string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<TimeRangeDataPointDto> datapoints)
    {
        var d = datapoints.ToArray();
        if (d.Length == 0) return;

        // Same single-row loop rationale as InsertDataAsync — typed columns need their own
        // native param types per row, the bulk-unnest path can't carry that information.
        var sql = BuildTimeRangeInsertSql(qualifiedTable, userColumnNames);

        // AB#4278: chunk into bounded sub-batches so one resilience attempt (and its timeout) never
        // spans more inserts than can complete in the budget; a retry replays only the sub-batch.
        for (var offset = 0; offset < d.Length; offset += MaxRowsPerInsertBatch)
        {
            var start = offset;
            var end = Math.Min(offset + MaxRowsPerInsertBatch, d.Length);
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var lease = await LeaseConnectionAsync(tenantId);
                var connection = lease.Connection;
                for (var i = start; i < end; i++)
                {
                    await ExecuteSingleTimeRangeInsertAsync(connection, sql, d[i], userColumnNames);
                }
            });
        }
    }

    private static async Task ExecuteSingleTimeRangeInsertAsync(
        NpgsqlConnection connection, string sql, TimeRangeDataPointDto dto, IReadOnlyList<string> userColumnNames)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.WindowStart}", dto.From));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.WindowEnd}", dto.To));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.RtId}", (object?)dto.RtId?.ToString() ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.CkTypeId}", (object?)dto.CkTypeId?.ToString() ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.RtWellKnownName}", (object?)dto.RtWellKnownName ?? DBNull.Value));
        foreach (var col in userColumnNames)
        {
            var value = dto.Attributes != null && dto.Attributes.TryGetValue(col, out var v) ? v : null;
            cmd.Parameters.Add(new NpgsqlParameter($"@{col}", value ?? (object)DBNull.Value));
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static string BuildTimeRangeInsertSql(string qualifiedTable, IReadOnlyList<string> userColumnNames)
    {
        // Column order matches the DDL emitted by GenerateCreateTimeRangeTable: window_start,
        // window_end, rtid, ckTypeId, rtWellKnownName, then user columns. The CONFLICT clause's
        // natural key is the full window (start, end) + entity (rtid, ckTypeId) — a re-delivery
        // of the same window for the same entity upserts; different windows even for the same
        // entity are independent rows. was_updated flips to TRUE on any conflict, regardless of
        // whether the user-column values actually changed (concept §5: "ever updated" signal,
        // not value-change detection).
        var allColumns = new List<string>(5 + userColumnNames.Count)
        {
            Constants.WindowStart, Constants.WindowEnd, Constants.RtId, Constants.CkTypeId, Constants.RtWellKnownName
        };
        allColumns.AddRange(userColumnNames);

        var columnList = string.Join(", ", allColumns.Select(c => $"\"{c}\""));
        var paramList = string.Join(", ", allColumns.Select(c => $"@{c}"));
        var conflictUpdates = userColumnNames.Count == 0
            ? string.Empty
            : ", " + string.Join(", ", userColumnNames.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));

        return $"INSERT INTO {qualifiedTable} ({columnList}) VALUES ({paramList}) "
             + $"ON CONFLICT (\"{Constants.WindowStart}\", \"{Constants.WindowEnd}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\") "
             + $"DO UPDATE SET \"{Constants.RtChangedDateTime}\" = CURRENT_TIMESTAMP, "
             + $"\"{Constants.WasUpdated}\" = TRUE"
             + conflictUpdates;
    }

    /// <summary>
    /// Executes a single per-archive INSERT using the raw Npgsql command API. We bind parameters
    /// directly rather than going through Dapper because Dapper's <c>DynamicParameters</c> erases
    /// the runtime CLR type when it serialises the value, causing CrateDB to receive every
    /// user-column value as a TEXT literal — fine for strings but corrupts doubles, ints, and
    /// timestamps the moment a typed CrateDB column is on the receiving end.
    /// </summary>
    private static async Task ExecuteSingleInsertAsync(
        NpgsqlConnection connection, string sql, DataPointDto dto, IReadOnlyList<string> userColumnNames)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.RtId}", (object?)dto.RtId?.ToString() ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.CkTypeId}", (object?)dto.CkTypeId?.ToString() ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.Timestamp}", dto.Timestamp));
        cmd.Parameters.Add(new NpgsqlParameter($"@{Constants.RtWellKnownName}", (object?)dto.RtWellKnownName ?? DBNull.Value));
        foreach (var col in userColumnNames)
        {
            var value = dto.Attributes != null && dto.Attributes.TryGetValue(col, out var v) ? v : null;
            cmd.Parameters.Add(new NpgsqlParameter($"@{col}", value ?? (object)DBNull.Value));
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static string BuildSingleRowInsertSql(string qualifiedTable, IReadOnlyList<string> userColumnNames)
    {
        // Column-explicit INSERT against a per-archive table. Columns = standard time-series set
        // (rtId, ckTypeId, timestamp, rtWellKnownName) plus the user-defined columns. Unknown
        // attributes on the data point are dropped: DDL would have rejected them at activation, so
        // the data plane stays permissive against outdated callers.
        var allColumns = new List<string>(4 + userColumnNames.Count)
        {
            Constants.RtId, Constants.CkTypeId, Constants.Timestamp, Constants.RtWellKnownName
        };
        allColumns.AddRange(userColumnNames);

        var columnList = string.Join(", ", allColumns.Select(c => $"\"{c}\""));
        var paramList = string.Join(", ", allColumns.Select(c => $"@{c}"));
        var conflictUpdates = userColumnNames.Count == 0
            ? string.Empty
            : ", " + string.Join(", ", userColumnNames.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));

        return $"INSERT INTO {qualifiedTable} ({columnList}) VALUES ({paramList}) "
             + $"ON CONFLICT (\"{Constants.Timestamp}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\") "
             + $"DO UPDATE SET \"{Constants.RtChangedDateTime}\" = CURRENT_TIMESTAMP, "
             + $"\"{Constants.RtCreationDateTime}\" = \"{Constants.RtCreationDateTime}\""
             + conflictUpdates;
    }

    public async Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId);
            await lease.Connection.ExecuteAsync(string.Format(Queries.RefreshTable, qualifiedTable));
        });
    }

    public async Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("sql must not be empty.", nameof(sql));
        }

        return await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId, cancellationToken);
            // Dapper's ExecuteAsync returns the number of affected rows (Npgsql surfaces the
            // CrateDB `INSERT 0 N` / `UPDATE N` row count via NpgsqlCommand.ExecuteNonQuery).
            return await lease.Connection.ExecuteAsync(sql);
        });
    }

    public async Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameterizedSql))
        {
            throw new ArgumentException("sql must not be empty.", nameof(parameterizedSql));
        }

        if (argumentSets.Count == 0)
        {
            return;
        }

        await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId, cancellationToken);
            await using var batch = new NpgsqlBatch(lease.Connection);
            foreach (var set in argumentSets)
            {
                var command = new NpgsqlBatchCommand(parameterizedSql);
                foreach (var value in set)
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = CoerceBulkParameter(value) });
                }

                batch.BatchCommands.Add(command);
            }

            // One prepared statement bound + executed per set in a single flush → CrateDB's bulk
            // path (dramatically cheaper than N individual UPDATE statements).
            return await batch.ExecuteNonQueryAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Normalises a bulk parameter for Npgsql/CrateDB: <c>null</c> → <see cref="DBNull"/>; a
    /// <see cref="DateTime"/> is pinned to UTC kind so Npgsql sends <c>timestamptz</c> (matching the
    /// archive's <c>timestamp with time zone</c> key columns — an unspecified-kind DateTime would be
    /// sent as <c>timestamp</c> and mis-compare the key predicate).
    /// </summary>
    private static object CoerceBulkParameter(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>
    /// Leases a CrateDB connection whose disposal swallows the benign reset-time <c>ROLLBACK</c>
    /// (see <see cref="DisposeConnectionSafelyAsync"/>). Every CrateDB access opens its connection
    /// through this so a failed statement surfaces its real error instead of being masked by the
    /// dispose-time ROLLBACK.
    /// </summary>
    private async ValueTask<ConnectionLease> LeaseConnectionAsync(string tenantId,
        CancellationToken cancellationToken = default)
        => new ConnectionLease(await CreateConnectionAsync(tenantId, cancellationToken), this);

    /// <summary>
    /// Disposes a CrateDB connection, swallowing the benign <c>ROLLBACK</c> that Npgsql emits while
    /// resetting the connection on close/return-to-pool. CrateDB has no transactions and auto-commits
    /// every statement, but its Postgres-protocol implementation leaves the connection flagged
    /// "in transaction", so Npgsql's <c>Reset()</c> sends a <c>ROLLBACK</c> — which CrateDB rejects
    /// with <c>XX000 "mismatched input 'ROLLBACK'"</c>. The statement itself already committed, so this
    /// dispose-time error is cosmetic. Worse, it MASKS the real error: on a failed statement the
    /// dispose-time ROLLBACK exception replaces the genuine execution exception, so the caller only
    /// ever saw the confusing ROLLBACK. This stalled rollup aggregation indefinitely — a rollup whose
    /// source CrateDB table was missing failed every tick, but the logged error was the masking
    /// ROLLBACK rather than the real <c>42P01 Relation … unknown</c> (voest
    /// <c>energy-measurements-daily</c>). <c>No Reset On Close</c> only suppresses <c>DISCARD ALL</c>,
    /// not this in-transaction rollback. Any other dispose error is rethrown.
    /// </summary>
    private async Task DisposeConnectionSafelyAsync(NpgsqlConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (PostgresException ex) when (IsBenignCrateResetRollback(ex))
        {
            _logger.LogDebug(ex,
                "Ignored benign CrateDB reset-time ROLLBACK on connection dispose (CrateDB has no "
                + "transactions; the statement already auto-committed).");
        }
    }

    /// <summary>
    /// True for the specific "CrateDB rejected the reset-time ROLLBACK" error
    /// (<c>XX000</c> / <c>mismatched input 'ROLLBACK'</c>) so only that benign case is swallowed by
    /// <see cref="DisposeConnectionSafelyAsync"/> — every other failure still surfaces.
    /// </summary>
    private static bool IsBenignCrateResetRollback(PostgresException ex) =>
        string.Equals(ex.SqlState, "XX000", StringComparison.Ordinal)
        && ex.MessageText.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An <see cref="IAsyncDisposable"/> lease over a CrateDB <see cref="NpgsqlConnection"/> whose
    /// disposal routes through <see cref="DisposeConnectionSafelyAsync"/>. Callers use
    /// <c>await using var lease = await LeaseConnectionAsync(...)</c> and access
    /// <see cref="Connection"/>.
    /// </summary>
    private sealed class ConnectionLease(NpgsqlConnection connection, CrateDatabaseClient owner)
        : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync() => await owner.DisposeConnectionSafelyAsync(Connection);
    }

    public async Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId)
    {
        return await _resilience.ExecuteAsync(async _ =>
        {
            var schema = TenantSchema.SchemaName(tenantId);
            await using var lease = await LeaseConnectionAsync(tenantId);
            var connection = lease.Connection;
            var rows = await connection.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema });
            return (IReadOnlyList<string>)rows.AsList();
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dtos.CrateTableStatsRow>> GetTableStatsAsync(
        string tenantId,
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken = default)
    {
        if (tableNames.Count == 0)
        {
            return Array.Empty<Dtos.CrateTableStatsRow>();
        }

        return await _resilience.ExecuteAsync(async _ =>
        {
            var schema = TenantSchema.SchemaName(tenantId);
            await using var lease = await LeaseConnectionAsync(tenantId, cancellationToken);
            var connection = lease.Connection;

            // Two single-round-trip queries against the introspection surface, then merged in
            // memory by table name. We do NOT join sys.shards with sys.health server-side because
            // sys.health rows are per-partition and the join would multiply shard rows on
            // partitioned tables (none of ours are partitioned today, but the join would silently
            // misreport totals the first time someone partitions an archive).
            //
            // primary = true filter ensures replica copies aren't double-counted.
            var shardRows = (await connection.QueryAsync<(string TableName, long NumDocs, long SizeBytes)>(
                @"SELECT table_name AS TableName,
                         COALESCE(SUM(num_docs), 0) AS NumDocs,
                         COALESCE(SUM(size), 0) AS SizeBytes
                  FROM sys.shards
                  WHERE schema_name = @schema
                    AND table_name = ANY(@tables)
                    AND ""primary"" = true
                  GROUP BY table_name",
                new { schema, tables = tableNames.ToArray() })).ToDictionary(r => r.TableName);

            var healthRows = (await connection.QueryAsync<(string TableName, string Health)>(
                @"SELECT table_name AS TableName, health AS Health
                  FROM sys.health
                  WHERE table_schema = @schema
                    AND table_name = ANY(@tables)",
                new { schema, tables = tableNames.ToArray() })).ToDictionary(r => r.TableName);

            // Only return rows for tables we actually saw shards for — callers treat missing
            // entries as "table doesn't exist yet". Health may be missing even when shards exist
            // (rare race during partition rebalance) → null health → Unknown on the caller side.
            var result = new List<Dtos.CrateTableStatsRow>(shardRows.Count);
            foreach (var (tableName, shardData) in shardRows)
            {
                healthRows.TryGetValue(tableName, out var healthData);
                result.Add(new Dtos.CrateTableStatsRow(
                    tableName,
                    healthData.Health,
                    shardData.NumDocs,
                    shardData.SizeBytes));
            }
            return (IReadOnlyList<Dtos.CrateTableStatsRow>)result;
        });
    }

    public async Task DeleteStreamDataDatabaseAsync(string tenantId)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId);
            var connection = lease.Connection;
            var schema = TenantSchema.SchemaName(tenantId);

            // CrateDB has no explicit DROP SCHEMA. Drop every table the project owns inside the
            // tenant schema; CrateDB implicitly drops the schema once its last table is gone.
            // Each archive owns its own table after T17, so we enumerate them via
            // information_schema rather than hardcoding a single legacy table name.
            var tables = (await connection.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema })).ToList();

            foreach (var table in tables)
            {
                await connection.ExecuteAsync(string.Format(Queries.DeleteTableIfExists, $"\"{schema}\".\"{table}\""));
            }
        });
    }

    public async Task ExecuteDdlAsync(string tenantId, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("sql must not be empty.", nameof(sql));
        }

        await _resilience.ExecuteAsync(async _ =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId);
            var connection = lease.Connection;
            await connection.ExecuteAsync(sql);
        });
    }

    private Task<NpgsqlConnection> CreateConnectionAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        return _connectionAccess.CreateConnectionAsync(tenantId, cancellationToken);
    }

    public async Task<HealthCheckResult> CheckHealthAsync()
    {
        try
        {
            // Probe under the resilience pipeline so a single dropped connection (the transient
            // "Exception while reading from stream" class) is retried rather than reported as a hard
            // outage — a blip during a long backfill must not flap the health check. The pooled
            // datasource is never evicted here, so a broken physical connection is simply pruned and
            // the next probe / operation opens a fresh one; the cached datasource self-recovers
            // without a rebuild (AB#4278).
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var lease = await LeaseConnectionAsync("default");
                var connection = lease.Connection;
                await connection.ExecuteAsync("SELECT 1");
            });
            return HealthCheckResult.Healthy("CrateDB is healthy");
        }
        catch (Exception ex)
        {
            _logger.LogError("CrateDB is unhealthy: {Message}", ex.Message);
            return HealthCheckResult.Unhealthy("CrateDB is unhealthy");
        }
    }
}