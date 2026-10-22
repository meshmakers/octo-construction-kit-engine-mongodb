using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>The physical table of an archive the staging copy works on.</summary>
/// <param name="ArchiveRtId">Runtime id of the archive.</param>
/// <param name="Schema">Tenant schema (unquoted).</param>
/// <param name="TableName">Table name (unquoted).</param>
/// <param name="TimeColumn"><c>window_start</c> on a windowed archive, <c>timestamp</c> on a raw one.</param>
/// <param name="GenerationMapTableName">
/// Name of the generation map table (unquoted) when the archive is a rollup; <c>null</c> for every other archive,
/// whose copy never reads or writes a map.
/// </param>
internal sealed record ArchiveCopyTarget(
    OctoObjectId ArchiveRtId, string Schema, string TableName, string TimeColumn, string? GenerationMapTableName = null)
{
    public string QualifiedTable => $"\"{Schema}\".\"{TableName}\"";

    /// <summary>True for a rollup archive: it has a generation map next to its data table.</summary>
    public bool HasGenerationMap => GenerationMapTableName is not null;

    /// <summary>The generation map table; throws for an archive without one.</summary>
    public string QualifiedGenerationMap => GenerationMapTableName is { } name
        ? $"\"{Schema}\".\"{name}\""
        : throw new InvalidOperationException($"Archive '{ArchiveRtId}' is not a rollup and has no generation map.");
}

