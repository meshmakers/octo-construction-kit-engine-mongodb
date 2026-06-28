using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed <see cref="IArchiveRecomputeStateStore"/> (AB#4184). The recompute state lives on
/// the archive entity itself as runtime-state attributes, so each operation loads the
/// <see cref="RtArchive"/> (the unified Archive collection resolves the concrete subtype), mutates
/// the relevant attribute, and persists via the entity's concrete CkTypeId — the same idiom the
/// status / watermark / freeze writes use.
/// </summary>
public sealed class MongoArchiveRecomputeStateStore : IArchiveRecomputeStateStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoArchiveRecomputeStateStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public Task AppendDirtyWindowAsync(OctoObjectId archiveRtId, ArchiveDirtyWindow window) =>
        MutateAsync(archiveRtId, entity =>
        {
            var list = entity.DirtyWindows ?? new AttributeRecordValueList<RtCkArchiveDirtyWindowRecord>();
            list.Add(ToRecord(window));
            entity.DirtyWindows = list;
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveDirtyWindow>> GetDirtyWindowsAsync(OctoObjectId archiveRtId) =>
        ReadAsync(archiveRtId, entity =>
            (IReadOnlyList<ArchiveDirtyWindow>)(entity.DirtyWindows ?? Enumerable.Empty<RtCkArchiveDirtyWindowRecord>())
                .Select(FromRecord)
                .ToList());

    /// <inheritdoc />
    public Task ClearDirtyWindowsAsync(OctoObjectId archiveRtId) =>
        MutateAsync(archiveRtId, entity =>
            entity.DirtyWindows = new AttributeRecordValueList<RtCkArchiveDirtyWindowRecord>());

    /// <inheritdoc />
    public Task EnqueueRecomputeRangesAsync(OctoObjectId archiveRtId, IReadOnlyList<ArchiveRecomputeRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return Task.CompletedTask;
        }

        return MutateAsync(archiveRtId, entity =>
        {
            var list = entity.PendingRecomputeRanges ?? new AttributeRecordValueList<RtCkArchiveRecomputeRangeRecord>();
            list.AddRange(ranges.Select(ToRecord));
            entity.PendingRecomputeRanges = list;
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveRecomputeRange>> GetPendingRecomputeRangesAsync(OctoObjectId archiveRtId) =>
        ReadAsync(archiveRtId, entity =>
            (IReadOnlyList<ArchiveRecomputeRange>)(entity.PendingRecomputeRanges ?? Enumerable.Empty<RtCkArchiveRecomputeRangeRecord>())
                .Select(FromRecord)
                .ToList());

    /// <inheritdoc />
    public Task ClearPendingRecomputeRangesAsync(OctoObjectId archiveRtId) =>
        MutateAsync(archiveRtId, entity =>
            entity.PendingRecomputeRanges = new AttributeRecordValueList<RtCkArchiveRecomputeRangeRecord>());

    /// <inheritdoc />
    public Task MarkRecomputeStartedAsync(OctoObjectId archiveRtId, DateTime startedAt) =>
        MutateAsync(archiveRtId, entity =>
        {
            entity.RecomputeInProgress = true;
            entity.LastRecomputeStartedAt = startedAt;
        });

    /// <inheritdoc />
    public Task MarkRecomputeSucceededAsync(OctoObjectId archiveRtId, DateTime succeededAt) =>
        MutateAsync(archiveRtId, entity =>
        {
            entity.RecomputeInProgress = false;
            entity.LastRecomputeSuccessAt = succeededAt;
        });

    /// <inheritdoc />
    public Task MarkRecomputeFailedAsync(OctoObjectId archiveRtId, DateTime failedAt, string reason) =>
        MutateAsync(archiveRtId, entity =>
        {
            entity.RecomputeInProgress = false;
            entity.LastRecomputeFailureAt = failedAt;
            entity.LastRecomputeFailureReason = reason;
        });

    private async Task MutateAsync(OctoObjectId archiveRtId, Action<RtArchive> mutate)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        mutate(entity);

        // Persist via the concrete CkTypeId carried on the entity (Raw / TimeRange / Rollup); the
        // abstract <RtArchive> generic would hand the rule engine the abstract base type.
        await _tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.CkTypeId!, archiveRtId, entity);
    }

    private async Task<T> ReadAsync<T>(OctoObjectId archiveRtId, Func<RtArchive, T> read)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtArchive>(session, archiveRtId)
            ?? throw new ArchiveNotFoundException(archiveRtId);

        return read(entity);
    }

    private static RtCkArchiveDirtyWindowRecord ToRecord(ArchiveDirtyWindow window) => new()
    {
        WindowStart = window.WindowStart,
        WindowEnd = window.WindowEnd,
        ChangeKind = (RtCkRecomputeChangeKindEnum)(int)window.ChangeKind,
        Source = (RtCkRecomputeChangeSourceEnum)(int)window.Source,
        DetectedAt = window.DetectedAt,
    };

    private static ArchiveDirtyWindow FromRecord(RtCkArchiveDirtyWindowRecord record) => new(
        record.WindowStart,
        record.WindowEnd,
        (RecomputeChangeKind)(int)record.ChangeKind,
        (RecomputeChangeSource)(int)record.Source,
        record.DetectedAt);

    private static RtCkArchiveRecomputeRangeRecord ToRecord(ArchiveRecomputeRange range) => new()
    {
        DependentArchiveRtId = range.DependentArchiveRtId.ToString(),
        RangeStart = range.RangeStart,
        RangeEnd = range.RangeEnd,
        RtIdScope = range.RtIdScope?.ToString() ?? string.Empty,
        EnqueuedAt = range.EnqueuedAt,
    };

    private static ArchiveRecomputeRange FromRecord(RtCkArchiveRecomputeRangeRecord record) => new(
        string.IsNullOrEmpty(record.DependentArchiveRtId) ? default : new OctoObjectId(record.DependentArchiveRtId),
        record.RangeStart,
        record.RangeEnd,
        string.IsNullOrEmpty(record.RtIdScope) ? null : new OctoObjectId(record.RtIdScope),
        record.EnqueuedAt);
}
