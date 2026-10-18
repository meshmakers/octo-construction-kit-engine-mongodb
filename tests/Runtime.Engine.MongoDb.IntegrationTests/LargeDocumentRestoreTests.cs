using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#6981: a collection of large documents goes through the real <c>mongodump</c> / <c>mongorestore</c> path and
///     comes back complete. With the tool's own default batch of 1000 documents a message can end up over the server's
///     48,000,000 bytes once the documents average above ~48 KB (the restore of an EDA message collection failed with
///     <c>msgLen 48000084</c>); the restore now always passes <c>--batchSize</c>.
/// </summary>
[Collection(SystemCollection.Name)]
public class LargeDocumentRestoreTests(SystemFixture systemFixture)
{
    private const int DocumentCount = 2400;
    private const string CollectionName = "RtEntity_LargeMessage";

    // The tool fills a batch up to 48,000,000 bytes of documents and then adds its own envelope (84 bytes in the failure of
    // the tenant), so a batch of documents that sum to exactly 48,000,000 bytes is a message of 48,000,084. 480 documents of
    // 100,000 bytes (BSON size: 28 bytes of framing + the payload) aim at that boundary.
    private const int BoundaryDocumentBytes = 100_000;
    private const int BsonFramingBytes = 28;

    // This test does NOT reproduce the mongorestore 100.16.0 overshoot locally: with the tool's default of 1000 documents
    // (BatchSize = 1000) it also restored both generated data sets. The real failure came after 250,000 documents of 73 KB on
    // average (msgLen 48000084). The test guards that large documents restore with the explicit batch size; the regression proof
    // is the restore of the sbeg copy (AB#6981 acceptance criterion 3).

    /// <summary>Sizes spread around 73 KB with a multi-MB document every 37th, like the EDA messages of the failing tenant.</summary>
    [Fact]
    public Task DumpAndRestore_OfDocumentsAveragingOver70Kb_RestoresEveryDocument()
    {
        var random = new Random(6981);
        return RoundTripAsync(i => i % 37 == 0 ? random.Next(1_500_000, 2_200_000) : random.Next(56 * 1024, 90 * 1024), batchSize: null);
    }

    /// <summary>Documents of exactly the size at which a full batch lands on the 48,000,000 byte limit.</summary>
    [Fact]
    public Task DumpAndRestore_WhenABatchFillsTheMessageLimitExactly_RestoresEveryDocument()
        => RoundTripAsync(_ => BoundaryDocumentBytes - BsonFramingBytes, batchSize: null);

    private async Task RoundTripAsync(Func<int, int> payloadBytes, int? batchSize)
    {
        var ct = TestContext.Current.CancellationToken;
        var ops = systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();
        var databaseName = $"bigdoc_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), $"bigdoc_{Guid.NewGuid():N}");
        var archive = Path.Combine(directory, "dump.archive.gz");
        var database = GetDatabase(databaseName);
        Directory.CreateDirectory(directory);

        try
        {
            var totalBytes = await InsertDocumentsAsync(database, payloadBytes, ct);
            Assert.True(totalBytes / DocumentCount >= 70 * 1024, $"Average document size is {totalBytes / DocumentCount} bytes.");

            var dump = await ops.ExecuteMongoDumpAsync(MongoDumpOptions.ForArchive(databaseName, archive));
            Assert.True(dump.Success, $"Dump should succeed. Error: {dump.Error}");

            await database.Client.DropDatabaseAsync(databaseName, ct);

            var restore = await ops.ExecuteMongoRestoreAsync(
                new MongoRestoreOptions { Database = databaseName, Archive = archive, Gzip = true, Drop = true, BatchSize = batchSize },
                TimeSpan.FromMinutes(10), ct);

            Assert.True(restore.Success, $"Restore should succeed. Error: {restore.Error}");
            Assert.Contains("--batchSize=", restore.Command, StringComparison.Ordinal);
            var restored = await database.GetCollection<BsonDocument>(CollectionName)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            Assert.Equal(DocumentCount, restored);
        }
        finally
        {
            await database.Client.DropDatabaseAsync(databaseName, CancellationToken.None);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task<long> InsertDocumentsAsync(IMongoDatabase database, Func<int, int> payloadBytes, CancellationToken ct)
    {
        var collection = database.GetCollection<BsonDocument>(CollectionName);
        var random = new Random(1);
        long totalBytes = 0;
        var batch = new List<BsonDocument>();
        for (var i = 0; i < DocumentCount; i++)
        {
            // Random bytes do not compress away, so the dump is as large as the data.
            var payload = new byte[payloadBytes(i)];
            random.NextBytes(payload);
            totalBytes += payload.Length;
            batch.Add(new BsonDocument { { "_id", i }, { "payload", new BsonBinaryData(payload) } });
            if (batch.Count == 20)
            {
                await collection.InsertManyAsync(batch, cancellationToken: ct);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await collection.InsertManyAsync(batch, cancellationToken: ct);
        }

        return totalBytes;
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var config = systemFixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName);
    }
}
