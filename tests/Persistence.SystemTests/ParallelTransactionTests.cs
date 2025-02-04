using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class ParallelTransactionTests(GenerateSampleDataFixture generateSampleDataFixture)
    : IClassFixture<GenerateSampleDataFixture>
{
    [Fact]
    public async Task MultipleInserts_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        var sessionA = await tenantRepository.GetSessionAsync();
        var sessionB = await tenantRepository.GetSessionAsync();
        
        sessionA.StartTransaction();
        sessionB.StartTransaction();
        
        for (var i = 0; i < 5; i++)
        {
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(sessionA, rtContinent);
        }

        await sessionA.CommitTransactionAsync();
        
   
        for (var i = 0; i < 5; i++)
        {
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(sessionB, rtContinent);
        }
        await sessionB.CommitTransactionAsync();
    }

    private async Task PrepareData(ITenantRepository tenantRepository, int count)
    {
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        for (var i = 0; i < count; i++)
        {
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.Name = "test" + i;
            rtContinent.RtWellKnownName = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        }
        await session.CommitTransactionAsync();
    }
    
    private async Task<RtContinent> GetData(ITenantRepository tenantRepository, string prefix, int index)
    {
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        DataQueryOperation dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtStateOrProvince.RtWellKnownName), FieldFilterOperator.Equals, prefix + index);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session, dataQueryOperation);

        var rtContinent = resultSet.Items.FirstOrDefault();
        if (rtContinent == null)
        {
            throw new Exception("Entity not found");
        }
        await session.CommitTransactionAsync();

        return rtContinent;
    }
    
    private async Task UpdateData(ITenantRepository tenantRepository, IOctoSession session, RtContinent rtContinent)
    {
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, rtContinent.RtId, rtContinent);
    }
    
    [Fact]
    public async Task MultipleUpdates_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        await PrepareData(tenantRepository, 5);
        
        var rtContinentA = await GetData(tenantRepository, "test", 0);
        var rtContinentB = await GetData(tenantRepository, "test", 0);

        var sessionA = await tenantRepository.GetSessionAsync();
        var sessionB = await tenantRepository.GetSessionAsync();
        
        sessionA.StartTransaction();
        sessionB.StartTransaction();
        
        rtContinentA.Name = "updated0";
        await UpdateData(tenantRepository, sessionA, rtContinentA);

        await sessionA.CommitTransactionAsync();
        
        rtContinentB.Name = "updated1";
        await UpdateData(tenantRepository, sessionB, rtContinentB);

        await sessionB.CommitTransactionAsync();
    }
}