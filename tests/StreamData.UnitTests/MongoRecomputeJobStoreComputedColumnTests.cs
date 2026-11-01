using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8002: computed-column backfill jobs live in the RecomputeJob history (so the recompute-jobs query
/// lists them), but the rollup orchestrator's active-job lookup must never return one — it would adopt,
/// coalesce into or fail the backfill.
/// </summary>
public class MongoRecomputeJobStoreComputedColumnTests
{
    private static readonly OctoObjectId Archive = new("aa00000000000000000004a0");
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static RtRecomputeJob Job(string id, RtCkRecomputeJobStateEnum state, string? staging) => new()
    {
        RtId = new OctoObjectId(id),
        ArchiveRtId = Archive.ToString(),
        State = state,
        Trigger = RtCkRecomputeTriggerEnum.Manual,
        RangeStart = DateTime.UnixEpoch,
        RangeEnd = Now,
        RtIdScope = string.Empty,
        ErrorReason = string.Empty,
        StagingTableName = staging ?? string.Empty,
    };

    private RtEntityQueryOptions? _lastOptions;

    private MongoRecomputeJobStore StoreOver(params RtRecomputeJob[] jobs)
    {
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(A.Fake<IOctoSession>());
        var result = A.Fake<IResultSet<RtRecomputeJob>>();
        A.CallTo(() => result.Items).Returns(jobs);
        A.CallTo(() => repository.GetRtEntitiesByTypeAsync<RtRecomputeJob>(A<IOctoSession>._, A<RtEntityQueryOptions>._))
            .Invokes(call => _lastOptions = call.GetArgument<RtEntityQueryOptions>(1))
            .Returns(result);
        return new MongoRecomputeJobStore(repository);
    }

    [Fact]
    public async Task ActiveLookup_SkipsTheNewerBackfillJob_AndReturnsTheRollupJob()
    {
        var store = StoreOver(
            Job("aa00000000000000000004b1", RtCkRecomputeJobStateEnum.Pending, null),
            Job("aa00000000000000000004b2", RtCkRecomputeJobStateEnum.Running, RecomputeJobSnapshot.ComputedColumnMarkerPrefix + "ExportPower"));

        var active = await store.GetActiveForArchiveAsync(Archive);

        Assert.NotNull(active);
        Assert.False(active!.IsComputedColumnBackfill);
        Assert.Equal(new OctoObjectId("aa00000000000000000004b1"), active.RtId);
    }

    [Fact]
    public async Task ActiveLookup_OnlyBackfillJobs_ReturnsNull_ButTheHistoryListsThem()
    {
        var store = StoreOver(
            Job("aa00000000000000000004b3", RtCkRecomputeJobStateEnum.Pending, RecomputeJobSnapshot.ComputedColumnMarkerPrefix + "ImportPower"));

        Assert.Null(await store.GetActiveForArchiveAsync(Archive));
        var history = await store.GetForArchiveAsync(Archive, 10);
        Assert.Equal("ImportPower", Assert.Single(history).ComputedColumnName);
    }

    [Fact]
    public async Task ArchiveLookup_FiltersByArchiveInTheQuery_NotAfterLoadingTheWholeHistory()
    {
        await StoreOver().GetForArchiveAsync(Archive, 10);

        var filter = Assert.Single(_lastOptions!.FieldFilters!);
        Assert.Equal(nameof(RtRecomputeJob.ArchiveRtId), filter.AttributePath);
        Assert.Equal(FieldFilterOperator.Equals, filter.Operator);
        Assert.Equal(Archive.ToString(), filter.ComparisonValue);
    }
}
