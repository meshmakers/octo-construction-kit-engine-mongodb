using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.ComputedColumns;

/// <summary>In-memory archive entity store, so the CrateDB side of the backfill job runs for real (AB#8002).</summary>
internal sealed class MemoryArchiveStore : IArchiveRuntimeStore
{
    private readonly Dictionary<OctoObjectId, ArchiveSnapshot> _archives = new();

    public void Put(ArchiveSnapshot snapshot) => _archives[snapshot.RtId] = snapshot;

    public CkArchiveColumnSpec? Column(OctoObjectId archiveRtId, string name) =>
        _archives[archiveRtId].Columns.FirstOrDefault(c => c.Name == name);

    public Task<ArchiveSnapshot?> GetAsync(OctoObjectId archiveRtId) =>
        Task.FromResult(_archives.TryGetValue(archiveRtId, out var a) ? a : null);

    public Task SetStatusAsync(OctoObjectId archiveRtId, CkArchiveStatus newStatus)
    {
        _archives[archiveRtId] = _archives[archiveRtId] with { Status = newStatus };
        return Task.CompletedTask;
    }

    public Task ArchiveEntityAsync(OctoObjectId archiveRtId)
    {
        _archives.Remove(archiveRtId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ArchiveSnapshot> EnumerateAsync()
    {
        foreach (var archive in _archives.Values.ToList())
        {
            yield return archive;
        }

        await Task.CompletedTask;
    }

    public Task AddComputedColumnAsync(OctoObjectId archiveRtId, CkArchiveColumnSpec column)
    {
        var a = _archives[archiveRtId];
        _archives[archiveRtId] = a with { Columns = a.Columns.Where(c => c.Name != column.Name).Append(column).ToList() };
        return Task.CompletedTask;
    }

    public Task SetComputedColumnStateAsync(OctoObjectId archiveRtId, string name, ComputedColumnState state) =>
        Change(archiveRtId, name, c => c with { ComputedState = state });

    public Task RemoveComputedColumnAsync(OctoObjectId archiveRtId, string name)
    {
        var a = _archives[archiveRtId];
        _archives[archiveRtId] = a with { Columns = a.Columns.Where(c => c.Name != name).ToList() };
        return Task.CompletedTask;
    }

    public Task SetPendingFormulaAsync(OctoObjectId archiveRtId, string name, string pendingFormula) =>
        Change(archiveRtId, name, c => c with { PendingFormula = pendingFormula });

    public Task SwapComputedColumnFormulaAsync(OctoObjectId archiveRtId, string name, string newFormula, int newVersion) =>
        Change(archiveRtId, name, c => c with { Formula = newFormula, ComputedVersion = newVersion, PendingFormula = null });

    public Task ClearPendingFormulaAsync(OctoObjectId archiveRtId, string name) =>
        Change(archiveRtId, name, c => c with { PendingFormula = null });

    private Task Change(OctoObjectId archiveRtId, string name, Func<CkArchiveColumnSpec, CkArchiveColumnSpec> change)
    {
        var a = _archives[archiveRtId];
        _archives[archiveRtId] = a with { Columns = a.Columns.Select(c => c.Name == name ? change(c) : c).ToList() };
        return Task.CompletedTask;
    }
}

/// <summary>In-memory recompute job store (AB#8002).</summary>
internal sealed class MemoryJobStore : IRecomputeJobStore
{
    private readonly Dictionary<OctoObjectId, RecomputeJobSnapshot> _jobs = new();
    private readonly Lock _lock = new();

    public Task<OctoObjectId> CreateAsync(RecomputeJobSnapshot job)
    {
        var id = OctoObjectId.GenerateNewId();
        lock (_lock)
        {
            _jobs[id] = job with { RtId = id };
        }

        return Task.FromResult(id);
    }

    public Task UpdateAsync(RecomputeJobSnapshot job)
    {
        lock (_lock)
        {
            _jobs[job.RtId] = job;
        }

        return Task.CompletedTask;
    }

    public Task<RecomputeJobSnapshot?> GetAsync(OctoObjectId jobRtId)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.TryGetValue(jobRtId, out var j) ? j : null);
        }
    }

    public Task<IReadOnlyList<RecomputeJobSnapshot>> GetForArchiveAsync(OctoObjectId archiveRtId, int limit)
    {
        lock (_lock)
        {
            IReadOnlyList<RecomputeJobSnapshot> list = _jobs.Values.Where(j => j.ArchiveRtId == archiveRtId)
                .OrderByDescending(j => j.RtId.ToString(), StringComparer.Ordinal).Take(limit).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<RecomputeJobSnapshot?> GetActiveForArchiveAsync(OctoObjectId archiveRtId)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.Values
                .Where(j => j.ArchiveRtId == archiveRtId && j.IsActive && !j.IsComputedColumnBackfill)
                .OrderByDescending(j => j.RtId.ToString(), StringComparer.Ordinal).FirstOrDefault());
        }
    }
}
