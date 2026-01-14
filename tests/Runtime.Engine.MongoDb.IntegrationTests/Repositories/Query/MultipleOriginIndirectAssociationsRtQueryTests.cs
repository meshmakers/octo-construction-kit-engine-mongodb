using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using Microsoft.Extensions.Logging;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection("Sequential")]
public class MultipleOriginIndirectAssociationsRtQueryTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _systemFixture;
    private readonly ILoggerFactory _loggerFactory;

    public MultipleOriginIndirectAssociationsRtQueryTests(SampleRtModelDataFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _systemFixture.OutputHelper = output;
        _loggerFactory = systemFixture.GetService<ILoggerFactory>();
    }

    [Fact]
    public async Task DirectAssociation_Inbound_MultipleDistricts_To_Municipalities_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // Multiple Districts as origin
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96b01"), // Pinzgau / Zell am See
            OctoObjectId.Parse("66803ecf4aa85720dda96b06")  // Salzburg Stadt
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkDistrictTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkMunicipalityTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Equal(2, resultSet.Count);

        // District 1 (Pinzgau) should have 1 Municipality (Fusch)
        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkDistrictTypeId, rtIds[0]), out var district1Results));
        Assert.Equal(1, district1Results.TotalCount);
        Assert.Contains(district1Results.Items, m => m.GetAttributeStringValue("Name") == "Fusch");

        // District 2 (Salzburg Stadt) should have 1 Municipality (Leopoldskron-Moos)
        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkDistrictTypeId, rtIds[1]), out var district2Results));
        Assert.Equal(1, district2Results.TotalCount);
        Assert.Contains(district2Results.Items, m => m.GetAttributeStringValue("Name") == "Leopoldskron-Moos");
    }

    [Fact]
    public async Task DirectAssociation_Outbound_MultipleMunicipalities_To_Districts_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // Multiple Municipalities as origin
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96b07"), // Fusch
            OctoObjectId.Parse("66803ecf4aa85720dda96b08")  // Leopoldskron-Moos
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkMunicipalityTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Outbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Equal(2, resultSet.Count);

        // Municipality 1 (Fusch) should have 1 District (Pinzgau / Zell am See)
        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkMunicipalityTypeId, rtIds[0]), out var municipality1Results));
        Assert.NotNull(municipality1Results);
        Assert.Equal(1, municipality1Results.TotalCount);
        Assert.Contains(municipality1Results.Items, d => d.GetAttributeStringValue("Name") == "Pinzgau / Zell am See");

        // Municipality 2 (Leopoldskron-Moos) should have 1 District (Salzburg Stadt)
        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkMunicipalityTypeId, rtIds[1]), out var municipality2Results));
        Assert.NotNull(municipality2Results);
        Assert.Equal(1, municipality2Results.TotalCount);
        Assert.Contains(municipality2Results.Items, d => d.GetAttributeStringValue("Name") == "Salzburg Stadt");
    }

    [Fact]
    public async Task IndirectAssociation_Inbound_MultipleStateOrProvince_To_Municipalities_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Salzburg)
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkMunicipalityTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        // Salzburg should have 2 Municipalities (Fusch, Leopoldskron-Moos) through Districts
        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, rtIds[0]), out var salzburg));
        Assert.NotNull(salzburg);
        Assert.Equal(2, salzburg.TotalCount);
        Assert.Contains(salzburg.Items, m => m.GetAttributeStringValue("Name") == "Fusch");
        Assert.Contains(salzburg.Items, m => m.GetAttributeStringValue("Name") == "Leopoldskron-Moos");
    }

    [Fact]
    public async Task IndirectAssociation_Outbound_MultipleMunicipalities_To_StateOrProvince_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // Multiple Municipalities as origin
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96b07"), // Fusch
            OctoObjectId.Parse("66803ecf4aa85720dda96b08")  // Leopoldskron-Moos
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkMunicipalityTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Outbound, TestCkIds.CkStateOrProvinceTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Equal(2, resultSet.Count);

        // Both municipalities should lead to Salzburg through Districts
        foreach (var result in resultSet.Values)
        {
            Assert.Equal(1, result.TotalCount);
            Assert.Contains(result.Items, s => s.GetAttributeStringValue("Name") == "Salzburg");
        }
    }

    [Fact]
    public async Task FieldFilter_Attribute_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Salzburg)
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkMunicipalityTypeId, false);

        // Filter for only "Fusch"
        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .FieldEquals("Name", "Fusch"));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        var salzburg = resultSet.First().Value;
        Assert.Equal(1, salzburg.TotalCount);
        Assert.Equal("Fusch", salzburg.Items.First().GetAttributeStringValue("Name"));
    }

    [Fact]
    public async Task FieldFilter_Attribute_Like_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Salzburg)
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, false);

        // Filter for districts containing "Zell"
        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("Name", FieldFilterOperator.Like, "*Zell*"));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        var salzburg = resultSet.First().Value;
        Assert.Equal(1, salzburg.TotalCount);
        Assert.Contains("Zell", salzburg.Items.First().GetAttributeStringValue("Name"));
    }

    [Fact]
    public async Task IgnoreDeletedByDefault_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Tirol) - which has deleted districts
        var rtIds = new[]
        {
            OctoObjectId.Parse("68fded922b85e5d74c05a560") // Tirol
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        var tirol = resultSet.First().Value;
        // Tirol has 4 districts total, but 2 are deleted (Imst, Kitzbühel)
        // Should only return 2 non-deleted districts (Lienz, Landeck)
        Assert.Equal(2, tirol.TotalCount);
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Lienz");
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Landeck");
        Assert.DoesNotContain(tirol.Items, d => d.GetAttributeStringValue("Name") == "Imst");
        Assert.DoesNotContain(tirol.Items, d => d.GetAttributeStringValue("Name") == "Kitzbühel");
    }

    [Fact]
    public async Task IncludeDeleted_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Tirol) - which has deleted districts
        var rtIds = new[]
        {
            OctoObjectId.Parse("68fded922b85e5d74c05a560") // Tirol
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, true);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        var tirol = resultSet.First().Value;
        // Should return all 4 districts including deleted ones
        Assert.Equal(4, tirol.TotalCount);
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Lienz");
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Landeck");
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Imst");
        Assert.Contains(tirol.Items, d => d.GetAttributeStringValue("Name") == "Kitzbühel");
    }

    [Fact]
    public async Task IndirectAssociation_IncludeDeleted_Inbound_Tirol_To_Municipalities_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Tirol) - which has deleted districts and municipalities
        var rtIds = new[]
        {
            OctoObjectId.Parse("68fded922b85e5d74c05a560") // Tirol
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkMunicipalityTypeId, true);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Single(resultSet);

        var tirol = resultSet.First().Value;
        // Tirol has 2 deleted municipalities through deleted district Kitzbühel (Fieberbrunn, Hochfilzen)
        Assert.Equal(2, tirol.TotalCount);
        Assert.Contains(tirol.Items, m => m.GetAttributeStringValue("Name") == "Fieberbrunn");
        Assert.Contains(tirol.Items, m => m.GetAttributeStringValue("Name") == "Hochfilzen");
    }

    [Fact]
    public async Task Paging_Skip_Take_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // StateOrProvince as origin (Salzburg)
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Salzburg has 6 districts, take first 3
        var resultSet = await query.ExecuteQuery(session, 0, 3);

        Assert.Single(resultSet);

        var salzburg = resultSet.First().Value;
        Assert.Equal(6, salzburg.TotalCount); // Total count should still be 6
        Assert.Equal(3, salzburg.Items.Count()); // But only 3 targets returned
    }

    [Fact]
    public async Task Paging_Skip_WithoutTake_ThrowsException()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Outbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Skip without take should throw
        await Assert.ThrowsAsync<OperationFailedException>(() => query.ExecuteQuery(session, 1, null));
    }

    [Fact]
    public async Task Paging_TakeOnly_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Take only (without skip) should work
        var resultSet = await query.ExecuteQuery(session, null, 3);

        Assert.Single(resultSet);

        var salzburg = resultSet.First().Value;
        Assert.Equal(6, salzburg.TotalCount);
        Assert.Equal(3, salzburg.Items.Count());
    }

    [Fact]
    public async Task MultipleOrigins_DifferentTargetCounts_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // Multiple StateOrProvinces with different numbers of districts
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99"), // Salzburg (6 districts)
            OctoObjectId.Parse("68fded922b85e5d74c05a560")  // Tirol (4 districts, 2 deleted by default)
        };

        var query = Prepare(systemContext, ckCacheService, rtIds, TestCkIds.CkStateOrProvinceTypeId,
            SystemCkIds.RtCkParentChildRoleId, GraphDirections.Inbound, TestCkIds.CkDistrictTypeId, false);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session, null, null);

        Assert.Equal(2, resultSet.Count);

        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, rtIds[0]), out var salzburg));
        Assert.NotNull(salzburg);
        Assert.Equal(6, salzburg.TotalCount);

        Assert.True(resultSet.TryGetValue(new RtEntityId(TestCkIds.RtCkStateOrProvinceTypeId, rtIds[1]), out var tirol));
        Assert.NotNull(tirol);
        Assert.Equal(2, tirol.TotalCount); // Only non-deleted
    }

    private MultipleOriginIndirectAssociationsRtQuery<RtEntity> Prepare(
        ISystemContext systemContext,
        ICkCacheService ckCacheService,
        IEnumerable<OctoObjectId> rtIds,
        CkId<CkTypeId> originCkTypeId,
        RtCkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection,
        CkId<CkTypeId> targetCkTypeId,
        bool includeArchivedEntities)
    {
        var originCkTypeGraph = ckCacheService.GetCkType(systemContext.TenantId, originCkTypeId);
        var targetCkTypeGraph = ckCacheService.GetCkType(systemContext.TenantId, targetCkTypeId);

        var mongoDbRepositoryDataSource = new MongoDbRepositoryDataSource(
            _loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(), _systemFixture.SystemDatabaseName,
            systemContext.TenantId);

        return new MultipleOriginIndirectAssociationsRtQuery<RtEntity>(
            ckCacheService, systemContext.TenantId, mongoDbRepositoryDataSource, "en",
            includeArchivedEntities, rtIds, originCkTypeGraph, roleId, graphDirection, targetCkTypeGraph);
    }
}
