using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     A rollup store that keeps its snapshots in memory and applies the watermark writes the repair makes, so a second
///     validation sees the rewound watermark. Only what the validator and the repair use is implemented.
/// </summary>
internal sealed class InMemoryRollupStore(params RollupArchiveSnapshot[] snapshots) : IRollupArchiveRuntimeStore
{
    private readonly Dictionary<OctoObjectId, RollupArchiveSnapshot> _snapshots = snapshots.ToDictionary(s => s.RtId);

    /// <summary>Every watermark write: rollup, new watermark, whether a rewind was allowed.</summary>
    public List<(OctoObjectId Rollup, DateTime BucketEnd, bool AllowRewind)> WatermarkWrites { get; } = [];

    public Task<RollupArchiveSnapshot?> GetAsync(OctoObjectId rollupRtId)
        => Task.FromResult(_snapshots.GetValueOrDefault(rollupRtId));

    public Task AdvanceWatermarkAsync(OctoObjectId rollupRtId, DateTime bucketEnd, bool allowRewind = false)
    {
        WatermarkWrites.Add((rollupRtId, bucketEnd, allowRewind));
        _snapshots[rollupRtId] = _snapshots[rollupRtId] with { LastAggregatedBucketEnd = bucketEnd };
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RollupArchiveSnapshot> EnumerateAsync()
    {
        await Task.CompletedTask;
        foreach (var snapshot in _snapshots.Values.ToList())
        {
            yield return snapshot;
        }
    }

    public Task<OctoObjectId> InsertAsync(
        string? rtWellKnownName, RtCkId<CkTypeId> targetCkTypeId, IReadOnlyList<RollupSourceReference> sources, TimeSpan bucketSize,
        TimeSpan watermarkLag, IReadOnlyList<CkRollupAggregationSpec> aggregations, IReadOnlyList<CkArchiveColumnSpec> columns,
        BucketAlignment bucketAlignment = BucketAlignment.FixedSize, string? referenceTimeZone = null, TimeSpan? carryLookback = null)
        => throw new NotSupportedException();

    public Task ArchiveEntityAsync(OctoObjectId rollupRtId) => throw new NotSupportedException();

    public Task SetFrozenUntilAsync(OctoObjectId rollupRtId, DateTime? frozenUntil) => throw new NotSupportedException();

    public Task<bool> TryPersistDerivedColumnsAsync(OctoObjectId rollupRtId) => throw new NotSupportedException();

    public Task<int> CountActiveRollupsForSourceAsync(OctoObjectId sourceArchiveRtId) => throw new NotSupportedException();
}
