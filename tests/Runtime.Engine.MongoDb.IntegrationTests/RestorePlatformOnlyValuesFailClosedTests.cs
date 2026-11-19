using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A restore that cannot remove the platform-only values safely fails and leaves no database that holds them: an
///     execution whose attributes an edited archive stores as an array, and a mongorestore that fails after writing.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestorePlatformOnlyValuesFailClosedTests(SystemFixture systemFixture)
{
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string ExecutionCkTypeId = "System.Communication/PipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:ZmFpbC1jbG9zZWQtc2VhbGVkLWNhbGxlci12YWx1ZQ";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    public static TheoryData<string> ArrayForms => ["keyValueDocuments", "keyValuePairs"];

    [Theory]
    [MemberData(nameof(ArrayForms))]
    public async Task AnExecutionWithAttributesStoredAsAnArray_FailsTheRestore_AndDropsTheRestoredDatabase(
        string arrayForm)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"arrsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"arrtgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"arr_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", ExecutionCkTypeId },
                    { "attributes", SealedCallerAsArray(arrayForm) }
                }, cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await systemContext.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.Contains("stores its attributes as 'array'", restoreResult.Error, StringComparison.Ordinal);
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

    [Fact]
    public async Task AMongoRestoreThatFailsAfterWriting_LeavesNoPlatformOnlyValueInTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"partsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"parttgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.archive.gz");
        var executionId = ObjectId.GenerateNewId();
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", executionId }, { "ckTypeId", ExecutionCkTypeId },
                    { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", SealedEnvelope } } }
                }, cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            var restoreResult = await BackupServiceWithMongoRestoreFailingAfterWrite().RestoreTenantAsync(
                targetTenantId, targetTenantId, archivePath, sourceTenantId, dropExistingTenant: false,
                attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
            var leftExecutions = await _support.GetDatabase(targetTenantId)
                .GetCollection<BsonDocument>(ExecutionCollectionName)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
            var leftExecution = Assert.Single(leftExecutions);
            Assert.False(leftExecution["attributes"].AsBsonDocument.Contains("sealedCaller"));
            Assert.Equal(1, leftExecution["attributes"]["status"].AsInt32);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await _support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    private static BsonArray SealedCallerAsArray(string arrayForm) => arrayForm == "keyValuePairs"
        ? [new BsonArray { "sealedCaller", SealedEnvelope }, new BsonArray { "status", 1 }]
        : [new BsonDocument { { "k", "sealedCaller" }, { "v", SealedEnvelope } }, new BsonDocument { { "k", "status" }, { "v", 1 } }];

    private TenantBackupService BackupServiceWithMongoRestoreFailingAfterWrite()
    {
        var provider = systemFixture.Provider!;
        return new TenantBackupService(systemFixture.GetSystemContext(),
            new MongoRestoreFailingAfterWrite(provider.GetRequiredService<IRepositoryOpsService>()),
            NullLogger<TenantBackupService>.Instance,
            provider.GetRequiredService<TenantOwnershipStore>(),
            provider.GetRequiredService<TenantDatabaseRegistrationLookup>(),
            provider.GetRequiredService<SystemDatabaseMarkerStore>(),
            new RestoredPlatformOnlyValueRemover(provider.GetRequiredService<IAdminRepositoryAccess>(),
                NullLogger<RestoredPlatformOnlyValueRemover>.Instance),
            provider.GetService<ITenantLifecycleStore>(), provider.GetService<IOptions<OctoSystemConfiguration>>());
    }

    private sealed class MongoRestoreFailingAfterWrite(IRepositoryOpsService inner) : IRepositoryOpsService
    {
        public Task<CommandResult> ExecuteMongoShellScriptAsync(string databaseName, string scriptPath,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoShellScriptAsync(databaseName, scriptPath, cancellationToken);

        public Task<CommandResult> ExecuteMongoShellCommandAsync(string databaseName, string command,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoShellCommandAsync(databaseName, command, cancellationToken);

        public Task<CommandResult> ExecuteMongoDumpAsync(MongoDumpOptions options, TimeSpan? timeout = null,
            CancellationToken? cancellationToken = null) =>
            inner.ExecuteMongoDumpAsync(options, timeout, cancellationToken);

        public async Task<CommandResult> ExecuteMongoRestoreAsync(MongoRestoreOptions options,
            TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
        {
            var writtenResult = await inner.ExecuteMongoRestoreAsync(options, timeout, cancellationToken);
            Assert.True(writtenResult.Success, $"The inner restore should write. Error: {writtenResult.Error}");
            return CommandResult.Failure("mongorestore timed out");
        }

        public Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);

        public Task<CommandResult> ExecuteCommandAsync(string fileName, IReadOnlyList<string> arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
    }
}
