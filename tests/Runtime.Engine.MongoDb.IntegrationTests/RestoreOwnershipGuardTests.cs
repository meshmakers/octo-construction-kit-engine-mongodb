using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Fixture for the restore ownership guard tests (AB#8672): its own system database (so the
///     destructive restore path cannot take out the shared system tenant) plus a log recorder for the
///     override assertion.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class RestoreOwnershipGuardFixture : SystemFixture
{
    public RestoreOwnershipGuardFixture()
    {
        Services.AddSingleton<ILoggerProvider>(Logs);
    }

    public RecordingLoggerProvider Logs { get; } = new();
}

[CollectionDefinition(Name)]
public class RestoreOwnershipGuardCollection : ICollectionFixture<RestoreOwnershipGuardFixture>
{
    public const string Name = "RestoreOwnershipGuard";
}

/// <summary>
///     A tenant restore must never overwrite a database that belongs to another OctoMesh instance on the
///     same MongoDB server (AB#8672). The "other instance" is simulated by the ownership marker (AB#4945)
///     with a foreign owner system database name — exactly what another instance's create or attach
///     leaves in the database.
/// </summary>
[Collection(RestoreOwnershipGuardCollection.Name)]
public class RestoreOwnershipGuardTests(RestoreOwnershipGuardFixture fixture)
{
    private const string ForeignInstance = "octosystemotherinstance";

    private ISystemContext SystemContext => fixture.GetSystemContext();

    private TenantOwnershipStore OwnershipStore => fixture.GetService<TenantOwnershipStore>();

    private string OwnInstanceIdentity =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value.SystemDatabaseName.Trim().ToLowerInvariant();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static string NewArchivePath() => Path.Combine(Path.GetTempPath(), $"rstguard_{Guid.NewGuid():N}.gz");

    [Fact]
    public async Task Restore_IntoDatabaseOwnedByAnotherInstance_IsRefusedNamingTheOwner_AndKeepsItsData()
    {
        var sourceTenantId = NewName("rgsrc");
        var foreignTenantId = NewName("rgforeign");
        var restoredTenantId = NewName("rgtarget");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceTenantId);
        await CreateForeignOwnedDatabaseAsync(foreignTenantId);
        try
        {
            await BackupAsync(sourceTenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(restoredTenantId, foreignTenantId, archivePath);

            Assert.False(result.Success);
            Assert.Contains("another OctoMesh instance", result.Error);
            Assert.Contains(ForeignInstance, result.Error);
            Assert.Contains(foreignTenantId, result.Error);

            // Nothing was dropped or attached: the foreign marker (and with it the database) is untouched.
            var marker = await OwnershipStore.GetAsync(foreignTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(ForeignInstance, marker!.OwnerSystemDatabaseName);
            Assert.True(await SystemContext.IsDatabaseExistingAsync(foreignTenantId));
            Assert.Null(await SystemContext.TryFindTenantContextAsync(restoredTenantId));
        }
        finally
        {
            await ReleaseForeignDatabaseAsync(foreignTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoDatabaseOwnedByAnotherInstance_ExistingTenantOfThisInstanceIsNotDropped()
    {
        // dropExistingTenant is destructive itself, so the guard must refuse BEFORE it runs.
        var tenantId = NewName("rgkeep");
        var foreignTenantId = NewName("rgforeign");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(tenantId);
        await CreateForeignOwnedDatabaseAsync(foreignTenantId);
        try
        {
            await BackupAsync(tenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(tenantId, foreignTenantId, archivePath,
                dropExistingTenant: true);

            Assert.False(result.Success);
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(tenantId));
        }
        finally
        {
            await ReleaseForeignDatabaseAsync(foreignTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, tenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoNewDatabase_Succeeds_AndThisInstanceOwnsTheResult()
    {
        var sourceTenantId = NewName("rgsrc");
        var restoredTenantId = NewName("rgnew");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceTenantId);
        try
        {
            await BackupAsync(sourceTenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(restoredTenantId));
            var marker = await OwnershipStore.GetAsync(restoredTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
            Assert.Equal(restoredTenantId, marker.TenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(SystemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_OfADumpTakenOnAnotherInstance_IntoNewDatabase_AttachesAndRestampsTheOwner()
    {
        // The archive carries the marker of the instance it was dumped from. Without removing it, the
        // attach after the restore would refuse the restored database as foreign-owned.
        var sourceTenantId = NewName("rgdump");
        var restoredTenantId = NewName("rgimport");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceTenantId);
        try
        {
            await OwnershipStore.StampAsync(sourceTenantId, sourceTenantId, ForeignInstance, Ct);
            await BackupAsync(sourceTenantId, archivePath);
            await OwnershipStore.StampAsync(sourceTenantId, sourceTenantId, OwnInstanceIdentity, Ct);

            var result = await SystemContext.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(restoredTenantId));
            var marker = await OwnershipStore.GetAsync(restoredTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(SystemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_OverOwnExistingTenant_Succeeds()
    {
        var tenantId = NewName("rgown");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(tenantId);
        try
        {
            await BackupAsync(tenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                dropExistingTenant: true);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(tenantId));
            var marker = await OwnershipStore.GetAsync(tenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(SystemContext, tenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoOwnDetachedDatabase_Succeeds()
    {
        // A detached database of this instance carries no marker (detach removes it) and no registry
        // row: restoring into it is a restore into an unowned database and works as before.
        var sourceTenantId = NewName("rgsrc");
        var detachedTenantId = NewName("rgdetached");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceTenantId);
        await CreateTenantAsync(detachedTenantId);
        try
        {
            await DetachTenantAsync(detachedTenantId);
            await BackupAsync(sourceTenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(detachedTenantId, detachedTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(detachedTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(SystemContext, sourceTenantId);
            await ThrowawayTenant.DropAsync(SystemContext, detachedTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task Restore_IntoDatabaseOwnedByAnotherInstance_WithOverride_Succeeds_AndLogsTheOverride()
    {
        var sourceTenantId = NewName("rgsrc");
        var foreignTenantId = NewName("rgoverride");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceTenantId);
        await CreateForeignOwnedDatabaseAsync(foreignTenantId);
        try
        {
            await BackupAsync(sourceTenantId, archivePath);

            var result = await SystemContext.RestoreTenantAsync(foreignTenantId, foreignTenantId, archivePath,
                overrideForeignOwnership: true);

            Assert.True(result.Success, $"Restore with override should succeed. Error: {result.Error}");
            Assert.NotNull(await SystemContext.TryFindTenantContextAsync(foreignTenantId));
            var marker = await OwnershipStore.GetAsync(foreignTenantId, Ct);
            Assert.NotNull(marker);
            Assert.Equal(OwnInstanceIdentity, marker!.OwnerSystemDatabaseName);

            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "OWNERSHIP OVERRIDE", foreignTenantId,
                ForeignInstance));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(SystemContext, sourceTenantId);
            await ReleaseForeignDatabaseAsync(foreignTenantId);
            DeleteQuietly(archivePath);
        }
    }

    private async Task CreateTenantAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DetachTenantAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.DetachChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task BackupAsync(string tenantId, string archivePath)
    {
        var backup = await SystemContext.BackupTenantAsync(tenantId, archivePath);
        Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");
    }

    /// <summary>
    ///     A tenant database of "another instance": created here, detached, then claimed by the foreign
    ///     owner — the state another instance's attach leaves on the shared server.
    /// </summary>
    private async Task CreateForeignOwnedDatabaseAsync(string tenantId)
    {
        await CreateTenantAsync(tenantId);
        await DetachTenantAsync(tenantId);
        await OwnershipStore.StampAsync(tenantId, tenantId, ForeignInstance, Ct);
    }

    /// <summary>Releases the simulated foreign claim so the physical database can be dropped.</summary>
    private async Task ReleaseForeignDatabaseAsync(string tenantId)
    {
        try
        {
            if (await SystemContext.TryFindTenantContextAsync(tenantId) == null &&
                await SystemContext.IsDatabaseExistingAsync(tenantId))
            {
                await OwnershipStore.RemoveAsync(tenantId, Ct);
                using var session = await SystemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }
        }
        catch (Exception)
        {
            // Cleanup only — must not mask the assertion failure.
        }

        await ThrowawayTenant.DropAsync(SystemContext, tenantId);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup only.
        }
    }
}
