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
///     The claim of a restore is exclusive per target database while the target is not a registered tenant: a second
///     restore of the same target is refused, and a restore whose claim was replaced before the attach drops the target
///     and fails instead of attaching what another restore wrote.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreConcurrentSameTargetTests(SystemFixture systemFixture)
{
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:Y29uY3VycmVudC1yZXN0b3Jlcy1zYW1lLXRhcmdldA";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task TwoConcurrentRestoresOfTheSameTarget_OnlyOneAttaches_AndNoSealedCallerSurvives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var secondMongoRestoreWrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecondMongoRestore =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondBackupService = RestoreBackupServiceFactory.Create(systemFixture,
                async (repositoryOps, restoreOptions) =>
                {
                    await repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5));
                    secondMongoRestoreWrote.TrySetResult();
                    await releaseSecondMongoRestore.Task;
                    return CommandResult.Failure("mongorestore exited with code 1");
                });
            Task<CommandResult>? secondRestore = null;
            var firstRemover = new RemoverWithHookAfterRemoval(AdminRepositoryAccess, async () =>
            {
                secondRestore = secondBackupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                    sourceTenantId, dropExistingTenant: false, attachTenant: true);
                await Task.WhenAny(secondRestore, secondMongoRestoreWrote.Task);
            });
            var firstBackupService = RestoreBackupServiceFactory.Create(systemFixture,
                (repositoryOps, restoreOptions) =>
                    repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5)),
                firstRemover);

            var firstResult = await firstBackupService.RestoreTenantAsync(targetTenantId, targetTenantId,
                archivePath, sourceTenantId, dropExistingTenant: false, attachTenant: true);
            var sealedCallersAfterTheFirstRestore = await CountSealedCallersAsync(targetTenantId, cancellationToken);
            releaseSecondMongoRestore.TrySetResult();
            var secondResult = await secondRestore!;

            Assert.True(firstResult.Success, $"The first restore should attach. Error: {firstResult.Error}");
            Assert.False(secondResult.Success);
            Assert.NotNull(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
            Assert.Equal(0, sealedCallersAfterTheFirstRestore);
            Assert.Equal(0, await CountSealedCallersAsync(targetTenantId, cancellationToken));
        });
    }

    [Fact]
    public async Task ARestoreWhoseClaimWasReplacedBeforeTheAttach_DropsTheTarget_AndFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WithSealedArchiveAsync(async (sourceTenantId, targetTenantId, archivePath) =>
        {
            var ownershipStore = systemFixture.Provider!.GetRequiredService<TenantOwnershipStore>();
            var remover = new RemoverWithHookAfterRemoval(AdminRepositoryAccess, async () =>
            {
                await ownershipStore.RemoveAsync(targetTenantId, cancellationToken);
                await ownershipStore.StampIfAbsentAndGetAsync(targetTenantId, targetTenantId, OwnerSystemDatabaseName,
                    cancellationToken, TenantOwnershipStore.NewClaimTime().AddMinutes(-1));
            });
            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                (repositoryOps, restoreOptions) =>
                    repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5)),
                remover);

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.Null(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
            Assert.False(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken));
        });
    }

    private IAdminRepositoryAccess AdminRepositoryAccess =>
        systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>();

    private string OwnerSystemDatabaseName =>
        systemFixture.GetSystemContext().DatabaseName.Trim().ToLowerInvariant();

    private async Task<long> CountSealedCallersAsync(string databaseName, CancellationToken cancellationToken) =>
        await _support.GetDatabase(databaseName).GetCollection<BsonDocument>(ExecutionCollectionName)
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("attributes.sealedCaller"),
                cancellationToken: cancellationToken);

    private async Task WithSealedArchiveAsync(Func<string, string, string, Task> restoreScenario)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"ccsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"cctgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"cc_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.GetDatabase(sourceTenantId).GetCollection<BsonDocument>(ExecutionCollectionName)
                .InsertOneAsync(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", "System.Communication/PipelineExecution" },
                    { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", SealedEnvelope } } }
                }, cancellationToken: cancellationToken);
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

    private sealed class RemoverWithHookAfterRemoval(IAdminRepositoryAccess adminRepositoryAccess,
        Func<Task> afterRemoval)
        : RestoredPlatformOnlyValueRemover(adminRepositoryAccess, NullLogger<RestoredPlatformOnlyValueRemover>.Instance)
    {
        public override async Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
            CancellationToken cancellationToken)
        {
            var removedPaths = await base.RemoveAsync(databaseName, cancellationToken);
            await afterRemoval();
            return removedPaths;
        }
    }
}
