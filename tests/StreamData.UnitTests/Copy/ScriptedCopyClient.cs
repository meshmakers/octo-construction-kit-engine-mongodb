using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>Answers each statement with a handler and records them (revealed and redacted spelling).</summary>
internal sealed class ScriptedCopyClient(Func<CopyStatement, CrateStatementResult> handler) : ICrateCopyClient
{
    public List<CopyStatement> Statements { get; } = [];

    public Task<CrateStatementResult> ExecuteStatementAsync(
        string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Statements.Add(statement);
        return Task.FromResult(handler(statement));
    }
}

/// <summary>
/// A small CrateDB stand-in for the staging copy: a source table of timestamps, a store (slice folder → rows),
/// a target table, and the answers to the metadata statements. It parses the statements the runner sends, so a
/// test sees what the statements say, not what a mock was told.
/// </summary>
internal sealed class SimulatedCrate : ICrateCopyClient
{
    private static readonly Regex Range = new(
        "\"(?<col>[a-z_]+)\" >= '(?<from>[^']+)' AND \"[a-z_]+\" < '(?<to>[^']+)'", RegexOptions.CultureInvariant);

    private static readonly Regex CopyToTarget = new("TO DIRECTORY '(?<uri>(?:[^']|'')*)'", RegexOptions.CultureInvariant);
    private static readonly Regex CopyFromSource = new("FROM '(?<uri>(?:[^']|'')*)/\\*'", RegexOptions.CultureInvariant);
    private static readonly Regex Table = new("(?:FROM|COPY|TABLE) \"(?<schema>[^\"]+)\"\\.\"(?<table>[^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex AlterSettings = new(
        "number_of_replicas = (?<replicas>'[^']*'|\\d+), refresh_interval = (?<refresh>\\d+)", RegexOptions.CultureInvariant);

    public const string SourceTable = "archive_source";
    public const string TargetTable = "archive_target";

    public List<DateTime> SourceRows { get; } = [];
    public List<DateTime> TargetRows { get; } = [];
    public Dictionary<string, List<DateTime>> Store { get; } = new();
    public string[] SourceColumns { get; set; } = ["timestamp", "rtid", "value"];
    public string[] TargetColumns { get; set; } = ["timestamp", "rtid", "value"];
    public string Filter { get; set; } = JobLogFilterGuard.RecommendedFilter;
    public long[] NodeDiskAvailable { get; set; } = [100L << 30];

    /// <summary>Bytes in use per node (the size of a node is its available plus its used bytes).</summary>
    public long[] NodeDiskUsed { get; set; } = [];

    /// <summary>The high disk watermark and whether the allocation threshold is on.</summary>
    public string HighWatermark { get; set; } = "90%";

    public bool WatermarkEnabled { get; set; } = true;

    /// <summary>Runs before a statement is answered; throw to fail it.</summary>
    public Action<CopyStatement>? Fault { get; set; }

    /// <summary>Every statement as executed (<b>may contain the credential</b>, for assertions about the SQL).</summary>
    public List<string> Executed { get; } = [];

    /// <summary>Every statement as it may be shown.</summary>
    public List<string> Shown { get; } = [];

    /// <summary>
    /// What the next <c>COPY FROM</c> statements report, in order: only the first <c>Rows</c> rows of the slice are
    /// loaded and the summary says <c>Errors</c> rejected rows with <c>ErrorText</c> (the way a refused shard request shows).
    /// </summary>
    public Queue<(int Rows, int Errors, string? ErrorText)> LoadScript { get; } = new();

    /// <summary>How often the heap of the nodes was read.</summary>
    public int HeapReads { get; private set; }

    /// <summary>The table settings now and the history of changes, as <c>replicas/refresh</c>.</summary>
    public string Replicas { get; set; } = "0-1";

    public long Refresh { get; set; } = 1000;

    public int Shards { get; set; } = 3;
    public List<string> SettingsHistory { get; } = [];

    public Task<CrateStatementResult> ExecuteStatementAsync(
        string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Executed.Add(statement.Reveal());
        Shown.Add(statement.Redacted);
        Fault?.Invoke(statement);
        return Task.FromResult(Answer(statement.Reveal()));
    }

    // ---- the generation map of a rollup (AB#6968) ---------------------------------------------------------

