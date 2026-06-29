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
                    a.TargetColumnName))
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
