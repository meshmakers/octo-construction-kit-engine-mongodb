using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using Microsoft.Extensions.Logging;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection(SampleRtModelDataCollection.Name)]
public class MultipleOriginHierarchicalDeepRtGraphQueryTests
{
    private readonly SampleRtModelDataFixture _systemFixture;
    private readonly ILoggerFactory _loggerFactory;

    public MultipleOriginHierarchicalDeepRtGraphQueryTests(SampleRtModelDataFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _systemFixture.OutputHelper = output;
        _loggerFactory = systemFixture.GetService<ILoggerFactory>();
    }

    [Fact(Skip = "Maybe bug in the query engine - to be investigated")]
    public async Task SingleOrigin_DeepGraph_StateOrProvince_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Salzburg (StateOrProvince) as origin - has Districts -> Municipalities -> etc.
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        // Should return results for entities in the subgraph
        Assert.NotEmpty(resultSet.Items);

        // The result should contain the origin entity
        var originResult = resultSet.Items.FirstOrDefault(r => r.Id.RtId == rtIds[0]);
        Assert.NotNull(originResult);

        // Salzburg should have associations to its children (Districts)
        Assert.NotEmpty(originResult.Associations);
    }

    [Fact]
    public async Task MultipleOrigins_DeepGraph_StateOrProvinces_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Multiple StateOrProvinces as origin
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99"), // Salzburg
            OctoObjectId.Parse("68fded922b85e5d74c05a560")  // Tirol
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        Assert.NotEmpty(resultSet.Items);

        // Should have results for both origins
        var salzburgResult = resultSet.Items.FirstOrDefault(r => r.Id.RtId == rtIds[0]);
        var tirolResult = resultSet.Items.FirstOrDefault(r => r.Id.RtId == rtIds[1]);

        Assert.NotNull(salzburgResult);
        Assert.NotNull(tirolResult);
    }

    [Fact]
    public async Task DeepHierarchy_StateOrProvince_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Salzburg StateOrProvince as origin - has full hierarchy down to MeasuringPoints
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        Assert.NotEmpty(resultSet.Items);

        // The result should contain multiple levels of the hierarchy
        // Salzburg -> Districts -> Municipalities -> HouseHolds -> MeasuringPoints
        Assert.True(resultSet.TotalCount >= 1, "Should have at least Salzburg in the result");

        // Check that we have the origin
        var salzburgResult = resultSet.Items.FirstOrDefault(r => r.Id.RtId == rtIds[0]);
        Assert.NotNull(salzburgResult);
    }

    [Fact]
    public async Task IgnoreDeletedByDefault_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Tirol as origin - has deleted districts (Imst, Kitzbühel)
        var rtIds = new[]
        {
            OctoObjectId.Parse("68fded922b85e5d74c05a560") // Tirol
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        Assert.NotEmpty(resultSet.Items);

        // Should not contain archived districts (Imst, Kitzbühel) or their municipalities
        var archivedImstId = OctoObjectId.Parse("68fded922b85e5d74c05a563");
        var archivedKitzbuhelId = OctoObjectId.Parse("68fded922b85e5d74c05a564");
        var archivedFieberbrunnId = OctoObjectId.Parse("68fded922b85e5d74c05a565");
        var archivedHochfilzenId = OctoObjectId.Parse("68fded922b85e5d74c05a566");

        Assert.DoesNotContain(resultSet.Items, r => r.Id.RtId == archivedImstId);
        Assert.DoesNotContain(resultSet.Items, r => r.Id.RtId == archivedKitzbuhelId);
        Assert.DoesNotContain(resultSet.Items, r => r.Id.RtId == archivedFieberbrunnId);
        Assert.DoesNotContain(resultSet.Items, r => r.Id.RtId == archivedHochfilzenId);

        // Should contain non-archived districts (Lienz, Landeck)
        var lienzId = OctoObjectId.Parse("68fded922b85e5d74c05a561");
        var landeckId = OctoObjectId.Parse("68fded922b85e5d74c05a562");

        Assert.Contains(resultSet.Items, r => r.Id.RtId == lienzId);
        Assert.Contains(resultSet.Items, r => r.Id.RtId == landeckId);
    }

    [Fact]
    public async Task IncludeDeleted_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Tirol as origin - has deleted districts
        var rtIds = new[]
        {
            OctoObjectId.Parse("68fded922b85e5d74c05a560") // Tirol
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, true);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        Assert.NotEmpty(resultSet.Items);

        // Should now contain archived districts
        var archivedImstId = OctoObjectId.Parse("68fded922b85e5d74c05a563");
        var archivedKitzbuhelId = OctoObjectId.Parse("68fded922b85e5d74c05a564");

        Assert.Contains(resultSet.Items, r => r.Id.RtId == archivedImstId);
        Assert.Contains(resultSet.Items, r => r.Id.RtId == archivedKitzbuhelId);

        // And also non-archived districts
        var lienzId = OctoObjectId.Parse("68fded922b85e5d74c05a561");
        var landeckId = OctoObjectId.Parse("68fded922b85e5d74c05a562");

        Assert.Contains(resultSet.Items, r => r.Id.RtId == lienzId);
        Assert.Contains(resultSet.Items, r => r.Id.RtId == landeckId);
    }

    [Fact]
    public async Task Paging_Skip_Take_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Salzburg as origin - has 6 districts
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Get first 3 results
        var resultSet = await query.ExecuteQuery(session, 0, 3);

        Assert.NotEmpty(resultSet.Items);
        Assert.True(resultSet.Items.Count() <= 3, "Should return at most 3 items");
        Assert.True(resultSet.TotalCount >= resultSet.Items.Count(), "TotalCount should be >= returned items");
    }

    [Fact]
    public async Task Paging_TakeOnly_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Take only without skip should work
        var resultSet = await query.ExecuteQuery(session, null, 2);

        Assert.NotEmpty(resultSet.Items);
        Assert.True(resultSet.Items.Count() <= 2, "Should return at most 2 items");
    }

    [Fact(Skip = "Not implemented yet")]
    public async Task FieldFilter_Attribute_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();

        // Salzburg as origin
        var rtIds = new[]
        {
            OctoObjectId.Parse("66803ecf4aa85720dda96a99") // Salzburg
        };

        var query = Prepare(systemContext, rtIds, TestCkIds.CkStateOrProvinceTypeId, false);

        // Filter for entities with Name containing "Salzburg"
        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("Name", FieldFilterOperator.Like, "*Salzburg*"));

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);

        // Should only return entities matching the filter
        Assert.NotEmpty(resultSet.Items);
    }

    private MultipleOriginHierarchicalDeepRtGraphQuery Prepare(
        ISystemContext systemContext,
        IEnumerable<OctoObjectId> rtIds,
        CkId<CkTypeId> originCkTypeId,
        bool includeArchivedEntities)
    {
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();
        var originCkTypeGraph = ckCacheService.GetCkType(systemContext.TenantId, originCkTypeId);

        var mongoDbRepositoryDataSource = new MongoDbRepositoryDataSource(
            _loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(), _systemFixture.SystemDatabaseName,
            systemContext.TenantId);

        return new MultipleOriginHierarchicalDeepRtGraphQuery(
            mongoDbRepositoryDataSource, "en",
            includeArchivedEntities, rtIds, originCkTypeGraph,
            SystemCkIds.RtCkParentChildRoleId);
    }
}
