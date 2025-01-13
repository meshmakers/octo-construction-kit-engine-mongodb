using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using TestCkModel.Generated.Test.v1;
using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

public class SerializeDeserializeTypedUntypedTests(ImportTestCkModelFixture systemFixture)
    : IClassFixture<ImportTestCkModelFixture>
{
    [Fact]
    public async Task CreateAndQuery_Scalar_Typed_OK()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
            rtContinent.Name = "test" + i;
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session2,
            DataQueryOperation.Create());

        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
    }
    
    [Fact]
    public async Task CreateAndQuery_Scalar_Untyped_OK()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync(new CkId<CkTypeId>(TestCkIds.ModelId, TestCkIds.ContinentTypeId));
            rtContinent.SetAttributeValue("Name", AttributeValueTypesDto.String, "test" + i);
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync(session2, "Test/Continent",
            DataQueryOperation.Create());

        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
    }
    
    [Fact]
    public async Task CreateAndQuery_WithRecord_Typed_DeserializeUntyped_OK()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtCustomer = await tenantRepository.CreateTransientRtEntityAsync<RtCustomer>();
            rtCustomer.Name = new RtContactNameRecord
            {
                Salutation = "Mr",
                FirstName = "John",
                LastName = "Doe",
                CompanyName = "Company" + i,
                AdditionalLine = "Additional" + i
            };
            await tenantRepository.InsertOneRtEntityAsync(session, rtCustomer);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync(session2, "Test/Customer",
            DataQueryOperation.Create());
        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
        
        var rtRecord = y.Items.First().GetRtRecordAttributeValue<RtContactNameRecord>("Name");
        Assert.Equal("Mr", rtRecord.Salutation);
        Assert.Equal("John", rtRecord.FirstName);
        Assert.Equal("Doe", rtRecord.LastName);
        Assert.Equal("Company0", rtRecord.CompanyName);
        Assert.Equal("Additional0", rtRecord.AdditionalLine);
    }
    
    [Fact]
    public async Task CreateAndQuery_WithRecord_Typed_DeserializeTyped_OK()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtCustomer = await tenantRepository.CreateTransientRtEntityAsync<RtCustomer>();
            rtCustomer.Name = new RtContactNameRecord
            {
                Salutation = "Mr",
                FirstName = "John",
                LastName = "Doe",
                CompanyName = "Company" + i,
                AdditionalLine = "Additional" + i
            };
            await tenantRepository.InsertOneRtEntityAsync(session, rtCustomer);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtCustomer>(session2, 
            DataQueryOperation.Create());
        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
        Assert.Equal("Mr", y.Items.First().Name?.Salutation);
        Assert.Equal("John", y.Items.First().Name?.FirstName);
        Assert.Equal("Doe", y.Items.First().Name?.LastName);
        Assert.Equal("Company0", y.Items.First().Name?.CompanyName);
        Assert.Equal("Additional0", y.Items.First().Name?.AdditionalLine);
    }
    
    [Fact]
    public async Task CreateAndQuery_GeoPoint()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        for (var i = 0; i < 5; i++)
        {
            var rtTestPosition = await tenantRepository.CreateTransientRtEntityAsync<RtTestPosition>();
            rtTestPosition.Name = "test" + i;
            rtTestPosition.GeoPosition = new Point(new Position(1+i, 2+i, 3+i));
            await tenantRepository.InsertOneRtEntityAsync(session, rtTestPosition);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtTestPosition>(session2, 
            DataQueryOperation.Create());
        await session2.CommitTransactionAsync();

        Assert.Equal(5, y.Items.Count());
        Assert.Equal("test0", y.Items.First().Name);
        Assert.Equal(1, y.Items.First().GeoPosition.Coordinates.Latitude);
        Assert.Equal(2, y.Items.First().GeoPosition.Coordinates.Longitude);
        Assert.Equal(3, y.Items.First().GeoPosition.Coordinates.Altitude);
    }
    
    [Fact]
    public async Task CreateAndQuery_EmbeddedBinary()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        var bytes = await File.ReadAllBytesAsync("testData/binary-test.png");

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var rtMenuItem = await tenantRepository.CreateTransientRtEntityAsync<RtMenuItem>();
        rtMenuItem.Name = "test-embedded-binary";
        rtMenuItem.Icon = bytes;
        await tenantRepository.InsertOneRtEntityAsync(session, rtMenuItem);

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtMenuItem>(session2, 
            DataQueryOperation.Create());
        await session2.CommitTransactionAsync();

        Assert.Single(y.Items);
        Assert.Equal("test-embedded-binary", y.Items.First().Name);
        Assert.Equal(bytes, y.Items.First().Icon);
    }
}