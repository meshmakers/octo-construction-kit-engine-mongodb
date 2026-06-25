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
        await using var connection = await CreateConnectionAsync(tenantId);

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
        await using var connection = await CreateConnectionAsync(tenantId, cancellationToken);
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
            await using var connection = await CreateConnectionAsync(tenantId);
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
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = await CreateConnectionAsync(tenantId);
            var sql = BuildSingleRowInsertSql(qualifiedTable, userColumnNames);
            foreach (var dto in d)
            {
                await ExecuteSingleInsertAsync(connection, sql, dto, userColumnNames);
            }
        });
    }

    public async Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, DataPointDto datapoint)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = await CreateConnectionAsync(tenantId);
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
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = await CreateConnectionAsync(tenantId);
            var sql = BuildTimeRangeInsertSql(qualifiedTable, userColumnNames);
            foreach (var dto in d)
            {
                await ExecuteSingleTimeRangeInsertAsync(connection, sql, dto, userColumnNames);
            }
        });
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
            await using var connection = await CreateConnectionAsync(tenantId);
            await connection.ExecuteAsync(string.Format(Queries.RefreshTable, qualifiedTable));
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
            await using var connection = await CreateConnectionAsync(tenantId, cancellationToken);
            // Dapper's ExecuteAsync returns the number of affected rows (Npgsql surfaces the
            // CrateDB `INSERT 0 N` / `UPDATE N` row count via NpgsqlCommand.ExecuteNonQuery).
            return await connection.ExecuteAsync(sql);
        });
    }

    public async Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId)
    {
        return await _resilience.ExecuteAsync(async _ =>
        {
            var schema = TenantSchema.SchemaName(tenantId);
            await using var connection = await CreateConnectionAsync(tenantId);
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
            await using var connection = await CreateConnectionAsync(tenantId, cancellationToken);

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
            await using var connection = await CreateConnectionAsync(tenantId);
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
            await using var connection = await CreateConnectionAsync(tenantId);
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
            await using var connection = await CreateConnectionAsync("default");
            await connection.ExecuteAsync("SELECT 1");
            return HealthCheckResult.Healthy("CrateDB is healthy");
        }
        catch (Exception ex)
        {
            _logger.LogError("CrateDB is unhealthy: {Message}", ex.Message);
            return HealthCheckResult.Unhealthy("CrateDB is unhealthy");
        }
    }
}