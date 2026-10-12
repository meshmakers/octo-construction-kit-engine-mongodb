using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// An in-memory stand-in for the three CrateDB tables a rollup recompute works on — the live rollup
/// table, its staging table and its pointer table. It understands exactly the statements
/// <see cref="CrateDbArchiveRecomputeExecutor"/> sends, applies them to rows held in lists and
/// records every call in order, so a test can assert both the statements and what they left behind.
/// Unlike CrateDB it has no refresh delay: a refresh is recorded and changes nothing.
/// </summary>
internal sealed class RecomputeCrateSimulator : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient
{
    private static readonly Regex RangeRegex = new(
        "\"window_start\" >= (?<from>-?\\d+) AND \"window_start\" < (?<to>-?\\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex GenerationRegex = new(
        "\"generation\" (?<op>!=|=|<) (?<g>\\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex ScopeRegex = new("\"rtid\" = '(?<scope>[^']*)'", RegexOptions.CultureInvariant);

    private static readonly Regex CopyGenerationRegex = new(", (?<g>\\d+) FROM ", RegexOptions.CultureInvariant);

    private static readonly Regex PointerValuesRegex = new(
        "VALUES \\((?<from>-?\\d+), (?<to>-?\\d+), '(?<scope>[^']*)', (?<g>\\d+)\\)", RegexOptions.CultureInvariant);

    private static readonly Regex ContainedRegex = new(
        "\"range_start\" >= (?<from>-?\\d+) AND \"range_end\" <= (?<to>-?\\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex PointerScopeRegex = new(
        "\"rtid_scope\" = '(?<scope>[^']*)'", RegexOptions.CultureInvariant);

    private static readonly Regex StagedBucketRegex = new(
        "\"window_start\" = (?<at>-?\\d+);", RegexOptions.CultureInvariant);

    private readonly string _liveTable;
    private readonly string _stagingTable;
    private readonly string _genMapTable;

    public RecomputeCrateSimulator(string tenantId, string rollupRtId)
    {
        _liveTable = TenantSchema.QualifiedArchiveTable(tenantId, rollupRtId);
        _stagingTable = RollupRecomputeSqlBuilder.StagingTable(tenantId, rollupRtId);
        _genMapTable = GenerationMapSqlBuilder.GenMapTable(tenantId, rollupRtId);
    }

    /// <summary>A row of the live or the staging table, reduced to what the recompute keys on.</summary>
    public sealed record Row(long WindowStartMs, long Generation, string RtId);

    /// <summary>One recorded call: what kind of statement it was and its SQL (or table).</summary>
    public sealed record Call(string Kind, string Text, long Rows = 0);

    public List<Row> Live { get; } = new();

    public List<Row> Staging { get; } = new();

    public List<GenerationRange> Pointers { get; } = new();

    public List<Call> Calls { get; } = new();

    public bool StagingExists { get; set; }

    /// <summary>
    /// The rows the n-th aggregation statement of a run stages (n counts from 0 and restarts never —
    /// it counts the aggregation statements of the simulator's lifetime).
    /// </summary>
    public Func<int, IEnumerable<Row>> StageForAggregate { get; set; } = _ => Array.Empty<Row>();

    /// <summary>Called before a statement is applied; may throw to make the statement fail.</summary>
    public Action<Call, CancellationToken>? BeforeStatement { get; set; }

    private int _aggregates;

    public IEnumerable<string> Kinds => Calls.Select(c => c.Kind);

    // ---- IStreamDataDatabaseClient ----

    public Task<long> GetCountAsync(string tenantId, string countQuery)
        => GetCountAsync(tenantId, countQuery, CancellationToken.None);

    public Task<long> GetCountAsync(string tenantId, string countQuery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string kind;
        long rows;
        if (countQuery.Contains($"FROM {_stagingTable}", StringComparison.Ordinal))
        {
            EnsureStagingExists();
            var ranged = RangeRegex.IsMatch(countQuery);
            kind = ranged ? "count:staged" : "count:all-staged";
            rows = ranged ? Select(Staging, countQuery).Count() : Staging.Count;
        }
        else if (countQuery.Contains($"FROM {_liveTable}", StringComparison.Ordinal))
        {
            kind = "count:" + GenerationKind(countQuery);
            rows = Select(Live, countQuery).Count();
        }
        else if (countQuery.Contains($"FROM {_genMapTable}", StringComparison.Ordinal))
        {
            kind = "count:pointer";
            rows = Pointers.Count(p => MatchesPointer(p, countQuery));
        }
        else
        {
            throw new NotSupportedException($"Unexpected count: {countQuery}");
        }

        var call = new Call(kind, countQuery, rows);
        BeforeStatement?.Invoke(call, cancellationToken);
        Calls.Add(call);
        return Task.FromResult(rows);
    }

    public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The recompute sends every statement with its statement class.");

    public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (statementClass != CrateStatementClass.Maintenance)
        {
            throw new InvalidOperationException($"Recompute statement sent as {statementClass}: {sql}");
        }

