using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

/// <summary>
///     AB#6538: the tenant index setup creates an index on <c>fs.files.metadata.rtEntityId</c>.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class GridFsOwnerIndexTests(ImportTestCkModelFixture fixture)
{
    private const string IndexName = "metadata_rtEntityId_1";

    [Fact]
    public async Task UpdateIndexes_CreatesOwnerIndexOnGridFsFiles_AndIsRepeatable()
    {
        var files = GetSystemMongoDatabase().GetCollection<BsonDocument>("fs.files");
        var systemContext = fixture.GetSystemContext();

        // Start from a database without the index (it may exist from an earlier run).
        if ((await ListIndexNamesAsync(files)).Contains(IndexName))
        {
            await files.Indexes.DropOneAsync(IndexName, TestContext.Current.CancellationToken);
        }

        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            await systemContext.UpdateIndexesAsync(adminSession);
        }

        var cursor = await files.Indexes.ListAsync(TestContext.Current.CancellationToken);
        var index = (await cursor.ToListAsync(TestContext.Current.CancellationToken))
            .Single(x => x["name"].AsString == IndexName);
        Assert.Equal(1, index["key"].AsBsonDocument["metadata.rtEntityId"].ToInt32());
        Assert.True(index["sparse"].ToBoolean());

        // Running the index update again must not fail and must not duplicate the index.
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            await systemContext.UpdateIndexesAsync(adminSession);
        }

        Assert.Equal(1, (await ListIndexNamesAsync(files)).Count(x => x == IndexName));
    }

    private static async Task<List<string>> ListIndexNamesAsync(IMongoCollection<BsonDocument> collection)
    {
        // The collection does not exist before the first upload; listing then yields nothing or an error.
        try
        {
            var cursor = await collection.Indexes.ListAsync(TestContext.Current.CancellationToken);
            return (await cursor.ToListAsync(TestContext.Current.CancellationToken)).Select(x => x["name"].AsString).ToList();
        }
        catch (MongoCommandException)
        {
            return [];
        }
    }

    private IMongoDatabase GetSystemMongoDatabase()
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        var client = new MongoClient(urlBuilder.ToMongoUrl());
        return client.GetDatabase(config.SystemDatabaseName);
    }
}
