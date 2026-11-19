using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A restore leaves out the values only the platform may hold: the secret defaults collection, the secret values of
///     an Application's secret default declarations and the sealed caller of a pipeline execution. The tenant's own
///     secrets come back unchanged.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestorePlatformOnlyValuesTests(SystemFixture systemFixture)
{
    private const string SecretDefaultCollectionName = "RtEntity_SystemCommunicationSecretDefault";
    private const string WorkloadCollectionName = "RtEntity_SystemCommunicationDeployableEntity";
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string ApplicationCkTypeId = "System.Communication/Application";
    private const string AdapterCkTypeId = "System.Communication/Adapter";
    private const string ExecutionCkTypeId = "System.Communication/PipelineExecution";
    private const string PlatformEnvelope = "enc:v2:k1:cGxhdGZvcm0tb25seS1zZWNyZXQtdmFsdWUtZm9yLXRlc3Rz";
    private const string OwnEnvelope = "enc:v2:k1:dGVuYW50LW93bi1zZWNyZXQtdmFsdWUtZm9yLXRoZS10ZXN0";
    private const string SealedEnvelope = "enc:v2:k1:c2VhbGVkLWNhbGxlci1lbnZlbG9wZS1mb3ItdGhlLXRlc3Rz";
    private const string LegacyCallerToken = "enc:v1:bGVnYWN5LWNhbGxlci1hY2Nlc3MtdG9rZW4tZm9yLXRlc3Q=";

    private static readonly ObjectId ApplicationId = ObjectId.GenerateNewId();
    private static readonly ObjectId AdapterId = ObjectId.GenerateNewId();
    private static readonly ObjectId ExecutionId = ObjectId.GenerateNewId();

    [Fact]
    public void PlatformOnlyValues_MirrorWhatRevealSecretRefuses_PlusTheSealedCaller()
    {
        Assert.Equal([SecretDefaultCollectionName], PlatformOnlyRestoreValues.LeftOutCollections);
        Assert.Equal([new PlatformOnlyRecordValue(ApplicationCkTypeId, "SecretDefaults", "SecretValue")],
            PlatformOnlyRestoreValues.RecordValues);
        Assert.Equal(
        [
            new PlatformOnlyAttributeValue(ExecutionCkTypeId, "SealedCaller"),
            new PlatformOnlyAttributeValue(ExecutionCkTypeId, "CallerAccessToken")
        ], PlatformOnlyRestoreValues.AttributeValues);
    }

    [Fact]
    public async Task RestoreOfAnArchiveWithPlatformOnlyValues_LeavesThemOut_AndKeepsEverythingElse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"restsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"resttgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"restplat_{Guid.NewGuid():N}.archive.gz");

        try
        {
            await CreateTenantAsync(sourceTenantId);
            var sourceDatabase = GetDatabase(sourceTenantId);
            await SeedPlatformOnlyAndOwnValuesAsync(sourceDatabase, cancellationToken);
            await DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
            var targetDatabase = GetDatabase(targetTenantId);
            var restoredCollectionNames = await (await targetDatabase.ListCollectionNamesAsync(
                cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
            Assert.DoesNotContain(SecretDefaultCollectionName, restoredCollectionNames);

            var application = await FindAsync(targetDatabase, WorkloadCollectionName, ApplicationId, cancellationToken);
            var declarations = application["attributes"]["secretDefaults"].AsBsonArray;
            var firstDeclaration = declarations[0]["attributes"].AsBsonDocument;
            Assert.False(firstDeclaration.Contains("secretValue"));
            Assert.Equal("ec-captcha-secret", firstDeclaration["key"].AsString);
            Assert.Equal("child-a", firstDeclaration["allowedTenantIds"][0].AsString);
            Assert.Equal("CAPTCHA secret", firstDeclaration["description"].AsString);
            Assert.False(declarations[1]["attributes"].AsBsonDocument.Contains("secretValue"));
            Assert.Equal("energy-community", application["attributes"]["name"].AsString);

            var adapter = await FindAsync(targetDatabase, WorkloadCollectionName, AdapterId, cancellationToken);
            Assert.Equal(OwnEnvelope, adapter["attributes"]["apiKey"]["e"].AsString);

            var execution = await FindAsync(targetDatabase, ExecutionCollectionName, ExecutionId, cancellationToken);
            Assert.False(execution["attributes"].AsBsonDocument.Contains("sealedCaller"));
            Assert.False(execution["attributes"].AsBsonDocument.Contains("callerAccessToken"));
            Assert.Equal(1, execution["attributes"]["status"].AsInt32);

            Assert.Equal(4, restoreResult.RemovedPlatformOnlyValues.Count);
            Assert.Contains($"{ApplicationCkTypeId}@{ApplicationId}.SecretDefaults[0].SecretValue",
                restoreResult.RemovedPlatformOnlyValues);
            Assert.Contains($"{ApplicationCkTypeId}@{ApplicationId}.SecretDefaults[1].SecretValue",
                restoreResult.RemovedPlatformOnlyValues);
            Assert.Contains($"{ExecutionCkTypeId}@{ExecutionId}.SealedCaller",
                restoreResult.RemovedPlatformOnlyValues);
            Assert.Contains($"{ExecutionCkTypeId}@{ExecutionId}.CallerAccessToken",
                restoreResult.RemovedPlatformOnlyValues);
            var everythingReported = string.Join(" ", restoreResult.RemovedPlatformOnlyValues) + restoreResult.Output;
            Assert.DoesNotContain("enc:v", everythingReported, StringComparison.Ordinal);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    [Fact]
    public async Task RestoreOfAnArchiveWithoutPlatformOnlyValues_RemovesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"restsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"resttgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"restplat_{Guid.NewGuid():N}.archive.gz");

        try
        {
            await CreateTenantAsync(sourceTenantId);
            await GetDatabase(sourceTenantId).GetCollection<BsonDocument>(WorkloadCollectionName)
                .InsertOneAsync(OwnSecretAdapter(), cancellationToken: cancellationToken);
            await DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
            Assert.Empty(restoreResult.RemovedPlatformOnlyValues);
            var adapter = await FindAsync(GetDatabase(targetTenantId), WorkloadCollectionName, AdapterId,
                cancellationToken);
            Assert.Equal(OwnEnvelope, adapter["attributes"]["apiKey"]["e"].AsString);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    private static async Task SeedPlatformOnlyAndOwnValuesAsync(IMongoDatabase database,
        CancellationToken cancellationToken)
    {
        await database.GetCollection<BsonDocument>(SecretDefaultCollectionName).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "ckTypeId", "System.Communication/SecretDefault" },
            { "attributes", new BsonDocument { { "name", "ec-captcha-secret" }, { "value", StoredSecret(PlatformEnvelope) } } }
        }, cancellationToken: cancellationToken);

        var declarationWithAllowedTenants = new BsonDocument
        {
            { "ckRecordId", "System.Communication/SecretDefaultProjection" },
            {
                "attributes", new BsonDocument
                {
                    { "key", "ec-captcha-secret" },
                    { "secretValue", StoredSecret(PlatformEnvelope) },
                    { "allowedTenantIds", new BsonArray { "child-a" } },
                    { "description", "CAPTCHA secret" }
                }
            }
        };
        var declarationWithLegacyValue = new BsonDocument
        {
            { "ckRecordId", "System.Communication/SecretDefaultProjection" },
            { "attributes", new BsonDocument { { "key", "maps-key" }, { "secretValue", "legacy-plaintext" } } }
        };
        var workloads = database.GetCollection<BsonDocument>(WorkloadCollectionName);
        await workloads.InsertOneAsync(new BsonDocument
        {
            { "_id", ApplicationId },
            { "ckTypeId", ApplicationCkTypeId },
            {
                "attributes", new BsonDocument
                {
                    { "name", "energy-community" },
                    { "secretDefaults", new BsonArray { declarationWithAllowedTenants, declarationWithLegacyValue } }
                }
            }
        }, cancellationToken: cancellationToken);
        await workloads.InsertOneAsync(OwnSecretAdapter(), cancellationToken: cancellationToken);

        await database.GetCollection<BsonDocument>(ExecutionCollectionName).InsertOneAsync(new BsonDocument
        {
            { "_id", ExecutionId },
            { "ckTypeId", ExecutionCkTypeId },
            {
                "attributes", new BsonDocument
                {
                    { "status", 1 },
                    { "sealedCaller", SealedEnvelope },
                    { "callerAccessToken", LegacyCallerToken }
                }
            }
        }, cancellationToken: cancellationToken);
    }

    private static BsonDocument OwnSecretAdapter() => new()
    {
        { "_id", AdapterId },
        { "ckTypeId", AdapterCkTypeId },
        { "attributes", new BsonDocument { { "name", "adapter" }, { "apiKey", StoredSecret(OwnEnvelope) } } }
    };

    private static BsonDocument StoredSecret(string envelope) => new()
    {
        { "_t", "OctoSecret" }, { "e", envelope }, { "t", new BsonDateTime(DateTime.UtcNow) }
    };

    private async Task CreateTenantAsync(string tenantId)
    {
        var systemContext = systemFixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DumpWithoutAnyExclusionAsync(string tenantId, string archivePath,
        CancellationToken cancellationToken)
    {
        var repositoryOps = systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();
        var dumpResult = await repositoryOps.ExecuteMongoDumpAsync(
            MongoDumpOptions.ForArchive(tenantId, archivePath), TimeSpan.FromMinutes(5), cancellationToken);
        Assert.True(dumpResult.Success, $"Dump should succeed. Error: {dumpResult.Error}");
    }

    private static Task<BsonDocument> FindAsync(IMongoDatabase database, string collectionName, ObjectId documentId,
        CancellationToken cancellationToken) =>
        database.GetCollection<BsonDocument>(collectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", documentId)).SingleAsync(cancellationToken);

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var configuration = systemFixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(configuration.DatabaseHost),
            Username = configuration.AdminUser,
            Password = configuration.AdminUserPassword,
            AuthenticationSource = configuration.AuthenticationDatabaseName,
            DatabaseName = configuration.AuthenticationDatabaseName,
            DirectConnection = configuration.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName);
    }
}
