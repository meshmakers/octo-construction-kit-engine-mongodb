using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The system-database marker (AB#8672): the system database of an instance WITHOUT tenants has no
///     tenant registry rows, so before the marker the other instances on the server did not recognise it
///     and a restore could overwrite it. Instance A is the fixture, instance B its
///     <see cref="TwoInstanceFixture.Other" />; the fresh instance without tenants is created per test.
/// </summary>
[Collection(TwoInstanceCollection.Name)]
public class SystemDatabaseMarkerTests(TwoInstanceFixture fixture)
{
    private ISystemContext InstanceA => fixture.GetSystemContext();

    private ISystemContext InstanceB => fixture.Other.GetSystemContext();

    private string SystemDatabaseB => fixture.Other.SystemDatabaseName.ToLowerInvariant();

    private TenantDatabaseRegistrationLookup Lookup => fixture.GetService<TenantDatabaseRegistrationLookup>();

    private SystemDatabaseMarkerStore MarkerStore => fixture.GetService<SystemDatabaseMarkerStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private static string NewArchivePath() => Path.Combine(Path.GetTempPath(), $"sysmarker_{Guid.NewGuid():N}.gz");

    [Fact]
    public async Task FreshInstanceWithoutTenants_IsRecognisedAsSystemDatabase()
    {
        await using var fresh = new SystemFixture();
        await fresh.InitializeAsync();
        var freshSystemDatabase = fresh.SystemDatabaseName.ToLowerInvariant();

        using (var session = await fresh.GetSystemContext().GetAdminSessionAsync())
        {
            Assert.Equal(0L, (await fresh.GetSystemContext().GetAllTenantsAsync(session)).TotalCount);
        }

        Assert.NotNull(await MarkerStore.GetStampedAtUtcAsync(freshSystemDatabase, Ct));
        Assert.True(await Lookup.IsSystemDatabaseAsync(freshSystemDatabase, Ct));
        Assert.Empty(await Lookup.FindRegisteringSystemDatabasesAsync(freshSystemDatabase, Ct));
    }

    [Fact]
    public async Task Restore_IntoSystemDatabaseOfFreshInstanceWithoutTenants_IsRefused_EvenWithOverride()
    {
        var sourceTenantId = NewName("smsrc");
        var archivePath = NewArchivePath();
        await using var fresh = new SystemFixture();
        await fresh.InitializeAsync();
        var freshSystemDatabase = fresh.SystemDatabaseName.ToLowerInvariant();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            var backup = await InstanceA.BackupTenantAsync(sourceTenantId, archivePath);
            Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");

            var refused = await InstanceA.RestoreTenantAsync(NewName("smsys"), freshSystemDatabase, archivePath);
            var refusedWithOverride = await InstanceA.RestoreTenantAsync(NewName("smsys"), freshSystemDatabase,
                archivePath, overrideForeignOwnership: true);

            Assert.False(refused.Success);
            Assert.Contains("system database of another OctoMesh instance", refused.Error);
            Assert.False(refusedWithOverride.Success);
            Assert.Contains("cannot be overridden", refusedWithOverride.Error);
            Assert.True(await fresh.GetSystemContext().IsSystemTenantExistingAsync());
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task StartupStamp_OfAnInstanceWithoutMarker_IsIdempotent_AndTheRegistryRuleStaysTheFallback()
    {
        var tenantOfB = NewName("smtenantb");

        // B as it ran before the marker shipped: no marker, recognised only while it registers a tenant.
        await MarkerStore.RemoveAsync(SystemDatabaseB, Ct);
        await CreateTenantAsync(InstanceB, tenantOfB);
        try
        {
            Assert.Null(await MarkerStore.GetStampedAtUtcAsync(SystemDatabaseB, Ct));
            Assert.True(await Lookup.IsSystemDatabaseAsync(SystemDatabaseB, Ct));

            // The start of the new version (EnsureSystemCkModelAsync) stamps it, a second start keeps the stamp.
            await InstanceB.EnsureSystemCkModelAsync();
            var firstStamp = await MarkerStore.GetStampedAtUtcAsync(SystemDatabaseB, Ct);
            await InstanceB.EnsureSystemCkModelAsync();
            var secondStamp = await MarkerStore.GetStampedAtUtcAsync(SystemDatabaseB, Ct);

            Assert.NotNull(firstStamp);
            Assert.Equal(firstStamp, secondStamp);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceB, tenantOfB);
        }

        // Without any tenant B is still recognised, now by its marker.
        Assert.True(await Lookup.IsSystemDatabaseAsync(SystemDatabaseB, Ct));
    }

    [Fact]
    public async Task Restore_OfADumpCarryingTheSystemDatabaseMarker_RemovesItFromTheRestoredTenantDatabase()
    {
        var sourceTenantId = NewName("smsrc");
        var restoredTenantId = NewName("smrestored");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(InstanceA, sourceTenantId);
        try
        {
            // A dump of a system database carries its marker; the restored copy is a tenant database.
            await MarkerStore.StampIfAbsentAsync(sourceTenantId, sourceTenantId, Ct);
            var backup = await InstanceA.BackupTenantAsync(sourceTenantId, archivePath);
            Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");

            var result = await InstanceA.RestoreTenantAsync(restoredTenantId, restoredTenantId, archivePath);

            Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
            Assert.Null(await MarkerStore.GetStampedAtUtcAsync(restoredTenantId, Ct));
            Assert.False(await Lookup.IsSystemDatabaseAsync(restoredTenantId, Ct));
            Assert.NotNull(await InstanceA.TryFindTenantContextAsync(restoredTenantId));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(InstanceA, sourceTenantId);
            await ThrowawayTenant.DropAsync(InstanceA, restoredTenantId);
            DeleteQuietly(archivePath);
        }
    }

    private static async Task CreateTenantAsync(ISystemContext instance, string tenantId)
    {
        using var session = await instance.GetAdminSessionAsync();
        session.StartTransaction();
        await instance.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
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
