using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(SystemCollection.Name)]
public class TenantBackupServiceTests(SystemFixture systemFixture)
{
    [Fact]
    public async Task BackupTenant_WithValidTenant_Succeeds()
    {
        var systemContext = systemFixture.GetSystemContext();

        var testTenantId = $"backuptest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create a child tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 2) Backup the tenant
            var backupResult = await systemContext.BackupTenantAsync(testTenantId, archivePath);

            // 3) Verify
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");
            Assert.True(File.Exists(archivePath), "Backup archive should exist");
        }
        finally
        {
            // Cleanup
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                    if (tenantExists)
                    {
                        await systemContext.DropChildTenantAsync(session, testTenantId);
                    }
                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task BackupTenant_WithDetachOption_DetachesTenant()
    {
        var systemContext = systemFixture.GetSystemContext();

        var testTenantId = $"backuptest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create a child tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 2) Backup with detach option
            var backupResult = await systemContext.BackupTenantAsync(testTenantId, archivePath, detachTenant: true);

            // 3) Verify backup succeeded
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");
            Assert.True(File.Exists(archivePath), "Backup archive should exist");

            // 4) Verify tenant is detached (should not be found)
            var tenantContext = await systemContext.TryFindTenantContextAsync(testTenantId);
            Assert.Null(tenantContext);
        }
        finally
        {
            // Cleanup - drop the database directly since tenant is detached
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                    if (tenantExists)
                    {
                        await systemContext.DropChildTenantAsync(session, testTenantId);
                    }
                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task RestoreTenant_WithValidArchive_Succeeds()
    {
        var systemContext = systemFixture.GetSystemContext();

        var testTenantId = $"restoretest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create a child tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 2) Backup the tenant
            var backupResult = await systemContext.BackupTenantAsync(testTenantId, archivePath);
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");

            // 3) Drop the tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 4) Restore the tenant
            var restoreResult = await systemContext.RestoreTenantAsync(
                testTenantId, testDatabaseName, archivePath,
                dropExistingTenant: true, attachTenant: true);

            // 5) Verify restore succeeded
            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            // 6) Verify tenant is attached and accessible
            var restoredTenantContext = await systemContext.GetChildTenantContextAsync(testTenantId);
            Assert.NotNull(restoredTenantContext);
            Assert.Equal(testTenantId.ToLower(), restoredTenantContext.TenantId);
            Assert.Equal(testDatabaseName.ToLower(), restoredTenantContext.DatabaseName);
        }
        finally
        {
            // Cleanup
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                    if (tenantExists)
                    {
                        await systemContext.DropChildTenantAsync(session, testTenantId);
                    }
                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task RestoreTenant_WithoutAttachOption_DoesNotAttachTenant()
    {
        var systemContext = systemFixture.GetSystemContext();

        var testTenantId = $"restoretest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create a child tenant and backup
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            var backupResult = await systemContext.BackupTenantAsync(testTenantId, archivePath);
            Assert.True(backupResult.Success);

            // 2) Drop the tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 3) Restore without attaching
            var restoreResult = await systemContext.RestoreTenantAsync(
                testTenantId, testDatabaseName, archivePath,
                dropExistingTenant: true, attachTenant: false);

            // 4) Verify restore succeeded
            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            // 5) Verify tenant is NOT attached (should not be found)
            var tenantContext = await systemContext.TryFindTenantContextAsync(testTenantId);
            Assert.Null(tenantContext);
        }
        finally
        {
            // Cleanup
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                    if (tenantExists)
                    {
                        await systemContext.DropChildTenantAsync(session, testTenantId);
                    }
                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task BackupTenant_WithNonExistentTenant_Fails()
    {
        var systemContext = systemFixture.GetSystemContext();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            var result = await systemContext.BackupTenantAsync("nonexistent_tenant", archivePath);

            Assert.False(result.Success);
            Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task RestoreTenant_WithNonExistentArchive_Fails()
    {
        var systemContext = systemFixture.GetSystemContext();
        var testTenantId = $"restoretest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var nonExistentArchive = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.tar.gz");

        var result = await systemContext.RestoreTenantAsync(
            testTenantId, testDatabaseName, nonExistentArchive);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // AB#4367 — restoring an archive under a DIFFERENT database name without passing the
    // source database name (the Refinery Studio restore path) must auto-detect the source
    // from the archive prelude and restore the data instead of silently restoring nothing.

    [Fact]
    public async Task RestoreTenant_ToDifferentDatabaseName_WithoutSourceName_RestoresDocuments()
    {
        var systemContext = systemFixture.GetSystemContext();

        var sourceTenantId = $"renamesrc_{Guid.NewGuid():N}";
        var sourceDatabaseName = sourceTenantId.ToLower();
        var targetTenantId = $"renametgt_{Guid.NewGuid():N}";
        var targetDatabaseName = targetTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create a source tenant (its database carries CkModel + system collections)
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceDatabaseName, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            // 2) Backup the source tenant
            var backupResult = await systemContext.BackupTenantAsync(sourceTenantId, archivePath);
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");

            // 3) Restore under a different tenant id AND database name, without sourceDatabaseName
            var restoreResult = await systemContext.RestoreTenantAsync(
                targetTenantId, targetDatabaseName, archivePath,
                dropExistingTenant: true, attachTenant: true);

            // 4) The restore must succeed and the target database must actually contain the data
            //    (attach fails when mongorestore restored nothing, because the database never exists)
            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            var restoredContext = await systemContext.GetChildTenantContextAsync(targetTenantId);
            Assert.NotNull(restoredContext);
            Assert.Equal(targetDatabaseName, restoredContext.DatabaseName);
        }
        finally
        {
            await TryDropTenant(systemContext, targetTenantId);
            await TryDropTenant(systemContext, sourceTenantId);
        }
    }

    [Fact]
    public async Task RestoreTenant_WithWrongExplicitSourceName_FailsWithDescriptiveError_AndKeepsExistingTarget()
    {
        var systemContext = systemFixture.GetSystemContext();

        var sourceTenantId = $"renamesrc_{Guid.NewGuid():N}";
        var sourceDatabaseName = sourceTenantId.ToLower();
        var targetTenantId = $"renametgt_{Guid.NewGuid():N}";
        var targetDatabaseName = targetTenantId.ToLower();
        var archivePath = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}.tar.gz");

        try
        {
            // 1) Create source tenant + backup
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceDatabaseName, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            var backupResult = await systemContext.BackupTenantAsync(sourceTenantId, archivePath);
            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");

            // 2) Create the target tenant that a doomed restore must NOT destroy
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, targetDatabaseName, targetTenantId);
                await session.CommitTransactionAsync();
            }

            // 3) Restore with a source database name that does not match the archive
            var restoreResult = await systemContext.RestoreTenantAsync(
                targetTenantId, targetDatabaseName, archivePath,
                sourceDatabaseName: "database_not_in_archive",
                dropExistingTenant: true, attachTenant: true);

            // 4) Must fail loudly, naming the archive's actual database
            Assert.False(restoreResult.Success);
            Assert.Contains(sourceDatabaseName, restoreResult.Error, StringComparison.OrdinalIgnoreCase);

            // 5) The pre-existing target tenant must not have been dropped
            var targetStillAttached = await systemContext.TryFindTenantContextAsync(targetTenantId);
            Assert.NotNull(targetStillAttached);
        }
        finally
        {
            await TryDropTenant(systemContext, targetTenantId);
            await TryDropTenant(systemContext, sourceTenantId);
        }
    }

    // AB#4209 Step 5 PR 2 — clone primitive coverage.
    // The clone is used by the DumpTenant --clean orchestrator (bot-services) to isolate
    // the cleanup pass from the live tenant. These tests pin the contract: clone produces
    // an attached temp tenant with its own database, the source tenant is untouched, and
    // the clone fails fast on non-existent sources without leaking a half-restored temp.

    [Fact]
    public async Task CloneTenantToTemp_WithValidSource_CreatesAttachedTempTenant()
    {
        var systemContext = systemFixture.GetSystemContext();

        var sourceTenantId = $"clonesrc_{Guid.NewGuid():N}";
        var sourceDatabaseName = sourceTenantId.ToLower();
        var tempTenantId = $"clonetmp_{Guid.NewGuid():N}";
        var tempDatabaseName = tempTenantId.ToLower();

        try
        {
            // 1) Create a source tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceDatabaseName, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            // 2) Clone the source to a temp tenant
            var cloneResult = await systemContext.CloneTenantToTempAsync(
                sourceTenantId, tempTenantId, tempDatabaseName);

            // 3) Verify clone succeeded
            Assert.True(cloneResult.Success, $"Clone should succeed. Error: {cloneResult.Error}");

            // 4) Verify temp tenant is attached and accessible
            var tempContext = await systemContext.GetChildTenantContextAsync(tempTenantId);
            Assert.NotNull(tempContext);
            Assert.Equal(tempTenantId.ToLower(), tempContext.TenantId);
            Assert.Equal(tempDatabaseName.ToLower(), tempContext.DatabaseName);

            // 5) Verify source tenant is still attached and unchanged
            var sourceContext = await systemContext.TryFindTenantContextAsync(sourceTenantId);
            Assert.NotNull(sourceContext);
            Assert.Equal(sourceTenantId.ToLower(), sourceContext.TenantId);
        }
        finally
        {
            await TryDropTenant(systemContext, tempTenantId);
            await TryDropTenant(systemContext, sourceTenantId);
        }
    }

    [Fact]
    public async Task CloneTenantToTemp_WithNonExistentSource_FailsWithoutCreatingTemp()
    {
        var systemContext = systemFixture.GetSystemContext();

        var tempTenantId = $"clonetmp_{Guid.NewGuid():N}";
        var tempDatabaseName = tempTenantId.ToLower();

        try
        {
            var result = await systemContext.CloneTenantToTempAsync(
                "nonexistent_source_tenant", tempTenantId, tempDatabaseName);

            Assert.False(result.Success);
            Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);

            // The temp tenant must not have been attached on the failure path.
            var tempContext = await systemContext.TryFindTenantContextAsync(tempTenantId);
            Assert.Null(tempContext);
        }
        finally
        {
            await TryDropTenant(systemContext, tempTenantId);
        }
    }

