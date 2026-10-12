using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB implementation of <see cref="IArchiveRecomputeExecutor"/> (AB#4184, Phase 3c). Aggregates
/// the source archive into a per-archive staging table for the whole range, then replaces the live
/// range from staging — the "shadow / staging" model. The staging table is created with the live
/// rollup's exact windowed shape (so the column copy is well-defined) and the per-bucket aggregation
/// reuses the proven <see cref="RollupAggregationSqlBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// CrateDB has no multi-statement transaction, so the range is not replaced in place. The staged
/// rows are copied into the live table under the next generation, a single-row write in the pointer
/// table makes that generation the active one — the commit — and the superseded rows are swept
/// afterwards (Phase 6).
/// </para>
/// <para>
/// Row bound (AB#6283): the copy and the sweep each run as several statements over consecutive
/// time slices of the range, and every slice is counted before it is executed
/// (<see cref="ExportSlicePlanner"/>), so no statement touches more than twice
/// <see cref="CrateRecomputeOptions.SliceTargetRows"/> rows whatever the range holds. The unit of
/// commit stays the whole call: every copy slice writes the same new generation, which no reader
/// selects before the pointer names it, and every sweep slice runs after the flip and its refresh.
/// </para>
/// <para>
/// Committed stays committed (AB#6283): once the pointer flip and its refresh have succeeded the
/// call reports the range as done. A failure of the cleanup behind the flip — the sweep, the
/// removal of contained pointer entries, the staging cleanup — is logged and not thrown, unless the
/// caller cancelled. Failing the call there would make the orchestrator aggregate and copy the whole
/// range again only to finish a delete, and keep the dependents of the rollup from being told about
/// values readers already see. The rows such a sweep leaves behind are reclaimed from the pointer
/// table: for a pointer entry of generation <c>g</c>, every reader selects a generation of at least
/// <c>g</c> inside the entry's range (and scope), so rows below <c>g</c> there are dead. The reclaim
/// runs for the entries of a failed sweep at the start of the next call for the rollup, and once
/// per process and rollup over all entries — which is what survives a restart.
/// </para>
/// <para>
/// Per-<c>rtId</c> scoped recompute (AB#4184): a non-null <c>rtIdScope</c> restricts the source
/// aggregation, the generation-pointer entry (genmap <c>rtid_scope</c>), and the post-flip sweep to
/// that single entity, so a recompute of one metering point leaves every other entity's rows (and
/// generations) in the range untouched. Planner-produced ranges pass <c>null</c> (whole range).
/// </para>
/// <para>
/// Multi-source rollups (AB#5157): the recompute orchestrator clips a dirty range to the validity
/// span of the source that produced it and calls <see cref="ExecuteAsync"/> once per source
/// sub-range with that source's <c>ArchiveSnapshot</c>. Per-source sub-range calls are safe — the
/// generation-pointer flip and the post-flip sweep are both scoped to <c>[rangeStart, rangeEnd)</c>,
/// so one source's sub-range never touches rows another source produced — but they MUST be
/// sequential: the staging table is per archive, not per range, and two overlapping executions on
/// the same rollup would race on it.
/// </para>
/// <para>
/// Visibility: CrateDB applies writes to the search path asynchronously, and every statement of the
/// commit sequence below that the next statement — or the next reader — depends on is followed by an
/// explicit refresh. That covers the pointer side-table as well as the live table: the next
/// generation number, the readers' generation predicate and the dependents' source read are all
/// search reads. A rollup <em>source</em> is read through <see cref="RollupSourceBucketAggregator"/>,
/// i.e. in its active generation only. See <c>docs/streamdata-rollup-source-generation-read.md</c>.
/// </para>
/// </remarks>
public sealed class CrateDbArchiveRecomputeExecutor : IArchiveRecomputeExecutor
{
    private readonly string _tenantId;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly IArchiveRuntimeStore _archiveStore;
    private readonly int _numberOfShards;
    private readonly int _numberOfReplicas;
    private readonly ILogger _logger;
    private readonly IRollupArchiveRuntimeStore? _rollupArchiveStore;
    private readonly RollupSourceBucketAggregator _bucketAggregator;
    private readonly int _sliceTargetRows;

    // Rollups whose pointer entries were all checked for dead rows by this process, and the entries
    // whose sweep failed and still has to be made up for. Both are keyed by the rollup's rtId.
    private readonly ConcurrentDictionary<string, bool> _reclaimPassDone = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<GenerationRange>> _pendingReclaims =
        new(StringComparer.Ordinal);

    /// <summary>Constructs the executor for one tenant.</summary>
    /// <param name="tenantId">The tenant whose CrateDB schema holds the archives.</param>
    /// <param name="databaseClient">Data-plane CrateDB client (aggregation, copy, sweep).</param>
    /// <param name="managementClient">DDL client (staging table create / drop, genmap).</param>
    /// <param name="archiveStore">Archive snapshots (the rollup's columns + aggregations).</param>
    /// <param name="numberOfShards">Shard count for the staging table.</param>
    /// <param name="numberOfReplicas">Replica count for the staging table.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="rollupArchiveStore">
    /// Rollup snapshots — needed to resolve a rollup <em>source's</em> logical specs for the
    /// per-source aggregation binding (AB#5157 §3 rule 2). Optional: without it a rollup source can
    /// only serve specs that name one of its physical columns verbatim.
    /// </param>
    /// <param name="sliceTargetRows">
    /// Rows a copy or sweep statement aims at (<see cref="CrateRecomputeOptions.SliceTargetRows"/>);
    /// a statement holds at most twice that.
    /// </param>
    public CrateDbArchiveRecomputeExecutor(
        string tenantId,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IArchiveRuntimeStore archiveStore,
        int numberOfShards,
        int numberOfReplicas,
        ILogger logger,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null,
        int sliceTargetRows = CrateRecomputeOptions.DefaultSliceTargetRows)
    {
        if (sliceTargetRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceTargetRows), "The slice target must be positive.");
        }

        _tenantId = tenantId;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _numberOfShards = numberOfShards;
        _numberOfReplicas = numberOfReplicas;
        _logger = logger;
        _rollupArchiveStore = rollupArchiveStore;
        _sliceTargetRows = sliceTargetRows;
        _bucketAggregator = new RollupSourceBucketAggregator(tenantId, databaseClient, managementClient, logger);
    }

    /// <inheritdoc />
    public async Task<RecomputeExecutionResult> ExecuteAsync(
        ArchiveSnapshot source,
        RollupArchiveSnapshot rollup,
        DateTime rangeStart,
        DateTime rangeEnd,
        OctoObjectId? rtIdScope,
        CancellationToken cancellationToken)
    {
        // Per-rtId scoped recompute (AB#4184): null = the whole range (all entities); otherwise the
        // aggregation, the generation-pointer entry, and the sweep are all restricted to this entity.
        var scope = rtIdScope?.ToString() ?? GenerationMapSqlBuilder.AllRtIdsScope;

        // Load the rollup's full archive snapshot — it carries Columns + RollupAggregations, which
        // the rollup-specific snapshot does not. Resolve the storage columns exactly as the table was
        // created, so the staging table matches the live table column-for-column.
        var rollupSnapshot = await _archiveStore.GetAsync(rollup.RtId)
            ?? throw new InvalidOperationException($"Rollup archive {rollup.RtId} not found for recompute.");
        if (rollupSnapshot.RollupAggregations is not { } aggregations)
        {
            throw new InvalidOperationException($"Archive {rollup.RtId} is not a rollup.");
        }

        var resolvedColumns = RollupColumnTypeResolver.Resolve(rollupSnapshot.Columns, aggregations);
        var columnNames = Constants.DefaultWindowedStreamDataFields
            .Concat(resolvedColumns.Select(ArchiveDdlGenerator.ResolveColumnName))
            .ToList();

        var sourceTable = TenantSchema.QualifiedArchiveTable(_tenantId, source.RtId.ToString());
        var liveTable = TenantSchema.QualifiedArchiveTable(_tenantId, rollup.RtId.ToString());
        var stagingTable = RollupRecomputeSqlBuilder.StagingTable(_tenantId, rollup.RtId.ToString());
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, rollup.RtId.ToString());

        // Dead rows first: what a failed sweep of an earlier call left, and — once per process — what
        // any earlier process left. Never fails this call; see ReclaimDeadGenerationsAsync.
        await ReclaimDeadGenerationsAsync(rollup.RtId.ToString(), liveTable, genMapTable, cancellationToken);

        // Per-source resolution (AB#5157 §3) — the same binding the forward aggregation uses for
        // this (rollup, source) pair, resolved once per execution and reused for every bucket. A
        // rollup source needs its rollup snapshot for the child-aggregation rule.
        RollupArchiveSnapshot? sourceRollup = null;
        if (source.RollupAggregations is not null)
        {
            if (_rollupArchiveStore is null)
            {
                // Same breadcrumb the forward aggregation path leaves: without the rollup store only
                // rule 1 applies, so a logical spec fails below with "no rollup snapshot".
                _logger.LogDebug(
                    "Source archive {SourceRtId} is a rollup but no rollup store is wired — only verbatim declared columns resolve.",
                    source.RtId);
            }
            else
            {
                sourceRollup = await _rollupArchiveStore.GetAsync(source.RtId);
            }
        }

        var resolvedAggregations = RollupAggregationColumns.ResolveForSource(
            aggregations, rollup.RtId, source, sourceRollup);

        // Fresh staging: drop any leftover from a crashed run, recreate with the live windowed shape.
        // includeGeneration:true so the shared RollupAggregationSqlBuilder (which now always writes a
        // generation column) targets a matching schema; staging's generation is always 0 and is
        // overridden to the new generation when the staged rows are copied into the live table.
        await _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable), cancellationToken);
        await _managementClient.ExecuteDdlAsync(_tenantId,
            ArchiveDdlGenerator.GenerateCreateWindowedTable(stagingTable, resolvedColumns, _numberOfShards, _numberOfReplicas, includeGeneration: true),
            cancellationToken);

        // Calendar buckets snap to this zone's local wall-clock boundaries (AB#4300 / O6); null ⇒
        // UTC. Resolved once per recompute — the enumerator yields the zone-correct UTC instants
        // that become each row's window_start / window_end.
        var zone = BucketBoundary.ResolveZone(rollup.ReferenceTimeZone);

        var rows = 0;
        var windows = 0;
        foreach (var (bucketStart, bucketEnd) in
                 RecomputeBucketEnumerator.Enumerate(rangeStart, rangeEnd, rollup.BucketAlignment, rollup.BucketSize, zone))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string BuildAggregateSql(IReadOnlyList<GenerationRange>? sourceGenerationRanges) =>
                RollupAggregationSqlBuilder.Build(
                    sourceTable,
                    stagingTable,
                    rollup.TargetCkTypeId.SemanticVersionedFullName,
                    resolvedAggregations,
                    bucketStart,
                    bucketEnd,
                    source.UsesWindowedStorage,
                    rtIdScope: scope,
                    // TWA carry is derived from source data with the same bounded lookback the forward
                    // aggregation uses — identical inputs ⇒ identical staged rows (AB#4336 D1).
                    carryLookback: rollup.CarryLookback,
                    sourceGenerationRanges: sourceGenerationRanges);

            // A discarded attempt may have staged a row for an entity the repeated attempt no longer
            // produces; clear the bucket from staging so it cannot be copied into the live table. The
            // delete is a search operation, so the staged rows have to be on the read path first.
            async Task DiscardStagedBucketAsync(CancellationToken ct)
            {
                await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable, CrateStatementClass.Maintenance, ct);
                await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                    RollupRecomputeSqlBuilder.BuildDeleteStagedBucket(stagingTable, bucketStart),
                    CrateStatementClass.Maintenance, ct);
                await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable, CrateStatementClass.Maintenance, ct);
            }

            // This per-bucket INSERT ... SELECT is the recompute source read. It runs through
            // CrateDatabaseClient.ExecuteNonQueryAsync, which is wrapped in the shared Polly resilience
            // pipeline (timeout + retry on the transient connector-reset class). AB#4283: the
            // orchestrator splits [from, to) into bucket-aligned chunks and calls this executor once
            // per chunk, which bounds the number of these statements per call; the rows a chunk holds
            // are bounded further down, by the slices of the copy and the sweep (AB#6283). Do NOT add
            // another retry loop here (it would multiply against the Polly retries and amplify load on
            // a struggling cluster).
            rows += await _bucketAggregator.AggregateAsync(
                source, bucketStart, bucketEnd, scope, BuildAggregateSql, DiscardStagedBucketAsync, cancellationToken);
            windows++;
        }

        // CrateDB applies inserts to the read path asynchronously, so the staged aggregates are not
        // yet visible to the next statement. Refresh staging before the staging->live copy, otherwise
        // the INSERT ... SELECT FROM staging reads zero rows and the live table ends up empty.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable, CrateStatementClass.Maintenance, cancellationToken);

        // ---- Atomic swap via the per-window generation pointer (Phase 6) ----
        // Instead of a non-atomic DELETE+INSERT over the live range, copy the staged rows into the
        // live table under a fresh generation (the previous generation stays visible), then flip the
        // active-generation pointer in a single-row write — the actual commit — and finally sweep the
        // superseded rows. A crash before the flip leaves readers on the previous generation; a crash
        // after the flip but before the sweep just leaves dead rows, which the reclaim pass removes.
        await _managementClient.ExecuteDdlAsync(_tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable), cancellationToken);
        // The next generation is MAX(generation)+1 over the pointer table — a search read. Refresh
        // first, or a run that follows another within the refresh interval is handed the same number
        // and its staged rows collide with the previous run's on the primary key.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);
        var generation = await ReadNextGenerationAsync(genMapTable, cancellationToken);
        var rowScope = string.IsNullOrEmpty(scope) ? null : scope;

        // 0. Leftover guard. The generation number comes from the pointer table alone, so a copy that
        //    was cut short before its flip (timeout, node loss, shutdown) left rows under the very
        //    number this run was just handed. They meet this run's rows on the primary key: the copy
        //    would not replace them, and a row for an entity this run no longer produces would stay
        //    and become visible with the flip. Remove them first. The count is a search read and has
        //    to see what the earlier attempt wrote — hence the refresh before it.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);
        var leftovers = await RunSlicedAsync(
            rangeStart, rangeEnd,
            (from, to) => RollupRecomputeSqlBuilder.BuildCountGeneration(liveTable, from, to, generation, rowScope),
            (from, to) => RollupRecomputeSqlBuilder.BuildDeleteGenerationSlice(liveTable, from, to, generation, rowScope),
            cancellationToken);
        if (leftovers.Rows > 0)
        {
            _logger.LogWarning(
                "Rollup {RollupRtId} range [{From:O},{To:O}) scope '{Scope}': removed {Rows} rows an earlier, uncommitted copy left under generation {Generation} before copying.",
                rollup.RtId, rangeStart, rangeEnd, rowScope ?? "(all)", leftovers.Rows, generation);
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);
        }

        // 1. Stage → live under the new generation, one statement per row-counted slice of the range
        //    (every staged window starts inside it). The slices read staging, refreshed above, and
        //    write disjoint keys, so nothing has to be refreshed between them. Refresh once at the
        //    end so the rows are on the read path before the pointer flip makes them authoritative.
        var copied = await RunSlicedAsync(
            rangeStart, rangeEnd,
            (from, to) => RollupRecomputeSqlBuilder.BuildCountStaged(stagingTable, from, to),
            (from, to) => RollupRecomputeSqlBuilder.BuildInsertFromStagingSlice(
                liveTable, stagingTable, columnNames, generation, from, to),
            cancellationToken);
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);

        // 2. Flip the pointer (atomic commit) — scoped to the entity when a per-rtId recompute.
        //    Refresh so every reader selects the new generation BEFORE the sweep removes the old one:
        //    a reader still on the old pointer after the sweep would find no rows for the range.
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            GenerationMapSqlBuilder.BuildUpsertPointer(
                genMapTable, rangeStart, rangeEnd, scope, generation),
            CrateStatementClass.Maintenance, cancellationToken);
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);

        // ---- Committed. From here on a failure is logged, not thrown (unless the caller cancelled):
        // readers are on the new generation, and what is left is cleanup. ----
        // The bounds exactly as the pointer entry holds them.
        var flipped = new GenerationRange(ToEpochMs(rangeStart), ToEpochMs(rangeEnd), scope, generation);

        // 3. Sweep the now-superseded generation(s) in the range (within the scope when set), again
        //    one statement per row-counted slice. The slices partition the WHOLE range, not only the
        //    windows that were staged: a window whose source rows are gone has no new row, and its
        //    old generation still has to go. Every slice runs after the flip and its refresh above.
        //    Refresh at the end so the deleted rows are gone from the read path when this method
        //    returns — the dependents of this rollup are recomputed right after it.
        var swept = default(SlicedRun);
        var sweepCompleted = await TryCleanupAsync("sweep of the superseded generations", rollup.RtId, flipped, async () =>
        {
            swept = await RunSlicedAsync(
                rangeStart, rangeEnd,
                (from, to) => RollupRecomputeSqlBuilder.BuildCountSuperseded(liveTable, from, to, generation, rowScope),
                (from, to) => RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(liveTable, from, to, generation, rowScope),
                cancellationToken);
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);
        }, cancellationToken);
        if (!sweepCompleted)
        {
            // Made up for at the start of the next call for this rollup.
            _pendingReclaims.GetOrAdd(rollup.RtId.ToString(), _ => new ConcurrentQueue<GenerationRange>()).Enqueue(flipped);
        }

        // 3b. Drop the pointer entries the flip just made redundant (AB#5189). The pointer is keyed
        // on the exact range, so a recompute over a different range adds an entry rather than
        // replacing the old one; an entry contained in this range is outranked by the entry just
        // flipped for every row it covers — also when the sweep above did not complete — so no
        // reader selects through it any more. Left alone they accumulate for the life of the rollup.
        await TryCleanupAsync("removal of the contained pointer entries", rollup.RtId, flipped, async () =>
        {
            await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                GenerationMapSqlBuilder.BuildDeleteContainedPointers(genMapTable, rangeStart, rangeEnd, scope),
                CrateStatementClass.Maintenance, cancellationToken);
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);
        }, cancellationToken);

        // 4. Drop staging.
        await TryCleanupAsync("staging cleanup", rollup.RtId, flipped, () =>
            _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable), cancellationToken),
            cancellationToken);

        _logger.LogInformation(
            "Recomputed rollup {RollupRtId} range [{From:O},{To:O}) scope '{Scope}' as generation {Generation}: " +
            "{Windows} windows / {Rows} staged rows copied in {CopySlices} statement(s), pointer flipped, " +
            "{SweptRows} superseded rows swept in {SweepSlices} statement(s){SweepNote}.",
            rollup.RtId, rangeStart, rangeEnd, rowScope ?? "(all)", generation, windows, rows,
            copied.Statements, swept.Rows, swept.Statements,
            sweepCompleted ? string.Empty : " — the sweep did not complete, the rest is reclaimed by the next call");

        return new RecomputeExecutionResult(rows, windows);
    }

    /// <summary>
    /// Runs a step that follows the pointer flip. A failure is logged with what an operator needs to
    /// find the rows and is not thrown — the range is committed. A cancellation by the caller is not
    /// swallowed: it propagates, and the orchestrator records an interruption.
    /// </summary>
    /// <returns><c>true</c> when the step completed.</returns>
    private async Task<bool> TryCleanupAsync(
        string step, OctoObjectId rollupRtId, GenerationRange flipped, Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Rollup {RollupRtId} range [{FromMs},{ToMs}) scope '{Scope}' is committed as generation {Generation}, but the {Step} failed: {Error}. Readers are not affected.",
                rollupRtId, flipped.StartMs, flipped.EndMs, flipped.Scope.Length == 0 ? "(all)" : flipped.Scope,
                flipped.Generation, step, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Deletes the rows no reader can select any more: inside the range (and scope) of a pointer
    /// entry, every row of a generation <b>below</b> the entry's. Such rows are what a sweep that
    /// did not complete leaves behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why they are dead: the read predicate takes, for a row, the newest pointer entry whose range
    /// and scope match it (<see cref="GenerationFilterSql"/>). A row inside an entry of generation
    /// <c>g</c> is therefore read at a generation of at least <c>g</c>. Strictly below — a row above
    /// <c>g</c> may belong to a newer recompute that overlaps the range, and rows outside every
    /// entry are the forward aggregation's generation 0.
    /// </para>
    /// <para>
    /// Two triggers: the entries whose sweep failed in this process, and — once per process and
    /// rollup — every entry of the pointer table. The second is what makes the first survive a
    /// restart, and what removes the rows left by sweeps that failed before this pass existed.
    /// </para>
    /// <para>
    /// Before every statement the entry is read again; when it is gone or names another generation
    /// (a rewind or a later recompute got in between) the entry is left alone. That narrows the
    /// interval in which a concurrent rewind could hand the range back to generation 0 under a
    /// running delete to the statement itself; it does not close it.
    /// </para>
    /// <para>
    /// A failure is logged and leaves the work for the next call; it never fails the recompute.
    /// </para>
    /// </remarks>
    private async Task ReclaimDeadGenerationsAsync(
        string rollupRtId, string liveTable, string genMapTable, CancellationToken cancellationToken)
    {
        var fullPass = !_reclaimPassDone.ContainsKey(rollupRtId);
        _pendingReclaims.TryGetValue(rollupRtId, out var pending);
        var remembered = pending?.ToArray() ?? Array.Empty<GenerationRange>();
        if (!fullPass && remembered.Length == 0)
        {
            return;
        }

        try
        {
            await _managementClient.ExecuteDdlAsync(
                _tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable), cancellationToken);
            // The entries are a search read; so is the re-read before every statement.
            await _databaseClient.RefreshArchiveTableAsync(
                _tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);

            // The pass over all entries covers the remembered ones as well.
            var entries = fullPass ? await ReadPointersAsync(genMapTable, cancellationToken) : remembered;

            var reclaimed = 0L;
            foreach (var entry in entries)
            {
                var entryScope = entry.Scope.Length == 0 ? null : entry.Scope;
                var run = await RunSlicedAsync(
                    FromEpochMs(entry.StartMs), FromEpochMs(entry.EndMs),
                    (from, to) => RollupRecomputeSqlBuilder.BuildCountBelowGeneration(liveTable, from, to, entry.Generation, entryScope),
                    (from, to) => RollupRecomputeSqlBuilder.BuildDeleteBelowGenerationSlice(liveTable, from, to, entry.Generation, entryScope),
                    cancellationToken,
                    async ct => await _databaseClient.GetCountAsync(
                        _tenantId,
                        GenerationMapSqlBuilder.BuildCountPointer(genMapTable, entry.StartMs, entry.EndMs, entry.Scope, entry.Generation),
                        ct) == 1);
                if (run.Statements > 0)
                {
                    reclaimed += run.Rows;
                    _logger.LogInformation(
                        "Rollup {RollupRtId}: reclaimed dead rows below generation {Generation} in [{FromMs},{ToMs}) scope '{Scope}' — {Rows} counted, {Statements} statement(s).",
                        rollupRtId, entry.Generation, entry.StartMs, entry.EndMs, entryScope ?? "(all)", run.Rows, run.Statements);
                }
            }

            if (reclaimed > 0)
            {
                await _databaseClient.RefreshArchiveTableAsync(
                    _tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);
            }

            // Done: drop exactly the remembered entries this pass covered (a sweep failing later
            // enqueues again), and do the pass over all entries only once.
            for (var i = 0; i < remembered.Length; i++)
            {
                pending!.TryDequeue(out _);
            }

            _reclaimPassDone[rollupRtId] = true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Rollup {RollupRtId}: reclaiming dead generations failed: {Error}. The recompute continues; the reclaim is repeated with the next call.",
                rollupRtId, ex.Message);
        }
    }

    private async Task<IReadOnlyList<GenerationRange>> ReadPointersAsync(
        string genMapTable, CancellationToken cancellationToken)
    {
        var entries = new List<GenerationRange>();
        await foreach (var row in _databaseClient.StreamRawRowsAsync(
                           _tenantId, GenerationMapSqlBuilder.BuildSelectAll(genMapTable), cancellationToken))
        {
            entries.Add(new GenerationRange(
                Convert.ToInt64(row["range_start"], CultureInfo.InvariantCulture),
                Convert.ToInt64(row["range_end"], CultureInfo.InvariantCulture),
                row.TryGetValue("rtid_scope", out var scope) && scope is not null ? scope.ToString() ?? string.Empty : string.Empty,
                Convert.ToInt64(row[Constants.Generation], CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    /// <summary>
    /// Runs one statement per time slice of <c>[rangeStart, rangeEnd)</c>, each slice sized by a
    /// count taken before it is executed: propose, count, accept or narrow
    /// (<see cref="ExportSlicePlanner"/>). The accepted slices partition the range without gap or
    /// overlap; an empty slice costs its count and no statement. A slice is executed with at most
    /// twice the target rows — except a single instant that holds more by itself, which time cannot
    /// split.
    /// </summary>
    /// <param name="rangeStart">Inclusive start of the range.</param>
    /// <param name="rangeEnd">Exclusive end of the range.</param>
    /// <param name="buildCount">Builds the count of the rows the statement would touch in a slice.</param>
    /// <param name="buildStatement">Builds the statement for a slice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="mayExecuteAsync">
    /// Asked before every statement; <c>false</c> ends the run without executing it.
    /// </param>
    /// <returns>The rows counted in the whole range and the statements executed.</returns>
    private async Task<SlicedRun> RunSlicedAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        Func<DateTime, DateTime, string> buildCount,
        Func<DateTime, DateTime, string> buildStatement,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? mayExecuteAsync = null)
    {
        var fromMs = ToEpochMs(rangeStart);
        var toMs = ToEpochMsCeiling(rangeEnd);
        var total = await _databaseClient.GetCountAsync(
            _tenantId, buildCount(FromEpochMs(fromMs), FromEpochMs(toMs)), cancellationToken);
        if (total <= 0)
        {
            return new SlicedRun(0, 0);
        }

        var planner = new ExportSlicePlanner(fromMs, toMs, total, _sliceTargetRows);
        var statements = 0;
        while (planner.TryNext(out var sliceFromMs, out var sliceToMs))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sliceFrom = FromEpochMs(sliceFromMs);
            var sliceTo = FromEpochMs(sliceToMs);
            // A proposal over the whole range was counted a moment ago.
            var sliceRows = sliceFromMs == fromMs && sliceToMs == toMs
                ? total
                : await _databaseClient.GetCountAsync(_tenantId, buildCount(sliceFrom, sliceTo), cancellationToken);

            if (!planner.Accept(sliceRows) || sliceRows <= 0)
            {
                continue;
            }

            if (mayExecuteAsync is not null && !await mayExecuteAsync(cancellationToken))
            {
                break;
            }

            await _databaseClient.ExecuteNonQueryAsync(
                _tenantId, buildStatement(sliceFrom, sliceTo), CrateStatementClass.Maintenance, cancellationToken);
            statements++;
        }

        return new SlicedRun(total, statements);
    }

    private readonly record struct SlicedRun(long Rows, int Statements);

    private static long ToEpochMs(DateTime value) => new DateTimeOffset(AsUtc(value)).ToUnixTimeMilliseconds();

    /// <summary>The first whole millisecond at or behind <paramref name="value"/>, as an exclusive end.</summary>
    private static long ToEpochMsCeiling(DateTime value)
    {
        var utc = AsUtc(value);
        var ms = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        return utc.Ticks % TimeSpan.TicksPerMillisecond == 0 ? ms : ms + 1;
    }

    private static DateTime FromEpochMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    /// <summary>
    /// Reads the next monotonic generation for the archive (<c>MAX(generation)+1</c> from the genmap
    /// table). The genmap table has just been created if absent, so the query always succeeds.
    /// </summary>
    private async Task<long> ReadNextGenerationAsync(string genMapTable, CancellationToken cancellationToken)
    {
        await foreach (var row in _databaseClient.StreamRawRowsAsync(
                           _tenantId, GenerationMapSqlBuilder.BuildNextGeneration(genMapTable), cancellationToken))
        {
            if (row.TryGetValue("next", out var value) && value is not null)
            {
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // Empty result (should not happen for an aggregate query) ⇒ first generation.
        return 1;
    }
}
