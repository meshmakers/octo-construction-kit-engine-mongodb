using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed implementation of <see cref="ICkRollupArchiveRuntimeStore"/>. Reads and writes
/// <c>CkRollupArchive</c> entities through the tenant repository's generic Rt API. Rollup-
/// archives concept §3, §5, §6. Pairs with the CrateDB orchestrator (not implemented yet): the
/// orchestrator advances <see cref="AdvanceWatermarkAsync"/> after each committed bucket; the
/// lifecycle service writes <see cref="SetFrozenUntilAsync"/> from the freeze / unfreeze mutations.
/// </summary>
public sealed class MongoCkRollupArchiveRuntimeStore : ICkRollupArchiveRuntimeStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoCkRollupArchiveRuntimeStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<CkRollupArchiveSnapshot?> GetAsync(OctoObjectId rollupRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkRollupArchive>(session, rollupRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        return MapToSnapshot(entity);
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> InsertAsync(
        string? rtWellKnownName,
        RtCkId<CkTypeId> targetCkTypeId,
        OctoObjectId sourceArchiveRtId,
        TimeSpan bucketSize,
        TimeSpan watermarkLag,
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        IReadOnlyList<CkArchiveColumnSpec> columns)
    {
        var columnList = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        columnList.AddRange(columns.Select(c => new RtCkArchiveColumnRecord
        {
            Path = c.Path,
            Indexed = c.Indexed,
            Required = c.Required,
        }));

        var aggregationList = new AttributeRecordValueList<RtCkRollupAggregationRecord>();
        aggregationList.AddRange(aggregations.Select(a => new RtCkRollupAggregationRecord
        {
            SourcePath = a.SourcePath,
            Function = (RtCkRollupFunctionEnum)(int)a.Function,
            TargetColumnName = a.TargetColumnName,
        }));

        var entity = new RtCkRollupArchive
        {
            RtWellKnownName = rtWellKnownName,
            TargetCkTypeId = targetCkTypeId.ToString(),
            Status = RtCkArchiveStatusEnum.Created,
            SourceArchiveRtId = sourceArchiveRtId.ToString(),
            BucketSizeMs = (int)bucketSize.TotalMilliseconds,
            WatermarkLagMs = (int)watermarkLag.TotalMilliseconds,
            // LastAggregatedBucketEnd starts null — set by ArchiveLifecycleService.ActivateAsync to
            // the activation timestamp (truncated to the bucket boundary). The orchestrator then
            // advances it forward, never backwards (except via rewindRollupWatermark). Concept §5.
            LastAggregatedBucketEnd = null,
            FrozenUntil = null,
            Columns = columnList,
            Aggregations = aggregationList,
        };

        var session = await _tenantRepository.GetSessionAsync();
        await _tenantRepository.InsertOneRtEntityAsync(session, entity);
        return entity.RtId;
    }

    /// <inheritdoc />
    public async Task ArchiveEntityAsync(OctoObjectId rollupRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkRollupArchive>(session, rollupRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return; // idempotent: already deleted (or never existed)
        }

        entity.RtState = RtState.Archived;
        entity.RtArchivedDateTime = DateTime.UtcNow;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtCkRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async Task AdvanceWatermarkAsync(OctoObjectId rollupRtId, DateTime bucketEnd, bool allowRewind = false)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkRollupArchive>(session, rollupRtId)
            ?? throw new ArchiveNotFoundException(rollupRtId);

        // Enforce monotonicity unless the caller explicitly opts into a rewind (the
        // rewindRollupWatermark admin mutation). Without this guard, an out-of-order orchestrator
        // tick could silently un-commit progress.
        if (!allowRewind && entity.LastAggregatedBucketEnd is { } current && bucketEnd < current)
        {
            throw new InvalidArchiveStateTransitionException(
                rollupRtId,
                (CkArchiveStatus)(int)entity.Status,
                $"advance watermark to {bucketEnd:O} (current {current:O} is later)");
        }

        entity.LastAggregatedBucketEnd = bucketEnd;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtCkRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async Task SetFrozenUntilAsync(OctoObjectId rollupRtId, DateTime? frozenUntil)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkRollupArchive>(session, rollupRtId)
            ?? throw new ArchiveNotFoundException(rollupRtId);

        // Monotonicity for the set-forward path is enforced by the lifecycle service before it
        // calls in; clearing (frozenUntil == null) is the unfreeze path and is always allowed.
        entity.FrozenUntil = frozenUntil;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtCkRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CkRollupArchiveSnapshot> EnumerateAsync()
    {
        var session = await _tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtCkRollupArchive>(session, queryOptions);

        foreach (var entity in result.Items)
        {
            if (entity.RtState == RtState.Archived) continue;
            yield return MapToSnapshot(entity);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountActiveRollupsForSourceAsync(OctoObjectId sourceArchiveRtId)
    {
        // Counting client-side keeps the store DB-neutral (the runtime repo doesn't expose a
        // typed count-with-filter for derived types). The rollup population is small (few per
        // source) so this is fine; revisit if it ever shows up in a hot path.
        var session = await _tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtCkRollupArchive>(session, queryOptions);

        var sourceId = sourceArchiveRtId.ToString();
        var count = 0;
        foreach (var entity in result.Items)
        {
            if (entity.RtState == RtState.Archived) continue;
            if (string.Equals(entity.SourceArchiveRtId, sourceId, StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static CkRollupArchiveSnapshot MapToSnapshot(RtCkRollupArchive entity)
    {
        var status = (CkArchiveStatus)(int)entity.Status;
        var targetCkTypeId = entity.TargetCkTypeId is null
            ? new RtCkId<CkTypeId>(string.Empty)
            : new RtCkId<CkTypeId>(entity.TargetCkTypeId);

        var sourceArchiveRtId = string.IsNullOrEmpty(entity.SourceArchiveRtId)
            ? default
            : new OctoObjectId(entity.SourceArchiveRtId);

        var aggregations = (entity.Aggregations ?? Enumerable.Empty<RtCkRollupAggregationRecord>())
            .Where(a => a.SourcePath is not null)
            .Select(a => new CkRollupAggregationSpec(
                a.SourcePath!,
                (CkRollupFunction)(int)a.Function,
                a.TargetColumnName))
            .ToList();

        return new CkRollupArchiveSnapshot(
            entity.RtId,
            targetCkTypeId,
            status,
            entity.RtWellKnownName,
            sourceArchiveRtId,
            TimeSpan.FromMilliseconds(entity.BucketSizeMs),
            TimeSpan.FromMilliseconds(entity.WatermarkLagMs),
            entity.LastAggregatedBucketEnd,
            aggregations,
            entity.FrozenUntil);
    }
}
