using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// Checks the rollups of a tenant for the inconsistencies a restore, or a dump taken during a recompute, can leave
/// (AB#5967 slice 2 / AB#6968; <see cref="RollupConsistencyCheck"/>). <b>Read only:</b> counts and MIN/MAX statements, no row is
/// read and nothing is changed (a <c>REFRESH TABLE</c> makes the last writes visible to the searches; it changes no data).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>(a) pointer without rows</b> - the part of a pointer entry that no entry of a higher generation outranks
/// holds no row of the entry's generation: readers see empty windows there.</description></item>
/// <item><description><b>(b) orphans above the pointer</b> - rows of a generation that no entry of that or a higher generation
/// covers. Not reported while a recompute of the rollup is running (<see cref="RollupArchiveSnapshot.RecomputeInProgress"/>): its
/// uncommitted copy looks the same.</description></item>
/// <item><description><b>(c) watermark ahead of the rows</b> - the watermark in the metadata is newer than the last rollup row and
/// a source holds rows in between: the forward aggregation would never fill the gap.</description></item>
/// <item><description><b>(d) missing generation map</b> - a rollup table without its pointer table.</description></item>
/// </list>
/// A rollup that was never activated (no table) is not inspected.
/// </remarks>
internal sealed class RollupConsistencyValidator(
    string tenantId,
    IStreamDataDatabaseClient databaseClient,
    IArchiveRuntimeStore archiveStore,
    IRollupArchiveRuntimeStore rollupStore,
    ILogger logger,
    TimeProvider clock)
{
    public async Task<RollupConsistencyReport> ValidateAsync(
        IReadOnlyCollection<OctoObjectId>? rollupRtIds, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var entries = new List<RollupConsistencyEntry>();
        await foreach (var rollup in rollupStore.EnumerateAsync().WithCancellation(cancellationToken))
        {
            if (rollupRtIds is not null && !rollupRtIds.Contains(rollup.RtId))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new RollupConsistencyEntry(rollup.RtId, await ValidateRollupAsync(rollup, cancellationToken)));
        }

        logger.LogInformation("Validated {Rollups} rollup(s) of tenant {TenantId} in {Elapsed} ms: {Inconsistent} inconsistent.",
            entries.Count, tenantId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, entries.Count(e => !e.IsOk));
        return new RollupConsistencyReport(entries, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The findings for one rollup; empty when it is consistent or has no tables yet.</summary>
    internal async Task<IReadOnlyList<RollupConsistencyFinding>> ValidateRollupAsync(
        RollupArchiveSnapshot rollup, CancellationToken cancellationToken)
    {
        if (rollup.Status == CkArchiveStatus.Created)
        {
            return [];
        }

        var id = rollup.RtId.ToString();
        var liveName = TenantSchema.ArchiveTableName(id);
        var live = TenantSchema.QualifiedArchiveTable(tenantId, id);
        if (!await TableExistsAsync(liveName, cancellationToken))
        {
            return [];
        }

        var findings = new List<RollupConsistencyFinding>();
        var genMapName = liveName + "__genmap";
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantId, id);
        var pointers = new List<GenerationRange>();
        if (await TableExistsAsync(genMapName, cancellationToken))
        {
            await databaseClient.RefreshArchiveTableAsync(tenantId, genMap, CrateStatementClass.Maintenance, cancellationToken);
            pointers.AddRange(await ReadPointersAsync(genMap, cancellationToken));
        }
        else
        {
            findings.Add(new RollupConsistencyFinding(RollupConsistencyCheck.MissingGenerationMap,
                $"Rollup {rollup.RtId} has a table but no generation map table ({genMapName}); recomputed rows cannot be selected."));
        }

        await databaseClient.RefreshArchiveTableAsync(tenantId, live, CrateStatementClass.Maintenance, cancellationToken);
        findings.AddRange(await CheckPointersWithoutRowsAsync(rollup, live, pointers, cancellationToken));
        if (rollup.RecomputeInProgress)
        {
            logger.LogDebug("Rollup {RollupRtId}: a recompute is running, orphan rows are not checked.", rollup.RtId);
        }
        else
        {
            findings.AddRange(await CheckOrphansAsync(rollup, live, pointers, cancellationToken));
        }

        findings.AddRange(await CheckWatermarkAsync(rollup, live, cancellationToken));

        foreach (var finding in findings)
        {
            logger.LogWarning("Rollup {RollupRtId} of tenant {TenantId} is inconsistent ({Check}): {Message}",
                rollup.RtId, tenantId, finding.Check, finding.Message);
        }

        return findings;
    }

    // (a) ------------------------------------------------------------------------------------------------------

    private async Task<IEnumerable<RollupConsistencyFinding>> CheckPointersWithoutRowsAsync(
        RollupArchiveSnapshot rollup, string live, IReadOnlyList<GenerationRange> pointers, CancellationToken cancellationToken)
    {
        var findings = new List<RollupConsistencyFinding>();
        foreach (var entry in pointers.OrderBy(p => p.StartMs).ThenBy(p => p.Generation))
        {
            foreach (var (fromMs, toMs) in RollupConsistencySql.GovernedRanges(entry, pointers))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = FromMs(fromMs);
                var to = FromMs(toMs);
                var rows = await databaseClient.GetCountAsync(tenantId,
                    RollupRecomputeSqlBuilder.BuildCountGeneration(live, from, to, entry.Generation, entry.Scope.Length == 0 ? null : entry.Scope),
                    CrateStatementClass.Maintenance, cancellationToken);
                if (rows == 0)
                {
                    findings.Add(new RollupConsistencyFinding(RollupConsistencyCheck.PointerWithoutRows,
                        $"Rollup {rollup.RtId}: the pointer names generation {entry.Generation} for [{from:O}, {to:O})" +
                        (entry.Scope.Length == 0 ? string.Empty : $" of entity {entry.Scope}") +
                        ", but the table holds no row of that generation there; readers see empty windows.",
                        from, to, entry.Generation, 0));
                }
            }
        }

        return findings;
    }

    // (b) ------------------------------------------------------------------------------------------------------

    private async Task<IEnumerable<RollupConsistencyFinding>> CheckOrphansAsync(
        RollupArchiveSnapshot rollup, string live, IReadOnlyList<GenerationRange> pointers, CancellationToken cancellationToken)
    {
        var findings = new List<RollupConsistencyFinding>();
        await foreach (var summary in ReadGenerationSummaryAsync(live, cancellationToken))
        {
            var covering = pointers.Where(p => p.Generation >= summary.Generation).ToList();
            var lastMs = summary.LastWindowStartMs;
            // Nothing to count when one unscoped covering entry spans all rows of the generation.
            if (covering.Any(p => p.Scope.Length == 0 && p.StartMs <= summary.FirstWindowStartMs && lastMs < p.EndMs))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var orphans = await databaseClient.GetCountAsync(tenantId,
                RollupConsistencySql.BuildCountOrphans(live, summary.Generation, covering), CrateStatementClass.Maintenance, cancellationToken);
            if (orphans > 0)
            {
                findings.Add(new RollupConsistencyFinding(RollupConsistencyCheck.OrphansAbovePointer,
                    $"Rollup {rollup.RtId}: {orphans} row(s) of generation {summary.Generation} lie outside every pointer entry of that or a higher generation " +
                    "(window starts between " + FromMs(summary.FirstWindowStartMs).ToString("O", CultureInfo.InvariantCulture) + " and " +
                    FromMs(lastMs).ToString("O", CultureInfo.InvariantCulture) + "); no reader selects them and no sweep removes them.",
                    FromMs(summary.FirstWindowStartMs), FromMs(lastMs + 1), summary.Generation, orphans));
            }
        }

        return findings;
    }

    // (c) ------------------------------------------------------------------------------------------------------

    private async Task<IEnumerable<RollupConsistencyFinding>> CheckWatermarkAsync(
        RollupArchiveSnapshot rollup, string live, CancellationToken cancellationToken)
    {
        var findings = new List<RollupConsistencyFinding>();
        if (rollup.LastAggregatedBucketEnd is not { } watermarkRaw)
        {
            return findings;
        }

        var watermark = DateTime.SpecifyKind(watermarkRaw, DateTimeKind.Utc);
        var lastRowEnd = await ReadLastWindowEndAsync(live, cancellationToken);
        if (lastRowEnd is { } end && end >= watermark)
        {
            return findings;
        }

        var from = lastRowEnd ?? DateTime.UnixEpoch;
        foreach (var source in rollup.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spanFrom = source.ValidFrom is { } vf && vf > from ? vf : from;
            var spanTo = source.ValidTo is { } vt && vt < watermark ? vt : watermark;
            if (spanFrom >= spanTo)
            {
                continue;
            }

            var sourceRows = await CountSourceRowsAsync(source.SourceArchiveRtId, spanFrom, spanTo, cancellationToken);
            if (sourceRows > 0)
            {
                var gap = lastRowEnd is null ? (TimeSpan?)null : watermark - from;
                findings.Add(new RollupConsistencyFinding(RollupConsistencyCheck.WatermarkAheadOfRows,
                    lastRowEnd is null
                        ? $"Rollup {rollup.RtId}: the watermark is {watermark:O}, but the table holds no rows although source {source.SourceArchiveRtId} has {sourceRows} row(s) before it; the forward aggregation would not fill them."
                        : $"Rollup {rollup.RtId}: the watermark is {watermark:O}, {gap} ahead of the last rollup row (ends {from:O}), and source {source.SourceArchiveRtId} has {sourceRows} row(s) in between; the forward aggregation would not fill the gap.",
                    from, watermark, null, sourceRows, gap));
                break; // one finding per rollup names the gap; the repair rewinds to the end of the rollup data
            }
        }

        return findings;
    }

    private async Task<long> CountSourceRowsAsync(OctoObjectId sourceRtId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var source = await archiveStore.GetAsync(sourceRtId);
        if (source is null)
        {
            return 0;
        }

        var table = TenantSchema.QualifiedArchiveTable(tenantId, sourceRtId.ToString());
        var timeColumn = source.UsesWindowedStorage ? Constants.WindowStart : Constants.Timestamp;
        try
        {
            return await databaseClient.GetCountAsync(tenantId,
                RollupConsistencySql.BuildCountSourceRows(table, timeColumn, ToMs(from), ToMs(to)),
                CrateStatementClass.Maintenance, cancellationToken);
        }
        catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
        {
            return 0; // a source that has no table yet holds nothing
        }
    }

    // reads ----------------------------------------------------------------------------------------------------

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
        => await databaseClient.GetCountAsync(tenantId, RollupConsistencySql.BuildTableExists(tenantId, tableName),
            CrateStatementClass.Maintenance, cancellationToken) > 0;

    private async Task<IReadOnlyList<GenerationRange>> ReadPointersAsync(string genMap, CancellationToken cancellationToken)
    {
        var entries = new List<GenerationRange>();
        await foreach (var row in databaseClient.StreamRawRowsAsync(
                           tenantId, GenerationMapSqlBuilder.BuildSelectAll(genMap), cancellationToken))
        {
            entries.Add(new GenerationRange(
                Convert.ToInt64(row["range_start"], CultureInfo.InvariantCulture),
                Convert.ToInt64(row["range_end"], CultureInfo.InvariantCulture),
                row.TryGetValue("rtid_scope", out var scope) && scope is not null ? scope.ToString() ?? string.Empty : string.Empty,
                Convert.ToInt64(row[Constants.Generation], CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    private readonly record struct GenerationSummary(long Generation, long Rows, long FirstWindowStartMs, long LastWindowStartMs);

    private async IAsyncEnumerable<GenerationSummary> ReadGenerationSummaryAsync(
        string live, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = new List<GenerationSummary>();
        await foreach (var row in databaseClient.StreamRawRowsAsync(
                           tenantId, RollupConsistencySql.BuildGenerationSummary(live), cancellationToken))
        {
            rows.Add(new GenerationSummary(
                Convert.ToInt64(row["g"], CultureInfo.InvariantCulture),
                Convert.ToInt64(row["c"], CultureInfo.InvariantCulture),
                ToMs(AsUtc(row["lo"]) ?? DateTime.UnixEpoch),
                ToMs(AsUtc(row["hi"]) ?? DateTime.UnixEpoch)));
        }

        foreach (var summary in rows.OrderBy(r => r.Generation))
        {
            yield return summary;
        }
    }

    private async Task<DateTime?> ReadLastWindowEndAsync(string live, CancellationToken cancellationToken)
    {
        await foreach (var row in databaseClient.StreamRawRowsAsync(
                           tenantId, RollupConsistencySql.BuildMaxWindowEnd(live), cancellationToken))
        {
            return row.TryGetValue("max_end", out var value) ? AsUtc(value) : null;
        }

        return null;
    }

    internal static DateTime? AsUtc(object? value) => value switch
    {
        null => null,
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => null,
    };

    internal static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    internal static long ToMs(DateTime value) => new DateTimeOffset(
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
        .ToUnixTimeMilliseconds();
}
