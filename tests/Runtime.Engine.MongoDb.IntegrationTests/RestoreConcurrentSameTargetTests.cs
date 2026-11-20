using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The claim of a restore is exclusive per target database while the target is not a registered tenant: a second
///     restore of the same target is refused at the claim, before its mongorestore runs. A restore that loses its claim
///     is covered by <see cref="RestoreClaimLostTests" />.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreConcurrentSameTargetTests(SystemFixture systemFixture)
{
    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task TwoConcurrentRestoresOfTheSameTarget_OnlyOneAttaches_AndNoSealedCallerSurvives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SealedExecutionArchive.RunAsync(systemFixture, _support, async (sourceTenantId, targetTenantId,
            archivePath) =>
        {
            var secondMongoRestoreWrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecondMongoRestore =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondMongoRestoreInvocations = 0;
            var secondBackupService = RestoreBackupServiceFactory.Create(systemFixture,
                async (repositoryOps, restoreOptions) =>
                {
                    Interlocked.Increment(ref secondMongoRestoreInvocations);
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
            var sealedCallersAfterTheFirstRestore =
                await SealedExecutionArchive.CountSealedCallersAsync(_support, targetTenantId, cancellationToken);
            releaseSecondMongoRestore.TrySetResult();
            var secondResult = await secondRestore!;

            Assert.True(firstResult.Success, $"The first restore should attach. Error: {firstResult.Error}");
            Assert.False(secondResult.Success);
            Assert.Equal(TenantException.DatabaseNameNotAvailable(targetTenantId).Message, secondResult.Error);
            Assert.Equal(0, Volatile.Read(ref secondMongoRestoreInvocations));
            Assert.NotNull(await systemFixture.GetSystemContext().TryFindTenantContextAsync(targetTenantId));
            Assert.Equal(0, sealedCallersAfterTheFirstRestore);
            Assert.Equal(0,
                await SealedExecutionArchive.CountSealedCallersAsync(_support, targetTenantId, cancellationToken));
        });
    }

    private IAdminRepositoryAccess AdminRepositoryAccess =>
        systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>();
}
