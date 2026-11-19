using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using MongoDB.Bson;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Without the CK type inheritance of the restored database the removal cannot tell whether an execution's type derives
///     from PipelineExecution: a restore that failed before the inheritance was written drops its target, and a completed
///     restore without inheritance fails and drops its target when an execution of an unknown type is present.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreWithoutCkTypeInheritanceTests(SystemFixture systemFixture)
{
    private const string DerivedExecutionCollectionName = "RtEntity_CustomRunsSpecialExecution";
    private const string SealedEnvelope = "enc:v2:k1:d2l0aG91dC1jay10eXBlLWluaGVyaXRhbmNl";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task ANonZeroExitBeforeTheInheritanceWasWritten_DropsTheTargetDatabase()
    {
        var restoreOutcome = await RestoreWithDerivedExecutionOnlyAsync(
            CommandResult.Failure("mongorestore exited with code 1"));

        Assert.False(restoreOutcome.Result.Success);
        Assert.False(restoreOutcome.TargetDatabaseExists);
        Assert.False(restoreOutcome.TargetIsAttached);
    }

    [Fact]
    public async Task ACompletedRestoreWithoutInheritance_AndAnExecutionOfAnUnknownType_FailsAndDropsTheTargetDatabase()
    {
        var restoreOutcome = await RestoreWithDerivedExecutionOnlyAsync(new CommandResult { Success = true });

        Assert.False(restoreOutcome.Result.Success);
        Assert.False(restoreOutcome.TargetDatabaseExists);
        Assert.False(restoreOutcome.TargetIsAttached);
    }

    private async Task<RestoreOutcome> RestoreWithDerivedExecutionOnlyAsync(CommandResult mongoRestoreResult)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var systemContext = systemFixture.GetSystemContext();
        var sourceTenantId = $"nihsrc_{Guid.NewGuid():N}";
        var targetTenantId = $"nihtgt_{Guid.NewGuid():N}";
        var archivePath = Path.Combine(Path.GetTempPath(), $"nih_{Guid.NewGuid():N}.archive.gz");
        try
        {
            await _support.CreateTenantAsync(sourceTenantId);
            await _support.DumpWithoutAnyExclusionAsync(sourceTenantId, archivePath, cancellationToken);
            var backupService = RestoreBackupServiceFactory.Create(systemFixture, async (_, restoreOptions) =>
            {
                await _support.GetDatabase(restoreOptions.Database!)
                    .GetCollection<BsonDocument>(DerivedExecutionCollectionName)
                    .InsertOneAsync(DerivedExecution(), cancellationToken: cancellationToken);
                return mongoRestoreResult;
            });

            var restoreResult = await backupService.RestoreTenantAsync(targetTenantId, targetTenantId, archivePath,
                sourceTenantId, dropExistingTenant: false, attachTenant: true);

            return new RestoreOutcome(restoreResult,
                await _support.DatabaseExistsAsync(targetTenantId, cancellationToken),
                await systemContext.TryFindTenantContextAsync(targetTenantId) != null);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(systemContext, targetTenantId);
            await _support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    private static BsonDocument DerivedExecution() => new()
    {
        { "_id", ObjectId.GenerateNewId() }, { "ckTypeId", "CustomRuns/SpecialExecution" },
        { "attributes", new BsonDocument { { "status", 1 }, { "sealedCaller", SealedEnvelope } } }
    };

    private sealed record RestoreOutcome(CommandResult Result, bool TargetDatabaseExists, bool TargetIsAttached);
}