/// <summary>
/// The native copy of one archive's data table to and from a staging location with CrateDB's own bulk
/// statements (AB#5967, concept <c>concept-tenant-backup-native-archive-dump.md</c> §6 and §7): planning in
/// counted time slices, <c>COPY TO</c> per slice, and <c>COPY FROM</c> per slice with the checks, bulk table
/// settings and segment merge around it. Everything it sends runs through <see cref="ICrateCopyClient"/>.
/// </summary>
/// <remarks>
/// <para>The credential of the staging location stays inside <see cref="CopyStatement"/>: log lines, result
/// messages and exceptions of this class carry only redacted text.</para>
/// <para>The generation map of a rollup (slice 2, AB#6968) is the last slice of a plan. A dump copies it first
/// and the data after it, a load loads the data first and the map last, so a pointer never names a version whose
/// rows are not there (concept §6). The repeat rule around a dump of a rollup lives in
/// <see cref="RollupArchiveDumper"/>.</para>
/// </remarks>
internal sealed class StagingCopyRunner(
    ILogger logger, string tenantId, ICrateCopyClient client, TimeProvider? timeProvider = null,
    long maxDocsPerMergedSegment = StagingCopyRunner.DefaultMaxDocsPerMergedSegment)
{
    /// <summary>
    /// Rows of one shard that the segment merge after a load puts into one segment at most. A merge to a single segment of a shard
    /// of 40 million rows ran a CrateDB node with a 1 GiB heap out of memory (<c>OutOfMemoryError</c> in the terms writer of the
    /// merge, the node exits); merged to segments of at most 10 million rows the same table merged within the same heap, and the table
    /// was not any bigger (203 bytes per row either way). Tables of up to 10 million rows per shard are still merged to one segment.
    /// </summary>
    internal const long DefaultMaxDocsPerMergedSegment = 10_000_000;

    /// <summary>Attempts to load one slice in total (AB#7274); the policy is internal to the runner.</summary>
    internal const int MaxLoadAttempts = 3;

    /// <summary>CrateDB's default <c>bulk_size</c> of a <c>COPY FROM</c>; every repeat halves the batch of the attempt before.</summary>
    internal const int DefaultBulkSize = 10_000;

    /// <summary>The wait before attempt 2 and attempt 3.</summary>
    internal static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// A loaded table takes up to twelve times its gzip size right after the load (before the segment merge, per copy of the
    /// data) and about nine and a half times after the merge: measured with the 119 million row raw archive of the sbeg
    /// tenant, whose rows are clustered by meter in the source while the load follows time slices (AB#7274, concept §7). A table
    /// that was written in arrival order needs less. The source table itself takes about four times its gzip size.
    /// </summary>
    internal const int DiskSpaceFactor = 12;

    private const int MaxErrorTextCharacters = 300;

    // ---- planning ----------------------------------------------------------------------------------------

    /// <summary>
    /// Cuts the data table into counted slices of about <see cref="ArchiveCopyOptions.TargetSliceRows"/> rows
    /// (the planner of the row export: propose, count, accept or narrow). A table that does not exist, or is
    /// empty, yields a plan without slices.
    /// </summary>
    public async Task<ArchiveCopyPlan> PlanAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var description = await DescribeAsync(target, ArchiveCopyTable.Data, options, cancellationToken);
        if (description.Columns.Count == 0)
        {
            return new ArchiveCopyPlan(target.ArchiveRtId, [description], []);
        }

        var bounds = await ReadBoundsAsync(target, options, cancellationToken);
        var slices = new List<ArchiveCopySlice>();
        if (bounds is { } b)
        {
            var planner = new ExportSlicePlanner(b.FromInclusiveMs, b.ToExclusiveMs, b.RowCount, options.TargetSliceRows);
            while (planner.TryNext(out var fromMs, out var toMs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).UtcDateTime;
                var toUtc = DateTimeOffset.FromUnixTimeMilliseconds(toMs).UtcDateTime;

                var rows = await CountAsync(CrateDbStreamDataRepository.BuildExportSliceCountSql(
                    target.QualifiedTable, target.TimeColumn, fromUtc, toUtc, toInclusive: false), options, cancellationToken);
                if (!planner.Accept(rows) || rows == 0)
                {
                    continue; // too large: proposed again narrower; empty: nothing to copy
                }

                slices.Add(ArchiveCopySlice.ForTimeRange(fromUtc, toUtc, rows));
            }
        }

        var tables = new List<ArchiveTableDescription> { description };
        if (target.HasGenerationMap)
        {
            var map = await DescribeAsync(target, ArchiveCopyTable.GenerationMap, options, cancellationToken);
            if (map.Columns.Count == 0)
            {
                // The validator reports a rollup without its map (check d); the copy has nothing to take along.
                logger.LogWarning("Rollup {ArchiveRtId} of tenant {TenantId} has no generation map table; the plan has data slices only.",
                    target.ArchiveRtId, tenantId);
            }
            else
            {
                tables.Add(map);
                var mapRows = await CountGenerationMapAsync(target, options, cancellationToken);
                if (mapRows > 0)
                {
                    slices.Add(ArchiveCopySlice.ForGenerationMap(mapRows)); // always the last slice
                }
            }
        }

        logger.LogInformation(
            "Planned the native copy of archive {ArchiveRtId} of tenant {TenantId}: {Slices} slices, {Rows} rows.",
            target.ArchiveRtId, tenantId, slices.Count, slices.Sum(s => s.RowCount));
        return new ArchiveCopyPlan(target.ArchiveRtId, tables, slices);
    }

    /// <summary>Rows of the generation map (after a refresh: the count is a search, it sees the last refresh only).</summary>
    internal async Task<long> CountGenerationMapAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        await RefreshAsync(target.QualifiedGenerationMap, options, cancellationToken);
        return await CountAsync($"SELECT COUNT(*) FROM {target.QualifiedGenerationMap}", options, cancellationToken);
    }

    internal Task RefreshAsync(string qualifiedTable, ArchiveCopyOptions options, CancellationToken cancellationToken)
        => client.ExecuteStatementAsync(tenantId, CopyStatement.Plain($"REFRESH TABLE {qualifiedTable}"),
            options.CommandTimeout, cancellationToken);

    // ---- description -------------------------------------------------------------------------------------

    /// <summary>Every column of the table with its CrateDB data type; empty when the table does not exist.</summary>
    public async Task<ArchiveTableDescription> DescribeAsync(
        ArchiveCopyTarget target, ArchiveCopyTable table, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var tableName = table == ArchiveCopyTable.Data ? target.TableName : target.TableName + "__genmap";
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT column_name, data_type FROM information_schema.columns " +
            $"WHERE table_schema = {CopyStatement.Literal(target.Schema)} AND table_name = {CopyStatement.Literal(tableName)} " +
            "ORDER BY ordinal_position", returnsRows: true), options.CommandTimeout, cancellationToken);

        var columns = result.Rows
            .Select(r => new ArchiveCopyColumn(Text(r, "column_name"), Text(r, "data_type")))
            .ToList();
        return new ArchiveTableDescription(target.ArchiveRtId, table, columns);
    }

    /// <summary>The version of the CrateDB nodes (several, comma-separated, during a rolling upgrade).</summary>
    public async Task<string> GetStorageVersionAsync(ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT DISTINCT version['number'] AS version FROM sys.nodes ORDER BY 1", returnsRows: true),
            options.CommandTimeout, cancellationToken);
        return string.Join(",", result.Rows.Select(r => Text(r, "version")).Where(v => v.Length > 0));
    }

    // ---- COPY TO -----------------------------------------------------------------------------------------

    /// <summary>Dumps the slices of <paramref name="plan"/> one at a time; see <see cref="IStreamDataRepository.CopyToStagingAsync"/>.</summary>
    public async IAsyncEnumerable<ArchiveCopySliceResult> CopyToAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        EnsurePlanFits(target, plan, mustBeSameArchive: true);
        await JobLogFilterGuard.EnsureFilteredAsync(client, tenantId, options, target.ArchiveRtId, cancellationToken);

        // The generation map first, the data after it (concept §6): rows a recompute adds in between are of a
        // version the copied map does not name yet, and the version the map names is still in the table because
        // its sweep is deferred by the grace period.
        foreach (var slice in plan.Slices.OrderBy(s => s.Table == ArchiveCopyTable.GenerationMap ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CopySliceToAsync(target, plan, slice, location, options, cancellationToken);
            yield return result;
            if (!result.IsSuccess)
            {
                yield break; // earlier slices stay valid; the caller decides what to do with the archive
            }
        }
    }

    private async Task<ArchiveCopySliceResult> CopySliceToAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopySlice slice, ArchiveCopyStagingLocation location,
        ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sliceLocation = location.WithSubPath(slice.GetStagingPath(plan.ArchiveRtId));
        CopyStatement statement;
        if (slice.Table == ArchiveCopyTable.GenerationMap)
        {
            // A flip is visible to a search after the next refresh only; the copy is a search.
            await RefreshAsync(target.QualifiedGenerationMap, options, cancellationToken);
            statement = CopyStatement.CopyToWhole(target.QualifiedGenerationMap, sliceLocation);
        }
        else
        {
            statement = CopyStatement.CopyTo(target.QualifiedTable, target.TimeColumn,
                slice.FromUtc!.Value, slice.ToUtc!.Value, sliceLocation);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await client.ExecuteStatementAsync(tenantId, statement, options.CommandTimeout, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var warning = outcome.AffectedRows == slice.RowCount
                ? null
                : $"Counted {slice.RowCount} rows before the copy, copied {outcome.AffectedRows}.";
            if (warning is not null)
            {
                // The tenant stays online while it is dumped, so the open bucket may have changed.
                logger.LogWarning("Archive {ArchiveRtId}, slice {Slice}: {Warning}", target.ArchiveRtId, slice.Key, warning);
            }

            return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Succeeded, outcome.AffectedRows, 0, elapsed, warning);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            return Failed(target, slice, "dump", sliceLocation.Scrub(ex.Message), Stopwatch.GetElapsedTime(started));
        }
    }

    // ---- COPY FROM ---------------------------------------------------------------------------------------

    /// <summary>Loads the slices of <paramref name="plan"/> one at a time; see <see cref="IStreamDataRepository.CopyFromStagingAsync"/>.</summary>
    public IAsyncEnumerable<ArchiveCopySliceResult> CopyFromAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        CancellationToken cancellationToken)
        => CopyFromAsync(target, plan, location, options, outcome: null, cancellationToken);

    /// <summary>
    /// As above; <paramref name="outcome"/> receives the rows the tables hold when the load got to the end
    /// (<see cref="IStreamDataRepository.LoadArchiveFromStagingAsync"/> turns them into the verification).
    /// </summary>
    public async IAsyncEnumerable<ArchiveCopySliceResult> CopyFromAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        LoadOutcome? outcome, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        EnsurePlanFits(target, plan, mustBeSameArchive: false);
        await JobLogFilterGuard.EnsureFilteredAsync(client, tenantId, options, target.ArchiveRtId, cancellationToken);

        // Checks that need no data, before anything is changed.
        await EnsureColumnsExistAsync(target, plan, options, cancellationToken);
        await EnsureDiskSpaceAsync(target, plan, options, cancellationToken);
        logger.LogInformation("Loading archive {ArchiveRtId} of tenant {TenantId} into CrateDB {Version}.",
            target.ArchiveRtId, tenantId, await GetStorageVersionAsync(options, cancellationToken));

        var dataSlices = plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).ToList();
        var mapSlices = plan.Slices.Where(s => s.Table == ArchiveCopyTable.GenerationMap).ToList();
        var previous = await ReadTableSettingsAsync(target, options, cancellationToken);
        var settingsRestored = false;
        var allLoaded = true;
        try
        {
            // Inside the try: an ALTER that fails after the server applied it (command timeout, cancellation) must still
            // reach the restore in the finally; restoring the values read above is harmless when nothing was applied.
            await ApplyBulkSettingsAsync(target, options, cancellationToken);
            foreach (var slice in dataSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await CopySliceFromAsync(target, plan, slice, location, options, cancellationToken);
                yield return result;
                if (!result.IsSuccess)
                {
                    allLoaded = false;
                    yield break;
                }
            }

            // Back to the normal settings before the merge, then the merge itself (concept §7, step 6).
            await RestoreSettingsAsync(target, previous, options, CancellationToken.None);
            settingsRestored = true;
            var dataRows = await FinishLoadAsync(target, plan, options, cancellationToken);
            if (outcome is not null)
            {
                outcome.DataRows = dataRows;
            }

            // The generation map last: a pointer must never name a version whose rows are not loaded yet.
            foreach (var slice in mapSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await CopySliceFromAsync(target, plan, slice, location, options, cancellationToken);
                yield return result;
                if (!result.IsSuccess)
                {
                    allLoaded = false;
                    yield break;
                }
            }

            if (target.HasGenerationMap && outcome is not null)
            {
                await RefreshAsync(target.QualifiedGenerationMap, options, cancellationToken);
                outcome.MapRows = await CountAsync($"SELECT COUNT(*) FROM {target.QualifiedGenerationMap}", options, cancellationToken);
            }
        }
        finally
        {
            // Also after a failed slice, an exception and a caller that walks away.
            if (!settingsRestored)
            {
                await RestoreSettingsSafelyAsync(target, previous, options);
            }

            if (!allLoaded)
            {
                logger.LogWarning("Archive {ArchiveRtId} of tenant {TenantId} was not loaded completely.",
                    target.ArchiveRtId, tenantId);
            }
        }
    }

    /// <summary>
    /// Loads one slice, at most <see cref="MaxLoadAttempts"/> times in total. The first attempt sends the statement as
    /// ever; a repeat (only after a transient failure, see <see cref="CopyFailureClassifier"/>) overwrites duplicates, so
    /// rows the failed attempt already wrote are replaced by the identical rows, loads in half the batch size of the
    /// attempt before, and waits <see cref="RetryBackoff"/> first (concept §7 step 4).
    /// </summary>
    private async Task<ArchiveCopySliceResult> CopySliceFromAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopySlice slice, ArchiveCopyStagingLocation location,
        ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sliceLocation = location.WithSubPath(slice.GetStagingPath(plan.ArchiveRtId));
        var started = Stopwatch.GetTimestamp();
        ArchiveCopySliceResult? result = null;
        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await WaitBeforeRetryAsync(target, slice, attempt, options, cancellationToken);
            }

            LoadVerdict verdict;
            (result, verdict) = await LoadSliceOnceAsync(target, slice, sliceLocation, options, attempt, cancellationToken);
            if (result.IsSuccess && attempt > 1)
            {
                (result, verdict) = await VerifyRetriedSliceAsync(target, slice, result, options, attempt, cancellationToken);
            }

            if (result.IsSuccess)
            {
                if (attempt > 1)
                {
                    logger.LogInformation("Archive {ArchiveRtId}, slice {Slice} (load): loaded on attempt {Attempt} of {MaxAttempts}.",
                        target.ArchiveRtId, slice.Key, attempt, MaxLoadAttempts);
                }

                return result with { Duration = Stopwatch.GetElapsedTime(started) };
            }

            logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} (load): attempt {Attempt} of {MaxAttempts} failed: {Reason}",
                target.ArchiveRtId, slice.Key, attempt, MaxLoadAttempts, result.Message);
            if (verdict != LoadVerdict.Transient || attempt == MaxLoadAttempts)
            {
                var attempts = attempt == 1 ? result.Message : $"{result.Message} (after {attempt} attempts)";
                return result with { Message = attempts, Duration = Stopwatch.GetElapsedTime(started) };
            }
        }

        throw new InvalidOperationException("Unreachable: the attempt loop returns."); // for the compiler
    }

    private async Task<(ArchiveCopySliceResult Result, LoadVerdict Verdict)> LoadSliceOnceAsync(
        ArchiveCopyTarget target, ArchiveCopySlice slice, ArchiveCopyStagingLocation sliceLocation,
        ArchiveCopyOptions options, int attempt, CancellationToken cancellationToken)
    {
        var statement = CopyStatement.CopyFrom(
            slice.Table == ArchiveCopyTable.GenerationMap ? target.QualifiedGenerationMap : target.QualifiedTable,
            sliceLocation,
            overwriteDuplicates: attempt > 1 || options.OverwriteDuplicates,
            bulkSize: attempt > 1 ? DefaultBulkSize >> (attempt - 1) : null);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await client.ExecuteStatementAsync(tenantId, statement, options.CommandTimeout, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);

            long success = 0, errors = 0;
            var errorTexts = new List<string>();
            foreach (var row in outcome.Rows)
            {
                var rowErrors = Long(row, "error_count");
                success += Long(row, "success_count");
                errors += rowErrors;
                if (rowErrors > 0)
                {
                    errorTexts.Add(sliceLocation.Scrub(Text(row, "errors")));
                }
            }

            var verdict = CopyFailureClassifier.ClassifySummary(success, errors, slice.RowCount, errorTexts);
            if (verdict == LoadVerdict.Complete)
            {
                return (new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Succeeded, success, 0, elapsed), verdict);
            }

            var firstError = errorTexts.FirstOrDefault(t => t.Length > 0);
            var reason = errors > 0
                ? $"{errors} rows were rejected while loading ({success} loaded, {slice.RowCount} expected)." +
                  (firstError is null ? string.Empty : " First error: " + Abbreviate(firstError))
                : $"{success} rows were loaded, {slice.RowCount} were expected.";
            if (outcome.Rows.Count > 0 && verdict == LoadVerdict.Transient)
            {
                LogSummaryRows(target, slice, attempt, outcome.Rows);
            }

            return (new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Failed, success, errors, elapsed, reason), verdict);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            var reason = sliceLocation.Scrub(ex.Message);
            return (new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Failed, 0, 0, Stopwatch.GetElapsedTime(started), reason),
                CopyFailureClassifier.ClassifyFailure(ex));
        }
    }

    /// <summary>The per-file, per-node rows of a short <c>RETURN SUMMARY</c>: which node counted how many rows.</summary>
    private void LogSummaryRows(
        ArchiveCopyTarget target, ArchiveCopySlice slice, int attempt, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            logger.LogWarning(
                "Archive {ArchiveRtId}, slice {Slice} (load), attempt {Attempt}, summary row {Row} of {Rows}: node {Node}, success_count {Success}, error_count {Errors}.",
                target.ArchiveRtId, slice.Key, attempt, i + 1, rows.Count, Text(rows[i], "node"), Long(rows[i], "success_count"),
                Long(rows[i], "error_count"));
        }
    }

    /// <summary>
    /// A retried slice that reports success is counted in the table: the summary of an attempt that overwrote rows is
    /// not proof that the range holds exactly the rows of the dump.
    /// </summary>
    private async Task<(ArchiveCopySliceResult Result, LoadVerdict Verdict)> VerifyRetriedSliceAsync(
        ArchiveCopyTarget target, ArchiveCopySlice slice, ArchiveCopySliceResult loaded, ArchiveCopyOptions options,
        int attempt, CancellationToken cancellationToken)
    {
        long held;
        try
        {
            if (slice.Table == ArchiveCopyTable.GenerationMap)
            {
                held = await CountGenerationMapAsync(target, options, cancellationToken);
            }
            else
            {
                await RefreshAsync(target.QualifiedTable, options, cancellationToken);
                held = await CountAsync(CrateDbStreamDataRepository.BuildExportSliceCountSql(
                    target.QualifiedTable, target.TimeColumn, slice.FromUtc!.Value, slice.ToUtc!.Value, toInclusive: false),
                    options, cancellationToken);
            }
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            var reason = $"The rows of the slice could not be counted after attempt {attempt}: {ex.Message}";
            return (loaded with { Status = ArchiveCopySliceStatus.Failed, Message = reason }, CopyFailureClassifier.ClassifyFailure(ex));
        }

        if (held == slice.RowCount)
        {
            return (loaded, LoadVerdict.Complete);
        }

        var mismatch = $"{held} rows are in the table for the slice after attempt {attempt}, {slice.RowCount} were expected.";
        return (loaded with { Status = ArchiveCopySliceStatus.Failed, Message = mismatch }, LoadVerdict.Transient);
    }

    private async Task WaitBeforeRetryAsync(
        ArchiveCopyTarget target, ArchiveCopySlice slice, int attempt, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var heap = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
                "SELECT name, heap['used'] AS heap_used, heap['max'] AS heap_max FROM sys.nodes", returnsRows: true),
                options.CommandTimeout, cancellationToken);
            var nodes = string.Join("; ", heap.Rows.Select(r =>
                $"{Text(r, "name")} {FormatBytes(Long(r, "heap_used"))} of {FormatBytes(Long(r, "heap_max"))}"));
            logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} (load): CrateDB heap before attempt {Attempt} of {MaxAttempts}: {Heap}",
                target.ArchiveRtId, slice.Key, attempt, MaxLoadAttempts, nodes);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} (load): the heap of the nodes could not be read: {Reason}",
                target.ArchiveRtId, slice.Key, ex.Message);
        }

        await Task.Delay(RetryBackoff[attempt - 2], timeProvider, cancellationToken);
    }

    // ---- checks, bulk settings, finish -------------------------------------------------------------------

    private async Task EnsureColumnsExistAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        foreach (var dumped in plan.Tables)
        {
            var existing = (await DescribeAsync(target, dumped.Table, options, cancellationToken))
                .Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = dumped.Columns.Select(c => c.Name).Where(c => !existing.Contains(c)).ToList();
            if (missing.Count > 0)
            {
                var what = dumped.Table == ArchiveCopyTable.Data ? "The table" : "The generation map table";
                throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.ColumnMissing,
                    $"{what} of archive '{target.ArchiveRtId}' has no column {string.Join(", ", missing.Select(m => $"'{m}'"))} " +
                    "that the dump contains. Nothing was loaded.", target.ArchiveRtId);
            }
        }
    }

    private async Task EnsureDiskSpaceAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        if (plan.Slices.Count == 0 || plan.Slices.Any(s => s.StagedBytes is null))
        {
            logger.LogDebug("The sizes of the staged files of archive {ArchiveRtId} are not known; skipping the disk space check.",
                target.ArchiveRtId);
            return;
        }

        var (nodes, watermark) = await StagingDiskHeadroomCheck.ReadAsync(client, tenantId, options.CommandTimeout, cancellationToken);
        var replicas = (await ReadTableSettingsAsync(target, options, cancellationToken)).NumberOfReplicas;
        var headroom = StagingDiskHeadroomCheck.Evaluate(plan.Slices.Sum(s => s.StagedBytes!.Value), DiskSpaceFactor, replicas,
            nodes, watermark, $"Loading archive '{target.ArchiveRtId}'");
        if (!headroom.IsSufficient)
        {
            throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.InsufficientDiskSpace,
                headroom.Reason + " Nothing was loaded.", target.ArchiveRtId);
        }
    }

    /// <summary>Segments per shard the merge after a load aims at: one per <paramref name="maxDocsPerSegment"/> rows of a shard, at least one.</summary>
    internal static int SegmentsPerShard(long rows, int shards, long maxDocsPerSegment)
    {
        var perShard = rows / Math.Max(shards, 1) + (rows % Math.Max(shards, 1) == 0 ? 0 : 1);
        return (int)Math.Clamp((perShard + maxDocsPerSegment - 1) / Math.Max(maxDocsPerSegment, 1), 1, 1000);
    }

    private async Task<int> ReadShardCountAsync(ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT number_of_shards FROM information_schema.tables " +
            $"WHERE table_schema = {CopyStatement.Literal(target.Schema)} AND table_name = {CopyStatement.Literal(target.TableName)}",
            returnsRows: true), options.CommandTimeout, cancellationToken);
        var shards = result.Rows.Count == 0 ? 1 : (int)Long(result.Rows[0], "number_of_shards");
        return Math.Max(shards, 1);
    }

    private async Task<TableSettings> ReadTableSettingsAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT number_of_replicas, settings['refresh_interval'] AS refresh_interval FROM information_schema.tables " +
            $"WHERE table_schema = {CopyStatement.Literal(target.Schema)} AND table_name = {CopyStatement.Literal(target.TableName)}",
            returnsRows: true), options.CommandTimeout, cancellationToken);

        var row = result.Rows.FirstOrDefault();
        var replicas = row is null ? null : Text(row, "number_of_replicas");
        long? refresh = row is not null && row.TryGetValue("refresh_interval", out var r) && r is not null
            ? Convert.ToInt64(r, CultureInfo.InvariantCulture)
            : null;
        return new TableSettings(string.IsNullOrEmpty(replicas) ? "0-1" : replicas, refresh);
    }

    private Task ApplyBulkSettingsAsync(ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
        => client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            $"ALTER TABLE {target.QualifiedTable} SET (number_of_replicas = 0, refresh_interval = 0)"),
            options.CommandTimeout, cancellationToken);

    private async Task RestoreSettingsAsync(
        ArchiveCopyTarget target, TableSettings previous, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var refresh = previous.RefreshIntervalMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "1000";
        await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            $"ALTER TABLE {target.QualifiedTable} SET (number_of_replicas = {CopyStatement.Literal(previous.NumberOfReplicas)}, refresh_interval = {refresh})"),
            options.CommandTimeout, cancellationToken);
    }

    private async Task RestoreSettingsSafelyAsync(ArchiveCopyTarget target, TableSettings previous, ArchiveCopyOptions options)
    {
        try
        {
            await RestoreSettingsAsync(target, previous, options, CancellationToken.None);
        }
        catch (Exception ex) when (IsStatementFailure(ex))
        {
            // Reported with the values to put back by hand; the load result stands on its own.
            logger.LogError(
                "Could not restore the table settings of archive {ArchiveRtId} of tenant {TenantId}; set number_of_replicas = '{Replicas}' and refresh_interval = {Refresh} by hand: {Reason}",
                target.ArchiveRtId, tenantId, previous.NumberOfReplicas, previous.RefreshIntervalMs ?? 1000, ex.Message);
        }
    }

    private async Task<long> FinishLoadAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain($"REFRESH TABLE {target.QualifiedTable}"),
            options.CommandTimeout, cancellationToken);

        if (options.OptimizeAfterLoad)
        {
            try
            {
                var expectedRows = plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount);
                var segments = SegmentsPerShard(expectedRows, await ReadShardCountAsync(target, options, cancellationToken),
                    maxDocsPerMergedSegment);
                await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
                    $"OPTIMIZE TABLE {target.QualifiedTable} WITH (max_num_segments = {segments})"),
                    options.OptimizeTimeout, cancellationToken);
            }
            catch (Exception ex) when (IsStatementFailure(ex))
            {
                // The merge only saves space and read time; the data is complete without it.
                logger.LogWarning("The segment merge of archive {ArchiveRtId} failed: {Reason}", target.ArchiveRtId, ex.Message);
            }

            await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain($"REFRESH TABLE {target.QualifiedTable}"),
                options.CommandTimeout, cancellationToken);
        }

        var rows = await CountAsync($"SELECT COUNT(*) FROM {target.QualifiedTable}", options, cancellationToken);
        var expected = plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount);
        if (rows != expected)
        {
            logger.LogWarning("Archive {ArchiveRtId} holds {Rows} rows after the load, the dump counted {Expected}.",
                target.ArchiveRtId, rows, expected);
        }

        return rows;
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static void EnsurePlanFits(ArchiveCopyTarget target, ArchiveCopyPlan plan, bool mustBeSameArchive)
    {
        if (mustBeSameArchive && plan.ArchiveRtId != target.ArchiveRtId)
        {
            throw new ArgumentException(
                $"The plan was made for archive '{plan.ArchiveRtId}', not for '{target.ArchiveRtId}'.", nameof(plan));
        }

        var planHasMap = plan.Tables.Any(t => t.Table == ArchiveCopyTable.GenerationMap) ||
                         plan.Slices.Any(s => s.Table == ArchiveCopyTable.GenerationMap);
        if (planHasMap && !target.HasGenerationMap)
        {
            throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.ColumnMissing,
                $"The dump of archive '{plan.ArchiveRtId}' contains a generation map, but archive '{target.ArchiveRtId}' is not a rollup " +
                "and has no generation map table. Nothing was loaded.", target.ArchiveRtId);
        }
    }

    private ArchiveCopySliceResult Failed(
        ArchiveCopyTarget target, ArchiveCopySlice slice, string direction, string scrubbedReason, TimeSpan elapsed)
    {
        logger.LogWarning("Archive {ArchiveRtId}, slice {Slice} ({Direction}) failed: {Reason}",
            target.ArchiveRtId, slice.Key, direction, scrubbedReason);
        return new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Failed, 0, 0, elapsed, scrubbedReason);
    }

    /// <summary>The failures of a statement that end one slice (a cancellation is not one of them).</summary>
    private static bool IsStatementFailure(Exception ex)
        => ex is StreamDataCopyStatementException or StreamDataStatementTimeoutException;

    private async Task<long> CountAsync(string sql, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(sql, returnsRows: true),
            options.CommandTimeout, cancellationToken);
        return result.Rows.Count == 0 ? 0 : Long(result.Rows[0], result.Rows[0].Keys.First());
    }

    private async Task<(long FromInclusiveMs, long ToExclusiveMs, long RowCount)?> ReadBoundsAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        var sql = CrateDbStreamDataRepository.BuildExportBoundsSql(target.QualifiedTable, target.TimeColumn, null);
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(sql, returnsRows: true),
            options.CommandTimeout, cancellationToken);
        var row = result.Rows.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var min = AsUtc(row.GetValueOrDefault("export_min"));
        var max = AsUtc(row.GetValueOrDefault("export_max"));
        var count = Long(row, "export_count");
        if (min is null || max is null || count <= 0)
        {
            return null;
        }

        // Timestamps have millisecond precision; the end is exclusive, one step behind the last row.
        return (new DateTimeOffset(min.Value).ToUnixTimeMilliseconds(),
            new DateTimeOffset(max.Value).ToUnixTimeMilliseconds() + 1, count);
    }

    private static DateTime? AsUtc(object? value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null
    };

    private static long Long(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;

    private static string Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static string Abbreviate(string text)
        => text.Length <= MaxErrorTextCharacters ? text : text[..MaxErrorTextCharacters] + " …";

    private static string FormatBytes(long bytes)
        => bytes >= 1L << 30 ? FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GiB")
            : bytes >= 1L << 20 ? FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MiB")
            : FormattableString.Invariant($"{bytes} bytes");

    private sealed record TableSettings(string NumberOfReplicas, long? RefreshIntervalMs);
}

/// <summary>The rows the tables of an archive hold after a load that ran to the end; <c>null</c> = not reached.</summary>
internal sealed class LoadOutcome
{
    public long? DataRows { get; set; }

    public long? MapRows { get; set; }
}
