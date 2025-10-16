using FakeItEasy;

using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Metrics.Meters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Microsoft.Extensions.Logging;

using MongoDB.Bson;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Repositories.Query;

[Collection("Sequential")]
public class SingleOriginRtQueryTests
    : IClassFixture<GenerateSampleDataFixture>
{
    private readonly GenerateSampleDataFixture _systemFixture;
    private readonly ILoggerFactory _loggerFactory;

    public SingleOriginRtQueryTests(GenerateSampleDataFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _systemFixture.OutputHelper = output;
        _loggerFactory = systemFixture.GetService<ILoggerFactory>();
    }

    [Fact]
    public async Task FieldFilter_SystemAttribute_RtWellKnownName_OK()
    {
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, out var query);

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
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create()
            .FieldEquals("RtId", ObjectId.Parse("66803ecf4aa85720dda96b15")));

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
        var systemContext = Prepare(TestCkIds.CkDistrictTypeId, out var query);

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
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, out var query);

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
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, out var query);

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
        var systemContext = Prepare(TestCkIds.CkCustomerTypeId, out var query);

        query.AddFieldFilterCriteria(FieldFilterCriteria.Create(LogicalOperator.Or)
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

    private ISystemContext Prepare(CkId<CkTypeId> ckTypeId, out SingleOriginRtQuery<RtEntity> query)
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
            ckTypeGraph, mongoDbRepositoryDataSource, "en");
        return systemContext;
    }
}
