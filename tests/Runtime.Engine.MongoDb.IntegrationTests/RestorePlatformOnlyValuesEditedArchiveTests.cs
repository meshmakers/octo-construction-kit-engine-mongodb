using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     An edited archive cannot carry a platform-only value past the restore: not under another spelling of its key, not
///     on a type derived from its type, and not when the removal fails. The secret default values of an Application
///     restore like every tenant secret.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestorePlatformOnlyValuesEditedArchiveTests(SystemFixture systemFixture)
{
    private const string EntityCollectionName = "RtEntity_SystemCommunicationEdited";
    private const string Envelope = "enc:v2:k1:ZWRpdGVkLWFyY2hpdmUtcGxhdGZvcm0tb25seS12YWx1ZQ";

    [Fact]
    public async Task KeysInAnotherCase_AreRemovedToo_AndTheSecretDefaultValueStays()
    {
        var applicationId = ObjectId.GenerateNewId();
        var executionId = ObjectId.GenerateNewId();
        var storedSecretDefaultValue = new BsonDocument
        {
            { "_t", "OctoSecret" }, { "e", Envelope }, { "t", new BsonDateTime(DateTime.UtcNow) }
        };
        var declaration = new BsonDocument
        {
            {
                "attributes",
                new BsonDocument { { "Key", "ec-captcha-secret" }, { "SECRETVALUE", storedSecretDefaultValue } }
            }
        };
        BsonDocument[] documents =
        [
            new()
            {
                { "_id", applicationId }, { "ckTypeId", "System.Communication/Application" },
                { "attributes", new BsonDocument { { "SecretDefaults", new BsonArray { declaration } } } }
            },
            new()
            {
                { "_id", executionId }, { "ckTypeId", "System.Communication/PipelineExecution" },
                { "attributes", new BsonDocument { { "SealedCaller", Envelope }, { "CallerAccessToken", Envelope }, { "status", 1 } } }
            }
        ];

        var (restoreResult, restoredDocuments) = await RestoreEditedArchiveAsync(documents, []);

        Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
        var application = Find(restoredDocuments, applicationId);
        var restoredDeclaration = application["attributes"]["SecretDefaults"][0]["attributes"].AsBsonDocument;
        var restoredSecretDefaultValue = ReadStoredSecret(restoredDeclaration["SECRETVALUE"]);
        Assert.Equal(RtSecretValueState.Protected, restoredSecretDefaultValue.State);
        Assert.Equal(Envelope, restoredSecretDefaultValue.Envelope);
        Assert.Equal("ec-captcha-secret", restoredDeclaration["Key"].AsString);
        var execution = Find(restoredDocuments, executionId);
        Assert.False(execution["attributes"].AsBsonDocument.Contains("SealedCaller"));
        Assert.False(execution["attributes"].AsBsonDocument.Contains("CallerAccessToken"));
        Assert.Equal(
        [
            $"System.Communication/PipelineExecution@{executionId}.SealedCaller",
            $"System.Communication/PipelineExecution@{executionId}.CallerAccessToken"
        ], restoreResult.RemovedPlatformOnlyValues);
    }

    private static RtSecretValue ReadStoredSecret(BsonValue storedValue)
    {
        using var reader = new BsonDocumentReader(new BsonDocument("value", storedValue));
        reader.ReadStartDocument();
        reader.ReadName("value");
        return new RtSecretValueSerializer().Deserialize(BsonDeserializationContext.CreateRoot(reader));
    }

    [Fact]
    public async Task ATypeDerivedFromPipelineExecution_LosesTheSealedCallerToo()
    {
        var executionId = ObjectId.GenerateNewId();
        BsonDocument[] documents =
        [
            new()
            {
                { "_id", executionId }, { "ckTypeId", "CustomRuns/SpecialExecution" },
                { "attributes", new BsonDocument { { "sealedCaller", Envelope }, { "status", 1 } } }
            }
        ];
        BsonDocument[] inheritanceRows =
        [
            new()
            {
                { "_id", ObjectId.GenerateNewId() }, { "ckModelId", "CustomRuns-1.0.0" },
                { "baseCkTypeId", "CustomRuns@1/RunBase-1" }, { "inheritorCkTypeId", "CustomRuns@1/SpecialExecution-1" }
            },
            new()
            {
                { "_id", ObjectId.GenerateNewId() }, { "ckModelId", "CustomRuns-1.0.0" },
                { "baseCkTypeId", "System.Communication@4/PipelineExecution-1" },
                { "inheritorCkTypeId", "CustomRuns@1/RunBase-1" }
            }
        ];

        var (restoreResult, restoredDocuments) = await RestoreEditedArchiveAsync(documents, inheritanceRows);

        Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");
        var execution = Find(restoredDocuments, executionId);
        Assert.False(execution["attributes"].AsBsonDocument.Contains("sealedCaller"));
        Assert.Equal(1, execution["attributes"]["status"].AsInt32);
        Assert.Equal([$"CustomRuns/SpecialExecution@{executionId}.SealedCaller"],
            restoreResult.RemovedPlatformOnlyValues);
    }

    [Fact]
    public async Task ARemovalThatFails_FailsTheRestore_AndDropsTheRestoredDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"editsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"edittgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"edited_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await CreateTenantAsync(sourceTenantId);
            await GetDatabase(sourceTenantId).GetCollection<BsonDocument>(EntityCollectionName)
                .InsertOneAsync(new BsonDocument { { "_id", 1 }, { "ckTypeId", "System.Communication/Application" } },
                    cancellationToken: cancellationToken);
            await DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await BackupServiceWithFailingRemover().RestoreTenantAsync(targetTenantId,
                targetTenantId, archivePath, sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            var databaseNames = await (await GetDatabase(targetTenantId).Client
                .ListDatabaseNamesAsync(cancellationToken)).ToListAsync(cancellationToken);
            Assert.DoesNotContain(targetTenantId, databaseNames);
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            File.Delete(archivePath);
        }
    }

    private async Task<(CommandResult RestoreResult, IReadOnlyList<BsonDocument> RestoredDocuments)> RestoreEditedArchiveAsync(
        IReadOnlyList<BsonDocument> entityDocuments, IReadOnlyList<BsonDocument> inheritanceRows)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"editsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"edittgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"edited_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await CreateTenantAsync(sourceTenantId);
            var sourceDatabase = GetDatabase(sourceTenantId);
            await sourceDatabase.GetCollection<BsonDocument>(EntityCollectionName)
                .InsertManyAsync(entityDocuments, cancellationToken: cancellationToken);
            if (inheritanceRows.Count > 0)
            {
                await sourceDatabase.GetCollection<BsonDocument>("CkTypeInheritance")
                    .InsertManyAsync(inheritanceRows, cancellationToken: cancellationToken);
            }

            await DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);
            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);
            var restoredDocuments = await GetDatabase(targetTenantId).GetCollection<BsonDocument>(EntityCollectionName)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
            return (restoreResult, restoredDocuments);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            File.Delete(archivePath);
        }
    }

    private TenantBackupService BackupServiceWithFailingRemover()
    {
        var provider = systemFixture.Provider!;
        return new TenantBackupService(systemFixture.GetSystemContext(),
            provider.GetRequiredService<IRepositoryOpsService>(), NullLogger<TenantBackupService>.Instance,
            provider.GetRequiredService<TenantOwnershipStore>(),
            provider.GetRequiredService<TenantDatabaseRegistrationLookup>(),
            provider.GetRequiredService<SystemDatabaseMarkerStore>(),
            new FailingRemover(provider.GetRequiredService<IAdminRepositoryAccess>()),
            provider.GetService<ITenantLifecycleStore>(), provider.GetService<IOptions<OctoSystemConfiguration>>());
    }

    private static BsonDocument Find(IReadOnlyList<BsonDocument> documents, ObjectId documentId) =>
        documents.Single(document => document["_id"] == documentId);

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

    private sealed class FailingRemover(IAdminRepositoryAccess adminRepositoryAccess)
        : RestoredPlatformOnlyValueRemover(adminRepositoryAccess, NullLogger<RestoredPlatformOnlyValueRemover>.Instance)
    {
        public override Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
            CancellationToken cancellationToken) =>
            throw new MongoException("connection lost while removing platform-only values");
    }
}
