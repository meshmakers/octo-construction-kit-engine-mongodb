using System.Collections.Concurrent;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     AB#8897 against real MongoDB: the configured time limit of runtime queries reaches every MongoDB operation of the
///     type, id and navigation queries and their counts, a call-site override replaces it, a host without the opt-in
///     runs without a limit, and MongoDB really stops an operation that exceeds the limit (coded time-out).
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class RuntimeQueryTimeLimitIntegrationTests(SampleRtModelDataFixture fixture)
{
    private static readonly TimeSpan Configured = TimeSpan.FromSeconds(120);

    private (TenantRepository Repository, ConcurrentQueue<(string Operation, TimeSpan? MaxTime)> Observed)
        CreateRepository(TimeSpan? configured, bool optedIn = true)
    {
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        var observed = new ConcurrentQueue<(string, TimeSpan?)>();
        repository.QueryTimeLimitSettings = optedIn ? new RuntimeQueryTimeLimitSettings(configured) : null;
        repository.RuntimeQueryMaxTimeObserver = (operation, maxTime) => observed.Enqueue((operation, maxTime));
        return (repository, observed);
    }

    [Fact]
    public async Task TheConfiguredLimit_ReachesEveryOperationOfTypeIdAndNavigationQueriesAndCounts()
    {
        var (repository, observed) = CreateRepository(Configured);
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var navigation = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            fixture.GetSystemContext().TenantId, TestCkIds.CkDistrictTypeId, ["name", "parent.testStateOrProvince->name"]);

        using var session = await repository.GetSessionAsync();
        var all = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());
        await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(), 0, 2, true);
        await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());
        await repository.GetRtEntitiesByIdAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            all.Items.Take(2).Select(e => e.RtId).ToList(), RtEntityQueryOptions.Create());
        await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create().WithCachingDisabled(), navigation, 0, 2, true);
        await repository.CountRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            RtEntityQueryOptions.Create(), navigation);

        var operations = observed.ToList();
        Assert.Contains(operations, o => o.Operation == "items");
        Assert.Contains(operations, o => o.Operation == "page");
        Assert.Contains(operations, o => o.Operation == "count");
        Assert.All(operations, o => Assert.Equal(Configured, o.MaxTime));
    }

    [Fact]
    public async Task AnOverrideAtTheCallSite_ReachesTheOperationsInsteadOfTheDefault()
    {
        var (repository, observed) = CreateRepository(Configured);

        using var session = await repository.GetSessionAsync();
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(30)))
        {
            await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create(), 0, 2, true);
        }

        var overridden = observed.ToList();
        observed.Clear();

        using (RuntimeQueryTimeLimit.Disable())
        {
            await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
                RtEntityQueryOptions.Create());
        }

        var disabled = observed.ToList();
        observed.Clear();

        await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());

        Assert.NotEmpty(overridden);
        Assert.All(overridden, o => Assert.Equal(TimeSpan.FromMinutes(30), o.MaxTime));
        Assert.Equal([("count", (TimeSpan?)null)], disabled);
        Assert.Equal([("count", (TimeSpan?)Configured)], observed.ToList());
    }

    [Fact]
    public async Task AHostWithoutTheOptIn_RunsWithoutALimit()
    {
        var (repository, observed) = CreateRepository(null, optedIn: false);

        using var session = await repository.GetSessionAsync();
        var count = await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create());

        Assert.True(count > 0);
        Assert.Empty(observed);
        Assert.Null(repository.CreateRuntimeQueryTimeLimiter("Test/StateOrProvince-1"));
    }

    [Fact]
    public async Task TheLimit_ReachesMongoDb_AsMaxTimeMS_OfEveryAggregateOfTheQuery()
    {
        // Read back from MongoDB's profiler what the server received: the observer alone would also pass if the
        // limit never reached the driver options.
        var (repository, _) = CreateRepository(Configured);
        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session,
            TestCkIds.RtCkStateOrProvinceTypeId, RtEntityQueryOptions.Create());
        var collection = query.GetMongoCollection();

        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var databaseName = collection.Database.DatabaseNamespace.DatabaseName;

        async Task<List<BsonDocument>> ProfiledAggregatesAsync(Func<Task> run) =>
            (await MongoProfiler.ProfileAggregatesAsync(config, databaseName, run))
            .Where(op => op["ns"].AsString == collection.CollectionNamespace.FullName)
            .ToList();

        Task PagedQueryWithCount() => repository.GetRtEntitiesByTypeAsync(session,
            TestCkIds.RtCkStateOrProvinceTypeId, RtEntityQueryOptions.Create(), 0, 2, true);

        var configured = await ProfiledAggregatesAsync(PagedQueryWithCount);
        List<BsonDocument> overridden;
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(30)))
        {
            overridden = await ProfiledAggregatesAsync(PagedQueryWithCount);
        }

        List<BsonDocument> disabled;
        using (RuntimeQueryTimeLimit.Disable())
        {
            disabled = await ProfiledAggregatesAsync(PagedQueryWithCount);
        }

        // Page and count: two aggregates per run, each with the limit that applied.
        Assert.Equal(2, configured.Count);
        Assert.All(configured, op => Assert.Equal(120_000, op["command"]["maxTimeMS"].ToInt64()));
        Assert.Equal(2, overridden.Count);
        Assert.All(overridden, op => Assert.Equal(1_800_000, op["command"]["maxTimeMS"].ToInt64()));
        Assert.Equal(2, disabled.Count);
        Assert.All(disabled, op => Assert.False(op["command"].AsBsonDocument.Contains("maxTimeMS")));
    }

    [Fact]
    public async Task AnOperationAboveTheLimit_IsStoppedByMongoDb_AndEndsWithTheCodedTimeout()
    {
        var (repository, _) = CreateRepository(TimeSpan.FromMilliseconds(50));
        var ckTypeId = TestCkIds.RtCkStateOrProvinceTypeId.ToString();
        var limiter = repository.CreateRuntimeQueryTimeLimiter(ckTypeId)!;
        var collection = repository.DataSource.GetRtDatabaseCollection<RtEntity>(
            await repository.GetCkTypeGraphAsync(TestCkIds.RtCkStateOrProvinceTypeId));
        // One document fanned out into 2,000,000 documents and counted: the server works far longer than the 50 ms
        // limit, without server-side JavaScript (an interrupted script ends with another error than the time limit).
        const int outer = 2_000;
        const int inner = 1_000;
        const int fanOut = outer * inner;
        var pipeline = PipelineDefinition<RtEntity, BsonDocument>.Create(
            new BsonDocument("$limit", 1),
            new BsonDocument("$project", new BsonDocument("a", new BsonDocument("$range", new BsonArray { 0, outer }))),
            new BsonDocument("$unwind", "$a"),
            new BsonDocument("$project", new BsonDocument("b", new BsonDocument("$range", new BsonArray { 0, inner }))),
            new BsonDocument("$unwind", "$b"),
            new BsonDocument("$count", "rows"));

        using var session = await repository.GetSessionAsync();
        var exception = await Assert.ThrowsAsync<RuntimeQueryTimeoutException>(() =>
            limiter.RunAsync("page", maxTime => collection
                .Aggregate(session, pipeline, RuntimeQueryTimeLimiter.CreateAggregateOptions(maxTime)).ToListAsync()));

        Assert.Equal(ckTypeId, exception.CkTypeId);
        Assert.Equal(fixture.GetSystemContext().TenantId, exception.TenantId);
        Assert.Equal(TimeSpan.FromMilliseconds(50), exception.MaxTime);
        Assert.IsType<MongoExecutionTimeoutException>(exception.InnerException);
        Assert.Contains(ckTypeId, exception.Message);

        // The same operation under an explicit override at the call site is not cut.
        using (RuntimeQueryTimeLimit.Disable())
        {
            var rows = await limiter.RunAsync("page", maxTime => collection
                .Aggregate(session, pipeline, RuntimeQueryTimeLimiter.CreateAggregateOptions(maxTime)).ToListAsync());
            Assert.Equal(fanOut, Assert.Single(rows)["rows"].ToInt32());
        }
    }
}
