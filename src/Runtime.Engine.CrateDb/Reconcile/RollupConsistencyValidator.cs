using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>A part of a pointer entry that no higher entry outranks and that holds no row of the entry's generation.</summary>
internal sealed record PointerGap(GenerationRange Entry, long FromMs, long ToMs);

/// <summary>Rows of a generation that none of <see cref="Covering"/> (the entries of that or a higher generation) covers.</summary>
internal sealed record OrphanSet(long Generation, long Rows, IReadOnlyList<GenerationRange> Covering, long FirstWindowStartMs, long LastWindowStartMs);

/// <summary>The watermark is ahead of the last rollup row while a source holds rows in between.</summary>
internal sealed record WatermarkGap(DateTime? LastRowEnd, DateTime Watermark, long SourceRows);

/// <summary>
/// What the validator found for one rollup, in the form the repair works from; <see cref="Findings"/> is the report.
/// </summary>
internal sealed record RollupAnalysis(
    bool MissingGenerationMap,
    IReadOnlyList<GenerationRange> Pointers,
    IReadOnlyList<PointerGap> PointerGaps,
    IReadOnlyList<OrphanSet> Orphans,
    WatermarkGap? Watermark,
    IReadOnlyList<RollupConsistencyFinding> Findings,
    RollupNotChecked? NotChecked = null)
{
    /// <summary>Nothing was inspected; <paramref name="notChecked"/> says why. There is nothing for a repair to work from.</summary>
    public static RollupAnalysis Unchecked(RollupNotChecked notChecked) => new(false, [], [], [], null, [], notChecked);

    /// <summary>True when there is no table to repair: the repair leaves such a rollup alone.</summary>
    public bool HasNoTable => NotChecked is { Cause: RollupNotCheckedCause.NoLiveTable or RollupNotCheckedCause.NeverActivated };
}

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
/// A rollup that cannot be inspected - no table (a restore that failed to load it, or never activated) or a source that is missing -
/// is never reported as consistent: its entry carries <see cref="RollupConsistencyEntry.NotChecked"/> with the reason, and the report
/// is not ok while any entry has one (AB#6968).
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
            var entry = await ValidateEntryAsync(rollup, cancellationToken);
            entries.Add(entry);
        }

        logger.LogInformation(
            "Validated {Rollups} rollup(s) of tenant {TenantId} in {Elapsed} ms: {Inconsistent} inconsistent, {NotChecked} not checked.",
            entries.Count, tenantId, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            entries.Count(e => e.Findings.Count > 0), entries.Count(e => e.IsNotChecked));
        return new RollupConsistencyReport(entries, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The entry of one rollup; each finding and each gap in the check is logged as a warning.</summary>
    internal async Task<RollupConsistencyEntry> ValidateEntryAsync(RollupArchiveSnapshot rollup, CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(rollup, cancellationToken);
        foreach (var finding in analysis.Findings)
        {
            logger.LogWarning("Rollup {RollupRtId} of tenant {TenantId} is inconsistent ({Check}): {Message}",
                rollup.RtId, tenantId, finding.Check, finding.Message);
        }

        if (analysis.NotChecked is { } notChecked)
        {
            // A rollup that was never activated is a normal state (it has no table by design): not a warning on every start.
            logger.Log(notChecked.Cause == RollupNotCheckedCause.NeverActivated ? LogLevel.Information : LogLevel.Warning,
                "Rollup {RollupRtId} of tenant {TenantId} could not be checked ({Cause}): {Reason}",
                rollup.RtId, tenantId, notChecked.Cause, notChecked.Reason);
        }

        return new RollupConsistencyEntry(rollup.RtId, analysis.Findings, analysis.NotChecked);
    }

    /// <summary>Runs the four checks of one rollup and keeps what the repair needs next to the findings.</summary>
    internal async Task<RollupAnalysis> AnalyzeAsync(RollupArchiveSnapshot rollup, CancellationToken cancellationToken)
    {
        var id = rollup.RtId.ToString();
        var liveName = TenantSchema.ArchiveTableName(id);
        var live = TenantSchema.QualifiedArchiveTable(tenantId, id);
        if (!await TableExistsAsync(liveName, cancellationToken))
        {
            // Nothing is found in a rollup that has no table because there is nothing to look at, which is not "consistent".
            return RollupAnalysis.Unchecked(rollup.Status == CkArchiveStatus.Created
                ? new RollupNotChecked(RollupNotCheckedCause.NeverActivated,
                    $"Rollup {rollup.RtId} was never activated (status Created) and has no table {liveName}; there is nothing to check.")
                : new RollupNotChecked(RollupNotCheckedCause.NoLiveTable,
                    $"Rollup {rollup.RtId} (status {rollup.Status}) has no table {liveName}; nothing could be checked. " +
                    "A restore that failed to load this rollup leaves this state: restore it again or activate it, then validate."));
        }

        var findings = new List<RollupConsistencyFinding>();
        var genMapName = liveName + "__genmap";
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantId, id);
        var pointers = new List<GenerationRange>();
        var missingMap = !await TableExistsAsync(genMapName, cancellationToken);
        if (missingMap)
        {
            findings.Add(new RollupConsistencyFinding(RollupConsistencyCheck.MissingGenerationMap,
                $"Rollup {rollup.RtId} has a table but no generation map table ({genMapName}); recomputed rows cannot be selected."));
        }
        else
        {
            await databaseClient.RefreshArchiveTableAsync(tenantId, genMap, CrateStatementClass.Maintenance, cancellationToken);
            pointers.AddRange(await ReadPointersAsync(genMap, cancellationToken));
        }

        await databaseClient.RefreshArchiveTableAsync(tenantId, live, CrateStatementClass.Maintenance, cancellationToken);

        var gaps = await CheckPointersWithoutRowsAsync(live, pointers, cancellationToken);
        findings.AddRange(gaps.Select(g => PointerGapFinding(rollup, g)));

        var orphans = new List<OrphanSet>();
        if (rollup.RecomputeInProgress)
        {
            logger.LogDebug("Rollup {RollupRtId}: a recompute is running, orphan rows are not checked.", rollup.RtId);
        }
        else
        {
            orphans.AddRange(await CheckOrphansAsync(live, pointers, cancellationToken));
            findings.AddRange(orphans.Select(o => OrphanFinding(rollup, o)));
        }

        var watermark = await CheckWatermarkAsync(rollup, live, cancellationToken);
        if (watermark is not null)
        {
            findings.Add(WatermarkFinding(rollup, watermark));
        }

        var sourceProblem = await FindMissingSourceAsync(rollup, cancellationToken);
        return new RollupAnalysis(missingMap, pointers, gaps, orphans, watermark, findings,
            sourceProblem is null ? null : new RollupNotChecked(RollupNotCheckedCause.SourceMissing, sourceProblem));
    }

    /// <summary>
    /// A source whose entity is gone, or that is activated and has no table: the watermark check counts rows in the sources and
    /// would read "none" for it. Null when every source is there. A source that was never activated has no table by design.
    /// </summary>
    private async Task<string?> FindMissingSourceAsync(RollupArchiveSnapshot rollup, CancellationToken cancellationToken)
    {
        foreach (var reference in rollup.Sources)
        {
            var source = await archiveStore.GetAsync(reference.SourceArchiveRtId);
            if (source is null)
            {
                return $"Source archive {reference.SourceArchiveRtId} of rollup {rollup.RtId} does not exist; the watermark of the rollup cannot be checked against it.";
            }

            var table = TenantSchema.ArchiveTableName(reference.SourceArchiveRtId.ToString());
            if (source.Status != CkArchiveStatus.Created && !await TableExistsAsync(table, cancellationToken))
            {
                return $"Source archive {reference.SourceArchiveRtId} of rollup {rollup.RtId} (status {source.Status}) has no table {table}; " +
                       "the watermark of the rollup cannot be checked against it.";
            }
        }

        return null;
    }

    // (a) ------------------------------------------------------------------------------------------------------

    private async Task<List<PointerGap>> CheckPointersWithoutRowsAsync(
        string live, IReadOnlyList<GenerationRange> pointers, CancellationToken cancellationToken)
    {
        var gaps = new List<PointerGap>();
        foreach (var entry in pointers.OrderBy(p => p.StartMs).ThenBy(p => p.Generation))
        {
            var governed = RollupConsistencySql.GovernedRanges(entry, pointers);
            if (governed.Count == 0)
            {
                continue; // fully outranked: no reader selects through it
            }

            cancellationToken.ThrowIfCancellationRequested();
            // An entry that holds rows of its generation somewhere is not a lost one: its range may reach past the data (the source
            // was quiet there), and a part that a later entry outranked leaves a tail without rows that is not a defect. Only an
            // entry without a single row of its generation names rows that are gone.
            var rows = await databaseClient.GetCountAsync(tenantId,
                RollupRecomputeSqlBuilder.BuildCountGeneration(live, FromMs(entry.StartMs), FromMs(entry.EndMs), entry.Generation,
                    entry.Scope.Length == 0 ? null : entry.Scope),
                CrateStatementClass.Maintenance, cancellationToken);
            if (rows == 0)
            {
                gaps.AddRange(governed.Select(g => new PointerGap(entry, g.FromMs, g.ToMs)));
            }
        }

        return gaps;
    }

    private static RollupConsistencyFinding PointerGapFinding(RollupArchiveSnapshot rollup, PointerGap gap) => new(
        RollupConsistencyCheck.PointerWithoutRows,
        $"Rollup {rollup.RtId}: the pointer names generation {gap.Entry.Generation} for [{FromMs(gap.FromMs):O}, {FromMs(gap.ToMs):O})" +
        (gap.Entry.Scope.Length == 0 ? string.Empty : $" of entity {gap.Entry.Scope}") +
        ", but the table holds no row of that generation there; readers see empty windows.",
        FromMs(gap.FromMs), FromMs(gap.ToMs), gap.Entry.Generation, 0);

    // (b) ------------------------------------------------------------------------------------------------------

    private async Task<List<OrphanSet>> CheckOrphansAsync(
        string live, IReadOnlyList<GenerationRange> pointers, CancellationToken cancellationToken)
    {
        var orphans = new List<OrphanSet>();
        foreach (var summary in await ReadGenerationSummaryAsync(live, cancellationToken))
        {
            var covering = pointers.Where(p => p.Generation >= summary.Generation).ToList();
            // Nothing to count when one unscoped covering entry spans all rows of the generation.
            if (covering.Any(p => p.Scope.Length == 0 && p.StartMs <= summary.FirstWindowStartMs && summary.LastWindowStartMs < p.EndMs))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var count = await databaseClient.GetCountAsync(tenantId,
                RollupConsistencySql.BuildCountOrphans(live, summary.Generation, covering), CrateStatementClass.Maintenance, cancellationToken);
            if (count > 0)
            {
                orphans.Add(new OrphanSet(summary.Generation, count, covering, summary.FirstWindowStartMs, summary.LastWindowStartMs));
            }
        }

        return orphans;
    }

    private static RollupConsistencyFinding OrphanFinding(RollupArchiveSnapshot rollup, OrphanSet orphan) => new(
        RollupConsistencyCheck.OrphansAbovePointer,
        $"Rollup {rollup.RtId}: {orphan.Rows} row(s) of generation {orphan.Generation} lie outside every pointer entry of that or a higher generation " +
        $"(window starts between {FromMs(orphan.FirstWindowStartMs).ToString("O", CultureInfo.InvariantCulture)} and " +
        $"{FromMs(orphan.LastWindowStartMs).ToString("O", CultureInfo.InvariantCulture)}); no reader selects them and no sweep removes them.",
        FromMs(orphan.FirstWindowStartMs), FromMs(orphan.LastWindowStartMs + 1), orphan.Generation, orphan.Rows);

    // (c) ------------------------------------------------------------------------------------------------------

    internal async Task<WatermarkGap?> CheckWatermarkAsync(
        RollupArchiveSnapshot rollup, string live, CancellationToken cancellationToken)
    {
        if (rollup.LastAggregatedBucketEnd is not { } watermarkRaw)
        {
            return null;
        }

        var watermark = DateTime.SpecifyKind(watermarkRaw, DateTimeKind.Utc);
        var lastRowEnd = await ReadLastWindowEndAsync(live, cancellationToken);
        if (lastRowEnd is { } end && end >= watermark)
        {
            return null;
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
                return new WatermarkGap(lastRowEnd, watermark, sourceRows);
            }
        }

        return null;
    }

    private static RollupConsistencyFinding WatermarkFinding(RollupArchiveSnapshot rollup, WatermarkGap gap)
    {
        var from = gap.LastRowEnd ?? DateTime.UnixEpoch;
        var span = gap.LastRowEnd is null ? (TimeSpan?)null : gap.Watermark - from;
        return new RollupConsistencyFinding(RollupConsistencyCheck.WatermarkAheadOfRows,
            gap.LastRowEnd is null
                ? $"Rollup {rollup.RtId}: the watermark is {gap.Watermark:O}, but the table holds no rows although a source has {gap.SourceRows} row(s) before it; the forward aggregation would not fill them."
                : $"Rollup {rollup.RtId}: the watermark is {gap.Watermark:O}, {span} ahead of the last rollup row (ends {from:O}), and a source has {gap.SourceRows} row(s) in between; the forward aggregation would not fill the gap.",
            from, gap.Watermark, null, gap.SourceRows, span);
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

    /// <summary>The first row of the earliest source of the rollup; null when no source holds a row.</summary>
    internal async Task<DateTime?> ReadEarliestSourceTimeAsync(RollupArchiveSnapshot rollup, CancellationToken cancellationToken)
    {
        DateTime? earliest = null;
        foreach (var reference in rollup.Sources)
        {
            var source = await archiveStore.GetAsync(reference.SourceArchiveRtId);
            if (source is null)
            {
                continue;
            }

            var table = TenantSchema.QualifiedArchiveTable(tenantId, reference.SourceArchiveRtId.ToString());
            var column = source.UsesWindowedStorage ? Constants.WindowStart : Constants.Timestamp;
            try
            {
                await foreach (var row in databaseClient.StreamRawRowsAsync(
                                   tenantId, RollupConsistencySql.BuildMinSourceTime(table, column), cancellationToken))
                {
                    if (row.TryGetValue("min_time", out var value) && AsUtc(value) is { } time && (earliest is null || time < earliest))
                    {
                        earliest = time;
                    }
                }
            }
            catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
            {
                // no table, no rows
            }
        }

        return earliest;
    }

    // reads ----------------------------------------------------------------------------------------------------

    internal async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
        => await databaseClient.GetCountAsync(tenantId, RollupConsistencySql.BuildTableExists(tenantId, tableName),
            CrateStatementClass.Maintenance, cancellationToken) > 0;

    internal async Task<IReadOnlyList<GenerationRange>> ReadPointersAsync(string genMap, CancellationToken cancellationToken)
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

    private async Task<List<GenerationSummary>> ReadGenerationSummaryAsync(string live, CancellationToken cancellationToken)
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

        return rows.OrderBy(r => r.Generation).ToList();
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

    /// <summary>First window start and last window end of the rollup table (epoch ms); null for an empty table.</summary>
    internal async Task<(long FromMs, long ToMs)?> ReadExtentAsync(string live, CancellationToken cancellationToken)
    {
        await foreach (var row in databaseClient.StreamRawRowsAsync(tenantId, RollupConsistencySql.BuildExtent(live), cancellationToken))
        {
            if (row.TryGetValue("lo", out var lo) && AsUtc(lo) is { } from && row.TryGetValue("hi", out var hi) && AsUtc(hi) is { } to)
            {
                return (ToMs(from), ToMs(to));
            }
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
