using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.CkTest.ConstructionKit.Generated.Test.v1;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class RtEntityTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;

    public RtEntityTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }

    [Fact]
    public async void TestGetIndirectRtAssociationTargets()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();

            OctoObjectId originRtId = OctoObjectId.Parse("5fc8fc3d8b2fc75f925e21bc");


            var r = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtCity, RtLocation>(session, originRtId,
                 SystemCkIds.ParentChild,
                 GraphDirections.Outbound);
        }
    }

    [Fact]
    public async void Test1()
    {
        var systemContext = _systemFixture.GetSystemContext();
        using var systemSession = await systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(systemSession, new CkModelId("System.Identity-1.0.0"), operationResult);
        await systemSession.CommitTransactionAsync();
        
        var tenantRepository = systemContext.GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();

            var scopeNames = new []{"openid", "profile", "offline_access", "identityAPI.full_access"};
            var dataQueryOperation = DataQueryOperation.Create()
                .FieldFilter( "Scopes", FieldFilterOperator.In, scopeNames);

            var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "System.Identity/ApiResource", dataQueryOperation);

            await session.CommitTransactionAsync();
            
            Assert.Equal(1, result.TotalCount);
            Assert.Single(result.Items);

            // var result = await tenantContext.GetRtEntitiesByTypeAsync(session, "PaketService.Contact",
            //     new DataQueryOperation(), 0, 5);
            //
            // var rtIds = result.Result.Select(x => x.RtId).ToList();
            // var deep = await tenantContext.GetRtAssociationTargetsAsync(session, rtIds,
            //     "System/PaketService.Contact", "System/ParentChild", "PaketService.ParcelShipment", GraphDirections.Outbound, null,
            //     new DataQueryOperation(), 0, 5);


            //  Assert.Equal(5, deep.Count);
            // Assert.Collection(deep, 
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count())
            //     );
        }
    }

    [Fact]
    public async void Test1_1()
    {
        var planetDesignation = Guid.NewGuid().ToString();
        var systemContext = _systemFixture.GetSystemContext();
        using var systemSession = await systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        OperationResult operationResult = new();
        await systemContext.ImportCkModelAsync(systemSession, new CkModelId("Test-1.0.0"), operationResult);
        await systemSession.CommitTransactionAsync();

        var tenantRepository = systemContext.GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();

            var planet = await tenantRepository.CreateTransientRtEntityAsync<RtPlanet>();
            planet.Designation = planetDesignation;

            await tenantRepository.InsertOneRtEntityAsync(session, planet);

            await session.CommitTransactionAsync();
        }

    }
    
    [Fact]
    public async void Test1_2()
    {
        var planetDesignation = "da1d0128-635f-46b3-8071-bf2a4d271c07";

        var systemContext = _systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var dataQueryOperation = DataQueryOperation.Create()
                .FieldFilter( nameof(RtPlanet.Designation), FieldFilterOperator.Equals, planetDesignation);
            
          //  var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "Test/Planet", dataQueryOperation);
            var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtPlanet>(session, dataQueryOperation);

            await session.CommitTransactionAsync();
            
            Assert.Equal(1, result.TotalCount);
            Assert.Single(result.Items);

        }
    }


    [Fact]
    public async void Test2()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using (var session = await tenantRepository.GetSessionAsync())
        {
            // var result = await tenantRepository.GetRtEntitiesByTypeAsync(session, "PaketService.ParcelShipment",
            //     new DataQueryOperation(), 0, 10);


            var dataOperation = DataQueryOperation.Create()
                .FieldFilter("LastName", FieldFilterOperator.Like, "K*");
            // [LastName, Kastler]
            // dataOperation.SortOrders = new List<SortOrderItem>(new[]
            // {
            //     new SortOrderItem("LastName", SortOrders.Descending)
            // });

            // var rtIds = result.Result.Select(x => x.RtId).ToList();
            // var deep = await tenantRepository.GetRtAssociationTargetsAsync(session, rtIds,
            //     "PaketService.ParcelShipment", "System.ParentChild", "PaketService.Contact", GraphDirections.Inbound, null,
            //     dataOperation, 0, 5);


            // Assert.Collection(deep, 
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count()),
            //     pair => Assert.Equal(5, pair.Value.Result.Count())
            //     );
        }
    }
}