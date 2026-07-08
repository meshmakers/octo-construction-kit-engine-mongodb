using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed implementation of <see cref="IArchiveRuntimeStore"/>. Reads and writes
/// <c>CkArchive</c> entities through the tenant repository's generic Rt API. Concept §11 — paired
/// with the CrateDB <c>IStreamDataRepository</c> by <see cref="ArchiveLifecycleService"/>: Crate
/// updates always run before Mongo writes so a transient Mongo failure can be retried without
/// leaving partial state visible to callers.
/// </summary>
public sealed class MongoArchiveRuntimeStore : IArchiveRuntimeStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoArchiveRuntimeStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<ArchiveSnapshot?> GetAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        return MapToSnapshot(entity);
    }

    /// <summary>
    /// Shared mapping from a runtime archive entity (any subtype: <c>RtArchive</c> base,
    /// <c>RtRollupArchive</c>, <c>RtTimeRangeArchive</c>) to the read-only snapshot consumed by
    /// the lifecycle / DDL paths. Made internal so subtype-specific stores
    /// (e.g. <c>MongoTimeRangeArchiveRuntimeStore</c>) can reuse it instead of duplicating the
    /// per-subtype branches.
    /// </summary>
    internal static ArchiveSnapshot MapToSnapshotPublic(RtArchive entity) => MapToSnapshot(entity);

    private static ArchiveSnapshot MapToSnapshot(RtArchive entity)
    {
        var status = (CkArchiveStatus)(int)entity.Status;
        var targetCkTypeId = entity.TargetCkTypeId is null
            ? new RtCkId<CkTypeId>(string.Empty)
            : new RtCkId<CkTypeId>(entity.TargetCkTypeId);

        // Subtype detection drives both the column mapping and the DDL branch downstream:
        // - RollupArchive: columns are derived storage names; aggregations are the authoritative
        //   spec (column types come from the function).
        // - TimeRangeArchive: columns are CK-type attribute paths (like raw archives), but the
        //   storage shape uses (window_start, window_end) + was_updated. Period is advisory.
        // - RawArchive / abstract Archive (legacy): plain attribute-path columns.
        IReadOnlyList<CkRollupAggregationSpec>? rollupAggregations = null;
        IReadOnlyList<CkArchiveColumnSpec> columns;
        var isTimeRange = false;
        System.TimeSpan? period = null;
        if (entity is RtRollupArchive rollup)
        {
            rollupAggregations = (rollup.Aggregations ?? Enumerable.Empty<RtCkRollupAggregationRecord>())
                .Where(a => a.SourcePath is not null)
                .Select(a => new CkRollupAggregationSpec(
                    a.SourcePath!,
                    (CkRollupFunction)(int)a.Function,
                    a.TargetColumnName,
                    // AB#4336: without the comparison value a StateDuration spec renders "= ''"
                    // in the recompute path (this shared snapshot feeds the recompute executor) —
                    // found by TimeWeightedAggregationTests.Recompute_Reproduces….
                    a.ComparisonValue))
                .ToList();
            // Aggregate columns are derived from the aggregation specs; any computed columns the
            // rollup also declares (concept §11) are appended after them. The generated aggregate
            // columns are the dehydrated-cache form; computed columns live in the entity's Columns.
            columns = RollupColumnGenerator.Generate(rollupAggregations);
            var rollupComputed = MapColumnSpecs(entity.Columns).Where(c => c.IsComputed).ToList();
            if (rollupComputed.Count > 0)
            {
                columns = columns.Concat(rollupComputed).ToList();
            }
            // A rollup's native window length is its bucket size — surface it as Period so a
            // downstream rollup's AB#4289 activation guard can enforce bucket-vs-source alignment
            // when this rollup is itself a source (rollup-on-rollup). BucketSizeMs is Int64 ms
            // (System.StreamData 1.6.3).
            period = System.TimeSpan.FromMilliseconds(rollup.BucketSizeMs);
        }
        else if (entity is RtTimeRangeArchive timeRange)
        {
            isTimeRange = true;
            // Period attribute is optional and advisory; the generated record exposes it as
            // a nullable TimeSpan via the inherited attribute machinery.
            period = timeRange.Period;
            columns = MapColumnSpecs(entity.Columns);
        }
        else
        {
            columns = MapColumnSpecs(entity.Columns);
        }

        return new ArchiveSnapshot(
            entity.RtId,
            targetCkTypeId,
            status,
            entity.RtWellKnownName,
            columns)
        {
            RollupAggregations = rollupAggregations,
            IsTimeRange = isTimeRange,
            Period = period,
        };
    }

    /// <summary>
    /// Projects the runtime column records onto <see cref="CkArchiveColumnSpec"/>, keeping both
    /// ingested columns (Path set) and computed columns (Formula set). The computed enum fields are
    /// cast straight across — the CK enum key values are aligned with
    /// <see cref="FormulaResultType"/> / <see cref="ComputedColumnState"/>.
    /// </summary>
    private static IReadOnlyList<CkArchiveColumnSpec> MapColumnSpecs(
        IEnumerable<RtCkArchiveColumnRecord>? columns)
    {
        return (columns ?? Enumerable.Empty<RtCkArchiveColumnRecord>())
            .Where(c => c.Path is not null || !string.IsNullOrWhiteSpace(c.Formula))
            .Select(c =>
            {
                var isComputed = !string.IsNullOrWhiteSpace(c.Formula);
                return new CkArchiveColumnSpec(c.Path ?? string.Empty, c.Indexed, c.Required)
                {
                    Name = c.Name,
                    Formula = c.Formula,
                    // The computed enum fields only carry meaning for computed columns and are
                    // optional on the CK record (null for ingested columns). Key values are aligned
                    // with the contracts enums, so a direct cast is correct when a value is present.
                    ResultType = isComputed && c.ResultType is { } rt ? (FormulaResultType)(int)rt : null,
                    ComputedState = isComputed && c.ComputedState is { } cs ? (ComputedColumnState)(int)cs : null,
                    ComputedVersion = isComputed && c.ComputedVersion is { } cv ? (int)cv : 0,
                    PendingFormula = isComputed ? c.PendingFormula : null,
                };
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task SetStatusAsync(OctoObjectId archiveRtId, CkArchiveStatus newStatus)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        entity.Status = (RtCkArchiveStatusEnum)(int)newStatus;
        // Update via the entity's *concrete* CkTypeId (TimeRangeArchive / RollupArchive / RawArchive).
        // Going through the <RtArchive> generic would pass the abstract base CkTypeId to the rule
        // engine, which rejects abstract types.
        await _tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.CkTypeId!, archiveRtId, entity);
    }

    /// <inheritdoc />
    public Task AddComputedColumnAsync(OctoObjectId archiveRtId, CkArchiveColumnSpec column) =>
        MutateAsync(archiveRtId, entity =>
        {
            var list = entity.Columns ?? new AttributeRecordValueList<RtCkArchiveColumnRecord>();
            list.Add(new RtCkArchiveColumnRecord
            {
                Path = column.Path,
                Indexed = column.Indexed,
                Required = column.Required,
                Name = column.Name,
                Formula = column.Formula,
                ResultType = column.ResultType is { } rt ? (RtCkComputedColumnResultTypeEnum)(int)rt : null,
                ComputedState = column.ComputedState is { } cs ? (RtCkComputedColumnStateEnum)(int)cs : null,
                ComputedVersion = column.ComputedVersion,
            });
            entity.Columns = list;
        });

    /// <inheritdoc />
    public Task SetComputedColumnStateAsync(OctoObjectId archiveRtId, string name, ComputedColumnState state) =>
        MutateComputedColumnAsync(archiveRtId, name,
            column => column.ComputedState = (RtCkComputedColumnStateEnum)(int)state);

    /// <inheritdoc />
    public Task RemoveComputedColumnAsync(OctoObjectId archiveRtId, string name) =>
        MutateAsync(archiveRtId, entity =>
        {
            if (entity.Columns is null)
            {
                return;
            }

            var kept = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
            kept.AddRange(entity.Columns.Where(
                c => !(!string.IsNullOrWhiteSpace(c.Formula) && string.Equals(c.Name, name, StringComparison.Ordinal))));
            entity.Columns = kept;
        });

    /// <inheritdoc />
    public Task SetPendingFormulaAsync(OctoObjectId archiveRtId, string name, string pendingFormula) =>
        MutateComputedColumnAsync(archiveRtId, name, column => column.PendingFormula = pendingFormula);

    /// <inheritdoc />
    public Task SwapComputedColumnFormulaAsync(OctoObjectId archiveRtId, string name, string newFormula, int newVersion) =>
        MutateComputedColumnAsync(archiveRtId, name, column =>
        {
            column.Formula = newFormula;
            column.ComputedVersion = newVersion;
            column.PendingFormula = null;
        });

    /// <inheritdoc />
    public Task ClearPendingFormulaAsync(OctoObjectId archiveRtId, string name) =>
        MutateComputedColumnAsync(archiveRtId, name, column => column.PendingFormula = null);

    private Task MutateComputedColumnAsync(OctoObjectId archiveRtId, string name, Action<RtCkArchiveColumnRecord> mutate) =>
        MutateAsync(archiveRtId, entity =>
        {
            // AttributeRecordValueList materialises a FRESH record per enumeration (CreateSubType), so
            // mutating an element obtained via FirstOrDefault/indexer changes only that throwaway copy —
            // the change must be written back by rebuilding + reassigning the list (same idiom as
            // RemoveComputedColumnAsync). Mutating in place silently no-op'd, which left every computed
            // column stuck in its last persisted state: the Backfilling/Active/Failed flips and the
            // formula-change swap never survived, so a column never became visible (AB#4189).
            if (entity.Columns is null)
            {
                throw new InvalidOperationException(
                    $"Computed column '{name}' not found on archive {archiveRtId}.");
            }

            var rebuilt = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
            var found = false;
            foreach (var column in entity.Columns)
            {
                if (!string.IsNullOrWhiteSpace(column.Formula) &&
                    string.Equals(column.Name, name, StringComparison.Ordinal))
                {
                    mutate(column);
                    found = true;
                }

                rebuilt.Add(column);
            }

            if (!found)
            {
                // The lifecycle service always loads + checks the column before calling these, so a
                // miss here is an internal invariant violation, not a user error.
                throw new InvalidOperationException(
                    $"Computed column '{name}' not found on archive {archiveRtId}.");
            }

            entity.Columns = rebuilt;
        });

    private async Task MutateAsync(OctoObjectId archiveRtId, Action<RtArchive> mutate)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        mutate(entity);

        // Persist via the concrete CkTypeId (Raw / TimeRange / Rollup); the abstract <RtArchive>
        // generic would hand the rule engine the abstract base type. Same idiom as SetStatusAsync.
        await _tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.CkTypeId!, archiveRtId, entity);
    }

    /// <inheritdoc />
    public async Task ArchiveEntityAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return; // idempotent: already deleted (or never existed)
        }

        entity.RtState = RtState.Archived;
        entity.RtArchivedDateTime = DateTime.UtcNow;
        // Same reason as SetStatusAsync: use the concrete CkTypeId carried on the entity.
        await _tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.CkTypeId!, archiveRtId, entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ArchiveSnapshot> EnumerateAsync()
    {
        var session = await _tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtArchive>(session, queryOptions);

        foreach (var entity in result.Items)
        {
            if (entity.RtState == RtState.Archived) continue;
            yield return MapToSnapshot(entity);
        }
    }
}
