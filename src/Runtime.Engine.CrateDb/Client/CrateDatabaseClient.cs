using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dapper;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Polly;
using Polly.Timeout;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// Client for interacting with the stream data database.
/// </summary>
internal class CrateDatabaseClient : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient,
    IStreamDataHealthCheckClient, ICrateCopyClient
{
    /// <summary>
    /// AB#4278: maximum rows written under one resilience (Polly) envelope. A bulk insert of millions
    /// of rows (the <c>import_archive_data</c> path) is split into sub-batches of this size, each
    /// opening its own connection and running inside its own per-attempt timeout, so no single attempt
    /// spans more rows than can complete within the timeout — and a transient retry replays only the
    /// failed sub-batch instead of the whole input. A sub-batch is one multi-row statement; see
    /// <see cref="RowsPerStatement"/> for the parameter cap that can make it smaller.
    /// </summary>
    private const int MaxRowsPerInsertBatch = 1000;

    /// <summary>
    /// Upper bound of bind parameters in one statement: the PostgreSQL wire protocol carries the
    /// parameter count as an unsigned 16-bit integer.
    /// </summary>
    private const int MaxParametersPerStatement = 65535;

    private readonly ILogger<CrateDatabaseClient> _logger;
    private readonly ICrateDbConnectionAccess _connectionAccess;
    private readonly StreamDataConfiguration _configuration;
    private readonly ResiliencePipeline _resilience;
    private readonly TimeSpan _interactiveStatementTimeout;
    private readonly TimeSpan _maintenanceStatementTimeout;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="connectionAccess"></param>
    /// <param name="configuration"></param>
    /// <param name="resilienceOptions">Tunables for the timeout/retry/circuit-breaker pipeline (concept §8 T13).</param>
    /// <param name="instanceConfiguration"></param>
    /// <param name="recomputeOptions">Carries the command timeout of maintenance statements.</param>
    public CrateDatabaseClient(ILogger<CrateDatabaseClient> logger, ICrateDbConnectionAccess connectionAccess,
        IOptions<StreamDataConfiguration> configuration,
        IOptions<CrateResilienceOptions> resilienceOptions,
        IOptions<StreamDataInstanceConfiguration> instanceConfiguration,
        IOptions<CrateRecomputeOptions> recomputeOptions)
    {
        _logger = logger;
        _connectionAccess = connectionAccess;
        _configuration = configuration.Value;
        _resilience = CrateResiliencePipeline.Build(resilienceOptions.Value, logger);
        _interactiveStatementTimeout = resilienceOptions.Value.PerAttemptTimeout;
        _maintenanceStatementTimeout = recomputeOptions.Value.CommandTimeout;

        // AB#4946: configure the process-wide schema instance prefix from the root "StreamData"
        // section (env OCTO_STREAMDATA__SCHEMAINSTANCEPREFIX — uniform across services). The
        // client is the singleton every CrateDB path flows through, so this runs before any
        // schema name is computed; SetInstancePrefix is idempotent and fails loud on conflict.
        TenantSchema.SetInstancePrefix(instanceConfiguration.Value.SchemaInstancePrefix);

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

    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query, CancellationToken cancellationToken = default)
        => StreamRawRowsCoreAsync(tenantId, query, commandTimeoutSeconds: null, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query, CrateStatementClass statementClass, CancellationToken cancellationToken)
        => StreamRawRowsCoreAsync(tenantId, query, CommandTimeoutSeconds(statementClass), cancellationToken);

    private int CommandTimeoutSeconds(CrateStatementClass statementClass)
    {
        var budget = statementClass == CrateStatementClass.Maintenance
            ? _maintenanceStatementTimeout
            : _interactiveStatementTimeout;
        return Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds));
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsCoreAsync(
        string tenantId, string query, int? commandTimeoutSeconds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Unbuffered enumeration: Dapper hands back rows one at a time off the open data reader so a
        // multi-GB archive export never materialises the whole table. The connection stays open for
        // the life of the enumeration (held by `await using`), which is why the export sends one
        // statement per time slice rather than one giant cursor — a slice is bounded.
        await using var lease = await LeaseConnectionAsync(tenantId, cancellationToken);
        var connection = lease.Connection;
        var rows = connection.QueryUnbufferedAsync(query, commandTimeout: commandTimeoutSeconds);
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

    public Task<long> GetCountAsync(string tenantId, string countQuery)
        => GetCountAsync(tenantId, countQuery, CancellationToken.None);

    public Task<long> GetCountAsync(string tenantId, string countQuery, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
        => ExecuteWithBudgetAsync(tenantId, countQuery, statementClass, "count", cancellationToken,
            (connection, command) => connection.ExecuteScalarAsync<long>(command));

    public async Task<long> GetCountAsync(string tenantId, string countQuery, CancellationToken cancellationToken)
    {
        return await _resilience.ExecuteAsync(async token =>
        {
            await using var lease = await LeaseConnectionAsync(tenantId, token);
            var connection = lease.Connection;
            return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(countQuery, cancellationToken: token));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<DataPointDto> datapoints, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
    {
        var d = datapoints.ToArray();
        if (d.Length == 0) return [];

        // Per-archive tables have a typed schema (one CrateDB column per archive column with the
        // CK-derived primitive type), so the legacy `unnest(@arr,...)` bulk path no longer fits:
        // it required `NpgsqlDbType.Array|Json` for the dynamic `data` blob, but typed columns
        // need their own native param types and silently drop values when forced through JSON.
        // AB#4773 kept one bind parameter per value; the rows now go out as ONE multi-row
        // `VALUES (…), (…)` statement per sub-batch rather than an NpgsqlBatch of single-row
        // commands. Typing is unchanged, but CrateDB analyses the statement once instead of once
        // per row — which became the dominant cost when the ConflictPrecedence guard grew every
        // statement to several KB: 10,000 rows took 7.4 s as single-row commands and 0.8 s as
        // multi-row statements (local CrateDB 5.10, octogrid EDA replay, 2026-09-24).
        // AB#8622: one statement shape per set of named columns (ArchiveColumnSets).
        return await InsertGroupedAsync(tenantId, qualifiedTable, d, userColumnNames, conflictPrecedence,
            windowed: false, includeOrigin: true,
            fixedParametersPerRow: 4 + ArchiveRowOriginColumns.Names.Count,
            keyOf: x => RowKey(x.RtId, x.CkTypeId, x.Timestamp, null),
            buildSql: (insert, updated, rows) => BuildRawInsertSql(qualifiedTable, insert, conflictPrecedence, rows, updated),
            bind: AddInsertParameters,
            sentRow: x => new ConflictOutcome.SentRow(x.RtId?.ToString(), x.CkTypeId?.ToString(),
                x.Timestamp, null, x.Attributes ?? new Dictionary<string, object?>(), x.Origin));
    }

    /// <summary>
    /// The primary key of an archive row as CrateDB compares it (AB#8679): entity, CK type and the timestamp or window
    /// at millisecond precision, the precision of a CrateDB <c>TIMESTAMP</c>. Two rows less than a millisecond apart
    /// are one row in CrateDB and therefore one key here, like the keys <see cref="ConflictOutcome" /> matches.
    /// </summary>
    internal static string RowKey(object? rtId, object? ckTypeId, DateTime start, DateTime? end)
        => new StoredRowKey(rtId?.ToString() ?? "", ckTypeId?.ToString() ?? "", start, end).Value;

    /// <summary>
    /// Writes the rows of one insert batch, one statement shape per set of named user columns (AB#8622,
    /// <see cref="ArchiveColumnSets" />), each in sub-batches of at most <see cref="RowsPerStatement" /> rows.
    /// Returns the rows the conflict guard did not store, with their positions in <paramref name="rows" />.
    /// Rows CrateDB did not write at all (a guarded statement without their <c>RETURNING</c> row, an unguarded one with
    /// fewer affected rows than sent) are written once more; when rows are still missing the write throws
    /// <see cref="StreamDataWriteIncompleteException" /> (AB#8679).
    /// </summary>
    private async Task<IReadOnlyList<SkippedArchiveRow>> InsertGroupedAsync<T>(
        string tenantId, string qualifiedTable, T[] rows, IReadOnlyList<string> userColumnNames,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence, bool windowed, bool includeOrigin,
        int fixedParametersPerRow, Func<T, string> keyOf,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, int, string> buildSql,
        Action<NpgsqlParameterCollection, T, IReadOnlyList<string>> bind,
        Func<T, ConflictOutcome.SentRow> sentRow)
        where T : RtTypeWithAttributes
    {
        // AB#7598: a guarded upsert reports which rows it kept the stored values for (RETURNING, see
        // ConflictOutcome); an unguarded one stores every row, which its affected-row count confirms (AB#8679).
        var guarded = conflictPrecedence is { Count: > 0 };
        var returning = guarded ? ConflictOutcome.ReturningClause(windowed, conflictPrecedence!, includeOrigin) : "";
        if (guarded)
        {
            // Validates the keys against all columns of the archive (a group's statement names fewer).
            BuildConflictGuard(conflictPrecedence!, userColumnNames);
        }

        var groups = ArchiveColumnSets.Group(rows.Length, i => rows[i].Attributes, i => keyOf(rows[i]),
            userColumnNames, conflictPrecedence);
        var skipped = new List<SkippedArchiveRow>();
        foreach (var group in groups)
        {
            var rowsPerStatement = RowsPerStatement(fixedParametersPerRow + group.InsertColumns.Count);
            var fullSql = group.Indexes.Count >= rowsPerStatement
                ? buildSql(group.InsertColumns, group.UpdatedColumns, rowsPerStatement) + returning
                : null;
            string SqlFor(int rowCount) => rowCount == rowsPerStatement && fullSql is not null
                ? fullSql
                : buildSql(group.InsertColumns, group.UpdatedColumns, rowCount) + returning;

            // AB#4278: chunk into bounded sub-batches so one resilience attempt (and its timeout) never
            // spans more inserts than can complete in the budget; a retry replays only the sub-batch.
            for (var offset = 0; offset < group.Indexes.Count; offset += rowsPerStatement)
            {
                var end = Math.Min(offset + rowsPerStatement, group.Indexes.Count);
                var batch = new int[end - offset];
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i] = group.Indexes[offset + i];
                }

                // One sub-batch as one statement; rows CrateDB did not write are written once more, then the write
                // fails (AB#8679, MissingRowRetry).
                skipped.AddRange(await MissingRowRetry.WriteAsync(batch, guarded, qualifiedTable, ExecuteSubBatchAsync, _logger));
            }

            // Runs one statement over `statementRows` and returns the skipped rows and the missing rows, both as
            // positions in `rows`. For an unguarded statement "missing" is the shortfall of the affected-row count.
            async Task<MissingRowRetry.StatementOutcome> ExecuteSubBatchAsync(
                int[] statementRows)
            {
                var sql = SqlFor(statementRows.Length);
                return await _resilience.ExecuteAsync(async _ =>
                {
                    await using var lease = await LeaseConnectionAsync(tenantId);
                    await using var cmd = new NpgsqlCommand(sql, lease.Connection);
                    foreach (var row in statementRows)
                    {
                        bind(cmd.Parameters, rows[row], group.InsertColumns);
                    }

                    if (!guarded)
                    {
                        // AB#8679: an upsert counts one affected row per row it inserted or updated; fewer means CrateDB
                        // dropped part of the statement. CrateDB counts a key named twice in one statement once per
                        // row sent, also for two points less than a millisecond apart (one stored row), so the count
                        // is compared with the rows sent (ArchiveWriteRepeatedKeyIntegrationTests).
                        var affected = await cmd.ExecuteNonQueryAsync();
                        var shortfall = Math.Max(0, statementRows.Length - affected);
                        return new MissingRowRetry.StatementOutcome([], statementRows.Take(shortfall).ToArray());
                    }

                    var returned = await ReadReturnedRowsAsync(cmd);
                    var sent = statementRows.Select(r => sentRow(rows[r])).ToList();
                    var outcome = ConflictOutcome.Decide(sent, returned, conflictPrecedence!, includeOrigin, windowed);
                    return new MissingRowRetry.StatementOutcome(
                        outcome.Skipped.Select(o => o with { Index = statementRows[o.Index] }).ToList(),
                        outcome.Missing.Select(m => statementRows[m]).ToArray());
                });
            }
        }

        if (groups.Count > 1)
        {
            skipped.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        return skipped;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> ReadStoredRowsAsync(
        string tenantId, string qualifiedTable, bool windowed, IReadOnlyList<StoredRowKey> keys,
        IReadOnlyList<string> columns)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        for (var offset = 0; offset < keys.Count; offset += StoredRowLookup.MaxKeysPerStatement)
        {
            var start = offset;
            var end = Math.Min(offset + StoredRowLookup.MaxKeysPerStatement, keys.Count);
            var sql = StoredRowLookup.BuildSelectSql(qualifiedTable, windowed, columns, end - start);
            var rows = await _resilience.ExecuteAsync(async _ =>
            {
                await using var lease = await LeaseConnectionAsync(tenantId);
                await using var cmd = new NpgsqlCommand(sql, lease.Connection);
                for (var i = start; i < end; i++)
                {
                    var key = keys[i];
                    cmd.Parameters.Add(new NpgsqlParameter
                    {
                        Value = StoredRowLookup.AsUtc(key.Start),
                        NpgsqlDbType = NpgsqlDbType.TimestampTz
                    });
                    if (windowed)
                    {
                        cmd.Parameters.Add(new NpgsqlParameter
                        {
                            Value = StoredRowLookup.AsUtc(key.End!.Value),
                            NpgsqlDbType = NpgsqlDbType.TimestampTz
                        });
                    }

                    cmd.Parameters.Add(new NpgsqlParameter { Value = key.RtId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = key.CkTypeId });
                }

                return await ReadReturnedRowsAsync(cmd);
            });

            foreach (var row in rows)
            {
                if (StoredRowKey.Of(row, windowed) is { } key)
                {
                    result[key] = row;
                }
            }
        }

        return result;
    }

    /// <summary>Reads the rows a statement returned (its <c>RETURNING</c> clause) as column → value.</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadReturnedRowsAsync(NpgsqlCommand cmd)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Rows per multi-row statement for a row of <paramref name="parametersPerRow"/> bind parameters:
    /// <see cref="MaxRowsPerInsertBatch"/>, lowered for a wide archive so the statement stays within
    /// <see cref="MaxParametersPerStatement"/>. More rows per statement would gain nothing measurable —
    /// the per-statement analysis is already spread over a thousand rows.
    /// </summary>
    internal static int RowsPerStatement(int parametersPerRow)
        => Math.Max(1, Math.Min(MaxRowsPerInsertBatch, MaxParametersPerStatement / Math.Max(1, parametersPerRow)));

    /// <summary>
    /// Renders the <c>VALUES</c> list for <paramref name="rowCount"/> rows of
    /// <paramref name="parametersPerRow"/> positional parameters each (<c>($1, $2), ($3, $4)</c>),
    /// appending <paramref name="rowSuffix"/> — a constant such as the rollup generation — inside
    /// every row. The parameters are bound in the same order by the <c>Add*Parameters</c> helpers.
    /// </summary>
    internal static string BuildValuesList(int parametersPerRow, int rowCount, string rowSuffix = "")
    {
        var sb = new StringBuilder(rowCount * parametersPerRow * 7);
        var parameter = 1;
        for (var row = 0; row < rowCount; row++)
        {
            sb.Append(row == 0 ? "(" : ", (");
            for (var column = 0; column < parametersPerRow; column++)
            {
                if (column > 0)
                {
                    sb.Append(", ");
                }

                sb.Append('$').Append(parameter++);
            }

            sb.Append(rowSuffix).Append(')');
        }

        return sb.ToString();
    }

    public Task<IReadOnlyList<SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, DataPointDto datapoint, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
        => InsertDataAsync(tenantId, qualifiedTable, userColumnNames, [datapoint], conflictPrecedence);

    public async Task<IReadOnlyList<SkippedArchiveRow>> InsertTimeRangeDataAsync(
        string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
        IEnumerable<TimeRangeDataPointDto> datapoints, bool generationTracked = false,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
    {
        var d = datapoints.ToArray();
        if (d.Length == 0) return [];

        // Same shape as InsertDataAsync (AB#4773, AB#8622): one bind parameter per value, one multi-row
        // statement per sub-batch and set of named columns.
        var includeOrigin = !generationTracked;
        return await InsertGroupedAsync(tenantId, qualifiedTable, d, userColumnNames, conflictPrecedence,
            windowed: true, includeOrigin,
            fixedParametersPerRow: 5 + (generationTracked ? 0 : ArchiveRowOriginColumns.Names.Count),
            keyOf: x => RowKey(x.RtId, x.CkTypeId, x.From, x.To),
            buildSql: (insert, updated, rows) => BuildTimeRangeInsertSql(qualifiedTable, insert, generationTracked,
                conflictPrecedence, rows, updated),
            bind: (parameters, dto, columns) => AddTimeRangeInsertParameters(parameters, dto, columns, includeOrigin),
            sentRow: x => new ConflictOutcome.SentRow(x.RtId?.ToString(), x.CkTypeId?.ToString(),
                x.From, x.To, x.Attributes ?? new Dictionary<string, object?>(), x.Origin));
    }

    /// <summary>
    /// Binds one time-range row as positional parameters, in the column order of
    /// <see cref="BuildTimeRangeInsertSql"/>.
    /// </summary>
    private static void AddTimeRangeInsertParameters(
        NpgsqlParameterCollection parameters, TimeRangeDataPointDto dto, IReadOnlyList<string> userColumnNames,
        bool includeOrigin)
    {
        parameters.Add(new NpgsqlParameter { Value = dto.From });
        parameters.Add(new NpgsqlParameter { Value = dto.To });
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.RtId?.ToString() ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.CkTypeId?.ToString() ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.RtWellKnownName ?? DBNull.Value });
        if (includeOrigin)
        {
            AddOriginParameters(parameters, dto.Origin, dto.ManualWrittenBy, dto.ManualWrittenAt, dto.ManualReason);
        }

        foreach (var col in userColumnNames)
        {
            var value = dto.Attributes != null && dto.Attributes.TryGetValue(col, out var v) ? v : null;
            parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
    }

    internal static string BuildTimeRangeInsertSql(string qualifiedTable, IReadOnlyList<string> userColumnNames,
        bool generationTracked = false,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null,
        int rowCount = 1,
        IReadOnlyList<string>? updatedColumnNames = null)
    {
        // AB#8622: userColumnNames is the INSERT list of the statement, updatedColumnNames (default: the same)
        // the user columns a winning write replaces. A column the row does not name is in neither list, so a
        // new row stores NULL for it (user columns are nullable without a DEFAULT, AB#6647) and an existing
        // row keeps its stored value.
        //
        // Column order matches the DDL emitted by GenerateCreateWindowedTable: window_start,
        // window_end, rtid, ckTypeId, rtWellKnownName, then user columns. The CONFLICT clause's
        // natural key is the full window (start, end) + entity (rtid, ckTypeId) — a re-delivery
        // of the same window for the same entity upserts; different windows even for the same
        // entity are independent rows. was_updated flips to TRUE on any conflict, regardless of
        // whether the user-column values actually changed (concept §5: "ever updated" signal,
        // not value-change detection).
        //
        // AB#4773: rollup tables key their PK with the additional `generation` column (AB#4184
        // Phase 6), and CrateDB rejects a conflict target that does not name the full PK. For
        // those, generation 0 is written explicitly and joins the conflict target: an archive
        // data import always lands in a freshly provisioned table with an empty generation map,
        // where generation 0 is the active lane. Exported rows of a swept table carry exactly
        // one (the active) generation per window, so collapsing onto 0 is faithful; transient
        // unswept duplicates collapse via the upsert.
        //
        // AB#6647: was_updated is written explicitly as FALSE for a fresh row instead of being left
        // to the column DEFAULT. CrateDB before 5.10.12 stores the new rows of a multi-row upsert
        // that also hits an existing key in the same shard WITHOUT a deterministic DEFAULT on the
        // replica copy: the flag is then missing there, reads as NULL although the column is
        // NOT NULL, and a rollup over it loses the row. A column named in the INSERT list is not
        // affected. No column of this statement may rely on a deterministic DEFAULT
        // (CURRENT_TIMESTAMP defaults are shipped from the primary and are safe).
        var allColumns = new List<string>(6 + ArchiveRowOriginColumns.Names.Count + userColumnNames.Count)
        {
            Constants.WindowStart, Constants.WindowEnd, Constants.RtId, Constants.CkTypeId, Constants.RtWellKnownName
        };
        // AB#5356: a time-range row records its origin; a rollup row is derived and has no origin columns.
        var includeOrigin = !generationTracked;
        if (includeOrigin)
        {
            allColumns.AddRange(ArchiveRowOriginColumns.Names);
        }

        allColumns.AddRange(userColumnNames);

        var columnList = string.Join(", ", allColumns.Select(c => $"\"{c}\"")) + $", \"{Constants.WasUpdated}\"";
        if (generationTracked)
        {
            columnList += $", \"{Constants.Generation}\"";
        }

        var values = BuildValuesList(allColumns.Count, rowCount, generationTracked ? ", FALSE, 0" : ", FALSE");
        var conflictGenerationSuffix = generationTracked ? $", \"{Constants.Generation}\"" : string.Empty;
        var guard = BuildConflictGuard(conflictPrecedence ?? [], userColumnNames, updatedColumnNames);
        var updated = updatedColumnNames ?? userColumnNames;
        var conflictUpdates = BuildConflictUpdates(includeOrigin ? WithOriginColumns(updated) : updated, guard);

        // The rejected branch reads the stored flag through COALESCE: a row stored without the flag
        // (see above) must come out of a losing write as FALSE, not keep its NULL.
        return $"INSERT INTO {qualifiedTable} ({columnList}) VALUES {values} "
             + $"ON CONFLICT (\"{Constants.WindowStart}\", \"{Constants.WindowEnd}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\"{conflictGenerationSuffix}) "
             + $"DO UPDATE SET \"{Constants.RtChangedDateTime}\" = {Guarded("CURRENT_TIMESTAMP", Constants.RtChangedDateTime, guard)}, "
             + $"\"{Constants.WasUpdated}\" = {Guarded("TRUE", Constants.WasUpdated, guard, storedNullReplacement: "FALSE")}"
             + conflictUpdates;
    }

    /// <summary>
    /// Builds the boolean predicate that decides whether a conflicting insert may replace the stored
    /// row, or <c>null</c> when the archive did not opt in (no <c>ConflictPrecedence</c>) and the
    /// historical unconditional last-write-wins update applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The keys are compared <b>lexicographically</b> in declaration order: the first key decides,
    /// and a later one only breaks a tie in every key before it. That matters more than it looks.
    /// Lexicographic ordering is a total order over the DATA, so the surviving value is its maximum —
    /// the same value whichever write lands first (for writes the keys tell apart; two writes equal
    /// in every key are not ordered and the later one replaces the stored row, see the last paragraph). Read as a conjunction instead ("better rank AND
    /// newer"), the result would still depend on arrival order: an older but better-ranked value
    /// would lose to a newer worse-ranked one that happened to arrive first, which is exactly the
    /// defect the opt-in exists to remove.
    /// </para>
    /// <para>
    /// Null handling is deliberate and asymmetric, per key. A stored <c>NULL</c> counts as worse than
    /// anything — rows written before the archive opted in carry no keys, and refusing to ever update
    /// them would freeze the archive's whole history. An incoming <c>NULL</c> counts as worse than a
    /// stored value: a data point that cannot prove it is better must not win. Both null is a tie, so
    /// an archive whose keys are entirely absent behaves exactly as it did before opting in.
    /// </para>
    /// <para>
    /// The last key admits equality so that re-delivering the identical document is idempotent rather
    /// than a no-op that leaves the row looking untouched.
    /// </para>
    /// <para>
    /// AB#8622: a ranking column the statement's rows do not send (not in <paramref name="sentColumnNames" />) keeps
    /// its stored value, and the guard compares the incoming point against that merged row:
    /// <c>COALESCE(EXCLUDED."k", "k")</c> equals the stored key, so the next key decides. An explicit null on a ranking
    /// column is refused before the write (<c>ArchiveRankingColumnValidator</c>), so a sent key is never NULL.
    /// </para>
    /// </remarks>
    /// <param name="precedence">The archive's conflict precedence, physical column names.</param>
    /// <param name="userColumnNames">The INSERT list of the statement; every ranking column must be in it.</param>
    /// <param name="sentColumnNames">
    /// The user columns the rows of the statement send (its replaced columns), or <c>null</c> when every column is sent.
    /// </param>
    internal static string? BuildConflictGuard(
        IReadOnlyList<ArchiveConflictKey> precedence, IReadOnlyList<string> userColumnNames,
        IReadOnlyCollection<string>? sentColumnNames = null)
    {
        if (precedence.Count == 0)
        {
            return null;
        }

        // The guard reads EXCLUDED."k", so every key has to be part of the INSERT column list. A name
        // that is not a user column of this archive would produce SQL that fails at execution time on
        // every single write; refuse to build it instead.
        foreach (var key in precedence)
        {
            if (key.IsOrigin)
            {
                // AB#5356: the origin key ranks manual above automated. LowerWins would mean an automated
                // delivery displaces a manual value, the opposite of what the key exists for.
                // AB#7656: Ignore is the opt-out and is removed before the guard is built.
                if (key.Order != ConflictKeyOrder.HigherWins)
                {
                    throw new InvalidOperationException(
                        $"Archive conflict precedence key '{ArchiveConflictKey.OriginKey}' must be declared with " +
                        $"Order {ConflictKeyOrder.HigherWins} (manual ranks above automated) or " +
                        $"{ConflictKeyOrder.Ignore} (the archive does not rank by origin).");
                }

                continue;
            }

            if (key.Order is not (ConflictKeyOrder.HigherWins or ConflictKeyOrder.LowerWins))
            {
                throw new InvalidOperationException(
                    $"Archive conflict precedence key '{key.Column}' is declared with Order {key.Order}, which only " +
                    $"'{ArchiveConflictKey.OriginKey}' accepts. Use {ConflictKeyOrder.HigherWins} or {ConflictKeyOrder.LowerWins}.");
            }

            if (!userColumnNames.Contains(key.Column, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Archive conflict precedence key '{key.Column}' is not one of the archive's " +
                    $"columns ({string.Join(", ", userColumnNames)}). Declare it in the archive's " +
                    "Columns or remove it from ConflictPrecedence.");
            }
        }

        return BuildPrecedenceLevel(precedence, 0, sentColumnNames);
    }

    /// <summary>
    /// Renders the lexicographic comparison from key <paramref name="index" /> onwards.
    /// </summary>
    private static string BuildPrecedenceLevel(IReadOnlyList<ArchiveConflictKey> keys, int index,
        IReadOnlyCollection<string>? sentColumnNames)
    {
        var key = keys[index];
        var isLast = index == keys.Count - 1;
        if (key.IsOrigin)
        {
            // AB#5356: a manual write always wins and is never displaced by an automated one; two manual
            // writes do not fall through to the following keys (the later one wins, so a wrong manual value
            // is fixed with another manual write); two automated writes are ranked by the following keys.
            var bothAutomated = isLast ? "TRUE" : BuildPrecedenceLevel(keys, index + 1, sentColumnNames);
            return $"({ArchiveRowOriginColumns.IncomingIsManual} OR (NOT {ArchiveRowOriginColumns.StoredIsManual} AND {bothAutomated}))";
        }

        // AB#8622: an unsent ranking column is judged on the merged row, where it holds the stored value.
        var incoming = sentColumnNames is null || sentColumnNames.Contains(key.Column)
            ? $"EXCLUDED.\"{key.Column}\""
            : $"COALESCE(EXCLUDED.\"{key.Column}\", \"{key.Column}\")";
        var better = BuildKeyBetter(key, incoming);
        var equal = BuildKeyEqual(key.Column, incoming);

        // Last key: equality is accepted, which is what makes an identical re-delivery idempotent.
        return isLast
            ? $"({better} OR {equal})"
            : $"({better} OR ({equal} AND {BuildPrecedenceLevel(keys, index + 1, sentColumnNames)}))";
    }

    /// <summary>Incoming value is strictly better than the stored one for this key.</summary>
    private static string BuildKeyBetter(ArchiveConflictKey key, string incoming)
    {
        var c = key.Column;
        var op = key.Order == ConflictKeyOrder.HigherWins ? ">" : "<";
        return $"((\"{c}\" IS NULL AND {incoming} IS NOT NULL) "
             + $"OR (\"{c}\" IS NOT NULL AND {incoming} IS NOT NULL AND {incoming} {op} \"{c}\"))";
    }

    /// <summary>Incoming and stored value are indistinguishable for this key, nulls included.</summary>
    private static string BuildKeyEqual(string c, string incoming)
        => $"((\"{c}\" IS NULL AND {incoming} IS NULL) "
         + $"OR (\"{c}\" IS NOT NULL AND {incoming} IS NOT NULL AND {incoming} = \"{c}\"))";

    /// <summary>
    /// Renders one <c>ON CONFLICT DO UPDATE</c> assignment: the incoming value unguarded, or a
    /// <c>CASE</c> that falls back to the stored column when the guard rejects the write. With
    /// <paramref name="storedNullReplacement"/> the fallback reads the stored column through
    /// <c>COALESCE</c>, for a <c>NOT NULL</c> column whose stored value may be missing (AB#6647).
    /// </summary>
    private static string Guarded(
        string incomingExpression, string storedColumn, string? guard, string? storedNullReplacement = null)
    {
        if (guard is null)
        {
            return incomingExpression;
        }

        var stored = storedNullReplacement is null
            ? $"\"{storedColumn}\""
            : $"COALESCE(\"{storedColumn}\", {storedNullReplacement})";
        return $"CASE WHEN {guard} THEN {incomingExpression} ELSE {stored} END";
    }

    /// <summary>
    /// The columns a winning write replaces: the origin columns first, then the user columns. The origin is
    /// guarded like every value, so a losing write leaves the stored origin alone and an unguarded archive
    /// takes the origin of the last write together with its values.
    /// </summary>
    private static IReadOnlyList<string> WithOriginColumns(IReadOnlyList<string> userColumnNames)
        => [.. ArchiveRowOriginColumns.Names, .. userColumnNames];

    /// <summary>
    /// Renders the replaced-column part of the <c>DO UPDATE SET</c> list (leading comma included), or
    /// an empty string when there are no such columns.
    /// </summary>
    private static string BuildConflictUpdates(IReadOnlyList<string> replacedColumns, string? guard)
        => replacedColumns.Count == 0
            ? string.Empty
            : ", " + string.Join(", ",
                replacedColumns.Select(c => $"\"{c}\" = {Guarded($"EXCLUDED.\"{c}\"", c, guard)}"));

    /// <summary>
    /// Binds one raw row as positional parameters, in the column order of
    /// <see cref="BuildRawInsertSql"/>.
    /// </summary>
    private static void AddInsertParameters(
        NpgsqlParameterCollection parameters, DataPointDto dto, IReadOnlyList<string> userColumnNames)
    {
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.RtId?.ToString() ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.CkTypeId?.ToString() ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter { Value = dto.Timestamp });
        parameters.Add(new NpgsqlParameter { Value = (object?)dto.RtWellKnownName ?? DBNull.Value });
        AddOriginParameters(parameters, dto.Origin, dto.ManualWrittenBy, dto.ManualWrittenAt, dto.ManualReason);
        foreach (var col in userColumnNames)
        {
            var value = dto.Attributes != null && dto.Attributes.TryGetValue(col, out var v) ? v : null;
            parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
    }

    /// <summary>
    /// Binds the platform origin columns of one row (<see cref="ArchiveRowOriginColumns" />), in the order
    /// of <see cref="ArchiveRowOriginColumns.Names" />.
    /// </summary>
    private static void AddOriginParameters(NpgsqlParameterCollection parameters, ArchiveRowOrigin origin,
        string? writtenBy, DateTime? writtenAt, string? reason)
    {
        parameters.Add(new NpgsqlParameter { Value = ArchiveRowOriginColumns.ToStoredValue(origin) });
        // AB#7593: an automated row carries no writer, time or reason, whatever the DTO holds.
        var manual = origin == ArchiveRowOrigin.Manual;
        parameters.Add(new NpgsqlParameter { Value = manual ? (object?)writtenBy ?? DBNull.Value : DBNull.Value });
        parameters.Add(new NpgsqlParameter
        {
            Value = manual && writtenAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : (object)DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.TimestampTz
        });
        parameters.Add(new NpgsqlParameter { Value = manual ? (object?)reason ?? DBNull.Value : DBNull.Value });
    }

    internal static string BuildRawInsertSql(string qualifiedTable, IReadOnlyList<string> userColumnNames,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null, int rowCount = 1,
        IReadOnlyList<string>? updatedColumnNames = null)
    {
        // AB#8622: see BuildTimeRangeInsertSql — the INSERT list and the replaced columns are those the rows name.
        // Column-explicit INSERT against a per-archive table. Columns = standard time-series set
        // (rtId, ckTypeId, timestamp, rtWellKnownName) plus the user-defined columns. Unknown
        // attributes on the data point are dropped: DDL would have rejected them at activation, so
        // the data plane stays permissive against outdated callers.
        var allColumns = new List<string>(4 + ArchiveRowOriginColumns.Names.Count + userColumnNames.Count)
        {
            Constants.RtId, Constants.CkTypeId, Constants.Timestamp, Constants.RtWellKnownName
        };
        allColumns.AddRange(ArchiveRowOriginColumns.Names);
        allColumns.AddRange(userColumnNames);

        var columnList = string.Join(", ", allColumns.Select(c => $"\"{c}\""));
        var values = BuildValuesList(allColumns.Count, rowCount);
        var guard = BuildConflictGuard(conflictPrecedence ?? [], userColumnNames, updatedColumnNames);
        var conflictUpdates = BuildConflictUpdates(WithOriginColumns(updatedColumnNames ?? userColumnNames), guard);

        return $"INSERT INTO {qualifiedTable} ({columnList}) VALUES {values} "
             + $"ON CONFLICT (\"{Constants.Timestamp}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\") "
             + $"DO UPDATE SET \"{Constants.RtChangedDateTime}\" = {Guarded("CURRENT_TIMESTAMP", Constants.RtChangedDateTime, guard)}, "
             + $"\"{Constants.RtCreationDateTime}\" = \"{Constants.RtCreationDateTime}\""
             + conflictUpdates;
    }

    public Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable)
        => RefreshArchiveTableAsync(tenantId, qualifiedTable, CrateStatementClass.Interactive, CancellationToken.None);

    public async Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable,
        CrateStatementClass statementClass, CancellationToken cancellationToken)
    {
        await ExecuteStatementAsync(tenantId, string.Format(Queries.RefreshTable, qualifiedTable),
            statementClass, "refresh", cancellationToken);
    }

    public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync(tenantId, sql, CrateStatementClass.Interactive, cancellationToken);

    public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
        => ExecuteStatementAsync(tenantId, sql, statementClass, "non-query", cancellationToken);

    /// <summary>
    /// The one path of every statement that returns no rows (non-query, refresh, DDL).
    /// </summary>
    private Task<int> ExecuteStatementAsync(string tenantId, string sql, CrateStatementClass statementClass,
        string kind, CancellationToken cancellationToken)
        // Dapper's ExecuteAsync returns the number of affected rows (Npgsql surfaces the
        // CrateDB `INSERT 0 N` / `UPDATE N` row count via NpgsqlCommand.ExecuteNonQuery).
        => ExecuteWithBudgetAsync(tenantId, sql, statementClass, kind, cancellationToken,
            (connection, command) => connection.ExecuteAsync(command));

    /// <summary>
    /// Runs one statement under the time budget of its class. The caller's token and the pipeline's
    /// timeout token both reach the driver, and the command timeout is set explicitly from the
    /// statement class instead of being left to the driver's default. The pipeline's timeout sits a
    /// margin behind the command timeout, so the driver normally ends an overdue statement first and
    /// the server's answer says what happened.
    /// </summary>
    /// <remarks>
    /// The pipeline's timeout is ONE budget for the whole call — the wait for a connection and every
    /// retry included — while the command timeout restarts with each attempt. After a transient
    /// failure and a retry, or a slow connection lease, the pipeline's timeout can therefore end a
    /// statement that has not used up its own command timeout: it cancels the context's token, the
    /// driver cancels the statement, and CrateDB answers <c>XX000: Job killed</c> like for any other
    /// cancellation (the driver only turns <c>57014</c> into a cancellation, so the pipeline never
    /// sees one). That kill is the budget running out as well, and is reported as such.
    /// </remarks>
    private async Task<T> ExecuteWithBudgetAsync<T>(string tenantId, string sql, CrateStatementClass statementClass,
        string kind, CancellationToken cancellationToken, Func<NpgsqlConnection, CommandDefinition, Task<T>> execute)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("sql must not be empty.", nameof(sql));
        }

        var budget = statementClass == CrateStatementClass.Maintenance
            ? _maintenanceStatementTimeout
            : _interactiveStatementTimeout;
        var commandTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds));
        var commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds);
        var started = Stopwatch.GetTimestamp();

        var context = ResilienceContextPool.Shared.Get($"{statementClass}.{kind}", cancellationToken);
        context.Properties.Set(CrateResiliencePipeline.CallTimeoutKey,
            commandTimeout + CrateResiliencePipeline.CallTimeoutMargin);
        try
        {
            return await _resilience.ExecuteAsync(async ctx =>
            {
                await using var lease = await LeaseConnectionAsync(tenantId, ctx.CancellationToken);
                var attemptStarted = Stopwatch.GetTimestamp();
                try
                {
                    return await execute(lease.Connection, new CommandDefinition(
                        sql, commandTimeout: commandTimeoutSeconds, cancellationToken: ctx.CancellationToken));
                }
                catch (PostgresException ex) when (IsJobKilled(ex))
                {
                    // CrateDB answers every cancellation of a statement with XX000 "Job killed",
                    // whoever asked for it. Tell the cases this client causes apart; a kill by
                    // anyone else passes through unchanged.
                    switch (ClassifyJobKilled(
                                cancellationToken.IsCancellationRequested,
                                ctx.CancellationToken.IsCancellationRequested,
                                Stopwatch.GetElapsedTime(attemptStarted),
                                commandTimeout))
                    {
                        case JobKilledCause.CallerCancelled:
                            throw new OperationCanceledException(
                                "The CrateDB statement was cancelled by the caller.", ex, cancellationToken);
                        case JobKilledCause.BudgetElapsed:
                            // Elapsed over the whole call: that is what the budget was spent on.
                            throw new StreamDataStatementTimeoutException(
                                statementClass, commandTimeout, Stopwatch.GetElapsedTime(started), Abbreviate(sql), ex);
                        default:
                            throw;
                    }
                }
            }, context);
        }
        catch (TimeoutRejectedException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The driver could not end the statement inside the margin (or the retries used the
            // budget up), so the pipeline's timeout ended the call.
            throw new StreamDataStatementTimeoutException(
                statementClass, commandTimeout, Stopwatch.GetElapsedTime(started), Abbreviate(sql), ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>Who had a statement killed that CrateDB reports as <c>XX000: Job killed</c>.</summary>
    internal enum JobKilledCause
    {
        /// <summary>The caller's token was cancelled.</summary>
        CallerCancelled,

        /// <summary>The statement's command timeout, or the call-wide timeout of the pipeline, ran out.</summary>
        BudgetElapsed,

        /// <summary>Neither — someone else killed the job; the server's error passes through.</summary>
        Foreign,
    }

    /// <summary>
    /// Decides what a "Job killed" answer means. The caller's cancellation wins. Otherwise it is the
    /// budget when this attempt ran for its command timeout (the driver cancelled it) or when the
    /// pipeline's call-wide timeout has cancelled the context — the second covers an attempt that
    /// started late, after a retry or a slow connection lease, and was ended before its own command
    /// timeout.
    /// </summary>
    internal static JobKilledCause ClassifyJobKilled(
        bool callerCancelled, bool callTimeoutCancelled, TimeSpan attemptElapsed, TimeSpan commandTimeout)
    {
        if (callerCancelled)
        {
            return JobKilledCause.CallerCancelled;
        }

        return attemptElapsed >= commandTimeout || callTimeoutCancelled
            ? JobKilledCause.BudgetElapsed
            : JobKilledCause.Foreign;
    }

    /// <summary>CrateDB's answer to a cancelled statement: <c>XX000</c>, "Job killed. …".</summary>
    private static bool IsJobKilled(PostgresException ex) =>
        string.Equals(ex.SqlState, "XX000", StringComparison.Ordinal)
        && ex.MessageText.Contains("Job killed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The head of a statement for an error message: enough to name the kind and the table.</summary>
    private static string Abbreviate(string sql) => sql.Length <= 160 ? sql : sql[..160] + " …";

    /// <summary>
    /// Runs one statement of the staging copy (AB#5967) once, under a command timeout of its own and
    /// without the resilience pipeline: a retry could repeat a <c>COPY FROM</c> that already loaded rows.
    /// The statement text of a <c>COPY</c> carries the storage credential, and so does the message of the
    /// driver's exception, so no driver exception leaves this method: a failure becomes a
    /// <see cref="StreamDataCopyStatementException"/> with a scrubbed message and no inner exception, an
    /// overdue statement a <see cref="StreamDataStatementTimeoutException"/> with the redacted text, and a
    /// cancelled token an <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<CrateStatementResult> ExecuteStatementAsync(string tenantId, CopyStatement statement,
        TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(commandTimeout.TotalSeconds));
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var started = Stopwatch.GetTimestamp();

        // The driver ends an overdue statement at its command timeout; this token ends a call that does not
        // get that far (a connection that never opens), a margin behind it.
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callTimeout.CancelAfter(timeout + CrateResiliencePipeline.CallTimeoutMargin);

        try
        {
            await using var lease = await LeaseConnectionAsync(tenantId, callTimeout.Token);
            await using var command = new NpgsqlCommand(statement.Reveal(), lease.Connection)
            {
                CommandTimeout = timeoutSeconds
            };

            if (!statement.ReturnsRows)
            {
                var affected = await command.ExecuteNonQueryAsync(callTimeout.Token);
                return new CrateStatementResult(affected, []);
            }

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await using var reader = await command.ExecuteReaderAsync(callTimeout.Token);
            while (await reader.ReadAsync(callTimeout.Token))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = ReadCell(reader, i);
                }

                rows.Add(row);
            }

            return new CrateStatementResult(Math.Max(0, reader.RecordsAffected), rows);
        }
        catch (Exception ex) when (ex is not StreamDataCopyStatementException and not StreamDataStatementTimeoutException)
        {
            // Nothing below may let `ex` escape: its message can hold the statement text.
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The CrateDB statement was cancelled by the caller.", cancellationToken);
            }

            var isJobKilled = ex is PostgresException pg && IsJobKilled(pg);
            var isTimeout = ex is OperationCanceledException || ex is TimeoutException ||
                            ex is NpgsqlException { InnerException: TimeoutException } ||
                            (isJobKilled && ClassifyJobKilled(false, callTimeout.IsCancellationRequested, elapsed, timeout)
                                == JobKilledCause.BudgetElapsed);
            if (isTimeout)
            {
                throw new StreamDataStatementTimeoutException(
                    CrateStatementClass.Maintenance, timeout, elapsed, AbbreviateCopy(statement.Redacted), innerException: null);
            }

            var (sqlState, reason) = ex is PostgresException postgres
                ? (postgres.SqlState, postgres.MessageText + (string.IsNullOrEmpty(postgres.Detail) ? string.Empty : " " + postgres.Detail))
                : (null, ex.Message);
            throw new StreamDataCopyStatementException(statement.Redacted, sqlState, statement.Scrub(reason));
        }
    }

    /// <summary>The head of a redacted staging statement for an error message; long enough to name the location.</summary>
    private static string AbbreviateCopy(string redacted) => redacted.Length <= 400 ? redacted : redacted[..400] + " …";

    /// <summary>One cell of a result row; a type the driver cannot materialise reads as <c>null</c>.</summary>
    private static object? ReadCell(NpgsqlDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            return null;
        }
    }

    public Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default)
        => ExecuteBulkCoreAsync(tenantId, parameterizedSql, argumentSets, commandTimeoutSeconds: null, cancellationToken);

    /// <inheritdoc />
    public Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
        => ExecuteBulkCoreAsync(tenantId, parameterizedSql, argumentSets, CommandTimeoutSeconds(statementClass), cancellationToken);

    private async Task ExecuteBulkCoreAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, int? commandTimeoutSeconds, CancellationToken cancellationToken)
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
            if (commandTimeoutSeconds is { } seconds)
            {
                batch.Timeout = seconds;
            }

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
    /// and still hands the connection back to the pool (see <see cref="DisposeConnectionSafelyAsync"/>).
    /// Every CrateDB access opens its connection through this so a failed statement surfaces its real
    /// error instead of being masked by the dispose-time ROLLBACK.
    /// </summary>
    /// <exception cref="StreamDataConnectionPoolExhaustedException">
    /// No connection of the tenant's pool became free within the pool's wait time (AB#8266). Not an
    /// <see cref="NpgsqlException"/>, so the resilience pipeline neither retries it nor counts it.
    /// </exception>
    private async ValueTask<ConnectionLease> LeaseConnectionAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return new ConnectionLease(await CreateConnectionAsync(tenantId, cancellationToken), this);
        }
        catch (NpgsqlException ex) when (IsConnectionPoolExhausted(ex))
        {
            _logger.LogError(ex,
                "CrateDB connection pool of tenant '{TenantId}' is exhausted; the statement was not sent.",
                tenantId);
            throw new StreamDataConnectionPoolExhaustedException(tenantId, ex);
        }
    }

    /// <summary>
    /// True for the driver's "connection pool has been exhausted" error: a plain
    /// <see cref="NpgsqlException"/> with an inner <see cref="TimeoutException"/> raised while waiting for a
    /// pool slot. A timeout while connecting to the server has the same shape but another text and stays a
    /// transient connection failure.
    /// </summary>
    internal static bool IsConnectionPoolExhausted(Exception ex) =>
        ex is NpgsqlException and not PostgresException
        && ex.InnerException is TimeoutException
        && ex.Message.Contains("connection pool has been exhausted", StringComparison.OrdinalIgnoreCase);

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
    /// <para>
    /// Swallowing the error is not enough (AB#8266): the ROLLBACK exception leaves Npgsql's close
    /// <b>before</b> the connector is handed back to the pool, so the connection stays open and its
    /// pool slot is lost for the life of the process. CrateDB reports the failed-transaction state
    /// after every failed statement, so every failed statement (a rollup over a missing table, every
    /// tick) cost one of the tenant's <c>MaxPoolSize</c> slots until the pool was exhausted. The
    /// connection is therefore brought back to idle with a <c>SELECT 1</c> (CrateDB then reports the
    /// idle state, so the reset sends no ROLLBACK) and disposed again, which returns it to the pool.
    /// If that fails too, the physical connection is discarded so the slot is freed
    /// (<see cref="DiscardConnection"/>). Npgsql offers no setting that skips the rollback on reset
    /// (<c>No Reset On Close</c> covers only <c>DISCARD ALL</c>), and turning pooling off would cost a
    /// new connection per statement again (AB#4278).
    /// </para>
    /// </summary>
    private async Task DisposeConnectionSafelyAsync(NpgsqlConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (PostgresException ex) when (IsBenignCrateResetRollback(ex))
        {
            if (connection.State == ConnectionState.Closed)
            {
                _logger.LogDebug(ex,
                    "Ignored benign CrateDB reset-time ROLLBACK on connection dispose (CrateDB has no "
                    + "transactions; the statement already auto-committed).");
                return;
            }

            await ReturnConnectionAfterRejectedResetAsync(connection, ex);
        }
    }

    /// <summary>
    /// The connection is still open after the rejected reset ROLLBACK and has not been returned to the
    /// pool. A trivial statement puts it back into the idle state, after which the next dispose returns
    /// it; failing that, it is discarded. Never throws: the caller is a dispose.
    /// </summary>
    private async Task ReturnConnectionAfterRejectedResetAsync(NpgsqlConnection connection, PostgresException resetError)
    {
        try
        {
            await using (var command = new NpgsqlCommand("SELECT 1", connection))
            {
                command.CommandTimeout = ResetProbeTimeoutSeconds;
                await command.ExecuteScalarAsync();
            }

            await connection.DisposeAsync();
            if (connection.State == ConnectionState.Closed)
            {
                _logger.LogDebug(resetError,
                    "CrateDB rejected the reset-time ROLLBACK; the connection was brought back to idle and "
                    + "returned to the pool.");
                return;
            }
        }
        catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
        {
            _logger.LogWarning(recoveryError,
                "CrateDB connection could not be brought back to idle after the rejected reset-time ROLLBACK.");
        }

        var discarded = DiscardConnection(connection, resetError);
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception disposeError) when (disposeError is not OutOfMemoryException)
        {
            _logger.LogDebug(disposeError, "Dispose of a discarded CrateDB connection failed.");
        }

        if (discarded)
        {
            _logger.LogWarning(resetError,
                "CrateDB connection was discarded after the rejected reset-time ROLLBACK; its pool slot is free again.");
        }
        else
        {
            _logger.LogError(resetError,
                "CrateDB connection could neither be returned to nor removed from the pool after the rejected "
                + "reset-time ROLLBACK; one pool slot is lost until the process restarts.");
        }
    }

    /// <summary>Command timeout of the <c>SELECT 1</c> that brings a connection back to idle.</summary>
    private const int ResetProbeTimeoutSeconds = 5;

    /// <summary>
    /// The driver's internal <c>NpgsqlConnection.Connector</c> and <c>NpgsqlConnector.Break(Exception, bool)</c>.
    /// Breaking the connector is what the driver itself does with an unusable connection: it closes the
    /// socket and hands the connector back to the pool, which drops it and frees the slot. There is no public
    /// API for it. Resolved once; <c>null</c> when a driver version renamed them (pinned by a unit test, so a
    /// driver update that breaks this is seen in the build, not in production).
    /// </summary>
    private static readonly (PropertyInfo Connector, MethodInfo Break)? ConnectorBreak = ResolveConnectorBreak();

    internal static bool CanDiscardConnections => ConnectorBreak is not null;

    private static (PropertyInfo, MethodInfo)? ResolveConnectorBreak()
    {
        var connectorProperty = typeof(NpgsqlConnection).GetProperty(
            "Connector", BindingFlags.Instance | BindingFlags.NonPublic);
        var breakMethod = connectorProperty?.PropertyType.GetMethod(
            "Break", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Exception), typeof(bool)]);
        return connectorProperty is null || breakMethod is null ? null : (connectorProperty, breakMethod);
    }

    /// <summary>Breaks the physical connection behind <paramref name="connection"/>; true when it was broken.</summary>
    internal static bool DiscardConnection(NpgsqlConnection connection, Exception reason)
    {
        if (ConnectorBreak is not { } accessor)
        {
            return false;
        }

        try
        {
            var connector = accessor.Connector.GetValue(connection);
            if (connector is null)
            {
                return connection.State is ConnectionState.Closed or ConnectionState.Broken;
            }

            accessor.Break.Invoke(connector, [reason, false]);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for the specific "CrateDB rejected the reset-time ROLLBACK" error so only that benign case is
    /// swallowed by <see cref="DisposeConnectionSafelyAsync"/> — every other failure still surfaces.
    /// CrateDB 5.x answers it with <c>XX000</c> / <c>mismatched input 'ROLLBACK'</c>; 6.x with the syntax
    /// error <c>42601</c> / <c>extraneous input 'ROLLBACK'</c>. A <c>42601</c> is only accepted when the
    /// message names the rejected <c>ROLLBACK</c> token, not for any other syntax error.
    /// </summary>
    private static bool IsBenignCrateResetRollback(PostgresException ex) =>
        IsBenignCrateResetRollback(ex.SqlState, ex.MessageText);

    internal static bool IsBenignCrateResetRollback(string? sqlState, string? messageText)
    {
        if (messageText is null)
        {
            return false;
        }

        return (string.Equals(sqlState, "XX000", StringComparison.Ordinal)
                && messageText.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase))
               || (string.Equals(sqlState, PostgresErrorCodes.SyntaxError, StringComparison.Ordinal)
                   && messageText.Contains("input 'ROLLBACK'", StringComparison.OrdinalIgnoreCase));
    }

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
            var shardRows = await ReadShardStatsAsync(connection, schema, tableNames);

            // AB#8735: num_docs only counts the rows of the last refresh, and CrateDB skips the periodic
            // refresh of a shard nobody searched for 30 s ("search idle"). An archive that is written but
            // not read — an import into a Disabled archive, an ingest nobody queries — reported 0 (or an
            // old count) until someone searched it; a search only wakes the shard, the rows show up at the
            // next periodic refresh. So the existing tables are refreshed first and read again. A refresh
            // of a shard without unrefreshed writes changes nothing.
            if (shardRows.Count > 0 && await TryRefreshTablesAsync(connection, schema, shardRows.Keys.ToList()))
            {
                shardRows = await ReadShardStatsAsync(connection, schema, tableNames);
            }

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

    private static async Task<Dictionary<string, (string TableName, long NumDocs, long SizeBytes)>> ReadShardStatsAsync(
        NpgsqlConnection connection, string schema, IReadOnlyList<string> tableNames)
    {
        // primary = true filter ensures replica copies aren't double-counted.
        return (await connection.QueryAsync<(string TableName, long NumDocs, long SizeBytes)>(
            @"SELECT table_name AS TableName,
                     COALESCE(SUM(num_docs), 0) AS NumDocs,
                     COALESCE(SUM(size), 0) AS SizeBytes
              FROM sys.shards
              WHERE schema_name = @schema
                AND table_name = ANY(@tables)
                AND ""primary"" = true
              GROUP BY table_name",
            new { schema, tables = tableNames.ToArray() })).ToDictionary(r => r.TableName);
    }

    /// <summary>
    /// Refreshes <paramref name="tableNames"/> in one statement (AB#8735), so <c>num_docs</c> counts every row
    /// written before. Returns <c>false</c> when there is nothing to refresh or a table disappeared between the
    /// shard read and the refresh (dropped by a concurrent delete); the caller then reports the shard statistics
    /// as they are.
    /// </summary>
    private async Task<bool> TryRefreshTablesAsync(NpgsqlConnection connection, string schema,
        IReadOnlyList<string> tableNames)
    {
        var sql = BuildRefreshTablesSql(schema, tableNames);
        if (sql is null)
        {
            return false;
        }

        try
        {
            await connection.ExecuteAsync(sql);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(ex,
                "Refreshing {TableCount} archive tables in schema {Schema} failed because a table was dropped " +
                "meanwhile; the record counts of this call may lag behind",
                tableNames.Count, schema);
            return false;
        }
    }

    /// <summary>
    /// <c>REFRESH TABLE "schema"."archive_a", "schema"."archive_b"</c>, or <c>null</c> for no table. The names come
    /// from <c>sys.shards</c> of the tenant schema; a name that is not a plain lower-case identifier is left out.
    /// </summary>
    internal static string? BuildRefreshTablesSql(string schema, IReadOnlyList<string> tableNames)
    {
        var tables = tableNames
            .Where(IsPlainIdentifier)
            .Select(t => $"\"{schema}\".\"{t}\"")
            .ToList();
        return tables.Count == 0 ? null : "REFRESH TABLE " + string.Join(", ", tables);

        static bool IsPlainIdentifier(string name) =>
            name.Length > 0 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
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

    public Task ExecuteDdlAsync(string tenantId, string sql)
        => ExecuteDdlAsync(tenantId, sql, CrateStatementClass.Interactive, CancellationToken.None);

    public Task ExecuteDdlAsync(string tenantId, string sql, CancellationToken cancellationToken)
        => ExecuteDdlAsync(tenantId, sql, CrateStatementClass.Maintenance, cancellationToken);

    private async Task ExecuteDdlAsync(string tenantId, string sql, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
    {
        await ExecuteStatementAsync(tenantId, sql, statementClass, "ddl", cancellationToken);
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