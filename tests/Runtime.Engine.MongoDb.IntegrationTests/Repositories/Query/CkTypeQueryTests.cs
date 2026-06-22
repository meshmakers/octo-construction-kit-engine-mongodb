using FakeItEasy;

using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Metrics.Meters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using Microsoft.Extensions.Logging;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection(ImportTestCkModelCollection.Name)]
public class CkTypeQueryTests
{
    private readonly ImportTestCkModelFixture _fixture;
    private readonly ILoggerFactory _loggerFactory;

    public CkTypeQueryTests(ImportTestCkModelFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
        _loggerFactory = fixture.GetService<ILoggerFactory>();
    }

    [Fact]
    public async Task ExecuteQuery_ReturnsOnlyAvailableCkTypes()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Act
        var resultSet = await query.ExecuteQuery(session);

        // Assert
        Assert.True(resultSet.TotalCount > 0, "Should return CkTypes");
        Assert.All(resultSet.Items, ckType => Assert.Equal(ModelState.Available, ckType.ModelState));
    }

    [Fact]
    public async Task AddRtCkIdFilter_WithSimpleId_MatchesCkType()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Filter for Test/Customer (without version) - should match Test-1/Customer-1
        query.AddRtCkIdFilter(new List<RtCkId<CkTypeId>> { new("Test/Customer") });

        // Act
        var resultSet = await query.ExecuteQuery(session);

        // Assert
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Contains(resultSet.Items, ckType => ckType.CkTypeId.ToString().Contains("Customer"));
    }

    [Fact]
    public async Task AddRtCkIdFilter_WithVersionedId_MatchesExactCkType()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // First get all CkTypes to see the actual IDs in the database
        var allTypesQuery = CreateQuery();
        var allTypes = await allTypesQuery.ExecuteQuery(session);

        // Find a Customer type and get its exact ID
        var customerType = allTypes.Items.FirstOrDefault(t => t.CkTypeId.ToString().Contains("Customer"));
        Assert.NotNull(customerType);

        // Now filter using the exact CkTypeId from database
        var exactIdString = customerType.CkTypeId.ToString();
        var queryWithFilter = CreateQuery();
        queryWithFilter.AddRtCkIdFilter(new List<RtCkId<CkTypeId>> { new(exactIdString) });

        // Act
        var resultSet = await queryWithFilter.ExecuteQuery(session);

        // Assert
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Contains(resultSet.Items, ckType => ckType.CkTypeId.ToString().Contains("Customer"));
    }

    [Fact]
    public async Task AddRtCkIdFilter_WithMultipleIds_ReturnsMatchingCkTypes()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Filter for multiple types
        query.AddRtCkIdFilter(new List<RtCkId<CkTypeId>>
        {
            new("Test/Customer"),
            new("Test/Location")
        });

        // Act
        var resultSet = await query.ExecuteQuery(session);

        // Assert
        Assert.Equal(2, resultSet.TotalCount);
        Assert.Contains(resultSet.Items, ckType => ckType.CkTypeId.ToString().Contains("Customer"));
        Assert.Contains(resultSet.Items, ckType => ckType.CkTypeId.ToString().Contains("Location"));
    }

    [Fact]
    public async Task AddRtCkIdFilter_WithNullList_ReturnsAllCkTypes()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Get count without filter
        var unfiltered = await query.ExecuteQuery(session);
        var unfilteredCount = unfiltered.TotalCount;

        // Create new query and apply null filter
        var queryWithNullFilter = CreateQuery();
        queryWithNullFilter.AddRtCkIdFilter<RtCkId<CkTypeId>>(null);

        // Act
        var resultSet = await queryWithNullFilter.ExecuteQuery(session);

        // Assert
        Assert.Equal(unfilteredCount, resultSet.TotalCount);
    }

    [Fact]
    public async Task AddRtCkIdFilter_WithEmptyList_ReturnsAllCkTypes()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Get count without filter
        var unfiltered = await query.ExecuteQuery(session);
        var unfilteredCount = unfiltered.TotalCount;

        // Create new query and apply empty filter
        var queryWithEmptyFilter = CreateQuery();
        queryWithEmptyFilter.AddRtCkIdFilter(new List<RtCkId<CkTypeId>>());

        // Act
        var resultSet = await queryWithEmptyFilter.ExecuteQuery(session);

        // Assert
        Assert.Equal(unfilteredCount, resultSet.TotalCount);
    }

    [Fact]
    public async Task AddModelIdFilter_FiltersToSpecificModel()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Filter for Test model
        var testModelId = new CkModelId("Test");
        query.AddModelIdFilter(new List<CkModelId> { testModelId });

        // Act
        var resultSet = await query.ExecuteQuery(session);

        // Assert
        Assert.True(resultSet.TotalCount > 0, "Should return CkTypes from Test model");
        Assert.All(resultSet.Items, ckType => Assert.Equal(testModelId, ckType.CkModelId));
    }

    [Fact]
    public async Task AddModelIdFilter_WithNonExistingModel_ReturnsEmpty()
    {
        // Arrange
        var query = CreateQuery();
        var systemContext = _fixture.GetSystemContext();
        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Filter for non-existing model
        var nonExistingModelId = new CkModelId("NonExistingModel");
        query.AddModelIdFilter(new List<CkModelId> { nonExistingModelId });

        // Act
        var resultSet = await query.ExecuteQuery(session);

        // Assert
        Assert.Equal(0, resultSet.TotalCount);
    }

    private CkTypeQuery CreateQuery()
    {
        var systemContext = _fixture.GetSystemContext();

        var metricsContext = A.Fake<IMetricsContext>();
        A.CallTo(() => metricsContext.CreateRuntimeMeter(A<string>.Ignored))
            .Returns(A.Fake<IRuntimeMeter>());

        var mongoDbRepositoryDataSource = new MongoDbRepositoryDataSource(
            _loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _fixture.GetService<IUserRepositoryAccess>(),
            _fixture.SystemDatabaseName,
            systemContext.TenantId);

        return new CkTypeQuery(metricsContext, mongoDbRepositoryDataSource);
    }
}