        var kind = Classify(sql);
        var call = new Call(kind, sql);
        BeforeStatement?.Invoke(call, cancellationToken);
        Calls.Add(call);
        return Task.FromResult(Apply(kind, sql));
    }

    public Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable)
        => throw new NotSupportedException("The recompute refreshes with its statement class and token.");

    public Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable, CrateStatementClass statementClass,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (qualifiedTable == _stagingTable)
        {
            EnsureStagingExists();
        }

        var call = new Call("refresh:" + TableName(qualifiedTable), qualifiedTable);
        BeforeStatement?.Invoke(call, cancellationToken);
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        if (query.Contains("AS next", StringComparison.Ordinal))
        {
            Calls.Add(new Call("read:next-generation", query));
            yield return new Dictionary<string, object?>
            {
                ["next"] = Pointers.Count == 0 ? 1L : Pointers.Max(p => p.Generation) + 1,
            };
            yield break;
        }

        Calls.Add(new Call("read:pointers", query));
        foreach (var pointer in Pointers.ToList())
        {
            yield return new Dictionary<string, object?>
            {
                ["range_start"] = pointer.StartMs,
                ["range_end"] = pointer.EndMs,
                ["rtid_scope"] = pointer.Scope,
                ["generation"] = pointer.Generation,
            };
        }
    }

    public Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
        DataPointDto datapoint, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
        => throw new NotSupportedException();

    public Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
        IEnumerable<DataPointDto> datapoints, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
        => throw new NotSupportedException();

    public Task InsertTimeRangeDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
        IEnumerable<TimeRangeDataPointDto> datapoints, bool generationTracked = false,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null)
        => throw new NotSupportedException();

    public Task<List<DataPointDto>> GetDataAsync(string tenantId, string query) => throw new NotSupportedException();

    public Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CrateTableStatsRow>> GetTableStatsAsync(string tenantId,
        IReadOnlyList<string> tableNames, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    // ---- IStreamDataDatabaseManagementClient ----

    public Task DeleteStreamDataDatabaseAsync(string tenantId) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId) => throw new NotSupportedException();

    public Task ExecuteDdlAsync(string tenantId, string sql)
        => throw new NotSupportedException("The recompute sends its DDL with a token.");

    public Task ExecuteDdlAsync(string tenantId, string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string kind;
        if (sql.StartsWith($"DROP TABLE IF EXISTS {_stagingTable}", StringComparison.Ordinal))
        {
            kind = "ddl:drop-staging";
        }
        else if (sql.StartsWith($"CREATE TABLE IF NOT EXISTS {_stagingTable}", StringComparison.Ordinal))
        {
            kind = "ddl:create-staging";
        }
        else if (sql.StartsWith($"CREATE TABLE IF NOT EXISTS {_genMapTable}", StringComparison.Ordinal))
        {
            kind = "ddl:create-genmap";
        }
        else
        {
            throw new NotSupportedException($"Unexpected DDL: {sql}");
        }

        var call = new Call(kind, sql);
        BeforeStatement?.Invoke(call, cancellationToken);
        Calls.Add(call);

        if (kind == "ddl:drop-staging")
        {
            Staging.Clear();
            StagingExists = false;
        }
        else if (kind == "ddl:create-staging")
        {
            // CREATE TABLE IF NOT EXISTS: an existing table keeps its rows.
            StagingExists = true;
        }

        return Task.CompletedTask;
    }

    // ---- statement model ----

    private void EnsureStagingExists()
    {
        if (!StagingExists)
        {
            // CrateDB's wording for a missing table.
            throw new InvalidOperationException($"RelationUnknown[Relation '{_stagingTable}' unknown]");
        }
    }

    private string Classify(string sql)
    {
        if (sql.StartsWith($"INSERT INTO {_liveTable}", StringComparison.Ordinal))
        {
            return "copy";
        }

        if (sql.StartsWith($"INSERT INTO {_stagingTable}", StringComparison.Ordinal))
        {
            return "aggregate";
        }

        if (sql.StartsWith($"INSERT INTO {_genMapTable}", StringComparison.Ordinal))
        {
            return "flip";
        }

        if (sql.StartsWith($"DELETE FROM {_genMapTable}", StringComparison.Ordinal))
        {
            return "delete:contained-pointers";
        }

        if (sql.StartsWith($"DELETE FROM {_stagingTable}", StringComparison.Ordinal))
        {
            return StagedBucketRegex.IsMatch(sql) ? "delete:staged-bucket" : "delete:all-staged";
        }

        if (sql.StartsWith($"DELETE FROM {_liveTable}", StringComparison.Ordinal))
        {
            return "delete:" + GenerationKind(sql);
        }

        throw new NotSupportedException($"Unexpected statement: {sql}");
    }

    private int Apply(string kind, string sql)
    {
        switch (kind)
        {
            case "aggregate":
            {
                EnsureStagingExists();

                var staged = StageForAggregate(_aggregates++).ToList();
                Staging.AddRange(staged);
                return staged.Count;
            }
            case "copy":
            {
                EnsureStagingExists();
                var generation = long.Parse(CopyGenerationRegex.Match(sql).Groups["g"].Value, CultureInfo.InvariantCulture);
                var rows = (RangeRegex.IsMatch(sql) ? Select(Staging, sql) : Staging).ToList();
                foreach (var row in rows)
                {
                    // Like CrateDB's INSERT … SELECT without ON CONFLICT: a row whose key exists is skipped.
                    if (!Live.Any(l => l.WindowStartMs == row.WindowStartMs && l.RtId == row.RtId && l.Generation == generation))
                    {
                        Live.Add(row with { Generation = generation });
                    }
                }

                return rows.Count;
            }
            case "flip":
            {
                var m = PointerValuesRegex.Match(sql);
                var from = long.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture);
                var to = long.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture);
                var scope = m.Groups["scope"].Value;
                Pointers.RemoveAll(p => p.StartMs == from && p.EndMs == to && p.Scope == scope);
                Pointers.Add(new GenerationRange(from, to, scope,
                    long.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture)));
                return 1;
            }
            case "delete:contained-pointers":
            {
                var m = ContainedRegex.Match(sql);
                var from = long.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture);
                var to = long.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture);
                var scopes = PointerScopeRegex.Matches(sql);
                // The last scope literal belongs to the "other than the entry just flipped" clause.
                var flippedScope = scopes[^1].Groups["scope"].Value;
                var onlyScope = scopes.Count > 1 ? scopes[0].Groups["scope"].Value : null;
                return Pointers.RemoveAll(p =>
                    p.StartMs >= from && p.EndMs <= to
                    && (onlyScope is null || p.Scope == onlyScope)
                    && !(p.StartMs == from && p.EndMs == to && p.Scope == flippedScope));
            }
            case "delete:staged-bucket":
            {
                var at = long.Parse(StagedBucketRegex.Match(sql).Groups["at"].Value, CultureInfo.InvariantCulture);
                return Staging.RemoveAll(r => r.WindowStartMs == at);
            }
            case "delete:all-staged":
            {
                EnsureStagingExists();
                var count = Staging.Count;
                Staging.Clear();
                return count;
            }
            default:
            {
                var doomed = Select(Live, sql).ToHashSet();
                return Live.RemoveAll(doomed.Contains);
            }
        }
    }

    private static IEnumerable<Row> Select(IEnumerable<Row> rows, string sql)
    {
        var range = RangeRegex.Match(sql);
        if (!range.Success)
        {
            throw new InvalidOperationException($"Statement without a window_start range: {sql}");
        }

        var from = long.Parse(range.Groups["from"].Value, CultureInfo.InvariantCulture);
        var to = long.Parse(range.Groups["to"].Value, CultureInfo.InvariantCulture);
        var selected = rows.Where(r => r.WindowStartMs >= from && r.WindowStartMs < to);

        var generation = GenerationRegex.Match(sql);
        if (generation.Success)
        {
            var g = long.Parse(generation.Groups["g"].Value, CultureInfo.InvariantCulture);
            selected = generation.Groups["op"].Value switch
            {
                "!=" => selected.Where(r => r.Generation != g),
                "=" => selected.Where(r => r.Generation == g),
                _ => selected.Where(r => r.Generation < g),
            };
        }

        var scope = ScopeRegex.Match(sql);
        if (scope.Success)
        {
            selected = selected.Where(r => r.RtId == scope.Groups["scope"].Value);
        }

        return selected;
    }

    private static string GenerationKind(string sql) => GenerationRegex.Match(sql).Groups["op"].Value switch
    {
        "!=" => "superseded",
        "=" => "generation",
        "<" => "below",
        _ => throw new NotSupportedException($"Statement on the live table without a generation predicate: {sql}"),
    };

    private static bool MatchesPointer(GenerationRange pointer, string sql)
    {
        var m = Regex.Match(sql,
            "\"range_start\" = (?<from>-?\\d+) AND \"range_end\" = (?<to>-?\\d+) AND \"rtid_scope\" = '(?<scope>[^']*)' AND \"generation\" = (?<g>\\d+)",
            RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            throw new NotSupportedException($"Unexpected pointer count: {sql}");
        }

        return pointer.StartMs == long.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture)
               && pointer.EndMs == long.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture)
               && pointer.Scope == m.Groups["scope"].Value
               && pointer.Generation == long.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture);
    }

    private string TableName(string qualifiedTable) =>
        qualifiedTable == _liveTable ? "live"
        : qualifiedTable == _stagingTable ? "staging"
        : qualifiedTable == _genMapTable ? "genmap"
        : throw new NotSupportedException($"Unexpected table: {qualifiedTable}");

    /// <summary>The <c>[from, to)</c> bounds of a recorded statement, in epoch milliseconds.</summary>
    public static (long From, long To) Bounds(Call call)
    {
        var range = RangeRegex.Match(call.Text);
        if (!range.Success)
        {
            throw new InvalidOperationException($"Statement without a window_start range: {call.Text}");
        }

        return (long.Parse(range.Groups["from"].Value, CultureInfo.InvariantCulture),
            long.Parse(range.Groups["to"].Value, CultureInfo.InvariantCulture));
    }
}
