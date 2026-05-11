using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed implementation of <see cref="ICkArchiveRuntimeStore"/>. Reads and writes
/// <c>CkArchive</c> entities through the tenant repository's generic Rt API. Concept §11 — paired
/// with the CrateDB <c>IStreamDataRepository</c> by <see cref="ArchiveLifecycleService"/>: Crate
/// updates always run before Mongo writes so a transient Mongo failure can be retried without
/// leaving partial state visible to callers.
/// </summary>
public sealed class MongoCkArchiveRuntimeStore : ICkArchiveRuntimeStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoCkArchiveRuntimeStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<CkArchiveSnapshot?> GetAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        return MapToSnapshot(entity);
    }

    private static CkArchiveSnapshot MapToSnapshot(RtCkArchive entity)
    {
        var status = (CkArchiveStatus)(int)entity.Status;
        var targetCkTypeId = entity.TargetCkTypeId is null
            ? new RtCkId<CkTypeId>(string.Empty)
            : new RtCkId<CkTypeId>(entity.TargetCkTypeId);

        var columns = (entity.Columns ?? Enumerable.Empty<RtCkArchiveColumnRecord>())
            .Where(c => c.Path is not null)
            .Select(c => new CkArchiveColumnSpec(c.Path!, c.Indexed, c.Required))
            .ToList();

        return new CkArchiveSnapshot(
            entity.RtId,
            targetCkTypeId,
            status,
            entity.RtWellKnownName,
            columns);
    }

    /// <inheritdoc />
    public async Task SetStatusAsync(OctoObjectId archiveRtId, CkArchiveStatus newStatus)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        entity.Status = (RtCkArchiveStatusEnum)(int)newStatus;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtCkArchive>(session, archiveRtId, entity);
    }

    /// <inheritdoc />
    public async Task ArchiveEntityAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtCkArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return; // idempotent: already deleted (or never existed)
        }

        entity.RtState = RtState.Archived;
        entity.RtArchivedDateTime = DateTime.UtcNow;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtCkArchive>(session, archiveRtId, entity);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CkArchiveSnapshot> EnumerateAsync()
    {
        var session = await _tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtCkArchive>(session, queryOptions);

        foreach (var entity in result.Items)
        {
            if (entity.RtState == RtState.Archived) continue;
            yield return MapToSnapshot(entity);
        }
    }
}
