using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// Repairs what <see cref="RollupConsistencyValidator"/> finds (AB#5967 slice 2 / AB#6968): validate, repair, validate again.
/// Idempotent - a second run on a repaired tenant finds nothing to do - and with a dry run it lists the planned steps and changes
/// nothing.
/// </summary>
/// <remarks>
/// <para>Per rollup, in this order (each step needs the one before):</para>
/// <list type="number">
/// <item><description><b>Create the generation map</b> when the table is missing.</description></item>
/// <item><description><b>Delete the orphans</b>: the rows of a generation that no pointer entry of that or a higher generation covers.
/// The pointed generations are not touched. Before any recompute, so the generation numbers a recompute is handed are free.</description></item>
/// <item><description><b>Recompute the pointer gaps</b>: only the part of an entry that no higher entry outranks and that holds no
/// row of its generation, with the scope of the entry, once per source span (the source clips the range). The new generation replaces
/// the gap in one pointer write and the recompute sweeps what it supersedes at once (no grace period: the rows below are the repair's
/// own garbage, and no dump is waiting for them). Rows recomputed later than the pointer are not deleted; they lie outside the gap.
/// A recompute that leaves no row (the source holds nothing there) has its pointer entry removed, so the validator is ok and the
/// window reads empty - which is what the source says. An entry whose generation has no row at all is removed after its gaps.</description></item>
/// <item><description><b>Rewind the watermark</b> to the end of the rollup data (or to the first source bucket when the rollup is empty)
/// and clear the recompute generations behind it, exactly as the <c>rewindRollupWatermark</c> mutation does; the forward aggregation
/// fills the gap on its next tick.</description></item>
/// </list>
/// <para>A failing step ends the repair of its rollup, the remaining steps of it are reported as skipped and the failed step is named
/// in the report; other rollups go on. The restore reconcile marker is cleared only when nothing failed and the second validation is
/// ok (and not on a dry run).</para>
/// </remarks>
internal sealed class RollupRepairer(
    string tenantId,
    IStreamDataDatabaseClient databaseClient,
    IStreamDataDatabaseManagementClient managementClient,
    IArchiveRuntimeStore archiveStore,
    IRollupArchiveRuntimeStore rollupStore,
    IStreamDataRepository repository,
    RollupConsistencyValidator validator,
    RestoreReconcileMarker marker,
    Func<CrateDbArchiveRecomputeExecutor> executorFactory,
    ILogger logger)
{
    private sealed record Step(
        RollupRepairKind Kind, string Description, DateTime? From, DateTime? To, long? Generation,
        Func<CancellationToken, Task> Run);

    public async Task<RollupRepairReport> RepairAsync(
        IReadOnlyCollection<OctoObjectId>? rollupRtIds, bool dryRun, CancellationToken cancellationToken)
    {
        var before = await validator.ValidateAsync(rollupRtIds, cancellationToken);
        var actions = new List<RollupRepairAction>();
        string? failedStep = null;
        var touched = new List<OctoObjectId>();

        await foreach (var rollup in rollupStore.EnumerateAsync().WithCancellation(cancellationToken))
        {
            if (rollupRtIds is not null && !rollupRtIds.Contains(rollup.RtId))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var analysis = await validator.AnalyzeAsync(rollup, cancellationToken);
            var steps = await PlanAsync(rollup, analysis, cancellationToken);
            if (steps.Count == 0)
            {
                continue;
            }

            touched.Add(rollup.RtId);
            var failed = false;
            foreach (var step in steps)
            {
                if (dryRun)
                {
                    actions.Add(Action(rollup, step, RollupRepairStatus.Planned));
                    continue;
                }

                if (failed)
                {
                    actions.Add(Action(rollup, step, RollupRepairStatus.Skipped));
                    continue;
                }

                try
                {
                    await step.Run(cancellationToken);
                    actions.Add(Action(rollup, step, RollupRepairStatus.Done));
                    logger.LogInformation("Rollup {RollupRtId} of tenant {TenantId}: {Step}", rollup.RtId, tenantId, step.Description);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failed = true;
                    var error = ex.Message;
                    actions.Add(Action(rollup, step, RollupRepairStatus.Failed, error));
                    failedStep ??= $"rollup {rollup.RtId}: {step.Description} failed: {error}";
                    logger.LogError(ex, "Rollup {RollupRtId} of tenant {TenantId}: the repair step '{Step}' failed; the remaining steps of this rollup are skipped.",
                        rollup.RtId, tenantId, step.Description);
                }
            }
        }

        if (dryRun)
        {
            return new RollupRepairReport(true, before, actions, null, null);
        }

        var after = await validator.ValidateAsync(rollupRtIds, cancellationToken);
        if (failedStep is null && after.IsOk)
        {
            // Only a repaired (or never broken) tenant leaves the pending state; a failing repair keeps it.
            if (await marker.IsPendingAsync(cancellationToken))
            {
                await marker.SetAsync(false, cancellationToken);
            }
        }
        else if (failedStep is null)
        {
            logger.LogWarning("The repair of tenant {TenantId} ran without a failing step, but the rollups are still inconsistent; the restore reconcile marker stays.", tenantId);
        }

        return new RollupRepairReport(false, before, actions, after, failedStep);
    }

    // planning --------------------------------------------------------------------------------------------------

    private async Task<List<Step>> PlanAsync(RollupArchiveSnapshot rollup, RollupAnalysis analysis, CancellationToken cancellationToken)
    {
        var id = rollup.RtId.ToString();
        var live = TenantSchema.QualifiedArchiveTable(tenantId, id);
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantId, id);
        var steps = new List<Step>();

        if (analysis.MissingGenerationMap)
        {
            steps.Add(new Step(RollupRepairKind.CreateGenerationMap,
                $"Create the generation map table of rollup {id}.", null, null, null,
                ct => managementClient.ExecuteDdlAsync(tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMap), ct)));
        }

        foreach (var orphan in analysis.Orphans)
        {
            var captured = orphan;
            steps.Add(new Step(RollupRepairKind.DeleteOrphans,
                $"Delete {captured.Rows} row(s) of generation {captured.Generation} that no pointer entry of that or a higher generation covers.",
                RollupConsistencyValidator.FromMs(captured.FirstWindowStartMs), RollupConsistencyValidator.FromMs(captured.LastWindowStartMs + 1),
                captured.Generation,
                async ct =>
                {
                    await databaseClient.ExecuteNonQueryAsync(tenantId,
                        RollupConsistencySql.BuildDeleteOrphans(live, captured.Generation, captured.Covering), CrateStatementClass.Maintenance, ct);
                    await databaseClient.RefreshArchiveTableAsync(tenantId, live, CrateStatementClass.Maintenance, ct);
                }));
        }

        foreach (var byEntry in analysis.PointerGaps.GroupBy(g => g.Entry))
        {
            var entry = byEntry.Key;
            foreach (var gap in byEntry)
            {
                var captured = gap;
                steps.Add(new Step(RollupRepairKind.RecomputePointerRange,
                    $"Recompute [{RollupConsistencyValidator.FromMs(captured.FromMs):O}, {RollupConsistencyValidator.FromMs(captured.ToMs):O})" +
                    (entry.Scope.Length == 0 ? string.Empty : $" of entity {entry.Scope}") +
                    $", which the pointer names as generation {entry.Generation} without rows.",
                    RollupConsistencyValidator.FromMs(captured.FromMs), RollupConsistencyValidator.FromMs(captured.ToMs), entry.Generation,
                    ct => RecomputeGapAsync(rollup, captured, genMap, live, ct)));
            }

            steps.Add(new Step(RollupRepairKind.RecomputePointerRange,
                $"Remove the pointer entry of generation {entry.Generation} when the table holds no row of it (all its windows are recomputed).",
                RollupConsistencyValidator.FromMs(entry.StartMs), RollupConsistencyValidator.FromMs(entry.EndMs), entry.Generation,
                ct => RemoveEntryIfEmptyAsync(entry, genMap, live, ct)));
        }

        if (analysis.Watermark is { } gap2)
        {
            var planned = await WatermarkTargetAsync(rollup, gap2, cancellationToken);
            if (planned is { } plannedTo)
            {
                // Planned from the first validation, decided again when it runs: the recompute of a pointer gap before it can
                // have filled the rollup up to the watermark, and then there is nothing to rewind (and nothing to clear).
                steps.Add(new Step(RollupRepairKind.RewindWatermark,
                    $"Rewind the watermark from {gap2.Watermark:O} to {plannedTo:O} (the end of the rollup data) if it is still ahead of the rows " +
                    "once the steps before have run; the forward aggregation fills the gap.",
                    plannedTo, gap2.Watermark, null,
                    ct => RewindWatermarkAsync(rollup, live, ct)));
            }
        }

        return steps;
    }

    private async Task RewindWatermarkAsync(RollupArchiveSnapshot rollup, string live, CancellationToken cancellationToken)
    {
        var current = await rollupStore.GetAsync(rollup.RtId) ?? rollup;
        await databaseClient.RefreshArchiveTableAsync(tenantId, live, CrateStatementClass.Maintenance, cancellationToken);
        if (await validator.CheckWatermarkAsync(current, live, cancellationToken) is not { } gap)
        {
            logger.LogInformation("Rollup {RollupRtId}: the watermark is no longer ahead of the rows; it is left alone.", rollup.RtId);
            return;
        }

        if (await WatermarkTargetAsync(current, gap, cancellationToken) is not { } to)
        {
            throw new InvalidOperationException($"Cannot find the end of the data of rollup {rollup.RtId} to rewind its watermark to.");
        }

        await rollupStore.AdvanceWatermarkAsync(rollup.RtId, to, allowRewind: true);
        // Pointer entries reaching past the boundary would hide the rows the forward aggregation writes (generation 0).
        await repository.ClearRecomputeGenerationsAsync(rollup.RtId, to, cancellationToken);
    }

    private async Task<DateTime?> WatermarkTargetAsync(RollupArchiveSnapshot rollup, WatermarkGap gap, CancellationToken cancellationToken)
    {
        var zone = BucketBoundary.ResolveZone(rollup.ReferenceTimeZone);
        if (gap.LastRowEnd is { } end)
        {
            return BucketBoundary.AlignDown(end, rollup.BucketAlignment, rollup.BucketSize, zone);
        }

        // No rollup row at all: start at the bucket of the earliest source row.
        var first = await validator.ReadEarliestSourceTimeAsync(rollup, cancellationToken);
        return first is { } time ? BucketBoundary.AlignDown(time, rollup.BucketAlignment, rollup.BucketSize, zone) : null;
    }

    // recompute of a gap ------------------------------------------------------------------------------------------

    private async Task RecomputeGapAsync(
        RollupArchiveSnapshot rollup, PointerGap gap, string genMap, string live, CancellationToken cancellationToken)
    {
        var executor = executorFactory();
        var scope = gap.Entry.Scope;
        var scopeId = scope.Length == 0 ? (OctoObjectId?)null : OctoObjectId.Parse(scope);
        var from = RollupConsistencyValidator.FromMs(gap.FromMs);
        var to = RollupConsistencyValidator.FromMs(gap.ToMs);

        await databaseClient.RefreshArchiveTableAsync(tenantId, genMap, CrateStatementClass.Maintenance, cancellationToken);
        var highestBefore = (await validator.ReadPointersAsync(genMap, cancellationToken)).Select(p => p.Generation).DefaultIfEmpty(0).Max();

        var recomputed = false;
        foreach (var reference in rollup.Sources)
        {
            if (reference.Clip(from, to) is not { } clipped)
            {
                continue;
            }

            var source = await archiveStore.GetAsync(reference.SourceArchiveRtId)
                ?? throw new InvalidOperationException($"Source archive {reference.SourceArchiveRtId} of rollup {rollup.RtId} was not found.");
            await executor.ExecuteAsync(source, rollup, clipped.From, clipped.To, scopeId, cancellationToken);
            recomputed = true;
        }

        if (!recomputed)
        {
            logger.LogWarning("Rollup {RollupRtId}: no source covers [{From:O}, {To:O}); nothing to recompute there.", rollup.RtId, from, to);
        }

        // An entry the recompute wrote that holds no row: the source has nothing in its range. Its generation, and everything
        // below it in the range, is gone (the recompute swept at once), so the windows read empty either way - drop the entry
        // so that the validator, which asks for rows behind every pointer, is ok.
        await databaseClient.RefreshArchiveTableAsync(tenantId, genMap, CrateStatementClass.Maintenance, cancellationToken);
        await databaseClient.RefreshArchiveTableAsync(tenantId, live, CrateStatementClass.Maintenance, cancellationToken);
        foreach (var created in (await validator.ReadPointersAsync(genMap, cancellationToken)).Where(p => p.Generation > highestBefore))
        {
            await RemoveEntryIfEmptyAsync(created, genMap, live, cancellationToken);
        }
    }

    private async Task RemoveEntryIfEmptyAsync(GenerationRange entry, string genMap, string live, CancellationToken cancellationToken)
    {
        var rows = await databaseClient.GetCountAsync(tenantId,
            RollupRecomputeSqlBuilder.BuildCountGeneration(live, RollupConsistencyValidator.FromMs(entry.StartMs),
                RollupConsistencyValidator.FromMs(entry.EndMs), entry.Generation, entry.Scope.Length == 0 ? null : entry.Scope),
            CrateStatementClass.Maintenance, cancellationToken);
        if (rows > 0)
        {
            return;
        }

        await databaseClient.ExecuteNonQueryAsync(tenantId,
            GenerationMapSqlBuilder.BuildDeletePointer(genMap, entry.StartMs, entry.EndMs, entry.Scope, entry.Generation),
            CrateStatementClass.Maintenance, cancellationToken);
        await databaseClient.RefreshArchiveTableAsync(tenantId, genMap, CrateStatementClass.Maintenance, cancellationToken);
    }

    private static RollupRepairAction Action(RollupArchiveSnapshot rollup, Step step, RollupRepairStatus status, string? error = null)
        => new(rollup.RtId, step.Kind, step.Description, status, step.From, step.To, step.Generation, error);
}
