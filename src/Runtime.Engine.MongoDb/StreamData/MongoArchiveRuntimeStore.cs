using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
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

    private static ArchiveSnapshot MapToSnapshot(RtArchive entity)
    {
        var status = (CkArchiveStatus)(int)entity.Status;
        var targetCkTypeId = entity.TargetCkTypeId is null
            ? new RtCkId<CkTypeId>(string.Empty)
            : new RtCkId<CkTypeId>(entity.TargetCkTypeId);

        // Rollup-archives concept §4: when the entity is a CkRollupArchive (Mongo polymorphism
        // surfaces it as a RtRollupArchive subclass instance), derive the columns from the
        // user-defined aggregations rather than the unused inherited Columns slot. The aggregations
        // themselves are also carried on the snapshot so the DDL path can derive the SQL column
        // type from each function (the derived column names are storage identifiers, not paths into
        // the CK type, so ArchivePathTypeResolver cannot resolve them).
        IReadOnlyList<CkRollupAggregationSpec>? rollupAggregations = null;
        IReadOnlyList<CkArchiveColumnSpec> columns;
        if (entity is RtRollupArchive rollup)
        {
            rollupAggregations = (rollup.Aggregations ?? Enumerable.Empty<RtCkRollupAggregationRecord>())
                .Where(a => a.SourcePath is not null)
                .Select(a => new CkRollupAggregationSpec(
                    a.SourcePath!,
                    (CkRollupFunction)(int)a.Function,
                    a.TargetColumnName))
                .ToList();
            columns = RollupColumnGenerator.Generate(rollupAggregations);
        }
        else
        {
            columns = (entity.Columns ?? Enumerable.Empty<RtCkArchiveColumnRecord>())
                .Where(c => c.Path is not null)
                .Select(c => new CkArchiveColumnSpec(c.Path!, c.Indexed, c.Required))
                .ToList();
        }

        return new ArchiveSnapshot(
            entity.RtId,
            targetCkTypeId,
            status,
            entity.RtWellKnownName,
            columns)
        {
            RollupAggregations = rollupAggregations,
        };
    }

    /// <inheritdoc />
    public async Task SetStatusAsync(OctoObjectId archiveRtId, CkArchiveStatus newStatus)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        entity.Status = (RtCkArchiveStatusEnum)(int)newStatus;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtArchive>(session, archiveRtId, entity);
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
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtArchive>(session, archiveRtId, entity);
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
