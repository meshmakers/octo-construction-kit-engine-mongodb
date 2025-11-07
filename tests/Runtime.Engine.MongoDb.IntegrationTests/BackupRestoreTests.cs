using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class BackupRestoreTests(SystemFixture systemFixture) : IClassFixture<SystemFixture>
{
    [Fact]
    public async Task BackupRestore_Roundtrip_Ok()
    {
        var systemContext = systemFixture.GetSystemContext();
        var repositoryOpsService = systemFixture.Provider!.GetRequiredService<IRepositoryOpsService>();

        var testTenantId = $"backuptest_{Guid.NewGuid():N}";
        var testDatabaseName = testTenantId.ToLower();
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}");
        var archivePath = Path.Combine(backupDirectory, "backup.tar.gz");

        try
        {
            // 1) Create a child tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify tenant exists
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "Child tenant should exist after creation");
            }

            // 2) Backup into temp folder
            Directory.CreateDirectory(backupDirectory);

            var dumpResult = await repositoryOpsService.ExecuteMongoDumpAsync(
                MongoDumpOptions.ForArchive(testDatabaseName, archivePath));

            Assert.True(dumpResult.Success, $"Backup should succeed. Error: {dumpResult.Error}");
            Assert.True(File.Exists(archivePath), "Backup archive should exist");

            // 3) Delete the tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, testTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify tenant is deleted
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                await session.CommitTransactionAsync();
                Assert.False(tenantExists, "Tenant should not exist after deletion");
            }

            // 4) Restore the backup
            var restoreResult = await repositoryOpsService.ExecuteMongoRestoreAsync(
                new MongoRestoreOptions
                {
                    Drop = true,
                    Archive = archivePath,
                    Database = testDatabaseName,
                    Gzip = true,
                    Verbose = true
                }, TimeSpan.FromMinutes(5), CancellationToken.None);

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            // 5) Activate the tenant (attach it back to the system)
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, testDatabaseName, testTenantId);
                await session.CommitTransactionAsync();
            }

            // 6) Verify everything works
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, testTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "Tenant should exist after restore and attach");
            }

            // Verify we can get the tenant context
            var restoredTenantContext = await systemContext.GetChildTenantContextAsync(testTenantId);
            Assert.NotNull(restoredTenantContext);
            Assert.Equal(testTenantId.ToLower(), restoredTenantContext.TenantId);
            Assert.Equal(testDatabaseName.ToLower(), restoredTenantContext.DatabaseName);
        }
        finally
        {
            // 7) Clean up
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

            // Clean up backup files
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task BackupRestore_WithDifferentTenantName_Ok()
    {
        var systemContext = systemFixture.GetSystemContext();

        var originalTenantId = $"backuptest_{Guid.NewGuid():N}";
        var originalDatabaseName = originalTenantId.ToLower();
        var newTenantId = $"restored_{Guid.NewGuid():N}";
        var newDatabaseName = newTenantId.ToLower();
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}");
        var archivePath = Path.Combine(backupDirectory, "backup.tar.gz");

        try
        {
            // 1) Create a child tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, originalDatabaseName, originalTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify original tenant exists
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "Original child tenant should exist after creation");
            }

            // 2) Backup tenant to archive
            Directory.CreateDirectory(backupDirectory);

            var backupResult = await systemContext.BackupTenantAsync(originalTenantId, archivePath);

            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");
            Assert.True(File.Exists(archivePath), "Backup archive should exist");

            // 3) Delete the original tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify original tenant is deleted
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.False(tenantExists, "Original tenant should not exist after deletion");
            }

            // 4) Restore the backup with a DIFFERENT tenant ID and database name
            // Using sourceDatabaseName parameter to enable namespace mapping
            var restoreResult = await systemContext.RestoreTenantAsync(
                newTenantId,
                newDatabaseName,
                archivePath,
                sourceDatabaseName: originalDatabaseName,
                dropExistingTenant: true,
                attachTenant: true,
                timeout: TimeSpan.FromMinutes(5));

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            // 5) Verify the new tenant works (RestoreTenantAsync already attached it)
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, newTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "New tenant should exist after restore and attach");
            }

            // Verify original tenant still does not exist
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var originalExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.False(originalExists, "Original tenant should still not exist");
            }

            // Verify we can get the new tenant context with correct names
            var restoredTenantContext = await systemContext.GetChildTenantContextAsync(newTenantId);
            Assert.NotNull(restoredTenantContext);
            Assert.Equal(newTenantId.ToLower(), restoredTenantContext.TenantId);
            Assert.Equal(newDatabaseName.ToLower(), restoredTenantContext.DatabaseName);
        }
        finally
        {
            // 6) Clean up - handle both potential tenants
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    
                    // Clean up original tenant if it still exists
                    var originalExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                    if (originalExists)
                    {
                        await systemContext.DropChildTenantAsync(session, originalTenantId);
                    }

                    // Clean up new tenant if it exists
                    var newExists = await systemContext.IsChildTenantExistingAsync(session, newTenantId);
                    if (newExists)
                    {
                        await systemContext.DropChildTenantAsync(session, newTenantId);
                    }

                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            // Clean up backup files
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }


    [Fact]
    public async Task BackupRestore_WithIndependentDatabaseAndTenantNames_Ok()
    {
        var systemContext = systemFixture.GetSystemContext();

        // Use completely independent names for database and tenant
        var originalTenantId = $"tenant_original_{Guid.NewGuid():N}";
        var originalDatabaseName = $"db_original_{Guid.NewGuid():N}".ToLower();
        var newTenantId = $"tenant_restored_{Guid.NewGuid():N}";
        var newDatabaseName = $"db_restored_{Guid.NewGuid():N}".ToLower();
        var backupDirectory = Path.Combine(Path.GetTempPath(), $"backup_{Guid.NewGuid():N}");
        var archivePath = Path.Combine(backupDirectory, "backup.tar.gz");

        try
        {
            // 1) Create a child tenant with independent database and tenant names
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, originalDatabaseName, originalTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify original tenant exists
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "Original child tenant should exist after creation");
            }

            // Verify tenant context has correct database name
            var originalTenantContext = await systemContext.GetChildTenantContextAsync(originalTenantId);
            Assert.NotNull(originalTenantContext);
            Assert.Equal(originalTenantId.ToLower(), originalTenantContext.TenantId);
            Assert.Equal(originalDatabaseName.ToLower(), originalTenantContext.DatabaseName);

            // 2) Backup tenant to archive
            Directory.CreateDirectory(backupDirectory);

            var backupResult = await systemContext.BackupTenantAsync(originalTenantId, archivePath);

            Assert.True(backupResult.Success, $"Backup should succeed. Error: {backupResult.Error}");
            Assert.True(File.Exists(archivePath), "Backup archive should exist");

            // 3) Delete the original tenant
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DropChildTenantAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
            }

            // Verify original tenant is deleted
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.False(tenantExists, "Original tenant should not exist after deletion");
            }

            // 4) Restore with BOTH different tenant ID AND different database name
            // This tests that tenant ID and database name can be completely independent
            var restoreResult = await systemContext.RestoreTenantAsync(
                newTenantId,
                newDatabaseName,
                archivePath,
                sourceDatabaseName: originalDatabaseName,
                dropExistingTenant: true,
                attachTenant: true,
                timeout: TimeSpan.FromMinutes(5));

            Assert.True(restoreResult.Success, $"Restore should succeed. Error: {restoreResult.Error}");

            // 5) Verify the new tenant exists with correct mappings
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var tenantExists = await systemContext.IsChildTenantExistingAsync(session, newTenantId);
                await session.CommitTransactionAsync();
                Assert.True(tenantExists, "New tenant should exist after restore and attach");
            }

            // Verify original tenant still does not exist
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                var originalExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                await session.CommitTransactionAsync();
                Assert.False(originalExists, "Original tenant should still not exist");
            }

            // Verify tenant context has correct independent names
            var restoredTenantContext = await systemContext.GetChildTenantContextAsync(newTenantId);
            Assert.NotNull(restoredTenantContext);
            Assert.Equal(newTenantId.ToLower(), restoredTenantContext.TenantId);
            Assert.Equal(newDatabaseName.ToLower(), restoredTenantContext.DatabaseName);
            
            // Verify the names are actually different (not just case differences)
            Assert.NotEqual(originalTenantId.ToLower(), newTenantId.ToLower());
            Assert.NotEqual(originalDatabaseName.ToLower(), newDatabaseName.ToLower());
        }
        finally
        {
            // 6) Clean up - handle both potential tenants
            try
            {
                using (var session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    
                    // Clean up original tenant if it still exists
                    var originalExists = await systemContext.IsChildTenantExistingAsync(session, originalTenantId);
                    if (originalExists)
                    {
                        await systemContext.DropChildTenantAsync(session, originalTenantId);
                    }

                    // Clean up new tenant if it exists
                    var newExists = await systemContext.IsChildTenantExistingAsync(session, newTenantId);
                    if (newExists)
                    {
                        await systemContext.DropChildTenantAsync(session, newTenantId);
                    }

                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            // Clean up backup files
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
