using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB implementation of <see cref="IStreamDataRepository"/>.
/// Encapsulates query orchestration, field resolution, pagination, and result transformation.
/// </summary>
internal class CrateDbStreamDataRepository : IStreamDataRepository, IArchiveRecomputeExecutor
{
    /// <summary>Rows read + written per page during an active-archive computed-column backfill (§8).</summary>
    private const int BackfillPageSize = 1000;

    private readonly ILogger<CrateDbStreamDataRepository> _logger;
    private CrateDbArchiveRecomputeExecutor? _recomputeExecutor;
    private readonly ICkCacheService _ckCacheService;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly IArchiveRuntimeStore _archiveStore;
    private readonly IRollupArchiveRuntimeStore? _rollupArchiveStore;
    private readonly StreamDataConfiguration _configuration;
    private readonly string _tenantId;
    private readonly IFormulaEngine _formulaEngine;
    private readonly IArchiveRecomputeStateStore? _recomputeStateStore;

    public CrateDbStreamDataRepository(
        ILogger<CrateDbStreamDataRepository> logger,
        ICkCacheService ckCacheService,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IOptions<StreamDataConfiguration> configuration,
        string tenantId,
        IArchiveRuntimeStore archiveStore,
        IFormulaEngine formulaEngine,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null,
        IArchiveRecomputeStateStore? recomputeStateStore = null)
    {
        _logger = logger;
        _ckCacheService = ckCacheService;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _archiveStore = archiveStore;
        _rollupArchiveStore = rollupArchiveStore;
        _configuration = configuration.Value;
        _tenantId = tenantId;
        _formulaEngine = formulaEngine;
        _recomputeStateStore = recomputeStateStore;
    }

    public Task EnsureDatabaseCreatedAsync()
    {
        // After T17 there is no per-tenant control plane table — schemas are created implicitly
        // by CrateDB the first time an archive table inside them is provisioned. Keep the method
        // as a no-op so the callsite that opts a tenant into stream data still has a hook.
        return Task.CompletedTask;
    }

    public Task DeleteDatabaseAsync()
    {
        return _managementClient.DeleteStreamDataDatabaseAsync(_tenantId);
    }

    public async Task EnsureArchiveCreatedAsync(ArchiveSnapshot snapshot)
    {
        // The tenant schema is implicit in the qualified table identifier — CrateDB creates the
        // schema on first table inside it, so no separate `CREATE SCHEMA` step is needed.
        //
        // Subtype branches:
        // - Rollup: columns are derived storage names (temperature_avg_sum, etc.); SQL type from
        //   the aggregation function (RollupColumnTypeResolver).
        // - Time-range: columns are CK-type attribute paths (resolved like raw archives), but the
        //   table shape uses (window_start, window_end) + was_updated instead of a single
        //   timestamp. See ArchiveDdlGenerator.GenerateCreateTimeRangeTable.
        // - Raw: columns are CK-type attribute paths; standard (timestamp, rtid, ckTypeId) PK.
        // Validate computed columns before provisioning — concept §9. Runs for every archive shape:
        // raw / time-range computed columns reference ingested columns, rollup-internal computed
        // columns (concept §11) reference the aggregate output columns by their physical name. The
        // validator skips non-computed columns, so it is a no-op for archives without formulas.
        ComputedColumnValidator.Validate(snapshot.RtId, snapshot.Columns, _formulaEngine);

        var resolvedColumns = snapshot.RollupAggregations is { } aggs
            ? RollupColumnTypeResolver.Resolve(snapshot.Columns, aggs)
            : ArchivePathTypeResolver.Resolve(
                _ckCacheService, _tenantId, snapshot.TargetCkTypeId, snapshot.Columns);

        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, snapshot.RtId.ToString());
        var shape = snapshot.RollupAggregations is not null ? "rollup"
            : snapshot.IsTimeRange ? "time-range"
            : "raw";

