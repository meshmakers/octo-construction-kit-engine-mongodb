using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed implementation of <see cref="IRollupArchiveRuntimeStore"/>. Reads and writes
/// <c>CkRollupArchive</c> entities through the tenant repository's generic Rt API. Rollup-
/// archives concept §3, §5, §6. Pairs with the CrateDB orchestrator (not implemented yet): the
/// orchestrator advances <see cref="AdvanceWatermarkAsync"/> after each committed bucket; the
/// lifecycle service writes <see cref="SetFrozenUntilAsync"/> from the freeze / unfreeze mutations.
/// </summary>
public sealed class MongoRollupArchiveRuntimeStore : IRollupArchiveRuntimeStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoRollupArchiveRuntimeStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<RollupArchiveSnapshot?> GetAsync(OctoObjectId rollupRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        // Load via the abstract base — the same RtId may resolve to RtTimeRangeArchive or
        // RtRawArchive in the unified Archive collection, and querying directly against
        // RtRollupArchive triggers a Bson down-cast exception on the deserialiser. Callers
        // (ArchiveLifecycleService.ValidateRollupForActivationAsync) treat a null result as
        // "not a rollup", which is exactly the semantic we want when the RtId belongs to a
        // sibling subtype.
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, rollupRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        if (entity is not RtRollupArchive rollup)
        {
            return null;
        }

        return MapToSnapshot(rollup);
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> InsertAsync(
        string? rtWellKnownName,
        RtCkId<CkTypeId> targetCkTypeId,
        OctoObjectId sourceArchiveRtId,
        TimeSpan bucketSize,
        TimeSpan watermarkLag,
        IReadOnlyList<CkRollupAggregationSpec> aggregations,
        IReadOnlyList<CkArchiveColumnSpec> columns,
        BucketAlignment bucketAlignment = BucketAlignment.FixedSize,
        string? referenceTimeZone = null)
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

        var entity = new RtRollupArchive
        {
            RtWellKnownName = rtWellKnownName,
            TargetCkTypeId = targetCkTypeId.ToString(),
            Status = RtCkArchiveStatusEnum.Created,
            SourceArchiveRtId = sourceArchiveRtId.ToString(),
            // AB#4281: BucketSizeMs / WatermarkLagMs are Int64 (System.StreamData 1.6.3). Cast to long
            // — a calendar-month (2,419,200,000 ms) or calendar-year (31,536,000,000 ms) bucket width
            // exceeds Int32.MaxValue, so an (int) cast would silently truncate before the widening.
            BucketSizeMs = (long)bucketSize.TotalMilliseconds,
            WatermarkLagMs = (long)watermarkLag.TotalMilliseconds,
            // LastAggregatedBucketEnd starts null — set by ArchiveLifecycleService.ActivateAsync to
            // the activation timestamp (truncated to the bucket boundary). The orchestrator then
            // advances it forward, never backwards (except via rewindRollupWatermark). Concept §5.
            LastAggregatedBucketEnd = null,
            FrozenUntil = null,
            BucketAlignment = (RtBucketAlignmentEnum)(int)bucketAlignment,
            // AB#4300 / decision O6 — persist the optional IANA reference time-zone. Empty/whitespace
            // is normalised to null so the read path's IsNullOrWhiteSpace check stays symmetric.
            ReferenceTimeZone = string.IsNullOrWhiteSpace(referenceTimeZone) ? null : referenceTimeZone,
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
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return; // idempotent: already deleted (or never existed)
        }

        entity.RtState = RtState.Archived;
        entity.RtArchivedDateTime = DateTime.UtcNow;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async Task AdvanceWatermarkAsync(OctoObjectId rollupRtId, DateTime bucketEnd, bool allowRewind = false)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId)
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
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async Task SetFrozenUntilAsync(OctoObjectId rollupRtId, DateTime? frozenUntil)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtRollupArchive>(session, rollupRtId)
            ?? throw new ArchiveNotFoundException(rollupRtId);

        // Monotonicity for the set-forward path is enforced by the lifecycle service before it
        // calls in; clearing (frozenUntil == null) is the unfreeze path and is always allowed.
        entity.FrozenUntil = frozenUntil;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtRollupArchive>(session, rollupRtId, entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RollupArchiveSnapshot> EnumerateAsync()
    {
        var session = await _tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtRollupArchive>(session, queryOptions);

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
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtRollupArchive>(session, queryOptions);

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

    private static RollupArchiveSnapshot MapToSnapshot(RtRollupArchive entity)
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

        // BucketAlignment is an optional CK attribute, so the generated property is nullable.
        // Pre-1.4.0 entities never wrote the field; treat absence as FixedSize (the same default
        // every new 1.4.0 entity gets via the attribute's defaultValues).
        var bucketAlignment = entity.BucketAlignment is { } a
            ? (BucketAlignment)(int)a
            : BucketAlignment.FixedSize;

        return new RollupArchiveSnapshot(
            entity.RtId,
            targetCkTypeId,
            status,
            entity.RtWellKnownName,
            sourceArchiveRtId,
            TimeSpan.FromMilliseconds(entity.BucketSizeMs),
            TimeSpan.FromMilliseconds(entity.WatermarkLagMs),
            entity.LastAggregatedBucketEnd,
            aggregations,
            entity.FrozenUntil)
        {
            BucketAlignment = bucketAlignment,
            // Optional IANA reference time zone for DST-correct calendar buckets (AB#4290 / O6).
            // Null (unset, or pre-1.6.4 entities) ⇒ UTC calendar boundaries.
            ReferenceTimeZone = string.IsNullOrWhiteSpace(entity.ReferenceTimeZone) ? null : entity.ReferenceTimeZone,
            // Optional TWA carry-in lookback bound (AB#4336 / System.StreamData 1.6.5). Null
            // (unset, or pre-1.6.5 entities) ⇒ the SQL builder's 35-day engine default.
            CarryLookback = entity.CarryLookbackMs is { } carryMs ? TimeSpan.FromMilliseconds(carryMs) : null,
            // Recompute observability (AB#4184) — projected from the engine-maintained Archive-base
            // attributes so rollupsFor can surface recompute health. Counts fall back to 0 when the
            // RecordArray attribute was never written (steady state / pre-1.6.0 entities).
            RecomputeInProgress = entity.RecomputeInProgress,
            LastRecomputeStartedAt = entity.LastRecomputeStartedAt,
            LastRecomputeSuccessAt = entity.LastRecomputeSuccessAt,
            LastRecomputeFailureAt = entity.LastRecomputeFailureAt,
            LastRecomputeFailureReason = string.IsNullOrEmpty(entity.LastRecomputeFailureReason)
                ? null
                : entity.LastRecomputeFailureReason,
            DirtyWindowsPending = entity.DirtyWindows?.Count ?? 0,
            PendingRecomputeRanges = entity.PendingRecomputeRanges?.Count ?? 0,
        };
    }
}
