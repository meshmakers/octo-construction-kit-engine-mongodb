using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
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
}
