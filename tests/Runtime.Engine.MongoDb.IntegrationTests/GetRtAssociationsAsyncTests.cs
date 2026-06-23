using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SampleRtModelDataCollection.Name)]
public class GetRtAssociationsAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture)
{
    [Fact]
    public async Task GetRtAssociationsAsync_OK()
    {
        var systemContext = sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder(nameof(RtEntity.RtId), SortOrders.Ascending);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            queryOptions, 0, 5);

        var rtEntityIds = result.Items.Select(x => x.ToRtEntityId()).ToList();
        var deep = await tenantRepository.GetRtAssociationsAsync(session, rtEntityIds,
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Inbound, 0, 5));

        Assert.Equal(5, deep.Count);
        Assert.Single(deep["Test/District@66803ecf4aa85720dda96b01"].Items);
        Assert.Empty(deep["Test/District@66803ecf4aa85720dda96b02"].Items);
        Assert.Empty(deep["Test/District@66803ecf4aa85720dda96b03"].Items);
        Assert.Empty(deep["Test/District@66803ecf4aa85720dda96b04"].Items);
        Assert.Empty(deep["Test/District@66803ecf4aa85720dda96b05"].Items);
    }
}
