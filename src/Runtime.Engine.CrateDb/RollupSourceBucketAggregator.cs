using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Runs one bucket's aggregation statement against its source so that a <b>rollup</b> source is read
/// in exactly one generation per window — the one its active-generation pointer names. Shared by the
/// forward aggregation and the recompute executor; a raw or time-range source has no generations and
/// is executed as is.
/// </summary>
/// <remarks>
/// <para>
/// The predicate alone is not enough. CrateDB has no snapshot spanning the pointer read and the
/// aggregation statement, so the source can commit a recompute in between: the pointer read says
/// generation N, the source flips to N+1 and sweeps N, and the statement then finds nothing (or only
/// part) of N. The pointer is therefore read again after the statement; if an entry overlapping the
/// bucket differs, the result is discarded and the bucket is aggregated again. An unchanged pointer
/// proves the statement ran before any flip of that range became visible, hence before the sweep of
/// the generation it selected.
/// </para>
/// <para>
/// The pointer is read per bucket, immediately around the statement. Reading it once per chunk or
/// per tick would stretch the unprotected interval from milliseconds to the whole run.
/// </para>
/// </remarks>
internal sealed class RollupSourceBucketAggregator
{
    /// <summary>Attempts per bucket before the source is reported as not settling.</summary>
    internal const int MaxAttempts = 3;

    private readonly string _tenantId;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _ensuredGenMaps = new(StringComparer.Ordinal);

    public RollupSourceBucketAggregator(
        string tenantId,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        ILogger logger)
    {
        _tenantId = tenantId;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _logger = logger;
    }

    /// <summary>
    /// Aggregates the bucket <c>[bucketStart, bucketEnd)</c> of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source archive the statement reads.</param>
    /// <param name="bucketStart">Inclusive bucket start.</param>
    /// <param name="bucketEnd">Exclusive bucket end.</param>
    /// <param name="buildSql">
    /// Builds the aggregation statement for the given source pointer entries — <c>null</c> for a raw
    /// or time-range source, the source's entries (possibly none) for a rollup source.
    /// </param>
    /// <param name="discardAttemptAsync">
    /// Removes what a discarded attempt wrote, before the bucket is aggregated again. Optional: an
    /// upsert target overwrites its own rows, so this only matters where a row of the discarded
    /// attempt may have no counterpart in the repeated one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rows written by the attempt that was kept.</returns>
    public async Task<int> AggregateAsync(
        ArchiveSnapshot source,
        DateTime bucketStart,
        DateTime bucketEnd,
        Func<IReadOnlyList<GenerationRange>?, string> buildSql,
        Func<CancellationToken, Task>? discardAttemptAsync,
        CancellationToken cancellationToken)
    {
        if (source.RollupAggregations is null)
        {
            return await _databaseClient.ExecuteNonQueryAsync(_tenantId, buildSql(null), cancellationToken);
        }

        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, source.RtId.ToString());
        await EnsureGenMapAsync(genMapTable);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = await LoadOverlappingAsync(genMapTable, bucketStart, bucketEnd, cancellationToken);
            var rows = await _databaseClient.ExecuteNonQueryAsync(_tenantId, buildSql(before), cancellationToken);
            var after = await LoadOverlappingAsync(genMapTable, bucketStart, bucketEnd, cancellationToken);

            if (SamePointers(before, after))
            {
                return rows;
            }

            if (attempt >= MaxAttempts)
            {
                throw new InvalidOperationException(
                    $"Source rollup {source.RtId} changed its active generation for bucket " +
                    $"[{bucketStart:O}, {bucketEnd:O}) during each of {MaxAttempts} aggregation attempts; " +
                    "the bucket is left to the next run.");
            }

            _logger.LogInformation(
                "Source rollup {SourceRtId} committed a recompute of bucket [{BucketStart:O}, {BucketEnd:O}) while it was being aggregated; aggregating it again (attempt {Attempt}/{MaxAttempts}).",
                source.RtId, bucketStart, bucketEnd, attempt + 1, MaxAttempts);

            if (discardAttemptAsync is not null)
            {
                await discardAttemptAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// The generation map is created at rollup activation; creating it here when absent keeps the
    /// pointer read strict — a failing read is then a real failure and never mistaken for "no
    /// pointer entries", which would send the scan to generation 0 and hide recomputed rows.
    /// </summary>
    private async Task EnsureGenMapAsync(string genMapTable)
    {
        if (_ensuredGenMaps.ContainsKey(genMapTable))
        {
            return;
        }

        await _managementClient.ExecuteDdlAsync(_tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable));
        _ensuredGenMaps[genMapTable] = true;
    }

    private async Task<IReadOnlyList<GenerationRange>> LoadOverlappingAsync(
        string genMapTable, DateTime bucketStart, DateTime bucketEnd, CancellationToken cancellationToken)
    {
        var ranges = new List<GenerationRange>();
        await foreach (var row in _databaseClient.StreamRawRowsAsync(
                           _tenantId, GenerationMapSqlBuilder.BuildSelectAll(genMapTable), cancellationToken))
        {
            var start = Convert.ToInt64(row["range_start"], CultureInfo.InvariantCulture);
            var end = Convert.ToInt64(row["range_end"], CultureInfo.InvariantCulture);
            var scope = row.TryGetValue("rtid_scope", out var s) && s is not null ? s.ToString() ?? string.Empty : string.Empty;
            var generation = Convert.ToInt64(row[Constants.Generation], CultureInfo.InvariantCulture);
            ranges.Add(new GenerationRange(start, end, scope, generation));
        }

        return GenerationFilterSql.Overlapping(ranges, bucketStart, bucketEnd);
    }

    private static bool SamePointers(IReadOnlyList<GenerationRange> before, IReadOnlyList<GenerationRange> after) =>
        before.Count == after.Count && new HashSet<GenerationRange>(before).SetEquals(after);
}
