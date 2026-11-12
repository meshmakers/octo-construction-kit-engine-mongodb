using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Count parity under data permissions, part 2 (AB#8895): the count-only path counts exactly what the caller's
///     item query returns - all for the manager, the owned subset for an employee, nothing for an outsider.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class CountOnlyDataPermissionTests(DataPermissionTestFixture fixture)
{
    [Theory]
    [MemberData(nameof(CountParityDataPermissionTests.Callers), MemberType = typeof(CountParityDataPermissionTests))]
    public async Task CountOnly_CountsWhatTheCallerSees(string caller, long expected, string? nameFilter)
    {
        var repository = await CountParityDataPermissionTests.SeedAsync(fixture);
        fixture.Resolver.Table = CountParityDataPermissionTests.BuildTable();
        try
        {
            using var session = await CountParityDataPermissionTests.OpenSessionAsync(repository, caller);
            session.StartTransaction();
            var old = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                CountParityDataPermissionTests.Options(nameFilter));
            var count = await repository.CountRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                CountParityDataPermissionTests.Options(nameFilter));
            await session.CommitTransactionAsync();

            Assert.Equal(expected, count);
            Assert.Equal(old.TotalCount, count);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [MemberData(nameof(CountParityDataPermissionTests.Callers), MemberType = typeof(CountParityDataPermissionTests))]
    public async Task GraphCountOnly_CountsWhatTheCallerSees(string caller, long expected, string? nameFilter)
    {
        // The graph count (persistent query rows) applies the same data-permission filter as its item query.
        var repository = await CountParityDataPermissionTests.SeedAsync(fixture);
        fixture.Resolver.Table = CountParityDataPermissionTests.BuildTable();
        try
        {
            using var session = await CountParityDataPermissionTests.OpenSessionAsync(repository, caller);
            session.StartTransaction();
            var old = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                CountParityDataPermissionTests.Options(nameFilter), new List<NavigationPair>());
            var count = await repository.CountRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                CountParityDataPermissionTests.Options(nameFilter), new List<NavigationPair>());
            await session.CommitTransactionAsync();

            Assert.Equal(expected, count);
            Assert.Equal(old.TotalCount, count);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }
}
