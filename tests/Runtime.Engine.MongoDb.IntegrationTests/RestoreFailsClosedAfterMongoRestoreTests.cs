using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Once mongorestore wrote, every exit that does not finish the removal of the platform-only values drops the target
///     database (or, when the drop fails, keeps the claim); a mongorestore that never started leaves the target as it was.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreFailsClosedAfterMongoRestoreTests(SystemFixture systemFixture)
{
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:ZmFpbHMtY2xvc2VkLWFmdGVyLW1vbmdvcmVzdG9yZQ";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task ACancellationAfterMongoRestoreWrote_DropsTheRestoredDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var restoreCancellation = new CancellationTokenSource();
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                async (repositoryOps, restoreOptions) =>
                {
                    var writtenResult = await repositoryOps.ExecuteMongoRestoreAsync(restoreOptions,
                        TimeSpan.FromMinutes(5));
                    await restoreCancellation.CancelAsync();
                    return writtenResult;
                });

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true,
                cancellationToken: restoreCancellation.Token);

            Assert.False(restoreResult.Success);
            Assert.False(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken));
            Assert.Null(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
        });
    }

    [Fact]
    public async Task ARemovalAndADropThatBothFail_KeepTheClaimOnTheRestoredDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                (repositoryOps, restoreOptions) =>
                    repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5)),
                new RemoverThatCannotRemoveOrDrop(systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>()));

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.Null(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
            var keptMarker = await systemFixture.Provider!.GetRequiredService<TenantOwnershipStore>()
                .GetAsync(targetTenantId, cancellationToken);
            Assert.NotNull(keptMarker);
            Assert.Equal(OwnerSystemDatabaseName, keptMarker.OwnerSystemDatabaseName);
            Assert.True(keptMarker.Quarantined);
        });
    }

    [Fact]
    public async Task ARestoreWhoseDropFailed_QuarantinesTheTarget_SoAPlainAttachOfThisInstanceIsRefused()
    {
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var systemContext = systemFixture.GetSystemContext();
            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                (repositoryOps, restoreOptions) =>
                    repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5)),
                new RemoverThatCannotRemoveOrDrop(systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>()));
            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);
            Assert.False(restoreResult.Success);

            using var attachSession = await systemContext.GetAdminSessionAsync();
            attachSession.StartTransaction();
            await Assert.ThrowsAsync<TenantException>(() =>
                systemContext.AttachChildTenantAsync(attachSession, targetTenantId, targetTenantId));

            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
        });
    }

    [Fact]
    public async Task ACloneWhoseMongoRestoreMayStillRun_ReportsTheProcessFlagsOfTheRestore()
    {
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, _) =>
        {
            var backupService = RestoreBackupServiceFactory.Create(systemFixture, (_, _) =>
            {
                var unconfirmedKillResult = CommandResult.Failure("mongorestore was cancelled; the kill failed");
                unconfirmedKillResult.Cancelled = true;
                unconfirmedKillResult.ProcessMayStillRun = true;
                return Task.FromResult(unconfirmedKillResult);
            });

            var cloneResult = await backupService.CloneTenantToTempAsync(sourceTenantId, targetTenantId,
                targetTenantId);

            Assert.False(cloneResult.Success);
            Assert.True(cloneResult.Cancelled);
            Assert.True(cloneResult.ProcessMayStillRun);
            Assert.False(cloneResult.ProcessNeverStarted);
        });
    }

    [Fact]
    public async Task ACloneWhoseMongoRestoreNeverStarted_ReportsIt()
    {
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, _) =>
        {
            var backupService = RestoreBackupServiceFactory.Create(systemFixture, (_, _) =>
            {
                var notStartedResult = CommandResult.Failure("Failed to start process 'mongorestore'");
                notStartedResult.ProcessNeverStarted = true;
                return Task.FromResult(notStartedResult);
            });

            var cloneResult = await backupService.CloneTenantToTempAsync(sourceTenantId, targetTenantId,
                targetTenantId);

            Assert.False(cloneResult.Success);
            Assert.True(cloneResult.ProcessNeverStarted);
        });
    }

    [Fact]
    public async Task AMongoRestoreThatNeverStarted_LeavesAnExistingDatabaseAsItWas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var systemContext = systemFixture.GetSystemContext();
            await _support.CreateTenantAsync(targetTenantId);
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, targetTenantId);
                await session.CommitTransactionAsync();
            }

            var existingExecution = SealedExecution();
            var existingExecutions = _support.GetDatabase(targetTenantId)
                .GetCollection<BsonDocument>(ExecutionCollectionName);
            await existingExecutions.InsertOneAsync(existingExecution, cancellationToken: cancellationToken);
            var backupService = RestoreBackupServiceFactory.Create(systemFixture, (_, _) =>
            {
                var notStartedResult = CommandResult.Failure("Failed to start process 'mongorestore'");
                notStartedResult.ProcessNeverStarted = true;
                return Task.FromResult(notStartedResult);
            });

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.True(restoreResult.ProcessNeverStarted);
            Assert.True(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken));
            var executionAfter = await existingExecutions
                .Find(Builders<BsonDocument>.Filter.Eq("_id", existingExecution["_id"]))
                .SingleAsync(cancellationToken);
            Assert.Equal(existingExecution, executionAfter);
            Assert.Null(await systemFixture.Provider!.GetRequiredService<TenantOwnershipStore>()
                .GetAsync(targetTenantId, cancellationToken));
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
        });
    }

    private string OwnerSystemDatabaseName =>
        systemFixture.GetSystemContext().DatabaseName.Trim().ToLowerInvariant();

    private async Task WithSealedArchiveAsync(Func<string, string, string, Task> restoreScenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"fcsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"fctgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"fc_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(SealedExecution(), cancellationToken: cancellationToken);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);

            await restoreScenario(sourceTenantId, targetTenantId, archivePath);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await _support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    private static BsonDocument SealedExecution() => new()
    {
        { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", "System.Communication/PipelineExecution" },
        { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", SealedEnvelope } } }
    };

    private sealed class RemoverThatCannotRemoveOrDrop(IAdminRepositoryAccess adminRepositoryAccess)
        : RestoredPlatformOnlyValueRemover(adminRepositoryAccess, NullLogger<RestoredPlatformOnlyValueRemover>.Instance)
    {
        public override Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
            CancellationToken cancellationToken) =>
            throw new MongoException("removal failed in the test");

        public override Task<bool> DropRestoredDatabaseAsync(string databaseName) => Task.FromResult(false);
    }
}
