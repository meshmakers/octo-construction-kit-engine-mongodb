using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
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
/// Per-<c>rtId</c> scoped recompute is not yet implemented (the aggregation builder has no rtId
/// filter); a non-null <paramref name="rtIdScope"/> throws. The planner-produced ranges always pass
/// <c>null</c>; only a manually scoped operator request would hit this.
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

    /// <summary>Constructs the executor for one tenant.</summary>
    public CrateDbArchiveRecomputeExecutor(
        string tenantId,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IArchiveRuntimeStore archiveStore,
        int numberOfShards,
        int numberOfReplicas,
        ILogger logger)
    {
        _tenantId = tenantId;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _numberOfShards = numberOfShards;
        _numberOfReplicas = numberOfReplicas;
        _logger = logger;
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
        if (rtIdScope is not null)
        {
            throw new NotSupportedException(
                "Per-rtId scoped recompute is not yet supported (AB#4184 Phase 3c); recompute the whole window range.");
        }

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

        // Fresh staging: drop any leftover from a crashed run, recreate with the live windowed shape.
        // includeGeneration:true so the shared RollupAggregationSqlBuilder (which now always writes a
        // generation column) targets a matching schema; staging's generation is always 0 and is
        // overridden to the new generation when the staged rows are copied into the live table.
        await _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable));
        await _managementClient.ExecuteDdlAsync(_tenantId,
            ArchiveDdlGenerator.GenerateCreateWindowedTable(stagingTable, resolvedColumns, _numberOfShards, _numberOfReplicas, includeGeneration: true));

        var rows = 0;
        var windows = 0;
        foreach (var (bucketStart, bucketEnd) in
                 RecomputeBucketEnumerator.Enumerate(rangeStart, rangeEnd, rollup.BucketAlignment, rollup.BucketSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var aggregateSql = RollupAggregationSqlBuilder.Build(
                sourceTable,
                stagingTable,
                rollup.TargetCkTypeId.SemanticVersionedFullName,
                aggregations,
                bucketStart,
                bucketEnd,
                source.UsesWindowedStorage);

            rows += await _databaseClient.ExecuteNonQueryAsync(_tenantId, aggregateSql, cancellationToken);
            windows++;
        }

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

        // 2. Flip the pointer (atomic commit).
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            GenerationMapSqlBuilder.BuildUpsertPointer(
                genMapTable, rangeStart, rangeEnd, GenerationMapSqlBuilder.AllRtIdsScope, generation),
            cancellationToken);

        // 3. Sweep the now-superseded generation(s) in the range.
        await _databaseClient.ExecuteNonQueryAsync(_tenantId,
            RollupRecomputeSqlBuilder.BuildSweepSupersededGenerations(liveTable, rangeStart, rangeEnd, generation),
            cancellationToken);

        // 4. Drop staging.
        await _managementClient.ExecuteDdlAsync(_tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable));

        _logger.LogInformation(
            "Recomputed rollup {RollupRtId} range [{From:O},{To:O}) as generation {Generation}: {Windows} windows / {Rows} staged rows, pointer flipped + superseded rows swept.",
            rollup.RtId, rangeStart, rangeEnd, generation, windows, rows);

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
