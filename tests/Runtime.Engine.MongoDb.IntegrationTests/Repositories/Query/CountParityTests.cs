using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Count parity, part 1 (AB#8895): proves the count of the existing item query per query shape of
///     <see cref="CountParityCases" /> - the unpaged query counts exactly the entities it returns and the paged
///     query ($facet) reports the same total. Written and green before the count-only path existed; it stays
///     unchanged, so the count-only path is measured against the old behaviour.
/// </summary>
[Collection(SampleRtModelDataCollection.Name)]
public class CountParityTests(SampleRtModelDataFixture fixture)
{
    [Theory]
    [MemberData(nameof(CountParityCases.Names), MemberType = typeof(CountParityCases))]
    public async Task OldItemQuery_UnpagedAndPagedCountAgree(string caseName)
    {
        var c = CountParityCases.Get(caseName);
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var unpaged = await CountParityCases.OldTotalCountAsync(repository, session, fixture, c);
        var paged = await CountParityCases.OldTotalCountAsync(repository, session, fixture, c, 0, 1);

        await session.CommitTransactionAsync();

        Assert.True(unpaged >= c.MinExpected, $"{caseName}: {unpaged} < {c.MinExpected}");
        Assert.Equal(unpaged, paged);
    }

    [Fact]
    public async Task OldMultiOriginQuery_UnpagedAndPagedCountAgreePerOrigin()
    {
        var repository = fixture.GetSystemContext().GetTenantRepository();

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();

        var districtRtIds = await CountParityCases.DistrictRtIdsAsync(repository, session);
        var unpaged = await CountParityCases.MunicipalitiesOfAsync(repository, session, districtRtIds);
        var paged = await CountParityCases.MunicipalitiesOfAsync(repository, session, districtRtIds, 0, 1);

        await session.CommitTransactionAsync();

        Assert.True(unpaged.Values.Sum(r => r.TotalCount) >= 2);
        foreach (var (origin, resultSet) in unpaged)
        {
            Assert.Equal(resultSet.Items.Count(), resultSet.TotalCount);
            Assert.Equal(resultSet.TotalCount, paged[origin].TotalCount);
        }
    }
}
