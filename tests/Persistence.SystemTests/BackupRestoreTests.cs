using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

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
}