    /// <summary>One entry of a generation map.</summary>
    public readonly record struct MapRow(long Start, long End, string Scope, long Generation);

    public List<MapRow> SourceMap { get; } = [];
    public List<MapRow> TargetMap { get; } = [];
    public Dictionary<string, List<MapRow>> MapStore { get; } = new();

    /// <summary>The columns of the map tables; empty = the rollup has no map table.</summary>
    public string[] MapColumns { get; set; } = ["range_start", "range_end", "rtid_scope", "generation"];

    /// <summary>How often the source map was read with a SELECT (the reads of the repeat rule).</summary>
    public int MapReads { get; private set; }

    private CrateStatementResult AnswerMap(string sql)
    {
        var isSource = sql.Contains(SourceTable, StringComparison.Ordinal);
        if (sql.Contains("information_schema.columns", StringComparison.Ordinal))
        {
            return new CrateStatementResult(0, MapColumns
                .Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["column_name"] = c, ["data_type"] = c == "rtid_scope" ? "text" : "bigint"
                }).ToList());
        }

        if (sql.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal))
        {
            return Row(("count", (long)(isSource ? SourceMap : TargetMap).Count));
        }

        if (sql.StartsWith("SELECT \"range_start\"", StringComparison.Ordinal))
        {
            MapReads++;
            return new CrateStatementResult(0, SourceMap
                .Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["range_start"] = m.Start, ["range_end"] = m.End, ["rtid_scope"] = m.Scope, ["generation"] = m.Generation
                }).ToList());
        }

        if (sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" TO DIRECTORY ", StringComparison.Ordinal))
        {
            MapStore[ArchiveCopyRedaction.RedactUri(Unquote(CopyToTarget.Match(sql).Groups["uri"].Value))] = SourceMap.ToList();
            return new CrateStatementResult(SourceMap.Count, []);
        }

        if (sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" FROM ", StringComparison.Ordinal))
        {
            var key = ArchiveCopyRedaction.RedactUri(Unquote(CopyFromSource.Match(sql).Groups["uri"].Value));
            if (!MapStore.TryGetValue(key, out var rows))
            {
                return new CrateStatementResult(0, [Summary(0, 0)]);
            }

            var script = LoadScript.TryDequeue(out var s) ? s : ((int Rows, int Errors, string? ErrorText)?)null;
            var loaded = 0;
            foreach (var row in rows.Where(row => !TargetMap.Contains(row)).Take(script?.Rows ?? int.MaxValue))
            {
                TargetMap.Add(row);
                loaded++;
            }

            return script is { } sc
                ? new CrateStatementResult(0, [Summary(sc.Rows, sc.Errors, sc.ErrorText)])
                : new CrateStatementResult(0, [Summary(loaded, 0)]);
        }

        return CrateStatementResult.Empty; // REFRESH TABLE
    }

    private CrateStatementResult Answer(string sql)
    {
        if (sql.Contains("__genmap", StringComparison.Ordinal))
        {
            return AnswerMap(sql);
        }

        if (sql.Contains("['watermark']['high']", StringComparison.Ordinal))
        {
            return Row(("high", HighWatermark), ("enabled", WatermarkEnabled));
        }

        if (sql.Contains("sys.cluster", StringComparison.Ordinal))
        {
            return Row(("jobs_log_filter", Filter));
        }

        if (sql.Contains("information_schema.columns", StringComparison.Ordinal))
        {
            var columns = sql.Contains(SourceTable, StringComparison.Ordinal) ? SourceColumns : TargetColumns;
            return new CrateStatementResult(0, columns
                .Select(c => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["column_name"] = c, ["data_type"] = c == "timestamp" ? "timestamp with time zone" : "text"
                }).ToList());
        }

        if (sql.Contains("export_min", StringComparison.Ordinal))
        {
            return SourceRows.Count == 0
                ? Row(("export_min", null), ("export_max", null), ("export_count", 0L))
                : Row(("export_min", SourceRows.Min()), ("export_max", SourceRows.Max()), ("export_count", (long)SourceRows.Count));
        }

        if (sql.StartsWith("SELECT COUNT(*) FROM", StringComparison.Ordinal))
        {
            return Row(("count", Count(sql)));
        }

        if (sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" TO DIRECTORY ", StringComparison.Ordinal))
        {
            var (from, to) = Bounds(sql);
            var rows = SourceRows.Where(r => r >= from && r < to).ToList();
            Store[ArchiveCopyRedaction.RedactUri(Unquote(CopyToTarget.Match(sql).Groups["uri"].Value))] = rows;
            return new CrateStatementResult(rows.Count, []);
        }

        if (sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains(" FROM ", StringComparison.Ordinal))
        {
            var key = ArchiveCopyRedaction.RedactUri(Unquote(CopyFromSource.Match(sql).Groups["uri"].Value));
            if (!Store.TryGetValue(key, out var rows))
            {
                return new CrateStatementResult(0, [Summary(0, 0)]);
            }

            var overwrite = sql.Contains("overwrite_duplicates = true", StringComparison.Ordinal);
            var script = LoadScript.TryDequeue(out var s) ? s : ((int Rows, int Errors, string? ErrorText)?)null;
            if (script is { } sc)
            {
                foreach (var row in rows.Take(sc.Rows).Where(row => !TargetRows.Contains(row)))
                {
                    TargetRows.Add(row);
                }

                return new CrateStatementResult(0, [Summary(sc.Rows, sc.Errors, sc.ErrorText)]);
            }

            var errors = 0;
            foreach (var row in rows)
            {
                if (TargetRows.Contains(row) && !overwrite)
                {
                    errors++;
                }
                else if (!TargetRows.Contains(row))
                {
                    TargetRows.Add(row);
                }
            }

            return new CrateStatementResult(0, [Summary(rows.Count - errors, errors)]);
        }

        if (sql.Contains("information_schema.tables", StringComparison.Ordinal))
        {
            return Row(("number_of_replicas", Replicas), ("refresh_interval", Refresh), ("number_of_shards", (long)Shards));
        }

        if (sql.StartsWith("ALTER TABLE", StringComparison.Ordinal))
        {
            var match = AlterSettings.Match(sql);
            Replicas = match.Groups["replicas"].Value.Trim('\'');
            Refresh = long.Parse(match.Groups["refresh"].Value, CultureInfo.InvariantCulture);
            SettingsHistory.Add($"{Replicas}/{Refresh}");
            return CrateStatementResult.Empty;
        }

        if (sql.Contains("heap['used']", StringComparison.Ordinal))
        {
            HeapReads++;
            return Row(("name", "node0"), ("heap_used", 900L << 20), ("heap_max", 1024L << 20));
        }

        if (sql.Contains("sys.nodes", StringComparison.Ordinal) && sql.Contains("available", StringComparison.Ordinal))
        {
            return new CrateStatementResult(0, NodeDiskAvailable
                .Select((a, i) =>
                {
                    var used = i < NodeDiskUsed.Length ? NodeDiskUsed[i] : 0;
                    return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["name"] = $"node{i}", ["available"] = a, ["used"] = used, ["size"] = a + used
                    };
                }).ToList());
        }

        if (sql.Contains("version['number']", StringComparison.Ordinal))
        {
            return Row(("version", "5.10.10"));
        }

        return CrateStatementResult.Empty; // REFRESH TABLE, OPTIMIZE TABLE
    }

    private long Count(string sql)
    {
        var isTarget = sql.Contains(TargetTable, StringComparison.Ordinal);
        if (!sql.Contains(" WHERE ", StringComparison.Ordinal))
        {
            return isTarget ? TargetRows.Count : SourceRows.Count;
        }

        var (from, to) = Bounds(sql);
        return (isTarget ? TargetRows : SourceRows).Count(r => r >= from && r < to);
    }

    private static (DateTime From, DateTime To) Bounds(string sql)
    {
        var match = Range.Match(sql);
        return (Parse(match.Groups["from"].Value), Parse(match.Groups["to"].Value));
    }

    private static DateTime Parse(string value) => DateTime.SpecifyKind(
        DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static string Unquote(string value) => value.Replace("''", "'", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, object?> Summary(long success, long errors, string? errorText = null)
        => new Dictionary<string, object?>
        {
            ["success_count"] = success, ["error_count"] = errors,
            ["errors"] = errors == 0 ? null : errorText ?? "{\"parse error\": {\"count\": " + errors + "}}"
        };

    private static CrateStatementResult Row(params (string Column, object? Value)[] cells)
        => new(0, [cells.ToDictionary(c => c.Column, c => c.Value)]);
}
