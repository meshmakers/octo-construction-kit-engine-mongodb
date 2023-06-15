using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class UnitTest1 : IClassFixture<TenantFixture>
{
    private readonly TenantFixture _tenantFixture;

    public UnitTest1(TenantFixture tenantFixture)
    {
        _tenantFixture = tenantFixture;
    }

    [Fact]
    public async void TestGetIndirectRtAssociationTargets()
    {
        var tenantContext = await _tenantFixture.GetTenantContextAsync();

        using (var session = await tenantContext.Repository.StartSessionAsync())
        {
            session.StartTransaction();

            ObjectId originRtId = ObjectId.Parse("5fc8fc3d8b2fc75f925e21bc");


            var r = await tenantContext.Repository.GetIndirectRtAssociationTargetsAsync<RtPlugMapping, RtPlug>(session, originRtId, Statics.RoleIdParentChild,
                GraphDirections.Outbound);

        }
    }

    [Fact]
    public async void Test1()
    {
        var tenantContext = await _tenantFixture.GetTenantContextAsync();

        using (var session = await tenantContext.Repository.StartSessionAsync())
        {
            var result = await tenantContext.Repository.GetRtEntitiesByTypeAsync(session, "PaketService.Contact",
                new DataQueryOperation(), 0, 5);

            var rtIds = result.Result.Select(x => x.RtId).ToList();
            var deep = await tenantContext.Repository.GetRtAssociationTargetsAsync(session, rtIds,
                "PaketService.Contact", "System.ParentChild", "PaketService.ParcelShipment", GraphDirections.Outbound, null,
                new DataQueryOperation(), 0, 5);


            Assert.Equal(5, deep.Count);
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
    public async void Test2()
    {
        var tenantContext = await _tenantFixture.GetTenantContextAsync();

        using (var session = await tenantContext.Repository.StartSessionAsync())
        {
            var result = await tenantContext.Repository.GetRtEntitiesByTypeAsync(session, "PaketService.ParcelShipment",
                new DataQueryOperation(), 0, 10);


            var dataOperation = new DataQueryOperation();
            dataOperation.FieldFilters = new List<FieldFilter>(new[]
                { new FieldFilter("LastName", FieldFilterOperator.Like, "K*") });
            // [LastName, Kastler]
            // dataOperation.SortOrders = new List<SortOrderItem>(new[]
            // {
            //     new SortOrderItem("LastName", SortOrders.Descending)
            // });

            var rtIds = result.Result.Select(x => x.RtId).ToList();
            var deep = await tenantContext.Repository.GetRtAssociationTargetsAsync(session, rtIds,
                "PaketService.ParcelShipment", "System.ParentChild", "PaketService.Contact", GraphDirections.Inbound, null,
                dataOperation, 0, 5);


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
