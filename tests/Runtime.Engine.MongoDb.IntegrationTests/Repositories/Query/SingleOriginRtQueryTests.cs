using FakeItEasy;

using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Metrics.Meters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

[Collection("Sequential")]
public class SingleOriginRtQueryTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _systemFixture;
    private readonly ILoggerFactory _loggerFactory;

    public SingleOriginRtQueryTests(SampleRtModelDataFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _systemFixture.OutputHelper = output;
        _loggerFactory = systemFixture.GetService<ILoggerFactory>();
    }

    [Fact]
    public async Task FieldFilter_SystemAttribute_RtWellKnownName_OK()
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .FieldEquals("RtWellKnownName", "TestCustomer3"));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Fact]
    public async Task FieldFilter_SystemAttribute_RtId_OK()
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .FieldEquals("RtId", ObjectId.Parse("66803ecf4aa85720dda96b15")));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Fact]
    public async Task FieldFilter_SystemAttribute_RtId_Equals_AsString_OK()
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        // Test with string value instead of ObjectId - should still work
        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .FieldEquals("RtId", "66803ecf4aa85720dda96b15"));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Theory]
    [InlineData(FieldFilterOperator.Equals, "Pinzgau / Zell am See")]
    [InlineData(FieldFilterOperator.Like, "*Pinzgau*")]
    [InlineData(FieldFilterOperator.MatchRegEx, "Zell")]
    public async Task FieldFilter_Attribute_OK(FieldFilterOperator fieldFilterOperator, object comparisonValue)
    {
        var systemContext = Prepare(TestCkIds.CkDistrictTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("Name", fieldFilterOperator, comparisonValue));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("Pinzgau / Zell am See", resultSet.Items.First().GetAttributeStringValue("Name"));
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b01"), resultSet.Items.First().RtId);
    }

    [Theory]
    [InlineData("Address.Street", FieldFilterOperator.Equals, "Neutorstraße 25")]
    [InlineData("Address.Street", FieldFilterOperator.Like, "*Neutorstraße*")]
    [InlineData("Address.Street", FieldFilterOperator.MatchRegEx, "Neutorstraße")]
    [InlineData("Address.Street", FieldFilterOperator.In, new object[] { "Neutorstraße 25", "Demo 25" })]
    public async Task FieldFilter_Attribute_Scalar_Embedded_OK(string attributePath,
        FieldFilterOperator fieldFilterOperator, object comparisonValue)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field(attributePath, fieldFilterOperator, comparisonValue));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Theory]
    [InlineData("EMailAddresses[*].EMailAddress", FieldFilterOperator.AnyEq, "jane.doe.office@demo.com")]
    [InlineData("EMailAddresses[0].EMailAddress", FieldFilterOperator.AnyEq, "jane.doe.office@demo.com")]
    [InlineData("EMailAddresses[1].EMailAddress", FieldFilterOperator.AnyEq, "jane.doe.private@demo.com")]
    public async Task FieldFilter_Attribute_Array_Wildcard_Embedded_OK(string attributePath,
        FieldFilterOperator fieldFilterOperator, string comparisonValue)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field(attributePath, fieldFilterOperator, comparisonValue));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Fact]
    public async Task FieldFilter_LogicalOr_Dim0_OK()
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create(LogicalOperators.Or)
            .FieldEquals("rtWellKnownName", "TestCustomer2")
            .FieldEquals("rtWellKnownName", "TestCustomer3"));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(2, resultSet.TotalCount);
        // Check if the result contains TestCustomer2 and TestCustomer3 in any order
        Assert.Contains(resultSet.Items, item => item.RtWellKnownName == "TestCustomer2");
        Assert.Contains(resultSet.Items, item => item.RtWellKnownName == "TestCustomer3");
        Assert.Contains(resultSet.Items, item => item.RtId == OctoObjectId.Parse("66803ecf4aa85720dda96b14"));
        Assert.Contains(resultSet.Items, item => item.RtId == OctoObjectId.Parse("66803ecf4aa85720dda96b15"));
    }

    [Theory]
    [InlineData(FieldFilterOperator.Like, "*96b15", 1)]
    [InlineData(FieldFilterOperator.Like, "66803ecf4aa85720dda96b15", 1)]
    [InlineData(FieldFilterOperator.Like, "66803ecf*", 3)] // All 3 customers match this prefix
    [InlineData(FieldFilterOperator.Like, "*dda96b15*", 1)]
    public async Task FieldFilter_SystemAttribute_RtId_Like_OK(FieldFilterOperator filterOperator, string pattern, int expectedCount)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("RtId", filterOperator, pattern));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(expectedCount, resultSet.TotalCount);
        Assert.Contains(resultSet.Items, item => item.RtId == OctoObjectId.Parse("66803ecf4aa85720dda96b15"));
    }

    [Theory]
    [InlineData(FieldFilterOperator.Contains, "dda96b15")]
    [InlineData(FieldFilterOperator.Contains, "66803ecf")]
    [InlineData(FieldFilterOperator.Contains, "4aa857")]
    public async Task FieldFilter_SystemAttribute_RtId_Contains_OK(FieldFilterOperator filterOperator, string pattern)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("RtId", filterOperator, pattern));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.True(resultSet.TotalCount >= 1);
        Assert.Contains(resultSet.Items, item => item.RtId == OctoObjectId.Parse("66803ecf4aa85720dda96b15"));
    }

    [Theory]
    [InlineData(FieldFilterOperator.StartsWith, "66803ecf4aa85720dda96b15")]
    [InlineData(FieldFilterOperator.StartsWith, "66803ecf")]
    public async Task FieldFilter_SystemAttribute_RtId_StartsWith_OK(FieldFilterOperator filterOperator, string pattern)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("RtId", filterOperator, pattern));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.True(resultSet.TotalCount >= 1);
        Assert.Contains(resultSet.Items, item => item.RtId == OctoObjectId.Parse("66803ecf4aa85720dda96b15"));
    }

    [Theory]
    [InlineData(FieldFilterOperator.EndsWith, "96b15")]
    [InlineData(FieldFilterOperator.EndsWith, "dda96b15")]
    public async Task FieldFilter_SystemAttribute_RtId_EndsWith_OK(FieldFilterOperator filterOperator, string pattern)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("RtId", filterOperator, pattern));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Theory]
    [InlineData(FieldFilterOperator.MatchRegEx, "66803ecf4aa85720dda96b15")]
    [InlineData(FieldFilterOperator.MatchRegEx, "66803ecf.*96b15")]
    [InlineData(FieldFilterOperator.MatchRegEx, ".*dda96b15$")]
    public async Task FieldFilter_SystemAttribute_RtId_MatchRegEx_OK(FieldFilterOperator filterOperator, string pattern)
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, false, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .Field("RtId", filterOperator, pattern));

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(1, resultSet.TotalCount);
        Assert.Equal("TestCustomer3", resultSet.Items.First().RtWellKnownName);
        Assert.Equal(OctoObjectId.Parse("66803ecf4aa85720dda96b15"), resultSet.Items.First().RtId);
    }

    [Fact]
    public async Task IgnoreDeletedByDefault_OK()
    {
        var systemContext = Prepare(TestCkIds.CkMunicipalityTypeId, false, out var query);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(2, resultSet.TotalCount);
        // Check if the result contains TestCustomer2 and TestCustomer3 in any order
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Fusch");
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Leopoldskron-Moos");
    }

    [Fact]
    public async Task IncludeDeleted_OK()
    {
        var systemContext = Prepare(TestCkIds.CkMunicipalityTypeId, true, out var query);

        var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var resultSet = await query.ExecuteQuery(session);
        Assert.Equal(4, resultSet.TotalCount);
        // Check if the result contains TestCustomer2 and TestCustomer3 in any order
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Fusch");
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Leopoldskron-Moos");
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Fieberbrunn");
        Assert.Contains(resultSet.Items, item => item.GetAttributeStringValue("Name") == "Hochfilzen");
    }

    [Fact]
    public async Task NavigationProperty_District_To_StateOrProvince_OK()
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();

        // Query Districts with navigation property to parent StateOrProvince
        var requestedFieldNames = new[]
        {
            "name",
            "parent.testStateOrProvince->name"
        };

        var navigationPairs = RtPathEvaluator.TokenizeAndGetNavigationPairs(
            ckCacheService,
            systemContext.TenantId,
            TestCkIds.CkDistrictTypeId,
            requestedFieldNames);

        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkDistrictTypeId,
            queryOptions,
            navigationPairs);

        Assert.NotEmpty(resultSet.Items);

        // Verify that we got districts with their parent StateOrProvince names
        var pinzgau = resultSet.Items.FirstOrDefault(d =>
            d.GetAttributeStringValue("Name") == "Pinzgau / Zell am See");
        Assert.NotNull(pinzgau);
    }

    private ISystemContext Prepare(CkId<CkTypeId> ckTypeId, bool includeArchivedEntities,
        out SingleOriginRtQuery<RtEntity> query)
    {
        var systemContext = _systemFixture.GetSystemContext();
        var ckCacheService = _systemFixture.GetService<ICkCacheService>();
        var ckTypeGraph = ckCacheService.GetCkType(systemContext.TenantId, ckTypeId);

        var metricsContext = A.Fake<IMetricsContext>();
        A.CallTo(() => metricsContext.CreateRuntimeMeter(A<string>.Ignored))
            .Returns(A.Fake<IRuntimeMeter>());

        var mongoDbRepositoryDataSource = new MongoDbRepositoryDataSource(
            _loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            _systemFixture.GetService<IUserRepositoryAccess>(), _systemFixture.SystemDatabaseName,
            systemContext.TenantId);

        query = new(metricsContext, ckCacheService, systemContext.TenantId,
            ckTypeGraph, mongoDbRepositoryDataSource, "en", includeArchivedEntities);
        return systemContext;
    }
}
