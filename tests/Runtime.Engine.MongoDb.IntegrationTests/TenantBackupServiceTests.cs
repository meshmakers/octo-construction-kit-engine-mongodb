using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class TenantBackupServiceTests(SystemFixture systemFixture) : IClassFixture<SystemFixture>
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
}
