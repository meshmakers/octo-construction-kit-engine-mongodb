using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class GetRtAssociationTargetsAsyncTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _sampleRtModelDataFixture;

    public GetRtAssociationTargetsAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture, ITestOutputHelper output)
    {
        _sampleRtModelDataFixture = sampleRtModelDataFixture;
        sampleRtModelDataFixture.OutputHelper = output;
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkDistrictTypeId,
            queryOptions, 0, 5);

        var rtIds = result.Items.Select(x => x.RtId).ToList();
        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, rtIds, TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Equal(5, deep.Count);
    }

    [Fact]
    public async Task GetRtAssociationTargetsAsync_Deleted_DefaultDoNotIncludeDeleted_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create();

        var rtEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(TestCkIds.RtCkDistrictTypeId, new OctoObjectId("68fded922b85e5d74c05a564")));
        Assert.NotNull(rtEntity);

        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, [rtEntity.RtId], TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Single(deep);
        Assert.NotNull(deep[rtEntity.ToRtEntityId()]);
        Assert.Equal(0, deep[rtEntity.ToRtEntityId()].TotalCount);
    }


    [Fact]
    public async Task GetRtAssociationTargetsAsync_FromDeleted_IncludeDeletedAssocs_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();


        var queryOptions = RtEntityQueryOptions.Create().Global(true);

        var rtEntity = await tenantRepository.GetRtEntityByRtIdAsync(session,
            new RtEntityId(TestCkIds.RtCkDistrictTypeId, new OctoObjectId("68fded922b85e5d74c05a564")));
        Assert.NotNull(rtEntity);

        var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, [rtEntity.RtId], TestCkIds.RtCkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId,
            TestCkIds.RtCkMunicipalityTypeId, GraphDirections.Inbound, null,
            queryOptions, 0, 5);

        Assert.Single(deep);
        Assert.NotNull(deep[rtEntity.ToRtEntityId()]);
        Assert.Equal(2, deep[rtEntity.ToRtEntityId()].TotalCount);
    }
}