        if (snapshot.UsesWindowedStorage)
        {
            // Drop the table if a pre-Phase-7 single-timestamp shape is sitting in CrateDB —
            // RollupArchive tables provisioned before the storage unification have a `timestamp`
            // column instead of `(window_start, window_end)`. The orchestrator's new aggregation
            // SQL would fail with column-not-found; recreating loses already-aggregated data, but
            // the orchestrator will backfill on the next watermark advance (concept-time-range §6).
            // Rollup archives carry the Phase-6 generation column (and a generation-map side-table)
            // so partial-range recomputes can flip a per-window pointer atomically. Time-range
            // archives are externally fed and never recomputed through that path, so they stay on the
            // plain windowed shape.
            var isRollup = snapshot.RollupAggregations is not null;

            await EnsureWindowedTableShapeAsync(snapshot.RtId.ToString(), requireGenerationColumn: isRollup);

            var sql = ArchiveDdlGenerator.GenerateCreateWindowedTable(
                qualifiedTable, resolvedColumns, _configuration.NumberOfShards, _configuration.NumberOfReplicas,
                includeGeneration: isRollup);
            _logger.LogDebug(
                "Provisioning windowed archive table {Table} with {ColumnCount} user columns for tenant {TenantId} (shape: {Shape})",
                qualifiedTable, resolvedColumns.Count, _tenantId, shape);
            await _managementClient.ExecuteDdlAsync(_tenantId, sql);

            if (isRollup)
            {
                // Created empty at activation so the read path can always issue a plain SELECT against
                // it (no rows ⇒ no generation predicate ⇒ all steady-state generation-0 rows returned).
                var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, snapshot.RtId.ToString());
                await _managementClient.ExecuteDdlAsync(_tenantId, GenerationMapSqlBuilder.BuildCreateTable(genMapTable));
            }
        }
        else
        {
            var sql = ArchiveDdlGenerator.GenerateCreateTable(
                qualifiedTable, resolvedColumns, _configuration.NumberOfShards, _configuration.NumberOfReplicas);
            _logger.LogDebug(
                "Provisioning raw archive table {Table} with {ColumnCount} user columns for tenant {TenantId}",
                qualifiedTable, resolvedColumns.Count, _tenantId);
            await _managementClient.ExecuteDdlAsync(_tenantId, sql);
        }
    }

    /// <summary>
    /// Migration helper for the windowed-storage shapes. Drops an existing CrateDB archive table when
    /// its shape predates what the current model needs, so the subsequent <c>CREATE TABLE IF NOT
    /// EXISTS</c> recreates it correctly:
    /// <list type="bullet">
    /// <item>Phase-7: a table lacking the <c>window_start</c> column is a pre-windowed
    /// single-<c>timestamp</c> RollupArchive — drop it.</item>
    /// <item>Phase-6 (AB#4184): when <paramref name="requireGenerationColumn"/> is true (rollup
    /// archives), a windowed table that lacks the <c>generation</c> column predates the generation
    /// pointer. Its PK does not include <c>generation</c>, so forward aggregation's
    /// <c>ON CONFLICT (…, generation)</c> would fail — drop it so it is recreated with the
    /// generation column keyed into the PK.</item>
    /// </list>
    /// Both drops lose already-aggregated bucket rows; the orchestrator re-aggregates them on the
    /// next watermark advance. No-op for fresh activations and tables already on the right shape.
    /// </summary>
    private async Task EnsureWindowedTableShapeAsync(string archiveRtId, bool requireGenerationColumn)
    {
        var schemaName = TenantSchema.SchemaName(_tenantId);
        var tableName = "archive_" + archiveRtId;
        var quotedTable = $"\"{schemaName}\".\"{tableName}\"";

        // information_schema.columns is the cheapest portable shape probe. CrateDB returns 1 if
        // window_start exists on the table, 0 if the table is missing OR has the legacy single-
        // timestamp shape. Distinguish by also checking whether the table itself exists.
        var hasWindowStartCol = await _databaseClient.GetCountAsync(_tenantId,
            $"SELECT count(*) FROM information_schema.columns " +
            $"WHERE table_schema = '{schemaName.Replace("'", "''")}' " +
            $"AND table_name = '{tableName.Replace("'", "''")}' " +
            $"AND column_name = '{Constants.WindowStart}'");
        if (hasWindowStartCol > 0)
        {
            // Already windowed. Phase-6 self-heal: a rollup table provisioned before Phase 6 has the
            // windowed shape but no `generation` column (which is now part of the rollup PK). Drop it
            // so CREATE recreates it with the generation-keyed PK; the orchestrator backfills.
            if (requireGenerationColumn)
            {
                var hasGenerationCol = await _databaseClient.GetCountAsync(_tenantId,
                    $"SELECT count(*) FROM information_schema.columns " +
                    $"WHERE table_schema = '{schemaName.Replace("'", "''")}' " +
                    $"AND table_name = '{tableName.Replace("'", "''")}' " +
                    $"AND column_name = '{Constants.Generation}'");
                if (hasGenerationCol == 0)
                {
                    _logger.LogWarning(
                        "Migrating rollup archive table {Table} to the Phase-6 generation-pointer shape: " +
                        "dropping the existing table (it predates the generation column / PK); the orchestrator " +
                        "will re-aggregate on the next watermark advance (any persisted bucket rows are lost).",
                        quotedTable);
                    await _managementClient.ExecuteDdlAsync(_tenantId, ArchiveDdlGenerator.GenerateDropTable(quotedTable));
                }
            }

            return; // already on the windowed shape (generation handled above for rollups)
        }

        var tableExists = await _databaseClient.GetCountAsync(_tenantId,
            $"SELECT count(*) FROM information_schema.tables " +
            $"WHERE table_schema = '{schemaName.Replace("'", "''")}' " +
            $"AND table_name = '{tableName.Replace("'", "''")}'");
        if (tableExists == 0)
        {
            return; // fresh activation, nothing to migrate
        }

        _logger.LogWarning(
            "Migrating archive table {Table} from pre-Phase-7 single-timestamp shape to windowed shape: " +
            "dropping the existing table; the orchestrator will re-aggregate on the next watermark advance " +
            "(any persisted bucket rows are lost in this drop).",
            quotedTable);
        await _managementClient.ExecuteDdlAsync(_tenantId, ArchiveDdlGenerator.GenerateDropTable(quotedTable));
    }

    /// <inheritdoc />
    public async Task DeleteArchiveAsync(OctoObjectId archiveRtId)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var sql = ArchiveDdlGenerator.GenerateDropTable(qualifiedTable);
        _logger.LogDebug("Dropping archive table {Table} for tenant {TenantId}", qualifiedTable, _tenantId);
        await _managementClient.ExecuteDdlAsync(_tenantId, sql);

        // Drop the Phase-6 generation-map side-table too (IF EXISTS — no-op for raw / time-range
        // archives that never had one).
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, archiveRtId.ToString());
        await _managementClient.ExecuteDdlAsync(_tenantId, GenerationMapSqlBuilder.BuildDropIfExists(genMapTable));
    }

    public async Task InsertAsync(OctoObjectId archiveRtId, StreamDataPoint datapoint)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);

        // See the bulk overload for the rationale; per-archive tables only accept rows of the
        // archive's own TargetCkTypeId.
        if (datapoint.CkTypeId != snapshot.TargetCkTypeId)
        {
            _logger.LogDebug(
                "Archive {ArchiveRtId} (target {TargetCkTypeId}): skipped 1 datapoint with mismatched CkTypeId {ActualCkTypeId}",
                archiveRtId, snapshot.TargetCkTypeId, datapoint.CkTypeId);
            return;
        }

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.insert");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());

        var (qualifiedTable, userColumnNames) = ResolveTableAndColumns(snapshot, archiveRtId);

        var sw = Stopwatch.StartNew();
        var computedPlan = BuildComputedPlan(snapshot);
        var dto = MapToDataPointDto(datapoint, computedPlan);
        await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, dto);
        sw.Stop();

        CrateDbDiagnostics.InsertDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()),
            new("batch_size_bucket", CrateDbDiagnostics.BatchSizeBucket(1)));
        CrateDbDiagnostics.InsertedPoints.Add(1,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()));

        await DetectAndRecordRetroactiveWriteAsync(
            archiveRtId, new[] { datapoint.Timestamp }, RecomputeChangeSource.Pipeline);
    }

    public async Task InsertAsync(OctoObjectId archiveRtId, IEnumerable<StreamDataPoint> datapoints)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);

        // Materialise once so we can both count and pass to the client.
        var materialised = datapoints as IReadOnlyList<StreamDataPoint> ?? datapoints.ToList();

        // Filter to items whose CkTypeId matches the archive's TargetCkTypeId. An archive is
        // schema-bound to one CkType: its user columns come from that type's attribute graph and
        // none of the others. Heterogeneous callers (e.g. the Loxone pipeline that flattens both
        // Control updates and EnergyIQ/Space mapping updates into a single _updateItems list)
        // would otherwise push rows of the wrong type into the table — semantically meaningless
        // and a frequent root cause of CrateDB connection-state pollution after a failed insert.
        var filtered = materialised.Where(p => p.CkTypeId == snapshot.TargetCkTypeId).ToList();
        var skipped = materialised.Count - filtered.Count;
        if (skipped > 0)
        {
            _logger.LogDebug(
                "Archive {ArchiveRtId} (target {TargetCkTypeId}): skipped {Skipped} of {Total} datapoints with mismatched CkTypeId",
                archiveRtId, snapshot.TargetCkTypeId, skipped, materialised.Count);
        }
        if (filtered.Count == 0)
        {
            return;
        }

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.insert");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());
        activity?.SetTag("streamdata.batch_size", filtered.Count);

        var (qualifiedTable, userColumnNames) = ResolveTableAndColumns(snapshot, archiveRtId);

        var sw = Stopwatch.StartNew();
        var computedPlan = BuildComputedPlan(snapshot);
        var dtos = filtered.Select(p => MapToDataPointDto(p, computedPlan));
        await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, dtos);
        sw.Stop();

        var bucket = CrateDbDiagnostics.BatchSizeBucket(filtered.Count);
        CrateDbDiagnostics.InsertDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()),
            new("batch_size_bucket", bucket));
        CrateDbDiagnostics.InsertedPoints.Add(filtered.Count,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()));

        await DetectAndRecordRetroactiveWriteAsync(
            archiveRtId, filtered.Select(p => p.Timestamp), RecomputeChangeSource.Pipeline);
    }

    /// <summary>
    /// Computes the qualified per-archive table name and the camelCase user-column list from the
    /// archive snapshot.
    /// </summary>
    private (string qualifiedTable, IReadOnlyList<string> userColumnNames) ResolveTableAndColumns(
        ArchiveSnapshot snapshot, OctoObjectId archiveRtId)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var userColumnNames = new List<string>(snapshot.Columns.Count);
        foreach (var c in snapshot.Columns)
        {
            if (!c.IsComputed)
            {
                userColumnNames.Add(ColumnNameMapper.PathToColumnName(c.Path));
                continue;
            }

            // Defensively skip a computed column missing its Name — activation DDL would have rejected
            // it, but the insert path must never throw on a malformed snapshot.
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            // The active physical column (versioned) always; and during a formula change the pending
            // versioned column too, so ingest dual-writes both (BuildComputedPlan emits both values).
            userColumnNames.Add(ComputedColumnNaming.Active(c));
            if (c.HasPendingFormula)
            {
                userColumnNames.Add(ComputedColumnNaming.Pending(c));
            }
        }

        return (qualifiedTable, userColumnNames);
    }

    public async Task InsertTimeRangeAsync(
        OctoObjectId archiveRtId,
        IEnumerable<TimeRangeStreamDataPoint> datapoints,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);

        // The repository serves any Archive subtype; refuse a TimeRange insert into a non-
        // TimeRange archive — the storage shape doesn't have window columns, so the SQL would
        // fail with a column-not-found error anyway. Surface the mismatch as a clear ArgumentException.
        if (!snapshot.IsTimeRange)
        {
            throw new ArgumentException(
                $"Archive '{archiveRtId}' is not a TimeRangeArchive; use InsertAsync instead.",
                nameof(archiveRtId));
        }

        var materialised = datapoints as IReadOnlyList<TimeRangeStreamDataPoint> ?? datapoints.ToList();

        // Same CkTypeId-binding rule as raw archive inserts: each per-archive table holds one CkType.
        var filtered = materialised.Where(p => p.CkTypeId == snapshot.TargetCkTypeId).ToList();
        var skipped = materialised.Count - filtered.Count;
        if (skipped > 0)
        {
            _logger.LogDebug(
                "Time-range archive {ArchiveRtId} (target {TargetCkTypeId}): skipped {Skipped} of {Total} datapoints with mismatched CkTypeId",
                archiveRtId, snapshot.TargetCkTypeId, skipped, materialised.Count);
        }
        if (filtered.Count == 0)
        {
            return;
        }

        // Validate windows up front so an entire batch is rejected (consistent with raw bulk insert
        // semantics: pre-validate, then commit, never partial). A single bad window aborts the call.
        foreach (var p in filtered)
        {
            if (p.To <= p.From)
            {
                throw new ArgumentException(
                    $"TimeRangeStreamDataPoint for entity '{p.RtId}': To ({p.To:O}) must be strictly greater than From ({p.From:O}).",
                    nameof(datapoints));
            }
        }

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.insertTimeRange");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());
        activity?.SetTag("streamdata.batch_size", filtered.Count);

        var (qualifiedTable, userColumnNames) = ResolveTableAndColumns(snapshot, archiveRtId);

        var sw = Stopwatch.StartNew();
        var computedPlan = BuildComputedPlan(snapshot);
        var dtos = filtered.Select(p => MapToTimeRangeDataPointDto(p, computedPlan));
        await _databaseClient.InsertTimeRangeDataAsync(_tenantId, qualifiedTable, userColumnNames, dtos);
        sw.Stop();

        var bucket = CrateDbDiagnostics.BatchSizeBucket(filtered.Count);
        CrateDbDiagnostics.InsertDurationMs.Record(sw.Elapsed.TotalMilliseconds,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()),
            new("batch_size_bucket", bucket));
        CrateDbDiagnostics.InsertedPoints.Add(filtered.Count,
            new("tenant", _tenantId),
            new("archive", archiveRtId.ToString()));

        // Time-range rows are keyed by their window start; a late window correction is retroactive.
        await DetectAndRecordRetroactiveWriteAsync(
            archiveRtId, filtered.Select(p => p.From), RecomputeChangeSource.Pipeline);
    }

    private Dtos.TimeRangeDataPointDto MapToTimeRangeDataPointDto(TimeRangeStreamDataPoint point,
        IReadOnlyList<ComputedColumnPlanItem> computedPlan)
    {
        // Normalise to UTC: the storage column is TIMESTAMP WITH TIME ZONE so Npgsql writes the
        // offset; downstream queries always read UTC. Matches MapToDataPointDto's behaviour for
        // single-timestamp archives.
        // Expand any nested RtRecord values into dotted attribute paths so an entity attribute
        // like Amount → RtRecord{Value, Unit} ends up as "Amount.Value" / "Amount.Unit" — that's
        // the same key shape the archive's Columns[] use, so the per-archive table's
        // user-column lookup picks them up. Without this, the Amount column stays NULL because
        // the dict has "Amount" → record instead of the leaf "Amount.Value".
        var flattened = FlattenRecordValues(point.Attributes);
        var attributes = flattened.ToDictionary(
            kv => ColumnNameMapper.PathToColumnName(kv.Key),
            kv => kv.Value,
            StringComparer.Ordinal);
        ApplyComputedColumns(attributes, computedPlan);
        return new Dtos.TimeRangeDataPointDto(attributes)
        {
            RtId = point.RtId,
            CkTypeId = point.CkTypeId,
            RtWellKnownName = point.RtWellKnownName,
            From = point.From.ToUniversalTime(),
            To = point.To.ToUniversalTime(),
        };
    }

    /// <summary>
    /// Walks an attribute dictionary and replaces every <see cref="RtRecord"/> value with one
    /// dotted-path entry per leaf attribute (recursively for nested records). Non-record values
    /// are passed through unchanged. Used by the archive insert paths so callers can pass the
    /// RtEntity attributes as-is, no pre-flattening needed in the pipeline node.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> FlattenRecordValues(
        IReadOnlyDictionary<string, object?> attrs)
    {
        var result = new Dictionary<string, object?>(attrs.Count, StringComparer.Ordinal);
        foreach (var kv in attrs)
        {
            FlattenInto(result, kv.Key, kv.Value);
        }
        return result;
    }

    private static void FlattenInto(Dictionary<string, object?> result, string prefix, object? value)
    {
        if (value is RtRecord record && record.Attributes.Count > 0)
        {
            foreach (var sub in record.Attributes)
            {
                FlattenInto(result, $"{prefix}.{sub.Key}", sub.Value);
            }
        }
        else
        {
            result[prefix] = value;
        }
    }

    /// <summary>
    /// Loads the active-generation ranges for a rollup archive from its <c>__genmap</c> side-table
    /// (AB#4184, Phase 6). Returns empty for non-rollup archives and for rollups that have never been
    /// recomputed (no genmap rows) or were provisioned before Phase 6 (no genmap table) — in all those
    /// cases the read path emits no generation predicate and every row (generation 0) is returned.
    /// </summary>
    private async Task<IReadOnlyList<GenerationRange>> LoadGenerationRangesAsync(
        ArchiveSnapshot snapshot, OctoObjectId archiveRtId)
    {
        if (snapshot.RollupAggregations is null)
        {
            return Array.Empty<GenerationRange>();
        }

        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, archiveRtId.ToString());
        var ranges = new List<GenerationRange>();
        try
        {
            await foreach (var row in _databaseClient.StreamRawRowsAsync(
                               _tenantId, GenerationMapSqlBuilder.BuildSelectAll(genMapTable)))
            {
                var start = Convert.ToInt64(row["range_start"], System.Globalization.CultureInfo.InvariantCulture);
                var end = Convert.ToInt64(row["range_end"], System.Globalization.CultureInfo.InvariantCulture);
                var scope = row.TryGetValue("rtid_scope", out var s) && s is not null ? s.ToString() ?? string.Empty : string.Empty;
                var generation = Convert.ToInt64(row[Constants.Generation], System.Globalization.CultureInfo.InvariantCulture);
                ranges.Add(new GenerationRange(start, end, scope, generation));
            }
        }
        catch (Exception ex)
        {
            // Pre-Phase-6 rollup table without a genmap side-table ⇒ no active generations; read all
            // rows unfiltered (they are all generation 0). Debug-level so it is observable but quiet.
            _logger.LogDebug(ex,
                "No generation map for rollup archive {ArchiveRtId}; reading all rows as generation 0.", archiveRtId);
        }

        return ranges;
    }

    public async Task<StreamDataQueryResult> ExecuteQueryAsync(OctoObjectId archiveRtId, StreamDataQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        // Windowed-storage archives (rollup / time-range, Phase 7+) have no `timestamp` column —
        // their time axis is `(window_start, window_end)`. Wire the query builder to use the
        // window_end column for time filtering / sorting and alias it as `timestamp` in the
        // result set so downstream row-mapping stays archive-flavor-agnostic (concept-time-range
        // §6 read-compatibility layer).
        if (snapshot.UsesWindowedStorage)
        {
            q.UseWindowedTimeAxis();
            // Phase 6 (AB#4184): for rollup archives, constrain reads to the active generation per
            // window so a query during a recompute never mixes generations. Marks the query
            // generation-tracked even when the genmap is empty (baseline generation = 0), which hides
            // a recompute's not-yet-committed next-generation rows. Time-range archives have no
            // generation column, so they are left untouched.
            if (snapshot.RollupAggregations is not null)
            {
                q.WithGenerationRanges(await LoadGenerationRangesAsync(snapshot, archiveRtId));
            }
        }
        q.IncludeDefaultVariables();
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Resolve and add columns
        var resolvedColumnNames = ResolveAndAddColumns(q, fieldResolver, options.Columns);

        // Time filter
        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        // RtId scope filter
        AddRtIdFilter(q, options.RtIds);

        // Sort orders
        AddSortOrders(q, fieldResolver, options.SortOrders);

        // Field filters
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        // Execute with pagination
        var (data, totalCount, effectiveOffset) = await ExecutePaginatedQueryAsync(
            q, options.Offset, options.PageSize, options.Limit);

        var rows = data.Select(dp => MapToStreamDataRow(dp, resolvedColumnNames)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = totalCount };
    }

    public async Task<StreamDataQueryResult> ExecuteAggregationQueryAsync(
        OctoObjectId archiveRtId, StreamDataAggregationQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        // AB#4336 §6.2: TimeWeightedAvg over a raw (event-based) archive needs the LOCF statement —
        // the standard single-scan builder cannot express the carry / interval weighting. Routed
        // before the query builder is assembled; plain columns ride along in the same statement.
        if (!snapshot.UsesWindowedStorage
            && options.AggregationColumns.Any(c => c.Function == AggregationFunction.TimeWeightedAverage))
        {
            return await ExecuteRawTimeWeightedAggregationAsync(
                archiveRtId, fieldResolver, options, options.AggregationColumns, Array.Empty<string>());
        }

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        // Rollup + time-range archives use the windowed (window_start, window_end) shape — the
        // time-filter / sort needs to target window_end, not the nonexistent `timestamp`. Pure-
        // aggregation SELECTs project no row-level time column, so only the WHERE clause is
        // affected, but the toggle has to land before the filter is added.
        if (snapshot.UsesWindowedStorage)
        {
            q.UseWindowedTimeAxis();
            // Phase 6 (AB#4184): for rollup archives, constrain reads to the active generation per
            // window so a query during a recompute never mixes generations. Marks the query
            // generation-tracked even when the genmap is empty (baseline generation = 0), which hides
            // a recompute's not-yet-committed next-generation rows. Time-range archives have no
            // generation column, so they are left untouched.
            if (snapshot.RollupAggregations is not null)
            {
                q.WithGenerationRanges(await LoadGenerationRangesAsync(snapshot, archiveRtId));
            }
        }
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Add aggregation columns. SQL aliases need to be unique (to support e.g. AVG+MAX
        // of the same attribute), so we use "{func}_{alias}". The output column name is
        // the original attribute path — we remap via outputNameBySqlAlias when building rows.
        var outputColumnNames = new List<string>();
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var col in options.AggregationColumns)
        {
            // Chain-aware path for RollupArchive: the operator queries a logical source path
            // (e.g. "Temperature") and we rewrite to the materialised _sum/_count columns per
            // concept-time-range §7. The chain walker handles arbitrary cascade depths (rollup-
            // over-rollup-…); falls through to the standard resolver when the rollup can't
            // chain the requested function or when the archive isn't a rollup at all.
            if (snapshot.RollupAggregations is { } rollupSpecs)
            {
                var targetFunc = MapAggregationFunction(col.Function);
                var chained = await ResolveRollupChainAggregationAsync(
                    snapshot, rollupSpecs, col.AttributePath, targetFunc, CancellationToken.None);
                if (chained != null)
                {
                    // Output key must be unique per (path, function) — two aggregations on the
                    // same attribute (MIN + MAX of amount.value) would otherwise collide on the
                    // same row.Values key and the wire would surface duplicate cells. The chain
                    // resolver's SqlAlias already has the function suffix ("amount.value_min");
                    // normalise via PathToColumnName so the key matches the camelCase form the
                    // wire mapping uses ("amountvalue_min").
                    var outputName = ColumnNameMapper.PathToColumnName(chained.SqlAlias);
                    q.AddRawAggregationExpression(chained.SqlExpression, chained.SqlAlias);
                    outputColumnNames.Add(outputName);
                    outputNameBySqlAlias[chained.SqlAlias] = outputName;
                    continue;
                }
            }

            // TWA over a windowed non-rollup source (TimeRangeArchive): each row's weight is its
            // own window length — expressible inline, no LOCF machinery (concept-time-weighted §3).
            // A rollup without a matching TWA pair deliberately falls through to the guard below:
            // its rows are aggregates, not observations, so window-length weighting would be wrong.
            if (col.Function == AggregationFunction.TimeWeightedAverage && snapshot.RollupAggregations is null)
            {
                var resolvedTwa = fieldResolver.Resolve(col.AttributePath);
                if (resolvedTwa == null) continue;
                var windowLenMs = $"(\"{Constants.WindowEnd}\"::bigint - \"{Constants.WindowStart}\"::bigint)";
                var twaAlias = $"{resolvedTwa.CrateDbName}_twavg";
                q.AddRawAggregationExpression(
                    $"SUM(CASE WHEN \"{resolvedTwa.CrateDbName}\" IS NOT NULL THEN \"{resolvedTwa.CrateDbName}\" * {windowLenMs} END)"
                    + $" / NULLIF(SUM(CASE WHEN \"{resolvedTwa.CrateDbName}\" IS NOT NULL THEN {windowLenMs} END), 0)",
                    twaAlias);
                outputColumnNames.Add(twaAlias);
                outputNameBySqlAlias[twaAlias] = twaAlias;
                continue;
            }

            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            // Same unique-output rule as the chain path: suffix the column name with the
            // lowercase function so MIN+MAX (or COUNT+SUM, etc.) of the same column don't
            // overwrite each other in row.Values.
            var outputName2 = $"{resolved.CrateDbName}_{aggFunc.ToString().ToLowerInvariant()}";
            outputColumnNames.Add(outputName2);
            outputNameBySqlAlias[sqlAlias] = outputName2;
        }

        // Time filter
        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing aggregation SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        var rows = data.Select(dp => MapAggregationRow(dp, outputColumnNames, outputNameBySqlAlias)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    public async Task<StreamDataQueryResult> ExecuteGroupedAggregationQueryAsync(
        OctoObjectId archiveRtId, StreamDataGroupedAggregationQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        var fieldResolver = CreateFieldResolver(snapshot);

        // AB#4336 §6.2: TWA over a raw archive — LOCF statement, grouped variant. See
        // ExecuteAggregationQueryAsync for the same routing rationale.
        if (!snapshot.UsesWindowedStorage
            && options.AggregationColumns.Any(c => c.Function == AggregationFunction.TimeWeightedAverage))
        {
            return await ExecuteRawTimeWeightedAggregationAsync(
                archiveRtId, fieldResolver, options, options.AggregationColumns, options.GroupByColumns);
        }

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        // Windowed-storage time-axis (rollup / time-range) — see ExecuteAggregationQueryAsync
        // for the same rationale.
        if (snapshot.UsesWindowedStorage)
        {
            q.UseWindowedTimeAxis();
            // Phase 6 (AB#4184): for rollup archives, constrain reads to the active generation per
            // window so a query during a recompute never mixes generations. Marks the query
            // generation-tracked even when the genmap is empty (baseline generation = 0), which hides
            // a recompute's not-yet-committed next-generation rows. Time-range archives have no
            // generation column, so they are left untouched.
            if (snapshot.RollupAggregations is not null)
            {
                q.WithGenerationRanges(await LoadGenerationRangesAsync(snapshot, archiveRtId));
            }
        }
        q.WithCkTypeIdFilter(options.CkTypeId);

        // Group-by columns as non-aggregation variables. The CrateQueryCompiler automatically
        // groups by all non-aggregation variables when aggregation variables are present.
        var outputColumnNames = new List<string>();
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var groupCol in options.GroupByColumns)
        {
            var resolved = fieldResolver.Resolve(groupCol);
            if (resolved == null) continue;

            q.AddVariable(resolved.CrateDbName, resolved.CrateDbName, null);
            outputColumnNames.Add(resolved.CrateDbName);
            // Grouping columns use CrateDbName as SQL alias — identity mapping
            outputNameBySqlAlias[resolved.CrateDbName] = resolved.CrateDbName;
        }

        // Aggregation columns with unique SQL aliases — same chain-aware rewrite as the non-
        // grouped aggregation path. Falls through to the standard simple aggregate when no
        // matching rollup spec exists or the function combination isn't chainable.
        foreach (var col in options.AggregationColumns)
        {
            if (snapshot.RollupAggregations is { } rollupSpecs)
            {
                var targetFunc = MapAggregationFunction(col.Function);
                var chained = await ResolveRollupChainAggregationAsync(
                    snapshot, rollupSpecs, col.AttributePath, targetFunc, CancellationToken.None);
                if (chained != null)
                {
                    // Output key must be unique per (path, function) — two aggregations on the
                    // same attribute (MIN + MAX of amount.value) would otherwise collide on the
                    // same row.Values key and the wire would surface duplicate cells. The chain
                    // resolver's SqlAlias already has the function suffix ("amount.value_min");
                    // normalise via PathToColumnName so the key matches the camelCase form the
                    // wire mapping uses ("amountvalue_min").
                    var outputName = ColumnNameMapper.PathToColumnName(chained.SqlAlias);
                    q.AddRawAggregationExpression(chained.SqlExpression, chained.SqlAlias);
                    outputColumnNames.Add(outputName);
                    outputNameBySqlAlias[chained.SqlAlias] = outputName;
                    continue;
                }
            }

            // TWA over a windowed non-rollup source (TimeRangeArchive): each row's weight is its
            // own window length — expressible inline, no LOCF machinery (concept-time-weighted §3).
            // A rollup without a matching TWA pair deliberately falls through to the guard below:
            // its rows are aggregates, not observations, so window-length weighting would be wrong.
            if (col.Function == AggregationFunction.TimeWeightedAverage && snapshot.RollupAggregations is null)
            {
                var resolvedTwa = fieldResolver.Resolve(col.AttributePath);
                if (resolvedTwa == null) continue;
                var windowLenMs = $"(\"{Constants.WindowEnd}\"::bigint - \"{Constants.WindowStart}\"::bigint)";
                var twaAlias = $"{resolvedTwa.CrateDbName}_twavg";
                q.AddRawAggregationExpression(
                    $"SUM(CASE WHEN \"{resolvedTwa.CrateDbName}\" IS NOT NULL THEN \"{resolvedTwa.CrateDbName}\" * {windowLenMs} END)"
                    + $" / NULLIF(SUM(CASE WHEN \"{resolvedTwa.CrateDbName}\" IS NOT NULL THEN {windowLenMs} END), 0)",
                    twaAlias);
                outputColumnNames.Add(twaAlias);
                outputNameBySqlAlias[twaAlias] = twaAlias;
                continue;
            }

            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            // Same unique-output rule as the chain path: suffix the column name with the
            // lowercase function so MIN+MAX (or COUNT+SUM, etc.) of the same column don't
            // overwrite each other in row.Values.
            var outputName2 = $"{resolved.CrateDbName}_{aggFunc.ToString().ToLowerInvariant()}";
            outputColumnNames.Add(outputName2);
            outputNameBySqlAlias[sqlAlias] = outputName2;
        }

        if (options.From is not null && options.To is not null)
        {
            q.WithTimeFilter(options.From.Value, options.To.Value);
        }

        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing grouped aggregation SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        var rows = data.Select(dp => MapAggregationRow(dp, outputColumnNames, outputNameBySqlAlias)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    /// <summary>
    /// Returns the bucket count to actually downsample to: the requested <paramref name="requestedLimit"/>,
    /// clamped down to the number of distinct source bins in range when the request is finer than the
    /// data. Runs one cheap <c>COUNT(DISTINCT)</c> against the same source filters already configured on
    /// <paramref name="q"/>. On any probe failure it falls back to the requested limit (the query still
    /// runs; worst case is the pre-fix behaviour). See AB#4246.
    /// </summary>
    private async Task<int> ResolveEffectiveDownsamplingLimitAsync(CrateQueryBuilder q, int requestedLimit)
    {
        try
        {
            var countSql = new CrateQueryCompiler().CompileDownsamplingBucketCountQuery(q);
            _logger.LogDebug("Executing downsampling bucket-count SQL: {Sql}", countSql);
            var countData = await _databaseClient.GetDataAsync(_tenantId, countSql);
            var first = countData.FirstOrDefault();
            if (first?.Attributes != null && first.Attributes.TryGetValue("c", out var countObj) && countObj != null)
            {
                var distinctBins = Convert.ToInt32(countObj);
                if (distinctBins > 0 && distinctBins < requestedLimit)
                {
                    _logger.LogDebug(
                        "Clamping downsampling bucket count from {Requested} to {Effective} (distinct source bins in range)",
                        requestedLimit, distinctBins);
                    return distinctBins;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Downsampling bucket-count probe failed; using requested limit {Limit}", requestedLimit);
        }

        return requestedLimit;
    }

    public async Task<StreamDataQueryResult> ExecuteDownsamplingQueryAsync(
        OctoObjectId archiveRtId, StreamDataDownsamplingQueryOptions options)
    {
        var snapshot = await EnsureArchiveActivatedAsync(archiveRtId);
        if (options.From is null || options.To is null || options.Limit is null)
        {
            throw StreamDataException.InvalidQueryParameters(
                "Downsampling queries require From, To, and Limit (bucket count).");
        }

        var fieldResolver = CreateFieldResolver(snapshot);

        var q = new CrateQueryBuilder(TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()));
        // Windowed-storage downsampling: the LEFT JOIN keys on window_start (the bin that contains
        // the window); the compiler adds the fully-contained predicate so straddling windows are
        // dropped (concept-time-range §7). UseWindowedTimeAxis sets the time axis to window_end.
        if (snapshot.UsesWindowedStorage)
        {
            q.UseWindowedTimeAxis();
            // Phase 6 (AB#4184): for rollup archives, constrain reads to the active generation per
            // window so a query during a recompute never mixes generations. Marks the query
            // generation-tracked even when the genmap is empty (baseline generation = 0), which hides
            // a recompute's not-yet-committed next-generation rows. Time-range archives have no
            // generation column, so they are left untouched.
            if (snapshot.RollupAggregations is not null)
            {
                q.WithGenerationRanges(await LoadGenerationRangesAsync(snapshot, archiveRtId));
            }
        }
        q.WithCkTypeIdFilter(options.CkTypeId);
        q.WithTimeFilter(options.From.Value, options.To.Value);

        // rtId scope + field filters are applied BEFORE the bucket-count probe so it counts exactly
        // the source rows the downsampling query will read. AddRtIdFilter emits an IN field-filter
        // (not an AddWhereIn on a SELECT variable), so it scopes this path even though the identity
        // column is only a group key here, not a selected output variable.
        AddRtIdFilter(q, options.RtIds);
        AddFieldFilters(q, fieldResolver, options.FieldFilters);

        // Clamp the requested bucket count to the number of distinct source bins in range. Without
        // this, a chart asking for more buckets than the data has distinct timestamps yields a bin
        // finer than the source resolution; for windowed archives the fully-contained predicate
        // then drops every window and every bin reads null (AB#4246). One cheap COUNT(DISTINCT).
        var effectiveLimit = await ResolveEffectiveDownsamplingLimitAsync(q, options.Limit.Value);
        q.WithDownsampling(effectiveLimit, options.From.Value, options.To.Value);

        // Timestamp is always first in the output for downsampling (the bin start time).
        // It maps from the "T" alias set by the downsampling SQL generator.
        q.AddVariable(Constants.Timestamp, "T", null);

        var outputColumnNames = new List<string> { Constants.Timestamp };
        var outputNameBySqlAlias = new Dictionary<string, string>();

        foreach (var col in options.AggregationColumns)
        {
            // Chain-aware path for rollups — same logic as ExecuteAggregationQueryAsync.
            // The downsampling SQL generator d.-prefixes column references inside aggregate
            // calls regardless of whether they came from AddAggregationVariable or
            // AddRawAggregationExpression (both share the `<func>("<col>")` shape).
            if (snapshot.RollupAggregations is { } rollupSpecs)
            {
                var targetFunc = MapAggregationFunction(col.Function);
                var chained = await ResolveRollupChainAggregationAsync(
                    snapshot, rollupSpecs, col.AttributePath, targetFunc, CancellationToken.None);
                if (chained != null)
                {
                    // Output key must be unique per (path, function) — two aggregations on the
                    // same attribute (MIN + MAX of amount.value) would otherwise collide on the
                    // same row.Values key and the wire would surface duplicate cells. The chain
                    // resolver's SqlAlias already has the function suffix ("amount.value_min");
                    // normalise via PathToColumnName so the key matches the camelCase form the
                    // wire mapping uses ("amountvalue_min").
                    var outputName = ColumnNameMapper.PathToColumnName(chained.SqlAlias);
                    q.AddRawAggregationExpression(chained.SqlExpression, chained.SqlAlias);
                    outputColumnNames.Add(outputName);
                    outputNameBySqlAlias[chained.SqlAlias] = outputName;
                    continue;
                }
            }

            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            var sqlAlias = $"{aggFunc}_{resolved.CrateDbName}";
            q.AddAggregationVariable(resolved.CrateDbName, aggFunc, sqlAlias);

            // Same unique-output rule as the chain path: suffix the column name with the
            // lowercase function so MIN+MAX (or COUNT+SUM, etc.) of the same column don't
            // overwrite each other in row.Values.
            var outputName2 = $"{resolved.CrateDbName}_{aggFunc.ToString().ToLowerInvariant()}";
            outputColumnNames.Add(outputName2);
            outputNameBySqlAlias[sqlAlias] = outputName2;
        }

        // Per-series grouping (AB#4233): group each requested column by an extra series-identity
        // column (typically the source rtId) in addition to the time bin, so interleaved series
        // stay separated. Resolve via the field resolver where possible; fall back to the
        // camelCase column form for identity columns (e.g. "rtId" → "rtid") that aren't part of
        // the configured archive column set the resolver was built from.
        foreach (var groupPath in options.GroupByColumnPaths ?? [])
        {
            var groupColumn = fieldResolver.Resolve(groupPath)?.CrateDbName
                ?? ColumnNameMapper.PathToColumnName(groupPath);
            q.WithDownsamplingGroupBy(groupColumn);
        }

        var compiler = new CrateQueryCompiler();
        var sql = compiler.CompileQuery(q);
        _logger.LogDebug("Executing downsampling SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);

        // Detect empty bins via __binCount (COUNT(d."Timestamp") from generate_series LEFT JOIN).
        // For empty bins, force aggregation values to null regardless of what CrateDB returned.
        const string binCountKey = "__binCount";
        var rows = new List<StreamDataRow>();
        foreach (var dp in data)
        {
            var isEmptyBin =
                dp.Attributes?.TryGetValue(binCountKey, out var binCountObj) == true &&
                binCountObj != null &&
                Convert.ToInt64(binCountObj) == 0;

            var values = new Dictionary<string, object?>();
            foreach (var col in outputColumnNames)
            {
                if (col == Constants.Timestamp)
                {
                    values[col] = dp.Timestamp;
                    continue;
                }

                if (isEmptyBin)
                {
                    values[col] = null;
                    continue;
                }

                // Find the SQL alias that maps to this output column name
                var sqlAlias = outputNameBySqlAlias
                    .FirstOrDefault(kvp => kvp.Value == col).Key;
                object? value = null;
                if (sqlAlias != null)
                {
                    dp.Attributes?.TryGetValue(sqlAlias, out value);
                }
                else
                {
                    dp.Attributes?.TryGetValue(col, out value);
                }

                values[col] = value;
            }

            rows.Add(new StreamDataRow
            {
                Timestamp = dp.Timestamp,
                RtId = dp.RtId,
                // The downsampling projection doesn't select cktypeid (it isn't grouped), so every
                // bin row — populated or empty — comes back with an empty/absent type. Stamp the
                // query's target Ck type unconditionally so the non-null GraphQL ckTypeId field
                // resolves; all rows of one archive share the same type anyway. (dp.CkTypeId here
                // is an empty RtCkId, not null, so a null-coalesce wouldn't fire.)
                CkTypeId = options.CkTypeId ?? dp.CkTypeId,
                RtCreationDateTime = dp.RtCreationDateTime,
                RtChangedDateTime = dp.RtChangedDateTime,
                Values = values
            });
        }

        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    /// <summary>
    /// Page size for the keyset export scan. ~5000 rows balances round-trip count against the
    /// memory held per page (one page is fully materialised by <see cref="StreamRawRowsAsync"/>'s
    /// per-row copy before the next page is fetched). Concept §4.1.
    /// </summary>
    private const int ExportPageSize = 5000;

    private static readonly System.Text.RegularExpressions.Regex RtIdHexRegex =
        new("^[0-9a-fA-F]{24}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ExportRowsAsync(
        OctoObjectId archiveRtId,
        TimeWindow? window,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Export does not require the archive to be Activated — a Disabled archive's table is
        // preserved and exporting it is explicitly allowed (concept §10). We only need the snapshot
        // to know the storage shape (raw vs windowed). A missing snapshot is a hard error; a Created
        // archive (no table yet) yields zero rows.
        var snapshot = await _archiveStore.GetAsync(archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var windowed = snapshot.UsesWindowedStorage;

        // Time-axis column: window_start for windowed archives, timestamp for raw.
        var timeColumn = windowed ? Constants.WindowStart : Constants.Timestamp;

        // Keyset cursor. Raw key: (timestamp, rtid). Windowed key: (window_start, rtid, cktypeid)
        // — window_start alone is not unique (multiple entities per window), so rtid + cktypeid
        // complete the natural key the windowed PK uses.
        DateTime? cursorTime = null;
        string? cursorRtId = null;
        string? cursorCkTypeId = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var sql = BuildExportPageSql(
                qualifiedTable, windowed, timeColumn, window,
                cursorTime, cursorRtId, cursorCkTypeId, ExportPageSize);

            var pageCount = 0;
            IReadOnlyDictionary<string, object?>? lastRow = null;

            await foreach (var row in _databaseClient.StreamRawRowsAsync(_tenantId, sql, ct))
            {
                pageCount++;
                lastRow = row;
                yield return row;
            }

            if (pageCount < ExportPageSize || lastRow is null)
            {
                yield break; // last (partial) page reached
            }

            // Advance the cursor to the last row of this page.
            cursorTime = AsUtcDateTime(lastRow.TryGetValue(timeColumn, out var t) ? t : null);
            cursorRtId = lastRow.TryGetValue(Constants.RtId, out var r) ? r as string : null;
            cursorCkTypeId = windowed
                ? (lastRow.TryGetValue(Constants.CkTypeId, out var c) ? c as string : null)
                : null;

            if (cursorTime is null || cursorRtId is null || (windowed && cursorCkTypeId is null))
            {
                // Defensive: a row without a usable cursor key would otherwise loop forever.
                _logger.LogWarning(
                    "Export of archive {ArchiveRtId} stopped early: last page row had no usable keyset cursor.",
                    archiveRtId);
                yield break;
            }
        }
    }

    /// <summary>
    /// Builds one keyset-pagination page query for the export scan. Emits <c>SELECT *</c> ordered by
    /// the natural key with a tuple cursor predicate and the optional <c>[FromUtc, ToUtc)</c> window
    /// predicate. Timestamps are embedded as quoted literals in CrateDB's canonical format (the
    /// export path is internal and the only string inputs are server-controlled identifiers, not
    /// user data).
    /// </summary>
    private static string BuildExportPageSql(
        string qualifiedTable, bool windowed, string timeColumn, TimeWindow? window,
        DateTime? cursorTime, string? cursorRtId, string? cursorCkTypeId, int pageSize)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT * FROM ").Append(qualifiedTable);

        var predicates = new List<string>();

        if (window is not null)
        {
            predicates.Add($"\"{timeColumn}\" >= '{FormatTs(window.FromUtc)}'");
            predicates.Add($"\"{timeColumn}\" < '{FormatTs(window.ToUtc)}'");
        }

        if (cursorTime is not null)
        {
            // Tuple-greater-than on the natural key. CrateDB supports row-value comparison, but we
            // spell it out to stay portable across the (time, rtid[, cktypeid]) key shapes.
            var ts = FormatTs(cursorTime.Value);
            if (windowed)
            {
                predicates.Add(
                    $"(\"{timeColumn}\" > '{ts}' " +
                    $"OR (\"{timeColumn}\" = '{ts}' AND \"{Constants.RtId}\" > '{EscapeLiteral(cursorRtId!)}') " +
                    $"OR (\"{timeColumn}\" = '{ts}' AND \"{Constants.RtId}\" = '{EscapeLiteral(cursorRtId!)}' " +
                    $"AND \"{Constants.CkTypeId}\" > '{EscapeLiteral(cursorCkTypeId!)}'))");
            }
            else
            {
                predicates.Add(
                    $"(\"{timeColumn}\" > '{ts}' " +
                    $"OR (\"{timeColumn}\" = '{ts}' AND \"{Constants.RtId}\" > '{EscapeLiteral(cursorRtId!)}'))");
            }
        }

        if (predicates.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

        sb.Append(" ORDER BY \"").Append(timeColumn).Append("\", \"").Append(Constants.RtId).Append('"');
        if (windowed)
        {
            sb.Append(", \"").Append(Constants.CkTypeId).Append('"');
        }

        sb.Append(" LIMIT ").Append(pageSize.ToString(CultureInfo.InvariantCulture)).Append(';');
        return sb.ToString();
    }

    private static string FormatTs(DateTime dt)
        => dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string EscapeLiteral(string s) => s.Replace("'", "''");

    private static DateTime? AsUtcDateTime(object? value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => null,
    };

    /// <inheritdoc />
    public async Task ImportRowsAsync(
        OctoObjectId archiveRtId,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        ArchiveImportMode mode,
        CancellationToken ct)
    {
        // Import targets a Disabled (or Activated) archive — we need the snapshot for the storage
        // shape and user-column physical names, not for the Activated guard (concept §7: import runs
        // while the archive is Disabled so no live writes race the import).
        var snapshot = await _archiveStore.GetAsync(archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var userColumnNames = snapshot.Columns
            .Select(c => ColumnNameMapper.PathToColumnName(c.Path))
            .ToList();
        var windowed = snapshot.UsesWindowedStorage;

        using var activity = CrateDbDiagnostics.ActivitySource.StartActivity("crate.importRows");
        activity?.SetTag("streamdata.tenant", _tenantId);
        activity?.SetTag("streamdata.archive.rtid", archiveRtId.ToString());

        var rawBatch = new List<DataPointDto>(ExportPageSize);
        var windowedBatch = new List<Dtos.TimeRangeDataPointDto>(ExportPageSize);
        var rowIndex = 0;

        await foreach (var row in rows.WithCancellation(ct))
        {
            if (windowed)
            {
                windowedBatch.Add(MapImportedWindowedRow(row, userColumnNames, rowIndex));
                if (windowedBatch.Count >= ExportPageSize)
                {
                    await _databaseClient.InsertTimeRangeDataAsync(_tenantId, qualifiedTable, userColumnNames, windowedBatch);
                    windowedBatch.Clear();
                }
            }
            else
            {
                rawBatch.Add(MapImportedRawRow(row, userColumnNames, rowIndex));
                if (rawBatch.Count >= ExportPageSize)
                {
                    await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, rawBatch);
                    rawBatch.Clear();
                }
            }

            rowIndex++;
        }

        if (windowed && windowedBatch.Count > 0)
        {
            await _databaseClient.InsertTimeRangeDataAsync(_tenantId, qualifiedTable, userColumnNames, windowedBatch);
        }
        else if (!windowed && rawBatch.Count > 0)
        {
            await _databaseClient.InsertDataAsync(_tenantId, qualifiedTable, userColumnNames, rawBatch);
        }

        // mode is honoured by the insert SQL: the existing INSERT … ON CONFLICT DO UPDATE for
        // windowed always overwrites user columns (Upsert semantics); the raw path's ON CONFLICT
        // preserves user columns (InsertOnly). For raw archives Upsert is currently mapped onto the
        // same conflict path — documented as a follow-up (the raw insert SQL would need an
        // overwrite variant to fully honour Upsert), surfaced here so the intent is explicit.
        _logger.LogDebug(
            "Imported {RowCount} rows into archive {ArchiveRtId} (windowed={Windowed}, mode={Mode}).",
            rowIndex, archiveRtId, windowed, mode);
    }

    /// <summary>
    /// Maps one exported NDJSON row (physical column name → value) back into a <see cref="DataPointDto"/>
    /// for the raw insert path. Validates the rtid is 24-char hex and surfaces a per-field error
    /// (concept §10 / feedback_rtid_must_be_hex) rather than letting a malformed id reach the DB.
    /// </summary>
    private static DataPointDto MapImportedRawRow(
        IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> userColumnNames, int rowIndex)
    {
        var rtId = ParseRtId(row, rowIndex);
        var ckTypeId = ParseCkTypeId(row, rowIndex);

        var attributes = new Dictionary<string, object?>(userColumnNames.Count);
        foreach (var col in userColumnNames)
        {
            attributes[col] = row.TryGetValue(col, out var v) ? v : null;
        }

        return new DataPointDto(attributes)
        {
            RtId = rtId,
            CkTypeId = ckTypeId,
            Timestamp = RequireUtc(row, Constants.Timestamp, rowIndex),
            RtWellKnownName = row.TryGetValue(Constants.RtWellKnownName, out var wkn) ? wkn as string : null,
            RtCreationDateTime = OptionalUtc(row, Constants.RtCreationDateTime),
            RtChangedDateTime = OptionalUtc(row, Constants.RtChangedDateTime),
        };
    }

    private static Dtos.TimeRangeDataPointDto MapImportedWindowedRow(
        IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> userColumnNames, int rowIndex)
    {
        var rtId = ParseRtId(row, rowIndex);
        var ckTypeId = ParseCkTypeId(row, rowIndex);

        var attributes = new Dictionary<string, object?>(userColumnNames.Count, StringComparer.Ordinal);
        foreach (var col in userColumnNames)
        {
            attributes[col] = row.TryGetValue(col, out var v) ? v : null;
        }

        return new Dtos.TimeRangeDataPointDto(attributes)
        {
            RtId = rtId,
            CkTypeId = ckTypeId,
            RtWellKnownName = row.TryGetValue(Constants.RtWellKnownName, out var wkn) ? wkn as string : null,
            From = RequireUtc(row, Constants.WindowStart, rowIndex),
            To = RequireUtc(row, Constants.WindowEnd, rowIndex),
        };
    }

    private static OctoObjectId ParseRtId(IReadOnlyDictionary<string, object?> row, int rowIndex)
    {
        var raw = row.TryGetValue(Constants.RtId, out var v) ? v as string : null;
        if (string.IsNullOrEmpty(raw) || !RtIdHexRegex.IsMatch(raw))
        {
            throw new ArgumentException(
                $"Import row {rowIndex}: field '{Constants.RtId}' must be a 24-character hex string, but was '{raw ?? "(null)"}'.",
                nameof(row));
        }
        return new OctoObjectId(raw);
    }

    private static RtCkId<CkTypeId> ParseCkTypeId(IReadOnlyDictionary<string, object?> row, int rowIndex)
    {
        var raw = row.TryGetValue(Constants.CkTypeId, out var v) ? v as string : null;
        if (string.IsNullOrEmpty(raw))
        {
            throw new ArgumentException(
                $"Import row {rowIndex}: field '{Constants.CkTypeId}' is required but was missing or empty.",
                nameof(row));
        }
        return new RtCkId<CkTypeId>(raw);
    }

    private static DateTime RequireUtc(IReadOnlyDictionary<string, object?> row, string column, int rowIndex)
    {
        var dt = AsUtcDateTime(row.TryGetValue(column, out var v) ? v : null);
        if (dt is null)
        {
            throw new ArgumentException(
                $"Import row {rowIndex}: field '{column}' is required but was missing or not a valid timestamp.",
                nameof(row));
        }
        return dt.Value;
    }

    private static DateTime OptionalUtc(IReadOnlyDictionary<string, object?> row, string column)
        => AsUtcDateTime(row.TryGetValue(column, out var v) ? v : null) ?? default;

    /// <inheritdoc />
    public async Task<int> AggregateBucketAsync(
        ArchiveSnapshot sourceArchive,
        RollupArchiveSnapshot rollup,
        DateTime bucketStart,
        DateTime bucketEnd,
        CancellationToken cancellationToken)
    {
        var sourceTable = TenantSchema.QualifiedArchiveTable(_tenantId, sourceArchive.RtId.ToString());
        var targetTable = TenantSchema.QualifiedArchiveTable(_tenantId, rollup.RtId.ToString());

        var sql = RollupAggregationSqlBuilder.Build(
            sourceTable,
            targetTable,
            // The query side filters by SemanticVersionedFullName (which drops the "-1" suffix
            // for type version 1). Raw + time-range archives write the same form via
            // RtCkId<CkTypeId>.ToString(); rollups have to match or the query never finds rows.
            rollup.TargetCkTypeId.SemanticVersionedFullName,
            rollup.Aggregations,
            bucketStart,
            bucketEnd,
            sourceArchive.UsesWindowedStorage,
            carryLookback: rollup.CarryLookback);

        _logger.LogDebug(
            "Rollup aggregation SQL for {RollupRtId} bucket [{BucketStart:O}, {BucketEnd:O}): {Sql}",
            rollup.RtId, bucketStart, bucketEnd, sql);

        var affected = await _databaseClient.ExecuteNonQueryAsync(_tenantId, sql, cancellationToken);

        CrateDbDiagnostics.RollupBucketUpserts.Add(affected,
            new("tenant", _tenantId),
            new("rollup", rollup.RtId.ToString()));

        // Rollup-internal computed columns (concept §11, approach a): after the aggregate columns
        // are written, evaluate any computed columns over the just-written bucket rows in .NET and
        // write the results back. Reuses the same plan / evaluation machinery as raw ingest.
        await EvaluateRollupComputedColumnsAsync(rollup.RtId, targetTable, bucketStart, bucketEnd, cancellationToken);

        return affected;
    }

    /// <inheritdoc />
    public async Task ClearRecomputeGenerationsAsync(
        OctoObjectId rollupRtId, DateTime fromBucketEnd, CancellationToken cancellationToken = default)
    {
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(_tenantId, rollupRtId.ToString());
        var liveTable = TenantSchema.QualifiedArchiveTable(_tenantId, rollupRtId.ToString());
        try
        {
            // 1. Drop the active-generation pointers reaching into the rewound range so those windows
            //    fall back to generation 0 (the forward re-aggregation target).
            await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                GenerationMapSqlBuilder.BuildDeleteGenerationsFrom(genMapTable, fromBucketEnd), cancellationToken);
            // 2. Remove the now-orphaned higher-generation rows in that range (generation 0 rows stay).
            await _databaseClient.ExecuteNonQueryAsync(_tenantId,
                RollupRecomputeSqlBuilder.BuildDeleteRecomputedRowsFrom(liveTable, fromBucketEnd), cancellationToken);
            await _databaseClient.RefreshArchiveTableAsync(_tenantId, liveTable);

            _logger.LogInformation(
                "Cleared recompute generations for rollup {RollupRtId} at/after {From:O} (watermark rewind).",
                rollupRtId, fromBucketEnd);
        }
        catch (Exception ex)
        {
            // No genmap side-table (non-rollup or pre-Phase-6) ⇒ nothing to clear. Idempotent no-op.
            _logger.LogDebug(ex,
                "ClearRecomputeGenerationsAsync is a no-op for {RollupRtId} (no recompute state).", rollupRtId);
        }
    }

    /// <inheritdoc />
    public Task<RecomputeExecutionResult> ExecuteAsync(
        ArchiveSnapshot source,
        RollupArchiveSnapshot rollup,
        DateTime rangeStart,
        DateTime rangeEnd,
        OctoObjectId? rtIdScope,
        CancellationToken cancellationToken)
    {
        // Reuse this repository's already-wired CrateDB clients / config / archive store — the
        // recompute executor needs the exact same plumbing as bucket aggregation.
        _recomputeExecutor ??= new CrateDbArchiveRecomputeExecutor(
            _tenantId, _databaseClient, _managementClient, _archiveStore,
            _configuration.NumberOfShards, _configuration.NumberOfReplicas, _logger);

        return _recomputeExecutor.ExecuteAsync(source, rollup, rangeStart, rangeEnd, rtIdScope, cancellationToken);
    }

    /// <summary>
    /// Information A (AB#4184): records a dirty window on the source archive when an ingest batch
    /// contains a timestamp at/before the high-water mark already consumed by dependent rollups
    /// (a correction / late value). No-op when recompute state tracking is not wired, the archive
    /// has no dependents, or every timestamp is a forward append. Cheap guards run before the rollup
    /// enumeration so the common forward-ingest path stays untouched.
    /// </summary>
    private async Task DetectAndRecordRetroactiveWriteAsync(
        OctoObjectId archiveRtId, IEnumerable<DateTime> timestamps, RecomputeChangeSource source)
    {
        if (_recomputeStateStore is null || _rollupArchiveStore is null)
        {
            return;
        }

        var consumedWatermark = await GetConsumedWatermarkAsync(archiveRtId);
        if (RetroactiveWriteDetector.TryBuildDirtyWindow(
                consumedWatermark, timestamps, source, DateTime.UtcNow, out var window))
        {
            await _recomputeStateStore.AppendDirtyWindowAsync(archiveRtId, window);
            _logger.LogInformation(
                "Archive {ArchiveRtId}: retroactive write detected over [{Start:O}, {End:O}); dirty window recorded for recompute.",
                archiveRtId, window.WindowStart, window.WindowEnd);
        }
    }

    /// <summary>
    /// The furthest point <b>any</b> dependent rollup has aggregated past — the consumed high-water
    /// mark of <paramref name="sourceArchiveRtId"/> taken as the <b>maximum</b> of the dependents'
    /// <c>LastAggregatedBucketEnd</c>. A write strictly before this is a retroactive correction for at
    /// least one dependent (AB#4288): because a source can feed rollups of different granularities
    /// whose watermarks differ widely (e.g. a yearly rollup lags because its bucket only closes
    /// annually while an hourly rollup sits near now), using the <i>minimum</i> here would silently
    /// miss corrections in the band between the slowest and fastest dependent. The orchestrator clamps
    /// each dependent's recompute range to its own watermark, so flagging on the maximum never makes a
    /// lagging dependent re-aggregate a forward, not-yet-closed bucket. Null when no dependent exists
    /// or none has aggregated yet (nothing has been consumed, so no write can be retroactive).
    /// </summary>
    private async Task<DateTime?> GetConsumedWatermarkAsync(OctoObjectId sourceArchiveRtId)
    {
        if (_rollupArchiveStore is null)
        {
            return null;
        }

        DateTime? max = null;
        await foreach (var rollup in _rollupArchiveStore.EnumerateAsync())
        {
            if (rollup.SourceArchiveRtId != sourceArchiveRtId)
            {
                continue;
            }
            if (rollup.LastAggregatedBucketEnd is { } watermark && (max is null || watermark > max))
            {
                max = watermark;
            }
        }
        return max;
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetArchiveMinTimestampAsync(
        OctoObjectId archiveRtId, CancellationToken cancellationToken = default)
    {
        // Need the snapshot only to pick the time-axis column: window_start for windowed (rollup /
        // time-range) tables, timestamp for raw tables. Created archives (no snapshot / no backing
        // table) resolve to null.
        var snapshot = await _archiveStore.GetAsync(archiveRtId);
        if (snapshot is null)
        {
            return null;
        }

        var timeColumn = snapshot.UsesWindowedStorage ? Constants.WindowStart : Constants.Timestamp;
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString());
        var sql = $"SELECT MIN(\"{timeColumn}\") AS \"min_ts\" FROM {qualifiedTable}";

        try
        {
            await foreach (var row in _databaseClient.StreamRawRowsAsync(_tenantId, sql, cancellationToken))
            {
                if (row.TryGetValue("min_ts", out var value))
                {
                    return AsUtcDateTimeFlexible(value);
                }
                break;
            }
        }
        catch (Exception ex) when (IsRelationUnknown(ex))
        {
            // AB#4284: ONLY "no backing table yet" (e.g. Created status: MIN over a missing table)
            // maps to "no data" → null. Any OTHER read failure (transient CrateDB read, connector
            // reset, timeout) must propagate so BackfillRollupFromSource ends the job Failed with the
            // real error instead of reporting a misleading "source holds no data" no-op. Debug so the
            // genuinely-empty path stays observable but quiet.
            _logger.LogDebug(ex,
                "Archive {ArchiveRtId}: min-timestamp probe found no backing table (treated as empty).", archiveRtId);
        }

        return null;
    }

    /// <summary>
    /// True when the exception is CrateDB's "relation/table unknown" error — the only failure that
    /// <see cref="GetArchiveMinTimestampAsync"/> treats as a genuine empty source (no backing table
    /// provisioned yet). Matched on message text (mirroring <see cref="IsColumnAlreadyExists"/>)
    /// because the driver surfaces it as a generic exception, and the inner exception chain is
    /// walked so a wrapped driver error is still classified. AB#4284.
    /// </summary>
    internal static bool IsRelationUnknown(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;

            // Explicit CrateDB/Npgsql tokens for a missing relation.
            if (message.Contains("RelationUnknown", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TableUnknown", StringComparison.OrdinalIgnoreCase)
                || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // CrateDB phrases it "Relation 'schema.table' unknown" — the noun and "unknown" are
            // separated by the quoted relation name, so require both tokens rather than an adjacent
            // substring. Guarded by "unknown" so it can never match a transient read message.
            if (message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("relation", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("table", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a CrateDB timestamp scalar to a UTC <see cref="DateTime"/>. Tolerates the wire-type
    /// variants an aggregate result may surface across driver/transport versions: a real
    /// <see cref="DateTime"/> / <see cref="DateTimeOffset"/>, or epoch-milliseconds as an integer.
    /// Returns null for SQL NULL (empty table) or an unrecognised shape.
    /// </summary>
    private static DateTime? AsUtcDateTimeFlexible(object? value) => value switch
    {
        null => null,
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTimeOffset dto => dto.UtcDateTime,
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
        _ => null,
    };

    private async Task EvaluateRollupComputedColumnsAsync(
        OctoObjectId rollupRtId,
        string targetTable,
        DateTime bucketStart,
        DateTime bucketEnd,
        CancellationToken cancellationToken)
    {
        // The rollup's computed columns live on its archive snapshot (the orchestrator only passes
        // the rollup-specific snapshot, which doesn't carry Columns). Load it; bail out fast for the
        // common case of a rollup without computed columns.
        var snapshot = await _archiveStore.GetAsync(rollupRtId);
        if (snapshot is null)
        {
            return;
        }

        var plan = BuildComputedPlan(snapshot);
        if (plan.Count == 0)
        {
            return;
        }

        // Aggregate columns are the non-computed columns; their Path is already the physical storage
        // name. Read them (plus the key columns) back for the bucket.
        var aggregateColumns = new List<string>();
        foreach (var column in snapshot.Columns)
        {
            if (!column.IsComputed)
            {
                aggregateColumns.Add(ColumnNameMapper.PathToColumnName(column.Path));
            }
        }

        var selectSql = RollupComputedColumnSqlBuilder.BuildSelect(
            targetTable, aggregateColumns, bucketStart, bucketEnd);

        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in _databaseClient.StreamRawRowsAsync(_tenantId, selectSql, cancellationToken))
        {
            rows.Add(new Dictionary<string, object?>(row, StringComparer.Ordinal));
        }

        foreach (var row in rows)
        {
            if (!row.TryGetValue(Constants.RtId, out var rtIdValue) || rtIdValue is null ||
                !row.TryGetValue(Constants.CkTypeId, out var ckTypeIdValue) || ckTypeIdValue is null)
            {
                continue;
            }

            // Evaluate the computed columns over the bucket row and collect their cells.
            ApplyComputedColumns(row, plan);

            var assignments = new List<(string Column, object? Value)>(plan.Count);
            foreach (var item in plan)
            {
                row.TryGetValue(item.ColumnName, out var value);
                assignments.Add((item.ColumnName, value));
            }

            var updateSql = RollupComputedColumnSqlBuilder.BuildUpdate(
                targetTable, assignments, rtIdValue.ToString()!, ckTypeIdValue.ToString()!,
                bucketStart, bucketEnd);

            await _databaseClient.ExecuteNonQueryAsync(_tenantId, updateSql, cancellationToken);
        }
    }

    /// <summary>
    /// Backfills one computed column across the existing rows of a live raw / time-range archive
    /// (AB#4189 Phase 7, §8): page through the rows, evaluate the column's formula in .NET over each
    /// row's operands, and write the result into the column's physical cell. The physical column must
    /// already exist (the lifecycle adds it via <c>ALTER TABLE ADD COLUMN</c> before the backfill).
    /// For an add the target is in a non-readable state (Backfilling) so the read path hides it
    /// (<see cref="ReadableComputedColumns"/>) until the caller flips it to
    /// <see cref="ComputedColumnState.Active"/>; for a formula change the target stays Active at the
    /// previous version while the new formula is backfilled into the pending versioned column.
    /// <para>
    /// Each page is fully materialised before its UPDATEs run — CrateDB / Npgsql cannot issue an
    /// UPDATE while a SELECT cursor is open on the same connection (the same constraint
    /// <see cref="EvaluateRollupComputedColumnsAsync"/> works around).
    /// </para>
    /// </summary>
    public async Task BackfillComputedColumnAsync(
        ArchiveSnapshot snapshot, string columnName, CancellationToken cancellationToken = default)
    {
        var target = snapshot.Columns.FirstOrDefault(
            c => c.IsComputed && string.Equals(c.Name, columnName, StringComparison.Ordinal));
        if (target?.Name is null || target.ResultType is null)
        {
            // Unknown / malformed column — validation upstream rejects this; never throw here.
            return;
        }

        // A formula change (PendingFormula set) backfills the NEW formula into the pending versioned
        // column ({base}__v{N+1}); the plain add / re-add case backfills the active formula into the
        // active column. Either way it is a single-target evaluation over the row's operands.
        var (formula, targetPhysical) = target.HasPendingFormula
            ? (target.PendingFormula!, ComputedColumnNaming.Pending(target))
            : (target.Formula!, ComputedColumnNaming.Active(target));
        var plan = new[] { new ComputedColumnPlanItem(targetPhysical, formula, target.ResultType.Value) };

        var table = TenantSchema.QualifiedArchiveTable(_tenantId, snapshot.RtId.ToString());
        var keyColumns = BackfillKeyColumns(snapshot);
        // Operands are the readable data-stream columns (ingested + already-active computed) at their
        // physical names, minus the target's own physical columns (a formula can't reference itself).
        var activeTargetPhysical = ComputedColumnNaming.Active(target);
        var valueColumns = ReadableOperandPhysicalColumns(snapshot)
            .Where(p => !string.Equals(p, targetPhysical, StringComparison.Ordinal)
                        && !string.Equals(p, activeTargetPhysical, StringComparison.Ordinal))
            .ToList();

        // Keyset (cursor) paging over the primary key, NOT OFFSET: each page seeks straight past the
        // previous page's last row via the key index, so memory stays O(BackfillPageSize) regardless
        // of archive size. OFFSET made CrateDB collect + sort the whole table up to the offset on
        // every page, tripping the query circuit breaker on the final pages of a large archive (AB#4189).
        IReadOnlyList<(string Column, object? Value)>? cursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var selectSql = ComputedColumnBackfillSqlBuilder.BuildSelect(
                table, keyColumns, valueColumns, BackfillPageSize, cursor);

            var page = new List<Dictionary<string, object?>>(BackfillPageSize);
            await foreach (var row in _databaseClient.StreamRawRowsAsync(_tenantId, selectSql, cancellationToken))
            {
                page.Add(new Dictionary<string, object?>(row, StringComparer.Ordinal));
            }

            // Write the whole page in ONE CrateDB bulk (one prepared UPDATE, one round-trip) instead
            // of N individual UPDATE statements — the per-statement cost dominated the backfill
            // (~500x on 4k rows measured). Each computed value is still evaluated per row in .NET.
            var updateSql = ComputedColumnBackfillSqlBuilder.BuildBulkUpdate(table, targetPhysical, keyColumns);
            var argumentSets = new List<IReadOnlyList<object?>>(page.Count);
            foreach (var row in page)
            {
                ApplyComputedColumns(row, plan);
                row.TryGetValue(targetPhysical, out var value);

                // Positional args: [computedValue, key0, key1, …] to match BuildBulkUpdate's $1..$n.
                var args = new object?[keyColumns.Count + 1];
                args[0] = value;
                for (var k = 0; k < keyColumns.Count; k++)
                {
                    row.TryGetValue(keyColumns[k], out var keyValue);
                    args[k + 1] = keyValue;
                }

                argumentSets.Add(args);
            }

            await _databaseClient.ExecuteBulkAsync(_tenantId, updateSql, argumentSets, cancellationToken);

            if (page.Count < BackfillPageSize)
            {
                break;
            }

            // Advance the cursor to the last row's key so the next page starts strictly after it.
            var lastRow = page[^1];
            var nextCursor = new List<(string Column, object? Value)>(keyColumns.Count);
            foreach (var key in keyColumns)
            {
                lastRow.TryGetValue(key, out var keyValue);
                nextCursor.Add((key, keyValue));
            }

            cursor = nextCursor;
        }

        // Make the backfilled cells visible to subsequent reads (CrateDB applies writes to the read
        // path asynchronously), mirroring the rollup recompute executor's refresh discipline.
        await _databaseClient.RefreshArchiveTableAsync(_tenantId, table);
    }

    /// <summary>
    /// The primary-key columns that address a single row for the backfill UPDATE: raw archives are
    /// keyed by <c>(timestamp, rtid, cktypeid)</c>, windowed (time-range) archives by
    /// <c>(window_start, window_end, rtid, cktypeid)</c>.
    /// </summary>
    private static IReadOnlyList<string> BackfillKeyColumns(ArchiveSnapshot snapshot) =>
        snapshot.UsesWindowedStorage
            ? new[] { Constants.WindowStart, Constants.WindowEnd, Constants.RtId, Constants.CkTypeId }
            : new[] { Constants.Timestamp, Constants.RtId, Constants.CkTypeId };

    /// <inheritdoc />
    public Task ValidateComputedColumnsAsync(
        OctoObjectId archiveRtId,
        IReadOnlyList<CkArchiveColumnSpec> prospectiveColumns,
        CancellationToken cancellationToken = default)
    {
        // Pure (no I/O): runs the same validator the activation path uses, over an arbitrary
        // prospective set. Throws ComputedColumnInvalidException (a StreamDataException → stable
        // GraphQL error code) on the first issue.
        ComputedColumnValidator.Validate(archiveRtId, prospectiveColumns, _formulaEngine);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task AddComputedColumnStorageAsync(
        ArchiveSnapshot snapshot, string columnName, CancellationToken cancellationToken = default)
    {
        var column = snapshot.Columns.FirstOrDefault(
            c => c.IsComputed && string.Equals(c.Name, columnName, StringComparison.Ordinal));
        if (column is null)
        {
            return; // nothing to provision — validation upstream rejects an unknown column
        }

        var table = TenantSchema.QualifiedArchiveTable(_tenantId, snapshot.RtId.ToString());
        var sql = ArchiveDdlGenerator.GenerateAddColumn(table, ComputedColumnDdl.Build(column));

        try
        {
            await _databaseClient.ExecuteNonQueryAsync(_tenantId, sql, cancellationToken);
        }
        catch (Exception ex) when (IsColumnAlreadyExists(ex))
        {
            // Idempotent: a re-add reuses the orphaned physical column left by a prior remove, and a
            // retried add finds the column already provisioned. The backfill overwrites every row.
            _logger.LogDebug(ex,
                "Physical column for computed column '{Column}' on archive {Archive} already exists; reusing it.",
                columnName, snapshot.RtId);
        }
    }

    /// <inheritdoc />
    public async Task AddPendingComputedColumnStorageAsync(
        ArchiveSnapshot snapshot, string columnName, CancellationToken cancellationToken = default)
    {
        var column = snapshot.Columns.FirstOrDefault(
            c => c.IsComputed && string.Equals(c.Name, columnName, StringComparison.Ordinal));
        if (column is null)
        {
            return;
        }

        // Guard (AB#4189 Phase 7 MVP limitation): a formula change re-versions the column's physical
        // name ({base} -> {base}__v{N+1}), so any other computed column whose formula references the
        // current physical name would silently bind to the orphaned old column. Reject the change and
        // make the operator re-point or remove the dependent first. (Direct-SQL / Grafana consumers
        // referencing the physical name must likewise follow the rename — documented.)
        var activePhysical = ComputedColumnNaming.Active(column);
        foreach (var other in snapshot.Columns)
        {
            if (ReferenceEquals(other, column) || !other.IsComputed || string.IsNullOrEmpty(other.Formula))
            {
                continue;
            }

            if (ReferencesToken(other.Formula!, activePhysical))
            {
                throw new ComputedColumnInvalidException(snapshot.RtId, column.Name,
                    $"cannot change the formula of computed column '{column.Name}' because computed column " +
                    $"'{other.Name}' references it; remove or re-point the dependent computed column first.");
            }
        }

        var table = TenantSchema.QualifiedArchiveTable(_tenantId, snapshot.RtId.ToString());
        var pendingName = ComputedColumnNaming.Pending(column);
        var sql = ArchiveDdlGenerator.GenerateAddColumn(table, ComputedColumnDdl.Build(column, pendingName));

        try
        {
            await _databaseClient.ExecuteNonQueryAsync(_tenantId, sql, cancellationToken);
        }
        catch (Exception ex) when (IsColumnAlreadyExists(ex))
        {
            // Idempotent: a retried formula change finds the pending column already provisioned.
            _logger.LogDebug(ex,
                "Pending physical column '{Pending}' on archive {Archive} already exists; reusing it.",
                pendingName, snapshot.RtId);
        }
    }

    /// <summary>
    /// True when a failed <c>ALTER TABLE … ADD COLUMN</c> is the benign "column already exists" case
    /// (CrateDB has no <c>ADD COLUMN IF NOT EXISTS</c>), so the add path can treat it as idempotent.
    /// </summary>
    private static bool IsColumnAlreadyExists(Exception ex) =>
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("already has a column", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<OctoObjectId, ArchiveStorageStats>> GetArchiveStatsAsync(
        IReadOnlyList<OctoObjectId> archiveRtIds,
        CancellationToken cancellationToken = default)
    {
        if (archiveRtIds.Count == 0)
        {
            return new Dictionary<OctoObjectId, ArchiveStorageStats>();
        }

        // Map every requested rtId to its CrateDB table name and remember the reverse direction
        // so the result-set lookup is O(1) regardless of how many tables CrateDB returned.
        var tableNameToRtId = archiveRtIds.ToDictionary(
            rtId => TenantSchema.ArchiveTableName(rtId.ToString()),
            rtId => rtId);

        var rows = await _databaseClient
            .GetTableStatsAsync(_tenantId, tableNameToRtId.Keys.ToList(), cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<OctoObjectId, ArchiveStorageStats>(archiveRtIds.Count);

        // Pre-populate with "does not exist" placeholders so callers always get an entry per
        // input rtId. The CrateDB query only returns rows for tables that actually have shards.
        foreach (var rtId in archiveRtIds)
        {
            result[rtId] = new ArchiveStorageStats(rtId, TableExists: false, RecordCount: 0, SizeBytes: 0, Health: ArchiveStorageHealth.Unknown);
        }

        foreach (var row in rows)
        {
            if (!tableNameToRtId.TryGetValue(row.TableName, out var rtId))
            {
                continue; // shouldn't happen — query was scoped to our tables — but be defensive.
            }
            result[rtId] = new ArchiveStorageStats(
                ArchiveRtId: rtId,
                TableExists: true,
                RecordCount: row.NumDocs,
                SizeBytes: row.SizeBytes,
                Health: MapHealth(row.Health));
        }

        return result;
    }

    /// <summary>
    /// Maps CrateDB's <c>sys.health.health</c> string (GREEN / YELLOW / RED) onto the backend-
    /// agnostic <see cref="ArchiveStorageHealth"/>. Null / unknown values become
    /// <see cref="ArchiveStorageHealth.Unknown"/> — UIs should render that visibly different from
    /// the green Good state rather than treating absence as "everything OK".
    /// </summary>
    private static ArchiveStorageHealth MapHealth(string? crateHealth) => crateHealth switch
    {
        "GREEN" => ArchiveStorageHealth.Good,
        "YELLOW" => ArchiveStorageHealth.Warning,
        "RED" => ArchiveStorageHealth.Critical,
        _ => ArchiveStorageHealth.Unknown,
    };

    #region Private helpers

    /// <summary>
    /// Verifies that the archive identified by <paramref name="archiveRtId"/> exists and is in
    /// status <see cref="CkArchiveStatus.Activated"/>; throws otherwise.
    /// </summary>
    private async Task<ArchiveSnapshot> EnsureArchiveActivatedAsync(OctoObjectId archiveRtId)
    {
        var snapshot = await _archiveStore.GetAsync(archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        if (snapshot.Status != CkArchiveStatus.Activated)
        {
            throw new ArchiveNotActivatedException(archiveRtId, snapshot.Status);
        }

        return snapshot;
    }

    private static StreamDataFieldResolver CreateFieldResolver(ArchiveSnapshot snapshot)
    {
        // T17 archives are CK-type-agnostic: the queryable column set is exactly the columns the
        // archive was configured to capture. Windowed (rollup + time-range) archives swap the
        // single `timestamp` default for the `window_start` / `window_end` / `was_updated` triple.
        var resolver = new StreamDataFieldResolver(
            snapshot.Columns.Where(c => !c.IsComputed).Select(c => c.Path),
            usesWindowedStorage: snapshot.UsesWindowedStorage);

        // Computed columns are registered explicitly with their *versioned* active physical name
        // (a formula change moves them to {base}__v{N}), which the generic PathToColumnName mapping
        // the ctor uses for ingested columns can't produce.
        foreach (var (name, physical) in ReadableComputedColumns(snapshot))
        {
            resolver.RegisterComputedColumn(name, physical);
        }

        return resolver;
    }

    /// <summary>
    /// The computed columns the read path may project, as (logical <c>Name</c>, active versioned
    /// physical name) pairs. A computed column mid-backfill (<see cref="ComputedColumnState.Pending"/> /
    /// <see cref="ComputedColumnState.Backfilling"/>) or whose backfill failed
    /// (<see cref="ComputedColumnState.Failed"/>) is excluded, so consumers keep seeing the previous
    /// archive state until the backfill commits (AB#4189 §8.3). A computed column created together with
    /// its archive carries no lifecycle state (<c>null</c>) and is live from creation. The ingest / DDL
    /// path (<see cref="ResolveTableAndColumns"/>) deliberately does <em>not</em> gate on state — even a
    /// Pending column's physical column exists and must be written on ingest; gating is a read concern.
    /// </summary>
    internal static IEnumerable<(string Name, string Physical)> ReadableComputedColumns(ArchiveSnapshot snapshot)
    {
        foreach (var c in snapshot.Columns)
        {
            // Defensively skip a computed column missing its Name — activation DDL would have rejected
            // it, but the read path must never throw on a malformed snapshot.
            if (!c.IsComputed || string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            if (c.ComputedState is null or ComputedColumnState.Active)
            {
                yield return (c.Name!, ComputedColumnNaming.Active(c));
            }
        }
    }

    /// <summary>
    /// The physical names of the readable data-stream columns that can feed a formula as operands:
    /// every ingested column plus every active computed column (at its versioned physical name).
    /// Used by the backfill to SELECT a row's operand values.
    /// </summary>
    private static IEnumerable<string> ReadableOperandPhysicalColumns(ArchiveSnapshot snapshot)
    {
        foreach (var c in snapshot.Columns)
        {
            if (!c.IsComputed)
            {
                yield return ColumnNameMapper.PathToColumnName(c.Path);
                continue;
            }

            if (string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            if (c.ComputedState is null or ComputedColumnState.Active)
            {
                yield return ComputedColumnNaming.Active(c);
            }
        }
    }

    /// <summary>
    /// Chain-aware aggregation resolution for a rollup archive. Loads the rollup snapshot from
    /// the rollup store (required for chain walking — null store ⇒ falls back to the 1-level
    /// resolver), then asks <see cref="RollupChainAggregationResolver"/> to map the (logical
    /// path, function) onto the right physical column on the *current* rollup. Used by the
    /// aggregation / grouped-aggregation / downsampling code paths.
    /// </summary>
    private async Task<RollupQueryAggregation?> ResolveRollupChainAggregationAsync(
        ArchiveSnapshot snapshot,
        IReadOnlyList<CkRollupAggregationSpec> rollupSpecs,
        string attributePath,
        AggregationFunctionDto targetFunc,
        CancellationToken cancellationToken)
    {
        // Always try the cascade walker first when we have access to the rollup store — it
        // also handles the 1-level case correctly.
        if (_rollupArchiveStore is not null)
        {
            var rollup = await _rollupArchiveStore.GetAsync(snapshot.RtId).ConfigureAwait(false);
            if (rollup is not null)
            {
                var chained = await RollupChainAggregationResolver.ResolveAsync(
                    rollup,
                    attributePath,
                    targetFunc,
                    id => _archiveStore.GetAsync(id),
                    id => _rollupArchiveStore.GetAsync(id),
                    cancellationToken).ConfigureAwait(false);
                if (chained is not null)
                {
                    return chained;
                }
            }
        }

        // Fallback: the 1-level resolver inspects only the current rollup's specs.
        return RollupQueryAggregationResolver.Resolve(rollupSpecs, attributePath, targetFunc);
    }

    private static List<string> ResolveAndAddColumns(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<string> columns)
    {
        var resolvedColumnNames = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var resolved = fieldResolver.Resolve(column);
            if (resolved == null) continue;

            // CrateDbName is the lower-case concatenated form (see ColumnNameMapper) and is what
            // StreamDataRow.Values is keyed by — both the SQL alias on the read side and the
            // dictionary key on the row-mapping side. The GraphQL wire form is decided separately
            // by StreamDataFieldResolverExtensions.ResolveToMappings (echoes the caller input).
            resolvedColumnNames.Add(resolved.CrateDbName);

            if (resolved.Category == StreamDataFieldCategory.Default)
            {
                // Default fields are already included by IncludeDefaultVariables()
                continue;
            }

            // SQL alias = CrateDbName so CrateDB returns the column already in canonical form.
            q.AddVariable(resolved.CrateDbName, resolved.CrateDbName, null);
        }

        return resolvedColumnNames;
    }

    private static void AddRtIdFilter(CrateQueryBuilder q, IReadOnlyList<OctoObjectId>? rtIds)
    {
        if (rtIds is not { Count: > 0 }) return;
        // Emit the rtId scope as an `In` field-filter on the raw `rtid` column rather than an
        // AddWhereIn on a SELECT variable. AddWhereIn only works when `rtid` is a registered
        // output variable — true on the simple/rows path (IncludeDefaultVariables) but NOT on the
        // aggregation / grouped-aggregation paths, where only the group-by columns and aggregates
        // are selected. There AddWhereIn threw "WhereIn Variable not found: 'rtid'" and broke the
        // pie-chart (grouped) widgets. The field-filter lands in the shared source-filter WHERE
        // conditions, independent of the SELECT list, so it scopes every query kind uniformly —
        // the same mechanism the downsampling path already uses.
        q.AddFieldFilter(Constants.RtId, Dtos.StreamDataFieldFilterOperator.In, string.Empty,
            valueList: rtIds.Select(x => x.ToString()).ToList());
    }

    private static void AddSortOrders(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<SortOrderItem>? sortOrders)
    {
        if (sortOrders == null) return;

        foreach (var sort in sortOrders)
        {
            var resolved = fieldResolver.Resolve(sort.AttributePath);
            if (resolved == null) continue;

            // SQL aliases now use CrateDbName (PascalCase) for both default and data fields.
            var sortPath = resolved.CrateDbName;

            var sortOrder = sort.SortOrder == SortOrders.Descending
                ? SortOrderDto.Descending
                : SortOrderDto.Ascending;

            q.OrderBy(sortPath, sortOrder);
        }
    }

    private static void AddFieldFilters(
        CrateQueryBuilder q,
        StreamDataFieldResolver fieldResolver,
        IReadOnlyList<FieldFilter>? fieldFilters)
    {
        if (fieldFilters == null) return;

        foreach (var filter in fieldFilters)
        {
            // IsNull and IsNotNull have no comparison value — allow them through
            var isNullCheck = filter.Operator is FieldFilterOperator.IsNull or FieldFilterOperator.IsNotNull;
            if (!isNullCheck && filter.ComparisonValue == null) continue;

            var resolved = fieldResolver.Resolve(filter.AttributePath);
            if (resolved == null) continue;

            var op = MapFieldFilterOperator(filter.Operator);

            switch (filter.Operator)
            {
                case FieldFilterOperator.Between:
                    q.AddFieldFilter(resolved.CrateDbName, op,
                        filter.ComparisonValue?.ToString() ?? "",
                        secondaryValue: filter.SecondaryValue?.ToString());
                    break;

                case FieldFilterOperator.In:
                case FieldFilterOperator.NotIn:
                {
                    // Parse the comparison value identically to the MongoDB runtime path so the
                    // same GraphQL `comparisonValue` syntax (incl. the `"[a, b]"` array form)
                    // works against CrateDB archives. See StreamDataFieldFilterValueParser.
                    var valueList = StreamDataFieldFilterValueParser.ParseInValues(filter.ComparisonValue);
                    q.AddFieldFilter(resolved.CrateDbName, op, "", valueList: valueList);
                    break;
                }

                case FieldFilterOperator.IsNull:
                case FieldFilterOperator.IsNotNull:
                    q.AddFieldFilter(resolved.CrateDbName, op, "");
                    break;

                default:
                    q.AddFieldFilter(resolved.CrateDbName, op, filter.ComparisonValue!.ToString()!);
                    break;
            }
        }
    }

    private async Task<(List<DataPointDto> Data, int TotalCount, int Offset)> ExecutePaginatedQueryAsync(
        CrateQueryBuilder q,
        int? offset,
        int? pageSize,
        int? rowCap)
    {
        var compiler = new CrateQueryCompiler();

        // Compile count query BEFORE setting LIMIT/OFFSET
        var countSql = compiler.CompileCountQuery(q);

        // Add Timestamp tiebreaker for deterministic pagination
        q.AddOrderByTiebreaker(Constants.Timestamp, SortOrderDto.Ascending);

        var effectiveOffset = offset.GetValueOrDefault(0);

        // Compute effective page limit considering rowCap
        int? effectivePageLimit = pageSize;
        if (rowCap.HasValue && effectivePageLimit.HasValue)
        {
            effectivePageLimit = Math.Min(effectivePageLimit.Value, Math.Max(0, rowCap.Value - effectiveOffset));
        }
        else if (rowCap.HasValue)
        {
            effectivePageLimit = Math.Max(0, rowCap.Value - effectiveOffset);
        }

        // Edge case: offset is beyond the row cap
        if (effectivePageLimit is <= 0)
        {
            var emptyCountResult = await _databaseClient.GetCountAsync(_tenantId, countSql);
            var emptyTotalCount = rowCap.HasValue
                ? (int)Math.Min(emptyCountResult, rowCap.Value)
                : (int)emptyCountResult;
            return ([], emptyTotalCount, effectiveOffset);
        }

        if (effectiveOffset > 0) q.WithOffset(effectiveOffset);
        if (effectivePageLimit is > 0) q.WithLimit(effectivePageLimit.Value);

        var dataSql = compiler.CompileQuery(q);

        _logger.LogDebug("Executing paginated SQL: {DataSql} | Count: {CountSql}", dataSql, countSql);

        // Execute count + data in parallel
        var countTask = _databaseClient.GetCountAsync(_tenantId, countSql);
        var dataTask = _databaseClient.GetDataAsync(_tenantId, dataSql);
        await Task.WhenAll(countTask, dataTask);

        var totalCount = countTask.Result;
        var effectiveTotalCount = rowCap.HasValue
            ? (int)Math.Min(totalCount, rowCap.Value)
            : (int)totalCount;

        return (dataTask.Result, effectiveTotalCount, effectiveOffset);
    }

    private static Dtos.StreamDataFieldFilterOperator MapFieldFilterOperator(FieldFilterOperator op)
    {
        return op switch
        {
            FieldFilterOperator.Equals           => Dtos.StreamDataFieldFilterOperator.Equals,
            FieldFilterOperator.NotEquals        => Dtos.StreamDataFieldFilterOperator.NotEquals,
            FieldFilterOperator.GreaterThan      => Dtos.StreamDataFieldFilterOperator.GreaterThan,
            FieldFilterOperator.GreaterEqualThan => Dtos.StreamDataFieldFilterOperator.GreaterThanOrEqual,
            FieldFilterOperator.LessThan         => Dtos.StreamDataFieldFilterOperator.LessThan,
            FieldFilterOperator.LessEqualThan    => Dtos.StreamDataFieldFilterOperator.LessThanOrEqual,
            FieldFilterOperator.Like             => Dtos.StreamDataFieldFilterOperator.Like,
            FieldFilterOperator.In               => Dtos.StreamDataFieldFilterOperator.In,
            FieldFilterOperator.NotIn            => Dtos.StreamDataFieldFilterOperator.NotIn,
            FieldFilterOperator.IsNull           => Dtos.StreamDataFieldFilterOperator.IsNull,
            FieldFilterOperator.IsNotNull        => Dtos.StreamDataFieldFilterOperator.IsNotNull,
            FieldFilterOperator.Between          => Dtos.StreamDataFieldFilterOperator.Between,
            FieldFilterOperator.MatchRegEx or
            FieldFilterOperator.AnyEq or
            FieldFilterOperator.AnyLike or
            FieldFilterOperator.Match or
            FieldFilterOperator.Contains or
            FieldFilterOperator.StartsWith or
            FieldFilterOperator.EndsWith => throw new NotSupportedException(
                $"Operator {op} is not supported for stream data queries against CrateDB."),
            _ => throw new NotSupportedException(
                $"Operator {op} is not supported for stream data queries against CrateDB.")
        };
    }

    private static AggregationFunctionDto MapAggregationFunction(AggregationFunction func)
    {
        return func switch
        {
            AggregationFunction.Average => AggregationFunctionDto.Avg,
            AggregationFunction.Minimum => AggregationFunctionDto.Min,
            AggregationFunction.Maximum => AggregationFunctionDto.Max,
            AggregationFunction.Count   => AggregationFunctionDto.Count,
            AggregationFunction.Sum     => AggregationFunctionDto.Sum,
            AggregationFunction.TimeWeightedAverage => AggregationFunctionDto.TimeWeightedAvg,
            _ => throw new ArgumentOutOfRangeException(nameof(func))
        };
    }

    /// <summary>
    /// TWA over a raw (event-based) archive — the query-time LOCF path (AB#4336 §6.2). Builds the
    /// carry-in + LEAD-weighted statement via <see cref="TimeWeightedQuerySqlBuilder"/> with the
    /// query window as the bucket; plain aggregation columns ride along carry-guarded. Grouped
    /// variant groups the outer SELECT; LOCF weighting stays per <c>rtId</c> either way. The carry
    /// scan uses the engine default lookback (raw archives carry no <c>CarryLookbackMs</c>).
    /// </summary>
    private async Task<StreamDataQueryResult> ExecuteRawTimeWeightedAggregationAsync(
        OctoObjectId archiveRtId,
        StreamDataFieldResolver fieldResolver,
        StreamDataQueryOptionsBase options,
        IReadOnlyList<AggregationColumn> aggregationColumns,
        IReadOnlyList<string> groupByColumnPaths)
    {
        if (options.From is null || options.To is null)
        {
            throw StreamDataException.InvalidQueryParameters(
                "TimeWeightedAverage over a raw archive requires From and To — the carry-in state is derived relative to the window.");
        }

        if (options.FieldFilters is { Count: > 0 })
        {
            throw StreamDataException.InvalidQueryParameters(
                "TimeWeightedAverage over a raw archive does not support field filters yet — scope via rtIds or query a TimeWeightedAvg rollup.");
        }

        var outputColumnNames = new List<string>();
        var outputNameBySqlAlias = new Dictionary<string, string>();

        var groupColumns = new List<string>();
        foreach (var groupCol in groupByColumnPaths)
        {
            var resolvedGroup = fieldResolver.Resolve(groupCol);
            if (resolvedGroup == null) continue;
            groupColumns.Add(resolvedGroup.CrateDbName);
            outputColumnNames.Add(resolvedGroup.CrateDbName);
            outputNameBySqlAlias[resolvedGroup.CrateDbName] = resolvedGroup.CrateDbName;
        }

        var twaColumns = new List<TimeWeightedQuerySqlBuilder.TwaColumn>();
        var plainColumns = new List<TimeWeightedQuerySqlBuilder.PlainColumn>();
        foreach (var col in aggregationColumns)
        {
            var resolved = fieldResolver.Resolve(col.AttributePath);
            if (resolved == null) continue;

            var aggFunc = MapAggregationFunction(col.Function);
            if (col.Function == AggregationFunction.TimeWeightedAverage)
            {
                // Alias matches the rollup read path and the GraphQL wire suffix ("_twavg").
                var twaAlias = $"{resolved.CrateDbName}_twavg";
                twaColumns.Add(new TimeWeightedQuerySqlBuilder.TwaColumn(resolved.CrateDbName, twaAlias));
                outputColumnNames.Add(twaAlias);
                outputNameBySqlAlias[twaAlias] = twaAlias;
            }
            else
            {
                // Same "{column}_{lowercaseFunction}" output key as the standard aggregation path.
                var alias = $"{resolved.CrateDbName}_{aggFunc.ToString().ToLowerInvariant()}";
                plainColumns.Add(new TimeWeightedQuerySqlBuilder.PlainColumn(
                    aggFunc.ToString().ToUpperInvariant(), resolved.CrateDbName, alias));
                outputColumnNames.Add(alias);
                outputNameBySqlAlias[alias] = alias;
            }
        }

        var sql = TimeWeightedQuerySqlBuilder.Build(
            TenantSchema.QualifiedArchiveTable(_tenantId, archiveRtId.ToString()),
            options.CkTypeId?.SemanticVersionedFullName,
            options.From.Value,
            options.To.Value,
            twaColumns,
            plainColumns,
            groupColumns,
            options.RtIds?.Select(id => id.ToString()).ToList(),
            RollupAggregationSqlBuilder.DefaultCarryLookback);

        _logger.LogDebug("Executing raw time-weighted aggregation SQL: {Sql}", sql);

        var data = await _databaseClient.GetDataAsync(_tenantId, sql);
        var rows = data.Select(dp => MapAggregationRow(dp, outputColumnNames, outputNameBySqlAlias)).ToList();
        return new StreamDataQueryResult { Rows = rows, TotalCount = rows.Count };
    }

    /// <summary>
    /// A single computed column resolved for ingest: the physical column name, its formula, and the
    /// declared result type the double result is cast back to (concept §5).
    /// </summary>
    private readonly record struct ComputedColumnPlanItem(
        string ColumnName, string Formula, FormulaResultType ResultType);

    /// <summary>
    /// Builds the per-archive computed-column evaluation plan in dependency order: a computed column
    /// whose formula references another computed column is evaluated after it (Kahn topological
    /// sort). On an unexpected cycle — validation rejects cycles up front — the declared order is
    /// used. Returns an empty list for the common case of an archive with no computed columns, so the
    /// ingest hot path pays nothing.
    /// </summary>
    private static IReadOnlyList<ComputedColumnPlanItem> BuildComputedPlan(ArchiveSnapshot snapshot)
    {
        List<ComputedColumnPlanItem>? items = null;
        foreach (var c in snapshot.Columns)
        {
            if (!c.IsComputed || string.IsNullOrWhiteSpace(c.Name) || c.ResultType is null)
            {
                continue;
            }

            items ??= new List<ComputedColumnPlanItem>();
            // The active formula into the active (versioned) physical column — keeps current readers
            // correct on every ingest.
            items.Add(new ComputedColumnPlanItem(
                ComputedColumnNaming.Active(c), c.Formula!, c.ResultType.Value));

            // During a formula change, also evaluate the pending formula into the pending versioned
            // column so rows ingested mid-backfill are already consistent at swap time (dual-write,
            // §8 D-7.3).
            if (c.HasPendingFormula)
            {
                items.Add(new ComputedColumnPlanItem(
                    ComputedColumnNaming.Pending(c), c.PendingFormula!, c.ResultType.Value));
            }
        }

        if (items is null)
        {
            return Array.Empty<ComputedColumnPlanItem>();
        }

        return items.Count <= 1 ? items : TopologicalSort(items);
    }

    private static IReadOnlyList<ComputedColumnPlanItem> TopologicalSort(List<ComputedColumnPlanItem> items)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            indexByName[items[i].ColumnName] = i;
        }

        var dependents = new List<int>[items.Count];
        var inDegree = new int[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            dependents[i] = new List<int>();
        }

        // item i references computed column 'name' ⇒ that column must be evaluated before i.
        for (var i = 0; i < items.Count; i++)
        {
            foreach (var (name, depIndex) in indexByName)
            {
                if (depIndex == i)
                {
                    continue;
                }

                if (ReferencesToken(items[i].Formula, name))
                {
                    dependents[depIndex].Add(i);
                    inDegree[i]++;
                }
            }
        }

        var queue = new Queue<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (inDegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var ordered = new List<ComputedColumnPlanItem>(items.Count);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            ordered.Add(items[i]);
            foreach (var dependent in dependents[i])
            {
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        // A cycle leaves some items unqueued; validation rejects cycles, so fall back to declared order.
        return ordered.Count == items.Count ? ordered : items;
    }

    /// <summary>Whole-word (identifier-boundary) check for <paramref name="token"/> in a formula.</summary>
    private static bool ReferencesToken(string formula, string token)
    {
        var idx = 0;
        while ((idx = formula.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !IsIdentifierChar(formula[idx - 1]);
            var afterPos = idx + token.Length;
            var afterOk = afterPos >= formula.Length || !IsIdentifierChar(formula[afterPos]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            idx = afterPos;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Evaluates each computed column in <paramref name="plan"/> over the row's current column
    /// values and writes the result back into <paramref name="attributes"/> under the column's
    /// physical name. The result also feeds any dependent computed column later in the plan. A
    /// formula that throws (or yields NaN / null) stores NULL — ingest never fails on a computed
    /// column (concept §5).
    /// </summary>
    private void ApplyComputedColumns(Dictionary<string, object?> attributes,
        IReadOnlyList<ComputedColumnPlanItem> plan)
    {
        if (plan.Count == 0)
        {
            return;
        }

        foreach (var item in plan)
        {
            object? result;
            try
            {
                var args = BuildFormulaArguments(attributes);
                result = _formulaEngine.Evaluate(item.Formula, args, item.ResultType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Computed column '{Column}' formula '{Formula}' failed to evaluate; storing NULL.",
                    item.ColumnName, item.Formula);
                result = null;
            }

            attributes[item.ColumnName] = result;
        }
    }

    /// <summary>
    /// Projects the row's column values onto the numeric argument map the formula engine binds.
    /// Columns that have no numeric reading (strings without a number, records, arrays, null) are
    /// simply not bound — a formula referencing them then fails its syntax check and the cell
    /// becomes NULL.
    /// </summary>
    private static IReadOnlyDictionary<string, double> BuildFormulaArguments(
        IReadOnlyDictionary<string, object?> attributes)
    {
        var args = new Dictionary<string, double>(attributes.Count, StringComparer.Ordinal);
        foreach (var kv in attributes)
        {
            if (TryToDouble(kv.Value, out var d))
            {
                args[kv.Key] = d;
            }
        }

        return args;
    }

    private static bool TryToDouble(object? value, out double result)
    {
        result = 0;
        switch (value)
        {
            case null: return false;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case decimal m: result = (double)m; return true;
            case bool bo: result = bo ? 1d : 0d; return true;
            case DateTime dt: result = dt.Ticks; return true;
            case string str:
                return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default: return false;
        }
    }

    private DataPointDto MapToDataPointDto(StreamDataPoint point,
        IReadOnlyList<ComputedColumnPlanItem> computedPlan)
    {
        // Re-key attributes from raw CK paths (e.g. "sensor.reading.value") to the camelCase
        // column names that exist on the per-archive table — the data plane no longer carries a
        // dynamic `data` blob, so the dictionary must align with the table schema directly.
        var attributes = new Dictionary<string, object?>(point.Attributes.Count);
        foreach (var kvp in point.Attributes)
        {
            attributes[ColumnNameMapper.PathToColumnName(kvp.Key)] = kvp.Value;
        }
        ApplyComputedColumns(attributes, computedPlan);
        return new DataPointDto(attributes)
        {
            RtId = point.RtId,
            CkTypeId = point.CkTypeId,
            Timestamp = point.Timestamp,
            RtWellKnownName = point.RtWellKnownName,
            RtCreationDateTime = point.RtCreationDateTime ?? default,
            RtChangedDateTime = point.RtChangedDateTime ?? default
        };
    }

    private static StreamDataRow MapToStreamDataRow(DataPointDto dp, List<string> columnNames)
    {
        var values = new Dictionary<string, object?>();
        foreach (var col in columnNames)
        {
            object? value = col switch
            {
                Constants.RtId => dp.RtId,
                Constants.CkTypeId => dp.CkTypeId,
                Constants.Timestamp => dp.Timestamp,
                Constants.RtWellKnownName => dp.RtWellKnownName,
                Constants.RtCreationDateTime => dp.RtCreationDateTime,
                Constants.RtChangedDateTime => dp.RtChangedDateTime,
                _ => dp.Attributes?.TryGetValue(col, out var v) == true ? v : null
            };
            values[col] = value;
        }

        return new StreamDataRow
        {
            RtId = dp.RtId,
            CkTypeId = dp.CkTypeId,
            Timestamp = dp.Timestamp,
            RtWellKnownName = dp.RtWellKnownName,
            RtCreationDateTime = dp.RtCreationDateTime,
            RtChangedDateTime = dp.RtChangedDateTime,
            Values = values
        };
    }

    /// <summary>
    /// Maps an aggregation query result row. Looks up values via the SQL alias
    /// (which may differ from the output column name when the same attribute is
    /// aggregated multiple times).
    /// </summary>
    private static StreamDataRow MapAggregationRow(
        DataPointDto dp,
        List<string> outputColumnNames,
        Dictionary<string, string> outputNameBySqlAlias)
    {
        var values = new Dictionary<string, object?>();

        // Always surface the SQL alias as a key too, so consumers that need to distinguish
        // multiple aggregations on the same attribute (e.g. the .Aggregations sub-connection
        // requesting Count + Min + Max + Avg + Sum on one path) can look up by
        // "Avg_voltage", "Min_voltage" etc.
        foreach (var kvp in outputNameBySqlAlias)
        {
            if (dp.Attributes != null && dp.Attributes.TryGetValue(kvp.Key, out var sqlAliasValue))
            {
                values[kvp.Key] = sqlAliasValue;
            }
        }

        // Also surface under the friendly output name (attribute path) for the .Rows
        // consumer contract. When multiple stats target the same path the last one wins;
        // consumers needing all stats should look up by SQL alias instead.
        foreach (var outputName in outputColumnNames)
        {
            var sqlAlias = outputNameBySqlAlias
                .FirstOrDefault(kvp => kvp.Value == outputName).Key;

            object? value = null;
            if (sqlAlias != null)
            {
                dp.Attributes?.TryGetValue(sqlAlias, out value);
            }
            else
            {
                dp.Attributes?.TryGetValue(outputName, out value);
            }

            values[outputName] = value;
        }

        return new StreamDataRow
        {
            RtId = dp.RtId,
            CkTypeId = dp.CkTypeId,
            Timestamp = dp.Timestamp,
            RtWellKnownName = dp.RtWellKnownName,
            RtCreationDateTime = dp.RtCreationDateTime,
            RtChangedDateTime = dp.RtChangedDateTime,
            Values = values
        };
    }

    #endregion
}
