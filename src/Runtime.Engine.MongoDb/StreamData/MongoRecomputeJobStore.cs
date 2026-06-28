using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// MongoDB-backed <see cref="IRecomputeJobStore"/> (AB#4184). Persists <c>RecomputeJob</c> entities —
/// the debuggable per-run recompute history — through the tenant repository's generic Rt API. Jobs
/// are ordered newest-first by their time-encoded <see cref="OctoObjectId"/>, so no extra sort
/// attribute is needed.
/// </summary>
public sealed class MongoRecomputeJobStore : IRecomputeJobStore
{
    private readonly ITenantRepository _tenantRepository;

    /// <summary>Constructs the store for a given tenant repository.</summary>
    public MongoRecomputeJobStore(ITenantRepository tenantRepository)
    {
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> CreateAsync(RecomputeJobSnapshot job)
    {
        var entity = new RtRecomputeJob();
        ApplyTo(entity, job);

        var session = await _tenantRepository.GetSessionAsync();
        await _tenantRepository.InsertOneRtEntityAsync(session, entity);
        return entity.RtId;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(RecomputeJobSnapshot job)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtRecomputeJob>(session, job.RtId)
            ?? throw new ArchiveNotFoundException(job.RtId);

        ApplyTo(entity, job);
        await _tenantRepository.UpdateOneRtEntityByIdAsync<RtRecomputeJob>(session, job.RtId, entity);
    }

    /// <inheritdoc />
    public async Task<RecomputeJobSnapshot?> GetAsync(OctoObjectId jobRtId)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var entity = await _tenantRepository.GetRtEntityByRtIdAsync<RtRecomputeJob>(session, jobRtId);
        if (entity is null || entity.RtState == RtState.Archived)
        {
            return null;
        }

        return ToSnapshot(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecomputeJobSnapshot>> GetForArchiveAsync(OctoObjectId archiveRtId, int limit)
    {
        var archiveId = archiveRtId.ToString();
        var jobs = await LoadActiveJobsForArchiveAsync(archiveId, _ => true);
        return jobs
            .OrderByDescending(e => e.RtId)
            .Take(limit)
            .Select(ToSnapshot)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RecomputeJobSnapshot?> GetActiveForArchiveAsync(OctoObjectId archiveRtId)
    {
        var archiveId = archiveRtId.ToString();
        var jobs = await LoadActiveJobsForArchiveAsync(archiveId, IsActive);
        var active = jobs.OrderByDescending(e => e.RtId).FirstOrDefault();
        return active is null ? null : ToSnapshot(active);
    }

    private async Task<List<RtRecomputeJob>> LoadActiveJobsForArchiveAsync(
        string archiveId, Func<RtCkRecomputeJobStateEnum, bool> statePredicate)
    {
        var session = await _tenantRepository.GetSessionAsync();
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtRecomputeJob>(session, RtEntityQueryOptions.Create());

        return result.Items
            .Where(e => e.RtState != RtState.Archived
                        && string.Equals(e.ArchiveRtId, archiveId, StringComparison.Ordinal)
                        && statePredicate(e.State))
            .ToList();
    }

    private static bool IsActive(RtCkRecomputeJobStateEnum state) =>
        state is RtCkRecomputeJobStateEnum.Pending
            or RtCkRecomputeJobStateEnum.Running
            or RtCkRecomputeJobStateEnum.Swapping;

    private static void ApplyTo(RtRecomputeJob entity, RecomputeJobSnapshot job)
    {
        entity.ArchiveRtId = job.ArchiveRtId.ToString();
        entity.State = (RtCkRecomputeJobStateEnum)(int)job.State;
        entity.Trigger = (RtCkRecomputeTriggerEnum)(int)job.Trigger;
        entity.RangeStart = job.RangeStart;
        entity.RangeEnd = job.RangeEnd;
        entity.RtIdScope = job.RtIdScope?.ToString() ?? string.Empty;
        entity.RowsProcessed = job.RowsProcessed;
        entity.WindowsProcessed = job.WindowsProcessed;
        entity.StartedAt = job.StartedAt;
        entity.FinishedAt = job.FinishedAt;
        entity.DurationMs = job.DurationMs;
        entity.ErrorReason = job.ErrorReason ?? string.Empty;
        entity.StagingTableName = job.StagingTableName ?? string.Empty;
    }

    private static RecomputeJobSnapshot ToSnapshot(RtRecomputeJob entity) => new(
        entity.RtId,
        string.IsNullOrEmpty(entity.ArchiveRtId) ? default : new OctoObjectId(entity.ArchiveRtId),
        (RecomputeJobState)(int)entity.State,
        (RecomputeTrigger)(int)entity.Trigger,
        entity.RangeStart,
        entity.RangeEnd,
        string.IsNullOrEmpty(entity.RtIdScope) ? null : new OctoObjectId(entity.RtIdScope),
        // CK Int attributes generate as long?; these observability counters comfortably fit int.
        (int?)entity.RowsProcessed,
        (int?)entity.WindowsProcessed,
        entity.StartedAt,
        entity.FinishedAt,
        (int?)entity.DurationMs,
        string.IsNullOrEmpty(entity.ErrorReason) ? null : entity.ErrorReason,
        string.IsNullOrEmpty(entity.StagingTableName) ? null : entity.StagingTableName);
}
