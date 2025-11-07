using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
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
            RtEntityQueryOptions.Create());

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
            var rtContinent = await tenantRepository.CreateTransientRtEntityAsync(TestCkIds.CkContinentTypeId);
            rtContinent.SetAttributeValue("Name", AttributeValueTypesDto.String, "test" + i);
            await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        }

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync(session2, "Test/Continent",
            RtEntityQueryOptions.Create());

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
            RtEntityQueryOptions.Create());
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
            RtEntityQueryOptions.Create());
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
            RtEntityQueryOptions.Create());
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
        
        var bytes = await File.ReadAllBytesAsync("testData/binary-test.png", TestContext.Current.CancellationToken);

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
            RtEntityQueryOptions.Create());
        await session2.CommitTransactionAsync();

        Assert.Single(y.Items);
        Assert.Equal("test-embedded-binary", y.Items.First().Name);
        Assert.Equal(bytes, y.Items.First().Icon);
    }
    
    [Fact]
    public async Task CreateAndQuery_StringArray()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var rtMenuItem = await tenantRepository.CreateTransientRtEntityAsync<RtTagsItem>();
        rtMenuItem.Name = "test-stringarray";
        rtMenuItem.Tags = new AttributeStringValueList(["tag1", "tag2"]);
        await tenantRepository.InsertOneRtEntityAsync(session, rtMenuItem);

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtTagsItem>(session2, 
            RtEntityQueryOptions.Create());
        await session2.CommitTransactionAsync();

        Assert.Single(y.Items);
        Assert.Equal("test-stringarray", y.Items.First().Name);
        Assert.Equal("tag1", y.Items.First().Tags?.First());
        Assert.Equal("tag2", y.Items.First().Tags?.ElementAt(1));
    }
    
    [Fact]
    public async Task CreateAndQuery_StringArray_Update()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var rtMenuItem = await tenantRepository.CreateTransientRtEntityAsync<RtTagsItem>();
        rtMenuItem.Name = "test-stringarray";
        rtMenuItem.Tags = new AttributeStringValueList(["tag1", "tag2"]);
        await tenantRepository.InsertOneRtEntityAsync(session, rtMenuItem);

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtTagsItem>(session2, 
            RtEntityQueryOptions.Create());
        await session2.CommitTransactionAsync();
        
        using var session3 = await tenantRepository.GetSessionAsync();
        session3.StartTransaction();
        var testItem = y.Items.First();
        testItem.Tags?.Add("tag3");
        testItem.Tags?.Add("tag4");
        await tenantRepository.UpdateOneRtEntityByIdAsync(session3, testItem.RtId, testItem);
        await session3.CommitTransactionAsync();
        
        using var session4 = await tenantRepository.GetSessionAsync();
        session4.StartTransaction();
        var z = await tenantRepository.GetRtEntitiesByTypeAsync<RtTagsItem>(session4, 
            RtEntityQueryOptions.Create());
        await session4.CommitTransactionAsync();

        Assert.Single(z.Items);
        Assert.Equal("test-stringarray", z.Items.First().Name);
        Assert.Equal("tag1", z.Items.First().Tags?.First());
        Assert.Equal("tag2", z.Items.First().Tags?.ElementAt(1));
        Assert.Equal("tag3", z.Items.First().Tags?.ElementAt(2));
        Assert.Equal("tag4", z.Items.First().Tags?.ElementAt(3));
    }
    
    [Fact]
    public async Task CreateAndQuery_StringArray_Replace()
    {
        await systemFixture.ClearCollectionAsync();
        var systemContext = systemFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var rtMenuItem = await tenantRepository.CreateTransientRtEntityAsync<RtTagsItem>();
        rtMenuItem.Name = "test-stringarray";
        rtMenuItem.Tags = new AttributeStringValueList(["tag1", "tag2"]);
        await tenantRepository.InsertOneRtEntityAsync(session, rtMenuItem);

        await session.CommitTransactionAsync();

        using var session2 = await tenantRepository.GetSessionAsync();
        session2.StartTransaction();
        var y = await tenantRepository.GetRtEntitiesByTypeAsync<RtTagsItem>(session2, 
            RtEntityQueryOptions.Create());
        await session2.CommitTransactionAsync();
        
        using var session3 = await tenantRepository.GetSessionAsync();
        session3.StartTransaction();
        var testItem = y.Items.First();
        testItem.Tags = new AttributeStringValueList(["tag1", "tag2", "tag3", "tag4"]);
        await tenantRepository.ReplaceOneRtEntityByIdAsync(session3, testItem.RtId, testItem);
        await session3.CommitTransactionAsync();
        
        using var session4 = await tenantRepository.GetSessionAsync();
        session4.StartTransaction();
        var z = await tenantRepository.GetRtEntitiesByTypeAsync<RtTagsItem>(session4, 
            RtEntityQueryOptions.Create());
        await session4.CommitTransactionAsync();

        Assert.Single(z.Items);
        Assert.Equal("test-stringarray", z.Items.First().Name);
        Assert.Equal("tag1", z.Items.First().Tags.First());
        Assert.Equal("tag2", z.Items.First().Tags.ElementAt(1));
        Assert.Equal("tag3", z.Items.First().Tags.ElementAt(2));
        Assert.Equal("tag4", z.Items.First().Tags.ElementAt(3));
    }
}
