using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class GetRtDeepGraphAsyncTests(GenerateSampleDataFixture generateSampleDataFixture)
    : IClassFixture<GenerateSampleDataFixture>
{
    [Fact]
    public async void GetSubgraphAsync_Default_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        
        var dataOperation = DataQueryOperation.Create();
        
        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, new []{new OctoObjectId("66803ecf4aa85720dda96a97")}, 
            new CkId<CkTypeId>("Test/Continent"), dataOperation);

        await session.CommitTransactionAsync();
        
        Assert.Equal(15, resultSet.TotalCount);
    }
    
    [Fact]
    public async void GetSubgraphAsync_Paging_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        
        var dataOperation = DataQueryOperation.Create();
        
        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, new []{new OctoObjectId("66803ecf4aa85720dda96a97")}, 
            new CkId<CkTypeId>("Test/Continent"), dataOperation, 1, 2);

        await session.CommitTransactionAsync();
        
        Assert.Equal(15, resultSet.TotalCount);
        Assert.Equal(2, resultSet.Items.Count());

    }
    
    [Fact]
    public async void GetSubgraphAsync_MultipleOriginRtIds_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        
        var dataOperation = DataQueryOperation.Create();
        
        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, new []
            {
                new OctoObjectId("66803ecf4aa85720dda96b07"),
                new OctoObjectId("66803ecf4aa85720dda96b08")
            }, 
            new CkId<CkTypeId>("Test/Municipality"), dataOperation);

        await session.CommitTransactionAsync();
        
        Assert.Equal(5, resultSet.TotalCount);
    }
}