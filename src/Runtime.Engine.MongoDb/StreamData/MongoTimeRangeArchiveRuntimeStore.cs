using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed implementation of <see cref="ITimeRangeArchiveRuntimeStore"/>. Reads
/// <c>TimeRangeArchive</c> entities via the tenant repository's generic Rt API and projects
/// them through <see cref="MongoArchiveRuntimeStore"/>'s shared mapping so the resulting
/// <see cref="ArchiveSnapshot"/> carries <c>IsTimeRange = true</c> and the advisory <c>Period</c>.
/// </summary>
/// <remarks>
/// Concept doc: <c>docs/concept-time-range-archives.md</c> §3. No insert path on this store —
/// time-range data inserts go through <see cref="IStreamDataRepository.InsertTimeRangeAsync"/>
/// directly to CrateDB. This store handles the metadata side: load the snapshot for the
/// activation DDL, the soft-delete entry point, and (later) any time-range-specific lifecycle.
/// </remarks>
public sealed class MongoTimeRangeArchiveRuntimeStore : ITimeRangeArchiveRuntimeStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoTimeRangeArchiveRuntimeStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<ArchiveSnapshot?> GetAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        // Load through the concrete subtype: Mongo polymorphism returns a RtTimeRangeArchive if
        // the document's ckTypeId is System.StreamData/TimeRangeArchive. Asking for the abstract
        // base would also work, but typing the request narrows the result so a caller that ends
        // up with a different subtype (rollup, raw) sees null.
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtTimeRangeArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        // Delegate the mapping to the shared archive store: it already detects subtypes and emits
        // the right snapshot shape (IsTimeRange = true + Period set when the underlying entity is
        // a RtTimeRangeArchive).
        return MongoArchiveRuntimeStore.MapToSnapshotPublic(entity);
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> InsertAsync(
        string? rtWellKnownName,
        RtCkId<CkTypeId> targetCkTypeId,
        IReadOnlyList<CkArchiveColumnSpec> columns,
        TimeSpan? period)
    {
        var columnList = new AttributeRecordValueList<RtCkArchiveColumnRecord>();
        columnList.AddRange(columns.Select(c => new RtCkArchiveColumnRecord
        {
            Path = c.Path,
            Indexed = c.Indexed,
            Required = c.Required,
        }));

        var entity = new RtTimeRangeArchive
        {
            RtWellKnownName = rtWellKnownName,
            TargetCkTypeId = targetCkTypeId.ToString(),
            Status = RtCkArchiveStatusEnum.Created,
            Columns = columnList,
            Period = period,
        };

        var session = await _tenantRepository.GetSessionAsync();
        await _tenantRepository.InsertOneRtEntityAsync(session, entity);
        return entity.RtId;
    }

    /// <inheritdoc />
    public async Task ArchiveEntityAsync(OctoObjectId archiveRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtTimeRangeArchive>(session, archiveRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return; // idempotent
        }

        entity.RtState = RtState.Archived;
        entity.RtArchivedDateTime = DateTime.UtcNow;
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtTimeRangeArchive>(session, archiveRtId, entity);
    }
}
