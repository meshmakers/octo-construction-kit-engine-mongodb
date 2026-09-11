using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
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
/// The DELETE-then-INSERT swap is <b>not</b> atomic in CrateDB (no multi-statement transaction). The
/// staging compute still narrows the inconsistency window to a single delete+insert rather than the
/// N per-bucket in-place upserts the forward orchestrator would do. Full per-window
/// generation-pointer atomicity is layered on with the Phase 6 read path.
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
    public CrateDbArchiveRecomputeExecutor(
        string tenantId,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IArchiveRuntimeStore archiveStore,
        int numberOfShards,
        int numberOfReplicas,
        ILogger logger,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null)
    {
        _tenantId = tenantId;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _numberOfShards = numberOfShards;
        _numberOfReplicas = numberOfReplicas;
        _logger = logger;
        _rollupArchiveStore = rollupArchiveStore;
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
        await _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable));
        await _managementClient.ExecuteDdlAsync(_tenantId,
            ArchiveDdlGenerator.GenerateCreateWindowedTable(stagingTable, resolvedColumns, _numberOfShards, _numberOfReplicas, includeGeneration: true));

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

            var aggregateSql = RollupAggregationSqlBuilder.Build(
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
                carryLookback: rollup.CarryLookback);

            // This per-bucket INSERT ... SELECT is the recompute source read. It runs through
            // CrateDatabaseClient.ExecuteNonQueryAsync, which is wrapped in the shared Polly resilience
            // pipeline (per-attempt timeout + retry on the transient connector-reset class). AB#4283:
            // the range-scale timeout ("the operation was canceled" on a decade-long recompute) is now
            // handled one level up — RecomputeOrchestrator.RecomputeArchiveAsync splits [from, to) into
            // bounded bucket-aligned chunks and calls this executor once per chunk, so neither this
            // aggregate loop nor the staging→live copy / sweep below ever spans more than a chunk's
            // worth of buckets in a single statement. Do NOT add another retry loop here (it would
            // multiply against the Polly retries and amplify load on a struggling cluster).
            rows += await _databaseClient.ExecuteNonQueryAsync(_tenantId, aggregateSql, cancellationToken);
            windows++;
        }

        // CrateDB applies inserts to the read path asynchronously, so the staged aggregates are not
        // yet visible to the next statement. Refresh staging before the staging->live copy, otherwise
        // the INSERT ... SELECT FROM staging reads zero rows and the live table ends up empty.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, stagingTable);

        // ---- Atomic swap via the per-window generation pointer (Phase 6) ----
        // Instead of a non-atomic DELETE+INSERT over the live range, copy the staged rows into the
        // live table under a fresh generation (the previous generation stays visible), then flip the
        // active-generation pointer in a single-row write — the actual commit — and finally sweep the
        // superseded rows. A crash before the flip leaves readers on the previous generation; a crash
        // after the flip but before the sweep just leaves dead rows the next sweep / activation can GC.
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, rollup.RtId.ToString());
        await _managementClient.ExecuteDdlAsync(_tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable));
        var generation = await ReadNextGenerationAsync(genMapTable, cancellationToken);

        // 1. Stage → live under the new generation. Refresh so the rows are on the read path before
        //    the pointer flip makes them authoritative.
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            RollupRecomputeSqlBuilder.BuildInsertFromStagingWithGeneration(liveTable, stagingTable, columnNames, generation),
            cancellationToken);
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable);

        // 2. Flip the pointer (atomic commit) — scoped to the entity when a per-rtId recompute.
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            GenerationMapSqlBuilder.BuildUpsertPointer(
                genMapTable, rangeStart, rangeEnd, scope, generation),
            cancellationToken);

        // 3. Sweep the now-superseded generation(s) in the range (within the scope when set).
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(
                liveTable, rangeStart, rangeEnd, generation,
                string.IsNullOrEmpty(scope) ? null : scope),
            cancellationToken);

        // 3b. Drop the pointer entries the flip just made redundant (AB#5189). The pointer is keyed
        // on the exact range, so a recompute over a different range adds an entry rather than
        // replacing the old one; after the sweep above, an entry contained in this range points at
        // rows that no longer exist. Left alone they accumulate for the life of the rollup.
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            GenerationMapSqlBuilder.BuildDeleteContainedPointers(genMapTable, rangeStart, rangeEnd, scope),
            cancellationToken);

        // 4. Drop staging.
        await _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable));

        _logger.LogInformation(
            "Recomputed rollup {RollupRtId} range [{From:O},{To:O}) scope '{Scope}' as generation {Generation}: " +
            "{Windows} windows / {Rows} staged rows, pointer flipped + superseded rows swept.",
            rollup.RtId, rangeStart, rangeEnd, string.IsNullOrEmpty(scope) ? "(all)" : scope, generation, windows, rows);

        return new RecomputeExecutionResult(rows, windows);
    }

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
