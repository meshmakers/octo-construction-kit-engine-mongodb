using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A restore that no longer holds its own claim on the target (another operation replaced or removed it) fails
///     without attaching and without dropping the target: the database may hold what that operation wrote, so it is left
///     unchanged for the operator.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreClaimLostTests(SystemFixture systemFixture)
{
    private const string LeftToOperatorText = "no longer holds its claim";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task ARestoreWhoseClaimWasReplacedBeforeTheAttach_FailsAndLeavesTheTargetUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SealedExecutionArchive.RunAsync(systemFixture, _support, async (sourceTenantId, targetTenantId,
            archivePath) =>
        {
            var replacementClaimTime = TenantOwnershipStore.NewClaimTime().AddMinutes(-1);
            var remover = new RemoverWithHookAfterRemoval(AdminRepositoryAccess, async () =>
            {
                await OwnershipStore.RemoveAsync(targetTenantId, cancellationToken);
                await OwnershipStore.StampIfAbsentAndGetAsync(targetTenantId, targetTenantId, OwnerSystemDatabaseName,
                    cancellationToken, replacementClaimTime);
            });

            var restoreResult = await RestoreWithRealMongoRestoreAsync(sourceTenantId, targetTenantId, archivePath,
                remover);

            await AssertLeftToTheOperatorAsync(restoreResult, targetTenantId, cancellationToken);
            var markerAfterTheRestore = await OwnershipStore.GetAsync(targetTenantId, cancellationToken);
            Assert.NotNull(markerAfterTheRestore);
            Assert.Equal(replacementClaimTime, markerAfterTheRestore.AttachedAtUtc.ToUniversalTime());
        });
    }

    [Fact]
    public async Task ARestoreWhoseClaimWasReplacedByAClaimOfTheSameMillisecond_FailsAndLeavesTheTargetUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SealedExecutionArchive.RunAsync(systemFixture, _support, async (sourceTenantId, targetTenantId,
            archivePath) =>
        {
            var replacementClaimId = Guid.NewGuid().ToString("N");
            var remover = new RemoverWithHookAfterRemoval(AdminRepositoryAccess,
                () => ReplaceClaimInTheSameMillisecondAsync(targetTenantId, replacementClaimId, cancellationToken));

            var restoreResult = await RestoreWithRealMongoRestoreAsync(sourceTenantId, targetTenantId, archivePath,
                remover);

            await AssertLeftToTheOperatorAsync(restoreResult, targetTenantId, cancellationToken);
            var markerAfterTheRestore = await OwnershipStore.GetAsync(targetTenantId, cancellationToken);
            Assert.NotNull(markerAfterTheRestore);
            Assert.Equal(replacementClaimId, markerAfterTheRestore.ClaimId);
        });
    }

    [Fact]
    public async Task ARestoreWhoseClaimWasRemovedBeforeTheAttach_FailsAndLeavesTheTargetUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SealedExecutionArchive.RunAsync(systemFixture, _support, async (sourceTenantId, targetTenantId,
            archivePath) =>
        {
            var remover = new RemoverWithHookAfterRemoval(AdminRepositoryAccess,
                () => OwnershipStore.RemoveAsync(targetTenantId, cancellationToken));

            var restoreResult = await RestoreWithRealMongoRestoreAsync(sourceTenantId, targetTenantId, archivePath,
                remover);

            await AssertLeftToTheOperatorAsync(restoreResult, targetTenantId, cancellationToken);
            Assert.Null(await OwnershipStore.GetAsync(targetTenantId, cancellationToken));
        });
    }

    [Fact]
    public async Task AFailedMongoRestoreWhoseClaimWasReplaced_FailsAndLeavesTheTargetUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SealedExecutionArchive.RunAsync(systemFixture, _support, async (sourceTenantId, targetTenantId,
            archivePath) =>
        {
            var replacementClaimId = Guid.NewGuid().ToString("N");
            var backupService = RestoreBackupServiceFactory.Create(systemFixture,
                async (repositoryOps, restoreOptions) =>
                {
                    await repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5));
                    await ReplaceClaimInTheSameMillisecondAsync(targetTenantId, replacementClaimId, cancellationToken);
                    return CommandResult.Failure("mongorestore exited with code 1");
                });

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            await AssertLeftToTheOperatorAsync(restoreResult, targetTenantId, cancellationToken);
            var markerAfterTheRestore = await OwnershipStore.GetAsync(targetTenantId, cancellationToken);
            Assert.NotNull(markerAfterTheRestore);
            Assert.Equal(replacementClaimId, markerAfterTheRestore.ClaimId);
        });
    }

    private TenantOwnershipStore OwnershipStore => systemFixture.Provider!.GetRequiredService<TenantOwnershipStore>();

    private IAdminRepositoryAccess AdminRepositoryAccess =>
        systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>();

    private string OwnerSystemDatabaseName =>
        systemFixture.GetSystemContext().DatabaseName.Trim().ToLowerInvariant();

    private async Task<CommandResult> RestoreWithRealMongoRestoreAsync(string sourceTenantId, string targetTenantId,
        string archivePath, RemoverWithHookAfterRemoval remover)
    {
        var backupService = RestoreBackupServiceFactory.Create(systemFixture,
            (repositoryOps, restoreOptions) =>
                repositoryOps.ExecuteMongoRestoreAsync(restoreOptions, TimeSpan.FromMinutes(5)),
            remover);
        return await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath, sourceTenantId,
            dropExistingTenant: false, attachTenant: true);
    }

    private async Task ReplaceClaimInTheSameMillisecondAsync(string targetTenantId, string replacementClaimId,
        CancellationToken cancellationToken)
    {
        var runningClaim = await OwnershipStore.GetAsync(targetTenantId, cancellationToken);
        Assert.NotNull(runningClaim);
        var sameMillisecond = runningClaim.AttachedAtUtc.ToUniversalTime();
        await OwnershipStore.RemoveAsync(targetTenantId, cancellationToken);
        await OwnershipStore.ClaimForRestoreAsync(targetTenantId, targetTenantId, OwnerSystemDatabaseName,
            sameMillisecond, replacementClaimId, cancellationToken);
    }

    private async Task AssertLeftToTheOperatorAsync(CommandResult restoreResult, string targetTenantId,
        CancellationToken cancellationToken)
    {
        Assert.False(restoreResult.Success);
        Assert.Contains(LeftToOperatorText, restoreResult.Error);
        Assert.Null(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
        Assert.True(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken),
            "The target database must be left to the operator, not dropped");
    }
}
