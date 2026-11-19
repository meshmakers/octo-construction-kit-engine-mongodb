using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A restore whose mongorestore was cancelled, may still run or threw drops its target database after the data was
///     written: no attachable database with a sealed caller is left, and the tenant is not attached. While mongorestore
///     may still run, the dropped target is claimed again for this instance and the claim is kept.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreInterruptedTests(SystemFixture systemFixture)
{
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:aW50ZXJydXB0ZWQtcmVzdG9yZS1zZWFsZWQtY2FsbGVy";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task AMongoRestoreCancelledAfterWriting_DropsTheTargetDatabase_AndReleasesTheClaim()
    {
        var interruptedRestore = await RestoreWithInterruptionAsync(async (repositoryOps, _) =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            return await repositoryOps.ExecuteCommandAsync("sleep", ["120"], timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellation.Token);
        });

        Assert.True(interruptedRestore.Result.Cancelled);
        Assert.False(interruptedRestore.Result.ProcessMayStillRun);
        Assert.Contains("was dropped", interruptedRestore.Result.Error, StringComparison.Ordinal);
        Assert.False(interruptedRestore.TargetDatabaseExists);
    }

    [Fact]
    public async Task AMongoRestoreThatMayStillRun_DropsTheTargetDatabase_AndKeepsTheClaim()
    {
        var interruptedRestore = await RestoreWithInterruptionAsync((_, _) =>
        {
            var stillRunningResult = CommandResult.Failure("Process timed out after 120000ms");
            stillRunningResult.ProcessMayStillRun = true;
            return Task.FromResult(stillRunningResult);
        });

        Assert.True(interruptedRestore.Result.ProcessMayStillRun);
        Assert.Contains("may still be running", interruptedRestore.Result.Error, StringComparison.Ordinal);
        Assert.Contains("claim", interruptedRestore.Result.Error, StringComparison.Ordinal);
        AssertOnlyTheKeptClaimIsLeft(interruptedRestore);
    }

    [Fact]
    public async Task AMongoRestoreThatThrowsAfterWriting_FailsTheRestore_DropsTheTargetDatabase_AndKeepsTheClaim()
    {
        var interruptedRestore = await RestoreWithInterruptionAsync(
            (_, _) => throw new InvalidOperationException("mongorestore pipe broke"));

        Assert.Contains("mongorestore pipe broke", interruptedRestore.Result.Error, StringComparison.Ordinal);
        AssertOnlyTheKeptClaimIsLeft(interruptedRestore);
    }

    private void AssertOnlyTheKeptClaimIsLeft(InterruptedRestore interruptedRestore)
    {
        var ownerSystemDatabaseName = systemFixture.GetSystemContext().DatabaseName.Trim().ToLowerInvariant();
        Assert.True(interruptedRestore.TargetDatabaseExists);
        Assert.All(interruptedRestore.TargetCollectionNames,
            collectionName => Assert.Equal(TenantOwnershipStore.CollectionName, collectionName));
        Assert.NotNull(interruptedRestore.TargetMarker);
        Assert.Equal(ownerSystemDatabaseName, interruptedRestore.TargetMarker.OwnerSystemDatabaseName);
    }

    private async Task<InterruptedRestore> RestoreWithInterruptionAsync(
        Func<IRepositoryOpsService, MongoRestoreOptions, Task<CommandResult>> interruption)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"intsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"inttgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"int_{Guid.NewGuid():N}.archive.gz");
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

            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                async (repositoryOps, restoreOptions) =>
                {
                    var writtenResult = await repositoryOps.ExecuteMongoRestoreAsync(restoreOptions,
                        TimeSpan.FromMinutes(5));
                    Assert.True(writtenResult.Success, $"The inner restore should write. Error: {writtenResult.Error}");
                    return await interruption(repositoryOps, restoreOptions);
                });
            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.DoesNotContain(SealedEnvelope, restoreResult.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
            var targetDatabase = _support.GetDatabase(targetTenantId);
            return new InterruptedRestore(restoreResult,
                await _support.DatabaseExistsAsync(targetTenantId, cancellationToken),
                await RestoreArchiveSupport.CollectionNamesAsync(targetDatabase, cancellationToken),
                await systemFixture.Provider!.GetRequiredService<TenantOwnershipStore>()
                    .GetAsync(targetTenantId, cancellationToken));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await _support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    private sealed record InterruptedRestore(CommandResult Result, bool TargetDatabaseExists,
        IReadOnlyList<string> TargetCollectionNames, TenantOwnershipRecord? TargetMarker);
}
