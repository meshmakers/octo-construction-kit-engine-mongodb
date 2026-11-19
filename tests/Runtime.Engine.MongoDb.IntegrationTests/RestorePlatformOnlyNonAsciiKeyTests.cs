using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     An attribute key spelled with a non-ASCII letter that upper-cases to an ASCII one (U+017F, the long s, becomes
///     'S') is read by the engine as the ASCII key. The server-side candidate search cannot match such a key ignoring case,
///     so a restore fails closed on any non-ASCII attribute name of a pipeline execution.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestorePlatformOnlyNonAsciiKeyTests(SystemFixture systemFixture)
{
    private const string LongSSealedCallerKey = "ſealedCaller";
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string ExecutionCkTypeId = "System.Communication/PipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:bG9uZy1zLXNlYWxlZC1jYWxsZXItdmFsdWU";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public void TheEngineReadsALongSKeyAsSealedCaller()
    {
        var storedAttributes = new BsonDocument { { LongSSealedCallerKey, SealedEnvelope } };

        var readAttributes = ReadAttributes(storedAttributes);

        Assert.Equal(SealedEnvelope, readAttributes["SealedCaller"]);
    }

    [Fact]
    public async Task AnExecutionWithANonAsciiAttributeName_FailsTheRestore_AndDropsTheRestoredDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"asciisrc_{Guid.NewGuid():N}";
        var targetTenantId = $"asciitgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"ascii_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", ExecutionCkTypeId },
                    { "attributes", new BsonDocument { { LongSSealedCallerKey, SealedEnvelope }, { "status", 1 } } }
                }, cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.Contains("attribute name that is not ASCII", restoreResult.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(SealedEnvelope, restoreResult.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.False(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken));
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            File.Delete(archivePath);
        }
    }

    private static Dictionary<string, object?> ReadAttributes(BsonDocument storedAttributes)
    {
        using var reader = new BsonDocumentReader(storedAttributes);
        return new RtAttributeDictionarySerializer().Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }
}
