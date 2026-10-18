using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// Dumps one archive completely and applies the <b>repeat rule</b> of a rollup's generation map (AB#5967 slice 2,
/// AB#6968; concept <c>concept-tenant-backup-native-archive-dump.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// A rollup is two tables, and a recompute changes both: it writes the new version's rows, then flips the pointer in
/// the map, then removes the old version. No statement sees both tables at one instant. The dump therefore takes the
/// map <em>first</em> (read it, copy it), then the data, then reads the map <em>again</em>. When the two reads
/// disagree a recompute flipped a pointer in between; the copy of this attempt may name a version the data copy
/// does not hold (or hold rows of a version the map does not name), so the rollup is copied again — at most
/// <see cref="ArchiveCopyOptions.MaxMapAttempts"/> times, then the dump fails and names the rollup.
/// </para>
/// <para>
/// What the repeat rule does not cover, the deferred sweep does (<c>RecomputeSweepOptions.GracePeriod</c>): a version the
/// copied map names is still in the table when the data is copied, as long as the copy takes less than the grace
/// period. The result carries both times (<see cref="ArchiveCopyDumpResult.MapReadAtUtc"/> and
/// <see cref="ArchiveCopyDumpResult.LastDataCopiedAtUtc"/>) so the caller can tell.
/// </para>
/// <para>An archive without a generation map is copied once and its map is never read.</para>
/// </remarks>
internal sealed class RollupArchiveDumper(
    ILogger logger, string tenantId, ICrateCopyClient client, StagingCopyRunner runner, TimeProvider clock)
{
    /// <summary>One pointer entry of the map: a range of window starts, the scope and the generation that is active there.</summary>
    internal readonly record struct MapEntry(long RangeStartMs, long RangeEndMs, string Scope, long Generation);

    public async Task<ArchiveCopyDumpResult> DumpAsync(
        ArchiveCopyTarget target, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var time = new MonotonicClock(clock);

        if (!target.HasGenerationMap)
        {
            var plan = await runner.PlanAsync(target, options, cancellationToken);
            var results = await CopyAsync(target, plan, location, options, cancellationToken);
            return new ArchiveCopyDumpResult(target.ArchiveRtId, CopiedPlan(plan, results), results, 1, null,
                results.Count == 0 ? null : time.Now());
        }

        for (var attempt = 1; attempt <= options.MaxMapAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await runner.PlanAsync(target, options, cancellationToken);

            var before = await ReadMapAsync(target, options, cancellationToken);
            var mapReadAt = time.Now();
            if (options.PhaseObserver is { } observer)
            {
                await observer(ArchiveCopyPhase.MapRead, attempt, cancellationToken);
            }

            var results = await CopyAsync(target, plan, location, options, cancellationToken);
            var lastDataCopiedAt = results.Count == 0 ? (DateTime?)null : time.Now();
            if (results.Count != plan.Slices.Count || results.Any(r => !r.IsSuccess))
            {
                // A failed slice ends the dump of this archive; the result says which one.
                return new ArchiveCopyDumpResult(target.ArchiveRtId, CopiedPlan(plan, results), results, attempt, mapReadAt, lastDataCopiedAt);
            }

            if (options.PhaseObserver is { } dataObserver)
            {
                await dataObserver(ArchiveCopyPhase.DataCopied, attempt, cancellationToken);
            }

            if (!options.RepeatOnMovedMap)
            {
                // Characterisation mode: no second read, no comparison - whatever the recompute did meanwhile is in the files.
                return new ArchiveCopyDumpResult(target.ArchiveRtId, CopiedPlan(plan, results), results, attempt, mapReadAt, lastDataCopiedAt);
            }

            var after = await ReadMapAsync(target, options, cancellationToken);
            if (SameMap(before, after))
            {
                logger.LogInformation(
                    "Dumped rollup {RollupRtId} of tenant {TenantId} in {Attempts} attempt(s): {Entries} pointer entries, " +
                    "map read at {MapReadAt:O}, last data slice at {LastData:O}.",
                    target.ArchiveRtId, tenantId, attempt, before.Count, mapReadAt, lastDataCopiedAt);
                return new ArchiveCopyDumpResult(target.ArchiveRtId, CopiedPlan(plan, results), results, attempt, mapReadAt, lastDataCopiedAt);
            }

            logger.LogWarning(
                "The generation map of rollup {RollupRtId} of tenant {TenantId} changed while it was dumped " +
                "(attempt {Attempt} of {MaxAttempts}: {Before} entries before, {After} after); copying the rollup again.",
                target.ArchiveRtId, tenantId, attempt, options.MaxMapAttempts, before.Count, after.Count);
        }

        throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.GenerationMapKeptMoving,
            $"The generation map of rollup '{target.ArchiveRtId}' changed during each of {options.MaxMapAttempts} attempts " +
            "to dump it (a recompute keeps flipping its pointers). The rollup was not dumped consistently; " +
            "dump it again when no recompute runs, or raise the number of attempts.", target.ArchiveRtId);
    }

    /// <summary>
    /// The plan as the files stand: each slice carries the rows the copy reported, not the rows counted before it.
    /// The tenant stays online while it is dumped, so the two can differ; a load verifies a slice against the number
    /// of rows in its files. Slices that were not copied (a failed dump) keep their counted rows.
    /// </summary>
    internal static ArchiveCopyPlan CopiedPlan(ArchiveCopyPlan plan, IReadOnlyList<ArchiveCopySliceResult> results)
    {
        var copied = results.Where(r => r.IsSuccess).ToDictionary(r => r.Slice.Key, r => r.CopiedRows, StringComparer.Ordinal);
        return plan with
        {
            Slices = plan.Slices
                .Select(s => copied.TryGetValue(s.Key, out var rows) && rows != s.RowCount
                    ? new ArchiveCopySlice(s.Table, s.Key, rows, s.FromUtc, s.ToUtc, s.StagedBytes)
                    : s)
                .ToList()
        };
    }

    /// <summary>Reads the whole generation map (after a refresh: a search sees the last refresh only).</summary>
    internal async Task<IReadOnlyList<MapEntry>> ReadMapAsync(
        ArchiveCopyTarget target, ArchiveCopyOptions options, CancellationToken cancellationToken)
    {
        await runner.RefreshAsync(target.QualifiedGenerationMap, options, cancellationToken);
        var result = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" " +
            $"FROM {target.QualifiedGenerationMap}", returnsRows: true), options.CommandTimeout, cancellationToken);

        return result.Rows
            .Select(r => new MapEntry(Long(r, "range_start"), Long(r, "range_end"), Text(r, "rtid_scope"), Long(r, "generation")))
            .OrderBy(e => e.RangeStartMs).ThenBy(e => e.RangeEndMs).ThenBy(e => e.Scope, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when both maps hold the same entries (same ranges, scopes and generations).</summary>
    internal static bool SameMap(IReadOnlyList<MapEntry> a, IReadOnlyList<MapEntry> b)
        => a.Count == b.Count && a.SequenceEqual(b);

    private async Task<List<ArchiveCopySliceResult>> CopyAsync(
        ArchiveCopyTarget target, ArchiveCopyPlan plan, ArchiveCopyStagingLocation location, ArchiveCopyOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<ArchiveCopySliceResult>();
        await foreach (var result in runner.CopyToAsync(target, plan, location, options, cancellationToken))
        {
            results.Add(result);
        }

        return results;
    }

    private static long Long(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : 0;

    private static string Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    /// <summary>A UTC clock that never goes backwards, so the map read is never later than the last data slice.</summary>
    private sealed class MonotonicClock(TimeProvider clock)
    {
        private DateTime _last = DateTime.MinValue;

        public DateTime Now()
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (now < _last)
            {
                now = _last;
            }

            _last = now;
            return now;
        }
    }
}
