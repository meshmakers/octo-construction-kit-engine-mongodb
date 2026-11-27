using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#9824: the archive and rollup archive enumerations and the recompute-job reads of an archive are engine-internal
/// whole-type reads (series resolution of a downsampling <c>GetQueryById@1</c> reads the rollup archives inside the
/// node, the computed-column backfill drain reads the recompute jobs every tick). They run inside
/// <see cref="RuntimeReadScope.Suppress" />, so the runtime read guard neither reports them as the node's unbounded
/// read nor lets them use up the node's one report; the node's scope is back once the read returned.
/// </summary>
public class ArchiveStoreRuntimeReadSuppressionTests
{
    private const string NodeType = "GetQueryById@1";

    [Fact]
    public async Task RollupEnumeration_InsideANode_ReadsSuppressed_AndRestoresTheNodeScope()
    {
        var (repository, seen) = RepositoryRecordingSuppression<RtRollupArchive>();
        var store = new MongoRollupArchiveRuntimeStore(repository);

        using (OpenNode())
        {
            await foreach (var _ in store.EnumerateAsync())
            {
            }

            AssertNodeScopeIsBack();
        }

        Assert.Equal([true], seen);
    }

    [Fact]
    public async Task CountActiveRollups_InsideANode_ReadsSuppressed_AndRestoresTheNodeScope()
    {
        var (repository, seen) = RepositoryRecordingSuppression<RtRollupArchive>();
        var store = new MongoRollupArchiveRuntimeStore(repository);

        using (OpenNode())
        {
            await store.CountActiveRollupsForSourceAsync(new OctoObjectId("aa00000000000000000002a0"));
            AssertNodeScopeIsBack();
        }

        Assert.Equal([true], seen);
    }

    [Fact]
    public async Task ArchiveEnumeration_InsideANode_ReadsSuppressed_AndRestoresTheNodeScope()
    {
        var (repository, seen) = RepositoryRecordingSuppression<RtArchive>();
        var store = new MongoArchiveRuntimeStore(repository);

        using (OpenNode())
        {
            await foreach (var _ in store.EnumerateAsync())
            {
            }

            AssertNodeScopeIsBack();
        }

        Assert.Equal([true], seen);
    }

    [Fact]
    public async Task RecomputeJobsOfAnArchive_InsideANode_ReadsSuppressed_AndRestoresTheNodeScope()
    {
        var (repository, seen) = RepositoryRecordingSuppression<RtRecomputeJob>();
        var store = new MongoRecomputeJobStore(repository);

        using (OpenNode())
        {
            await store.GetActiveForArchiveAsync(new OctoObjectId("aa00000000000000000002a0"));
            AssertNodeScopeIsBack();
        }

        Assert.Equal([true], seen);
    }

    private static IDisposable OpenNode()
    {
        var execution = RuntimeReadScope.BeginExecution(new RuntimeReadExecution("tenant-a", "pipeline-1", null,
            "flow-1", Guid.NewGuid().ToString()));
        var node = RuntimeReadScope.BeginNode(NodeType, "PipelineExecution/" + NodeType);
        return new Both(node, execution);
    }

    private static void AssertNodeScopeIsBack()
    {
        var current = RuntimeReadScope.Current;
        Assert.NotNull(current);
        Assert.Equal(NodeType, current.NodeType);
        Assert.False(current.Suppressed);
    }

    private static (ITenantRepository Repository, List<bool> Seen) RepositoryRecordingSuppression<TEntity>()
        where TEntity : RtEntity, new()
    {
        var seen = new List<bool>();
        var result = A.Fake<IResultSet<TEntity>>();
        A.CallTo(() => result.Items).Returns([]);
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(A.Fake<IOctoSession>());
        A.CallTo(() => repository.GetRtEntitiesByTypeAsync<TEntity>(A<IOctoSession>._, A<RtEntityQueryOptions>._,
                A<int?>._, A<int?>._))
            .ReturnsLazily(() =>
            {
                seen.Add(RuntimeReadScope.Current?.Suppressed ?? false);
                return Task.FromResult(result);
            });
        return (repository, seen);
    }

    private sealed class Both(IDisposable inner, IDisposable outer) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            outer.Dispose();
        }
    }
}
