using System.Collections.Concurrent;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>
///     AB#8897: the counts and fetches of an interface query (one per implementing collection) carry the configured
///     time limit, or the override of the call site.
/// </summary>
[Collection(InterfaceQueryCollection.Name)]
public class InterfaceQueryTimeLimitTests(InterfaceQueryFixture fixture)
{
    private static readonly RtCkId<CkInterfaceId> Named = new("InterfaceQuery/Named");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryCountAndFetch_CarriesTheLimit(bool inTransaction)
    {
        await fixture.ResetAsync();
        var repository = (TenantRepository)fixture.GetSystemContext().GetTenantRepository();
        await InterfaceQuerySeed.SeedStandardAsync(repository);
        var observed = new ConcurrentQueue<(string Operation, TimeSpan? MaxTime)>();
        repository.QueryTimeLimitSettings = new RuntimeQueryTimeLimitSettings(TimeSpan.FromSeconds(120));
        repository.RuntimeQueryMaxTimeObserver = (operation, maxTime) => observed.Enqueue((operation, maxTime));

        using var session = await repository.GetSessionAsync();
        if (inTransaction)
        {
            session.StartTransaction();
        }

        var result = await repository.GetRtEntitiesByInterfaceAsync(session, Named,
            RtEntityQueryOptions.Create().SortOrder("Name", SortOrders.Ascending), 0, 3, true);
        using (RuntimeQueryTimeLimit.Override(TimeSpan.FromMinutes(20)))
        {
            await repository.GetRtEntitiesByInterfaceAsync(session, Named, RtEntityQueryOptions.Create(), 0, 3, true);
        }

        if (inTransaction)
        {
            await session.CommitTransactionAsync();
        }

        Assert.Equal(3, result.Items.Count);
        var operations = observed.ToList();
        var configured = operations.Where(o => o.MaxTime == TimeSpan.FromSeconds(120)).ToList();
        var overridden = operations.Where(o => o.MaxTime == TimeSpan.FromMinutes(20)).ToList();
        Assert.Equal(operations.Count, configured.Count + overridden.Count);
        Assert.Contains(configured, o => o.Operation == "count");
        Assert.Contains(configured, o => o.Operation == "page");
        Assert.Contains(overridden, o => o.Operation == "count");
        Assert.Contains(overridden, o => o.Operation == "page");
    }
}
