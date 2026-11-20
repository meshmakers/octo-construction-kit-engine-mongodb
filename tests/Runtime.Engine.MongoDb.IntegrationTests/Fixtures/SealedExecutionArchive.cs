using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     An archive of a throwaway tenant with one pipeline execution that holds a sealed caller, and the clean-up of the
///     source and target tenants and databases whatever the scenario does.
/// </summary>
internal static class SealedExecutionArchive
{
    public const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:Y29uY3VycmVudC1yZXN0b3Jlcy1zYW1lLXRhcmdldA";

    /// <summary>Runs <paramref name="restoreScenario" /> with the source tenant id, the target tenant id and the archive path.</summary>
    public static async Task RunAsync(SystemFixture systemFixture, RestoreArchiveSupport support,
        Func<string, string, string, Task> restoreScenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"ccsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"cctgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"cc_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await support.CreateTenantAsync(sourceTenantId);
            await support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", "System.Communication/PipelineExecution" },
                    { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", SealedEnvelope } } }
                }, cancellationToken: cancellationToken);
            await support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            await restoreScenario(sourceTenantId, targetTenantId, archivePath);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    public static async Task<long> CountSealedCallersAsync(RestoreArchiveSupport support, string databaseName,
        CancellationToken cancellationToken) =>
        await support.GetDatabase(databaseName).GetCollection<BsonDocument>(ExecutionCollectionName)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("attributes.sealedCaller"),
                cancellationToken: cancellationToken);
}
