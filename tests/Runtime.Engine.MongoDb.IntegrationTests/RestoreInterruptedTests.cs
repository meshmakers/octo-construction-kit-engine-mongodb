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
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     A restore whose mongorestore was cancelled, may still run or threw drops its target database after the data was
///     written: no attachable database with a sealed caller is left, and the tenant is not attached.
/// </summary>
[Collection(SystemCollection.Name)]
public class RestoreInterruptedTests(SystemFixture systemFixture)
{
    private const string ExecutionCollectionName = "RtEntity_SystemCommunicationPipelineExecution";
    private const string SealedEnvelope = "enc:v2:k1:aW50ZXJydXB0ZWQtcmVzdG9yZS1zZWFsZWQtY2FsbGVy";

    private readonly RestoreArchiveSupport _support = new(systemFixture);

    [Fact]
    public async Task AMongoRestoreCancelledAfterWriting_DropsTheTargetDatabase()
    {
        var restoreResult = await RestoreWithInterruptionAsync(async (repositoryOps, _) =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            return await repositoryOps.ExecuteCommandAsync("sleep", ["120"], timeout: TimeSpan.FromMinutes(5),
                cancellationToken: cancellation.Token);
        });

        Assert.True(restoreResult.Cancelled);
        Assert.False(restoreResult.ProcessMayStillRun);
        Assert.Contains("was dropped", restoreResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMongoRestoreThatMayStillRun_DropsTheTargetDatabase()
    {
        var restoreResult = await RestoreWithInterruptionAsync((_, _) =>
        {
            var stillRunningResult = CommandResult.Failure("Process timed out after 120000ms");
            stillRunningResult.ProcessMayStillRun = true;
            return Task.FromResult(stillRunningResult);
        });

        Assert.True(restoreResult.ProcessMayStillRun);
        Assert.Contains("may still be running", restoreResult.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMongoRestoreThatThrowsAfterWriting_FailsTheRestore_AndDropsTheTargetDatabase()
    {
        var restoreResult = await RestoreWithInterruptionAsync(
            (_, _) => throw new InvalidOperationException("mongorestore pipe broke"));

        Assert.Contains("mongorestore pipe broke", restoreResult.Error, StringComparison.Ordinal);
    }

    private async Task<CommandResult> RestoreWithInterruptionAsync(
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

            var restoreResult = await BackupServiceWith(interruption).RestoreTenantAsync(targetTenantId,
                targetTenantId, archivePath, sourceTenantId, dropExistingTenant: false, attachTenant: true);

            Assert.False(restoreResult.Success);
            Assert.DoesNotContain(SealedEnvelope, restoreResult.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.False(await _support.DatabaseExistsAsync(targetTenantId, cancellationToken));
            Assert.Null(await systemContext.TryFindTenantContextAsync(targetTenantId));
            return restoreResult;
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, sourceTenantId);
            await _support.GetDatabase(targetTenantId).Client.DropDatabaseAsync(targetTenantId, CancellationToken.None);
            File.Delete(archivePath);
        }
    }

    private TenantBackupService BackupServiceWith(
        Func<IRepositoryOpsService, MongoRestoreOptions, Task<CommandResult>> interruption)
    {
        var provider = systemFixture.Provider!;
        return new TenantBackupService(systemFixture.GetSystemContext(),
            new MongoRestoreInterruptedAfterWrite(provider.GetRequiredService<IRepositoryOpsService>(), interruption),
            NullLogger<TenantBackupService>.Instance,
            provider.GetRequiredService<TenantOwnershipStore>(),
            provider.GetRequiredService<TenantDatabaseRegistrationLookup>(),
            provider.GetRequiredService<SystemDatabaseMarkerStore>(),
            new RestoredPlatformOnlyValueRemover(provider.GetRequiredService<IAdminRepositoryAccess>(),
                NullLogger<RestoredPlatformOnlyValueRemover>.Instance),
            provider.GetService<ITenantLifecycleStore>(), provider.GetService<IOptions<OctoSystemConfiguration>>());
    }

    /// <summary>Runs the real mongorestore (so the target holds the sealed caller), then reports the interruption.</summary>
    private sealed class MongoRestoreInterruptedAfterWrite(IRepositoryOpsService inner,
        Func<IRepositoryOpsService, MongoRestoreOptions, Task<CommandResult>> interruption) : IRepositoryOpsService
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
            return await interruption(inner, options);
        }

        public Task<CommandResult> ExecuteCommandAsync(string fileName, string arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);

        public Task<CommandResult> ExecuteCommandAsync(string fileName, IReadOnlyList<string> arguments,
            string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null) =>
            inner.ExecuteCommandAsync(fileName, arguments, workingDirectory, timeout, cancellationToken);
    }
}
