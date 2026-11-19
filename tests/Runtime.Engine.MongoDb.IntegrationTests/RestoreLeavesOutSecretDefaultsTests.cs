using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The secret defaults never come back with a restore: not into the tenant the archive was taken from, not when an
///     edited archive puts them into another collection or under a type derived from them, and not through a clone,
///     whose result names what was left out. The tenant's own secrets come back.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreLeavesOutSecretDefaultsTests(SystemFixture systemFixture)
{
    private const string SecretDefaultCollectionName = "RtEntity_SystemCommunicationSecretDefault";
    private const string SecretDefaultCkTypeId = "System.Communication/SecretDefault";
    private const string WorkloadCollectionName = "RtEntity_SystemCommunicationDeployableEntity";
    private const string EditedCollectionName = "RtEntity_SystemCommunicationEdited";
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string ExecutionCkTypeId = "System.Communication/PipelineExecution";
    private const string PlatformEnvelope = "enc:v2:k1:c2VjcmV0LWRlZmF1bHQtbGVmdC1vdXQtdmFsdWU";
    private const string OwnEnvelope = "enc:v2:k1:b3duLXNlY3JldC1yZXN0b3Jlcy1yZWFkYWJsZQ";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task RestoreIntoTheSameTenant_LeavesOutTheSecretDefaults_AndKeepsTheOwnSecrets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var tenantId = $"ownrest_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"own_{Guid.NewGuid():N}.archive.gz");
        var adapterId = ObjectId.GenerateNewId();
        try
        {
            await _support.CreateTenantAsync(tenantId);
            var database = _support.GetDatabase(tenantId);
            await database.GetCollection<BsonDocument>(SecretDefaultCollectionName)
                .InsertOneAsync(SecretDefault(ObjectId.GenerateNewId(), SecretDefaultCkTypeId),
                    cancellationToken: cancellationToken);
            await database.GetCollection<BsonDocument>(WorkloadCollectionName)
                .InsertOneAsync(OwnSecretAdapter(adapterId), cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(tenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(tenantId, tenantId, archivePath, tenantId,
                dropExistingTenant: true, attachTenant: true);

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
            var restoredDatabase = _support.GetDatabase(tenantId);
            Assert.DoesNotContain(SecretDefaultCollectionName,
                await RestoreArchiveSupport.CollectionNamesAsync(restoredDatabase, cancellationToken));
            var adapter = await restoredDatabase.GetCollection<BsonDocument>(WorkloadCollectionName)
                .Find(Builders<BsonDocument>.Filter.Eq("_id", adapterId)).SingleAsync(cancellationToken);
            Assert.Equal(OwnEnvelope, adapter["attributes"]["apiKey"]["e"].AsString);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task AnEditedArchiveWithSecretDefaultsElsewhere_LosesThemToo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"sdsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"sdtgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"sd_{Guid.NewGuid():N}.archive.gz");
        var movedSecretDefaultId = ObjectId.GenerateNewId();
        var derivedSecretDefaultId = ObjectId.GenerateNewId();
        var unrelatedEntityId = ObjectId.GenerateNewId();
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            var sourceDatabase = _support.GetDatabase(sourceTenantId);
            await sourceDatabase.GetCollection<BsonDocument>(EditedCollectionName).InsertManyAsync(
            [
                SecretDefault(movedSecretDefaultId, SecretDefaultCkTypeId),
                SecretDefault(derivedSecretDefaultId, "CustomDefaults/SpecialDefault"),
                new BsonDocument
                {
                    { "_id", unrelatedEntityId }, { "ckTypeId", "System.Communication/Application" },
                    { "attributes", new BsonDocument("name", "kept") }
                }
            ], cancellationToken: cancellationToken);
            await sourceDatabase.GetCollection<BsonDocument>("CkTypeInheritance").InsertOneAsync(new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "ckModelId", "CustomDefaults-1.0.0" },
                { "baseCkTypeId", "System.Communication@4/SecretDefault-1" },
                { "inheritorCkTypeId", "CustomDefaults@1/SpecialDefault-1" }
            }, cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
            var restoredEntities = await _support.GetDatabase(targetTenantId)
                .GetCollection<BsonDocument>(EditedCollectionName)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
            var keptEntity = Assert.Single(restoredEntities);
            Assert.Equal(unrelatedEntityId, keptEntity["_id"].AsObjectId);
            var expectedRemovedPaths = new[]
            {
                $"{SecretDefaultCkTypeId}@{movedSecretDefaultId}",
                $"CustomDefaults/SpecialDefault@{derivedSecretDefaultId}"
            };
            Assert.Equal(expectedRemovedPaths.Order(StringComparer.Ordinal),
                restoreResult.RemovedPlatformOnlyValues.Order(StringComparer.Ordinal));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ACloneOfATenant_ReportsTheLeftOutValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"clsrc_{Guid.NewGuid():N}";
        var cloneTenantId = $"cltgt_{Guid.NewGuid():N}";
        var executionId = ObjectId.GenerateNewId();
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", executionId }, { "ckTypeId", ExecutionCkTypeId },
                    { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", PlatformEnvelope } } }
                }, cancellationToken: cancellationToken);

            var cloneResult = await systemContext.CloneTenantToTempAsync(sourceTenantId, cloneTenantId, cloneTenantId,
                TimeSpan.FromMinutes(5), cancellationToken);

            Assert.True(cloneResult.Success, $"Clone should succeed. Error: {cloneResult.Error}");
            Assert.Equal([$"{ExecutionCkTypeId}@{executionId}.SealedCaller"], cloneResult.RemovedPlatformOnlyValues);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, cloneTenantId);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
        }
    }

    private static BsonDocument SecretDefault(ObjectId secretDefaultId, string ckTypeId) => new()
    {
        { "_id", secretDefaultId }, { "ckTypeId", ckTypeId },
        { "attributes", new BsonDocument { { "name", "ec-captcha-secret" }, { "value", StoredSecret(PlatformEnvelope) } } }
    };

    private static BsonDocument OwnSecretAdapter(ObjectId adapterId) => new()
    {
        { "_id", adapterId }, { "ckTypeId", "System.Communication/Adapter" },
        { "attributes", new BsonDocument { { "name", "adapter" }, { "apiKey", StoredSecret(OwnEnvelope) } } }
    };

    private static BsonDocument StoredSecret(string envelope) => new()
    {
        { "_t", "OctoSecret" }, { "e", envelope }, { "t", new BsonDateTime(DateTime.UtcNow) }
    };
}