    [Fact]
    public async Task CloneTenantToTemp_TempTenantIsIndependentFromSource()
    {
        // The whole point of cloning instead of mutating-in-place is that operations on the
        // temp tenant (the DumpTenant --clean orchestrator's cleanOverlayEntries call) must
        // not visibly affect the source. We verify isolation by dropping the temp tenant
        // and confirming the source is still attached.
        var systemContext = systemFixture.GetSystemContext();

        var sourceTenantId = $"clonesrc_{Guid.NewGuid():N}";
        var sourceDatabaseName = sourceTenantId.ToLower();
        var tempTenantId = $"clonetmp_{Guid.NewGuid():N}";
        var tempDatabaseName = tempTenantId.ToLower();

        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceDatabaseName, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            var cloneResult = await systemContext.CloneTenantToTempAsync(
                sourceTenantId, tempTenantId, tempDatabaseName);
            Assert.True(cloneResult.Success);

            // Drop the temp tenant; source must remain.
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, tempTenantId);
                await session.CommitTransactionAsync();
            }

            var sourceStillAttached = await systemContext.TryFindTenantContextAsync(sourceTenantId);
            Assert.NotNull(sourceStillAttached);
            var tempGone = await systemContext.TryFindTenantContextAsync(tempTenantId);
            Assert.Null(tempGone);
        }
        finally
        {
            await TryDropTenant(systemContext, tempTenantId);
            await TryDropTenant(systemContext, sourceTenantId);
        }
    }

    private static async Task TryDropTenant(Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext systemContext, string tenantId)
    {
        try
        {
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            if (await systemContext.IsChildTenantExistingAsync(session, tenantId))
            {
                await systemContext.DropChildTenantAsync(session, tenantId);
            }
            await session.CommitTransactionAsync();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
