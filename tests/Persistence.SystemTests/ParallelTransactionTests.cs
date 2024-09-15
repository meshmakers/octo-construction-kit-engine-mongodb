using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

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
            var rtPlanet = await tenantRepository.CreateTransientRtEntityAsync<RtStateOrProvince>();
            rtPlanet.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(sessionA, rtPlanet);
        }

        await sessionA.CommitTransactionAsync();
        
   
        for (var i = 0; i < 5; i++)
        {
            var rtPlanet = await tenantRepository.CreateTransientRtEntityAsync<RtStateOrProvince>();
            rtPlanet.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(sessionB, rtPlanet);
        }
        await sessionB.CommitTransactionAsync();
    }

    private async Task PrepareData(ITenantRepository tenantRepository, int count)
    {
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        for (var i = 0; i < count; i++)
        {
            var rtStateOrProvince = await tenantRepository.CreateTransientRtEntityAsync<RtStateOrProvince>();
            rtStateOrProvince.Name = "test" + i;
            rtStateOrProvince.RtWellKnownName = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(session, rtStateOrProvince);
        }
        await session.CommitTransactionAsync();
    }
    
    private async Task<RtStateOrProvince> GetData(ITenantRepository tenantRepository, string prefix, int index)
    {
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        DataQueryOperation dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtStateOrProvince.RtWellKnownName), FieldFilterOperator.Equals, prefix + index);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtStateOrProvince>(session, dataQueryOperation);

        var rtStateOrProvince = resultSet.Items.FirstOrDefault();
        if (rtStateOrProvince == null)
        {
            throw new Exception("Entity not found");
        }
        await session.CommitTransactionAsync();

        return rtStateOrProvince;
    }
    
    private async Task UpdateData(ITenantRepository tenantRepository, IOctoSession session, RtStateOrProvince rtStateOrProvince)
    {
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, rtStateOrProvince.RtId, rtStateOrProvince);
    }
    
    [Fact]
    public async Task MultipleUpdates_OK()
    {
        var systemContext = generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        await PrepareData(tenantRepository, 5);
        
        var rtStateOrProvinceA = await GetData(tenantRepository, "test", 0);
        var rtStateOrProvinceB = await GetData(tenantRepository, "test", 0);

        var sessionA = await tenantRepository.GetSessionAsync();
        var sessionB = await tenantRepository.GetSessionAsync();
        
        sessionA.StartTransaction();
        sessionB.StartTransaction();
        
        rtStateOrProvinceA.Name = "updated0";
        await UpdateData(tenantRepository, sessionA, rtStateOrProvinceA);

        await sessionA.CommitTransactionAsync();
        
        rtStateOrProvinceB.Name = "updated1";
        await UpdateData(tenantRepository, sessionB, rtStateOrProvinceB);

        await sessionB.CommitTransactionAsync();
    }
}