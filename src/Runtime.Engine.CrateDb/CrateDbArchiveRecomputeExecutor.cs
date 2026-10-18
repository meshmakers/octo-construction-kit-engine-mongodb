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
/// Staging table (AB#6283): one shard, no replicas — it is disposable, losing it fails one call,
/// which is retried — and it is kept between runs instead of being created and dropped by every
/// call. Per process and rollup it is dropped and created once, on first use (which also removes a
/// leftover of another shape or of a crashed process). After that a run of up to
/// <see cref="CrateRecomputeOptions.SliceTargetRows"/> staged rows issues no DDL at all: the rows
/// are deleted at the end. A larger run drops the table instead — deleting millions of staged rows
/// costs about as much as the sweep, a drop costs nothing — and the next run creates it again.
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
    private readonly ILogger _logger;
    private readonly IRollupArchiveRuntimeStore? _rollupArchiveStore;
    private readonly RollupSourceBucketAggregator _bucketAggregator;
    private readonly int _sliceTargetRows;

    // How long the rows a recompute superseded stay before the sweep (AB#6968); zero = sweep at once.
    private readonly TimeSpan _sweepGracePeriod;
    private readonly TimeProvider _clock;

    // The restore reconcile marker (AB#6968): a recompute, its reclaim and its sweep do not run on a tenant that is marked.
    // Null for the repair's own executor, which is there to clear the marker.
    private readonly Reconcile.RestoreReconcileMarker? _restoreGate;

    // Staging memo, pointer-table memo, reclaim memo and remembered failed sweeps. Shared with every
    // other executor of the process when the repository factory hands it in; see the type.
    private readonly RecomputeProcessState _state;

    /// <summary>Constructs the executor for one tenant.</summary>
    /// <param name="tenantId">The tenant whose CrateDB schema holds the archives.</param>
    /// <param name="databaseClient">Data-plane CrateDB client (aggregation, copy, sweep).</param>
    /// <param name="managementClient">DDL client (staging table create / drop, genmap).</param>
    /// <param name="archiveStore">Archive snapshots (the rollup's columns + aggregations).</param>
    /// <param name="numberOfShards">
    /// Not used any more: the staging table is always created with one shard (AB#6283). Kept so the
    /// constructor stays source compatible.
    /// </param>
    /// <param name="numberOfReplicas">
    /// Not used any more: the staging table is always created without replicas (AB#6283).
    /// </param>
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
    /// <param name="sweepGracePeriod">
    /// How long the superseded rows stay after the flip (<see cref="CrateRecomputeOptions.SweepGracePeriod"/>).
    /// <c>null</c> or zero sweeps at once, which is what a caller that builds the executor itself has always
    /// got; the repository passes the configured period.
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
        int sliceTargetRows = CrateRecomputeOptions.DefaultSliceTargetRows,
        TimeSpan? sweepGracePeriod = null)
        : this(tenantId, databaseClient, managementClient, archiveStore, logger, rollupArchiveStore,
            sliceTargetRows, new RecomputeProcessState(), sweepGracePeriod ?? TimeSpan.Zero, TimeProvider.System)
    {
        _ = numberOfShards;
        _ = numberOfReplicas;
    }

    /// <summary>
    /// Constructs the executor with the process's recompute state. This is the constructor the
    /// repository uses: an executor lives as long as its repository, i.e. one request or one
    /// orchestrator tick, and "once per process" only holds for state that is handed in.
    /// </summary>
    internal CrateDbArchiveRecomputeExecutor(
        string tenantId,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IArchiveRuntimeStore archiveStore,
        ILogger logger,
        IRollupArchiveRuntimeStore? rollupArchiveStore,
        int sliceTargetRows,
        RecomputeProcessState state,
        TimeSpan sweepGracePeriod,
        TimeProvider clock,
        Reconcile.RestoreReconcileMarker? restoreGate = null)
    {
        _restoreGate = restoreGate;
        if (sliceTargetRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sliceTargetRows), "The slice target must be positive.");
        }

        if (sweepGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepGracePeriod),
                "The sweep grace period must be zero (sweep at once) or positive.");
        }

        _sweepGracePeriod = sweepGracePeriod;
        _clock = clock;

        _tenantId = tenantId;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _logger = logger;
        _state = state;
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
        if (_restoreGate is not null && await _restoreGate.IsPendingAsync(cancellationToken))
        {
            throw new RestoreReconcilePendingException(_tenantId, rollup.RtId, "recompute");
        }

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
        // Deferred sweeps whose grace period has passed (AB#6968); the records are CrateDB rows, so this also
        // finds the ones a restart interrupted. With a grace period of zero nothing is deferred and the
        // records are not looked for (the host's sweep tick still drains the ones left from an earlier setting).
        if (_sweepGracePeriod > TimeSpan.Zero)
        {
            await SweepDueAsync(rollup.RtId, cancellationToken);
        }

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

        // An empty staging table with the live windowed shape. includeGeneration:true so the shared
        // RollupAggregationSqlBuilder (which always writes a generation column) targets a matching
        // schema; staging's generation is always 0 and is overridden to the new generation when the
        // staged rows are copied into the live table. One shard, no replicas: see the class remarks.
        var createStagingSql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
            stagingTable, resolvedColumns, numberOfShards: 1, numberOfReplicas: 0, includeGeneration: true);
        await PrepareStagingAsync(liveTable, stagingTable, createStagingSql, cancellationToken);

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
        await EnsureGenMapAsync(liveTable, genMapTable, cancellationToken);
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
        //
        //    With a sweep grace period above zero (AB#6968) neither the sweep nor the removal of the
        //    contained pointer entries runs now: both are recorded in the rollup's pending-sweep table and
        //    executed once the flip time plus the grace period has passed (SweepDueAsync). A native dump
        //    that copied the generation map before this flip still finds the rows its copy names, as long as
        //    its copy of the data starts within the grace period.
        var swept = default(SlicedRun);
        var sweepCompleted = true;
        var deferredUntil = (DateTime?)null;
        if (_sweepGracePeriod > TimeSpan.Zero)
        {
            var sweepTable = PendingSweepSqlBuilder.SweepTable(_tenantId, rollup.RtId.ToString());
            var flippedAtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            sweepCompleted = await TryCleanupAsync("recording of the deferred sweep", rollup.RtId, flipped, async () =>
            {
                await EnsureSweepTableAsync(liveTable, sweepTable, cancellationToken);
                await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                    PendingSweepSqlBuilder.BuildUpsert(sweepTable, flipped, flippedAtMs),
                    CrateStatementClass.Maintenance, cancellationToken);
                await _databaseClient.RefreshArchiveTableAsync(_tenantId, sweepTable, CrateStatementClass.Maintenance, cancellationToken);
            }, cancellationToken);
            if (sweepCompleted)
            {
                deferredUntil = FromEpochMs(flippedAtMs).Add(_sweepGracePeriod);
            }
            else
            {
                // Without the record nothing would ever sweep this entry on schedule; the reclaim of the next
                // call takes it (without the grace period) so the dead rows do not stay for good.
                _state.PendingReclaims.GetOrAdd(liveTable, _ => new ConcurrentQueue<GenerationRange>()).Enqueue(flipped);
            }
        }
        else
        {
            sweepCompleted = await TryCleanupAsync("sweep of the superseded generations", rollup.RtId, flipped, async () =>
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
                _state.PendingReclaims.GetOrAdd(liveTable, _ => new ConcurrentQueue<GenerationRange>()).Enqueue(flipped);
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
        }

        // 4. Clear staging: empty and keep a small table, drop a large one (see the class remarks).
        //    Whatever a failure leaves here, the next run finds and removes before it stages.
        await TryCleanupAsync("staging cleanup", rollup.RtId, flipped,
            () => ClearStagingAsync(liveTable, stagingTable, copied.Rows, cancellationToken),
            cancellationToken);

        _logger.LogInformation(
            "Recomputed rollup {RollupRtId} range [{From:O},{To:O}) scope '{Scope}' as generation {Generation}: " +
            "{Windows} windows / {Rows} staged rows copied in {CopySlices} statement(s), pointer flipped, " +
            "{SweptRows} superseded rows swept in {SweepSlices} statement(s){SweepNote}.",
            rollup.RtId, rangeStart, rangeEnd, rowScope ?? "(all)", generation, windows, rows,
            copied.Statements, swept.Rows, swept.Statements,
            deferredUntil is { } due
                ? $" — the sweep is deferred until {due:O} (sweep grace period {_sweepGracePeriod})"
                : sweepCompleted ? string.Empty : " — the sweep did not complete, the rest is reclaimed by the next call");

        return new RecomputeExecutionResult(rows, windows);
    }

    /// <summary>
    /// Leaves an existing, empty staging table of the given shape behind — with as little DDL as the
    /// state allows.
    /// </summary>
    private async Task PrepareStagingAsync(
        string stateKey, string stagingTable, string createSql, CancellationToken cancellationToken)
    {
        if (!_state.StagingTables.TryGetValue(stateKey, out var state) || state.CreateSql != createSql)
        {
            // First use by this process, or the rollup's columns changed: whatever is there may be
            // of another shape or hold a crashed run's rows.
            await RecreateStagingAsync(stateKey, stagingTable, createSql, cancellationToken);
            return;
        }

        if (!state.Exists)
        {
            // The last run dropped it (a large run).
            await _managementClient.ExecuteDdlAsync(_tenantId, createSql, cancellationToken);
            _state.StagingTables[stateKey] = state with { Exists = true };
            return;
        }

        // Kept from the last run. It is empty unless that run failed before clearing it, or failed
        // to clear it; the count is a search read, hence the refresh.
        long leftover;
        try
        {
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable, CrateStatementClass.Maintenance, cancellationToken);
            leftover = await _databaseClient.GetCountAsync(
                _tenantId, RollupRecomputeSqlBuilder.BuildCountAllStaged(stagingTable),
                CrateStatementClass.Maintenance, cancellationToken);
        }
        catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
        {
            // Dropped behind this process's back — by another instance after a large run, or with
            // the archive.
            await RecreateStagingAsync(stateKey, stagingTable, createSql, cancellationToken);
            return;
        }

        if (leftover <= 0)
        {
            return;
        }

        if (leftover <= _sliceTargetRows)
        {
            await _databaseClient.ExecuteNonQueryAsync(
                _tenantId, RollupRecomputeSqlBuilder.BuildDeleteAllStaged(stagingTable),
                CrateStatementClass.Maintenance, cancellationToken);
            // The aggregation upserts into staging and the copy reads it: the deleted rows have to
            // be off the read path before either.
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable, CrateStatementClass.Maintenance, cancellationToken);
            return;
        }

        await RecreateStagingAsync(stateKey, stagingTable, createSql, cancellationToken);
    }

    private async Task RecreateStagingAsync(
        string stateKey, string stagingTable, string createSql, CancellationToken cancellationToken)
    {
        await _managementClient.ExecuteDdlAsync(
            _tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable), cancellationToken);
        _state.StagingTables[stateKey] = new RecomputeProcessState.StagingTable(createSql, Exists: false);
        await _managementClient.ExecuteDdlAsync(_tenantId, createSql, cancellationToken);
        _state.StagingTables[stateKey] = new RecomputeProcessState.StagingTable(createSql, Exists: true);
    }

    /// <summary>
    /// Removes the staged rows of a finished run: a delete that keeps the table when they are few, a
    /// drop when they are many.
    /// </summary>
    private async Task ClearStagingAsync(
        string stateKey, string stagingTable, long stagedRows, CancellationToken cancellationToken)
    {
        if (stagedRows <= 0)
        {
            return;
        }

        if (stagedRows <= _sliceTargetRows)
        {
            await _databaseClient.ExecuteNonQueryAsync(
                _tenantId, RollupRecomputeSqlBuilder.BuildDeleteAllStaged(stagingTable),
                CrateStatementClass.Maintenance, cancellationToken);
            return;
        }

        await _managementClient.ExecuteDdlAsync(
            _tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable), cancellationToken);
        if (_state.StagingTables.TryGetValue(stateKey, out var state))
        {
            _state.StagingTables[stateKey] = state with { Exists = false };
        }
    }

    /// <summary>
    /// The pointer table is created at rollup activation; creating it here when absent covers
    /// rollups activated before it existed. Once per process and rollup.
    /// </summary>
    private async Task EnsureGenMapAsync(string stateKey, string genMapTable, CancellationToken cancellationToken)
    {
        if (_state.EnsuredGenMaps.ContainsKey(stateKey))
        {
            return;
        }

        await _managementClient.ExecuteDdlAsync(
            _tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable), cancellationToken);
        _state.EnsuredGenMaps[stateKey] = true;
    }

    /// <summary>
    /// The pending-sweep record table (AB#6968) is created when the first deferred sweep of a rollup is recorded.
    /// Once per process and rollup.
    /// </summary>
    private async Task EnsureSweepTableAsync(string stateKey, string sweepTable, CancellationToken cancellationToken)
    {
        if (_state.EnsuredSweepTables.ContainsKey(stateKey))
        {
            return;
        }

        await _managementClient.ExecuteDdlAsync(
            _tenantId, PendingSweepSqlBuilder.BuildCreateTable(sweepTable), cancellationToken);
        _state.EnsuredSweepTables[stateKey] = true;
    }

    private readonly record struct PendingSweep(GenerationRange Entry, long FlippedAtMs);

    /// <summary>The names of all pending-sweep record tables in the tenant's schema; one query for every rollup.</summary>
    internal async Task<HashSet<string>> ListSweepTablesAsync(CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in _databaseClient.StreamRawRowsAsync(
                           _tenantId, PendingSweepSqlBuilder.BuildListSweepTables(_tenantId), cancellationToken))
        {
            if (row.TryGetValue("table_name", out var name) && name is not null)
            {
                names.Add(name.ToString() ?? string.Empty);
            }
        }

        return names;
    }

    /// <summary>Whether the rollup has a pending-sweep record table (it exists once a sweep was deferred).</summary>
    private async Task<bool> SweepTableExistsAsync(string rollupRtId, CancellationToken cancellationToken)
        => await _databaseClient.GetCountAsync(_tenantId, PendingSweepSqlBuilder.BuildExists(_tenantId, rollupRtId),
            CrateStatementClass.Maintenance, cancellationToken) > 0;

    private async Task<IReadOnlyList<PendingSweep>> ReadSweepRecordsAsync(string sweepTable, CancellationToken cancellationToken)
    {
        // The records are a search read: the flip that wrote the last one refreshed the table, but a record
        // another process wrote may not be on the read path yet.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, sweepTable, CrateStatementClass.Maintenance, cancellationToken);
        var records = new List<PendingSweep>();
        await foreach (var row in _databaseClient.StreamRawRowsAsync(
                           _tenantId, PendingSweepSqlBuilder.BuildSelectAll(sweepTable), cancellationToken))
        {
            records.Add(new PendingSweep(
                new GenerationRange(
                    Convert.ToInt64(row["range_start"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row["range_end"], CultureInfo.InvariantCulture),
                    row.TryGetValue("rtid_scope", out var scope) && scope is not null ? scope.ToString() ?? string.Empty : string.Empty,
                    Convert.ToInt64(row[Constants.Generation], CultureInfo.InvariantCulture)),
                Convert.ToInt64(row["flipped_at_ms"], CultureInfo.InvariantCulture)));
        }

        return records;
    }

    /// <summary>
    /// Executes the deferred sweeps of one rollup whose flip time plus the sweep grace period has passed (AB#6968), and
    /// drops the records whose pointer entry is gone or was replaced. The records are CrateDB rows, so this also works
    /// after a restart. Returns how many records were executed or dropped; a failure is logged and leaves the record
    /// for the next call.
    /// </summary>
    internal async Task<int> SweepDueAsync(OctoObjectId rollupRtId, CancellationToken cancellationToken, bool tableKnownToExist = false)
    {
        var id = rollupRtId.ToString();
        var liveTable = TenantSchema.QualifiedArchiveTable(_tenantId, id);
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, id);
        var sweepTable = PendingSweepSqlBuilder.SweepTable(_tenantId, id);
        var processed = 0;
        try
        {
            if (!tableKnownToExist && !await SweepTableExistsAsync(id, cancellationToken))
            {
                return 0;
            }

            var graceMs = (long)_sweepGracePeriod.TotalMilliseconds;
            var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var due = (await ReadSweepRecordsAsync(sweepTable, cancellationToken))
                .Where(r => r.FlippedAtMs + graceMs <= nowMs)
                .OrderBy(r => r.Entry.Generation)
                .ToList();
            foreach (var record in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SweepRecordAsync(id, liveTable, genMapTable, sweepTable, record.Entry, cancellationToken);
                processed++;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Rollup {RollupRtId}: the deferred sweep failed: {Error}. The record stays; the sweep is repeated with the next call.",
                rollupRtId, ex.Message);
        }

        return processed;
    }

    private async Task SweepRecordAsync(
        string rollupRtId, string liveTable, string genMapTable, string sweepTable, GenerationRange entry,
        CancellationToken cancellationToken)
    {
        var entryScope = entry.Scope.Length == 0 ? null : entry.Scope;
        var from = FromEpochMs(entry.StartMs);
        var to = FromEpochMs(entry.EndMs);

        // The pointer entries are a search read, as is the check before every statement.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);
        async Task<bool> StillCurrentAsync(CancellationToken ct) => await _databaseClient.GetCountAsync(
            _tenantId,
            GenerationMapSqlBuilder.BuildCountPointer(genMapTable, entry.StartMs, entry.EndMs, entry.Scope, entry.Generation),
            CrateStatementClass.Maintenance, ct) == 1;

        if (await StillCurrentAsync(cancellationToken))
        {
            // Rows below the generation of the entry are dead for every reader (see ReclaimDeadGenerationsAsync); strictly
            // below, so the rows of a newer recompute that overlaps the range stay.
            var run = await RunSlicedAsync(
                from, to,
                (f, t) => RollupRecomputeSqlBuilder.BuildCountBelowGeneration(liveTable, f, t, entry.Generation, entryScope),
                (f, t) => RollupRecomputeSqlBuilder.BuildDeleteBelowGenerationSlice(liveTable, f, t, entry.Generation, entryScope),
                cancellationToken, StillCurrentAsync);
            if (run.Statements > 0)
            {
                await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable, CrateStatementClass.Maintenance, cancellationToken);
            }

            // The entries the flip made redundant (AB#5189): contained in this range, outranked by this entry.
            if (await StillCurrentAsync(cancellationToken))
            {
                await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                    GenerationMapSqlBuilder.BuildDeleteContainedPointers(genMapTable, from, to, entry.Scope),
                    CrateStatementClass.Maintenance, cancellationToken);
                await _databaseClient.RefreshArchiveTableAsync(_tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);
            }

            _logger.LogInformation(
                "Rollup {RollupRtId}: the deferred sweep of generation {Generation} in [{FromMs},{ToMs}) scope '{Scope}' ran — " +
                "{Rows} superseded rows in {Statements} statement(s).",
                rollupRtId, entry.Generation, entry.StartMs, entry.EndMs, entryScope ?? "(all)", run.Rows, run.Statements);
        }
        else
        {
            _logger.LogInformation(
                "Rollup {RollupRtId}: the pointer entry of the deferred sweep of generation {Generation} in [{FromMs},{ToMs}) scope '{Scope}' " +
                "is gone or was replaced; the record is dropped (the replacing recompute has its own).",
                rollupRtId, entry.Generation, entry.StartMs, entry.EndMs, entryScope ?? "(all)");
        }

        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            PendingSweepSqlBuilder.BuildDelete(sweepTable, entry), CrateStatementClass.Maintenance, cancellationToken);
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, sweepTable, CrateStatementClass.Maintenance, cancellationToken);
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
        var fullPass = !_state.ReclaimPassDone.ContainsKey(liveTable);
        _state.PendingReclaims.TryGetValue(liveTable, out var pending);
        var remembered = pending?.ToArray() ?? Array.Empty<GenerationRange>();
        if (!fullPass && remembered.Length == 0)
        {
            return;
        }

        try
        {
            await EnsureGenMapAsync(liveTable, genMapTable, cancellationToken);
            // The entries are a search read; so is the re-read before every statement.
            await _databaseClient.RefreshArchiveTableAsync(
                _tenantId, genMapTable, CrateStatementClass.Maintenance, cancellationToken);

            // The pass over all entries covers the remembered ones as well. An entry with a pending-sweep record
            // is left to its sweep: its older rows are not dead yet, a dump may still have to find them
            // (AB#6968); the record is executed once its grace period has passed.
            IReadOnlyList<GenerationRange> entries = remembered;
            if (fullPass)
            {
                var recorded = _sweepGracePeriod > TimeSpan.Zero
                    ? await ReadRecordedEntriesAsync(rollupRtId, cancellationToken)
                    : new HashSet<GenerationRange>();
                entries = (await ReadPointersAsync(genMapTable, cancellationToken))
                    .Where(e => !recorded.Contains(e)).ToList();
            }

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
                        CrateStatementClass.Maintenance, ct) == 1);
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

            _state.ReclaimPassDone[liveTable] = true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Rollup {RollupRtId}: reclaiming dead generations failed: {Error}. The recompute continues; the reclaim is repeated with the next call.",
                rollupRtId, ex.Message);
        }
    }

    /// <summary>The pointer entries that have a pending-sweep record (none when the rollup never deferred a sweep).</summary>
    private async Task<HashSet<GenerationRange>> ReadRecordedEntriesAsync(string rollupRtId, CancellationToken cancellationToken)
    {
        if (!await SweepTableExistsAsync(rollupRtId, cancellationToken))
        {
            return [];
        }

        var records = await ReadSweepRecordsAsync(PendingSweepSqlBuilder.SweepTable(_tenantId, rollupRtId), cancellationToken);
        return records.Select(r => r.Entry).ToHashSet();
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
        // The counts are maintenance statements like the statements they size: same budget, same
        // token, same errors.
        var total = await _databaseClient.GetCountAsync(
            _tenantId, buildCount(FromEpochMs(fromMs), FromEpochMs(toMs)), CrateStatementClass.Maintenance,
            cancellationToken);
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
                : await _databaseClient.GetCountAsync(
                    _tenantId, buildCount(sliceFrom, sliceTo), CrateStatementClass.Maintenance, cancellationToken);

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
