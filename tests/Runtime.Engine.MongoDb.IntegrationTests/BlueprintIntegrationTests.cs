using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for MongoDB blueprint functionality.
/// </summary>
[Collection(BlueprintCollection.Name)]
public class BlueprintIntegrationTests(BlueprintFixture fixture)
{
    private readonly BlueprintFixture _fixture = fixture;

    #region MongoBlueprintBackupService Tests

    [Fact]
    public async Task BackupService_CreateBackup_ReturnsBackupInfo()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var backupService = _fixture.GetBackupService();
        var systemContext = _fixture.GetSystemContext();

        // Create a test tenant first
        var tenantId = $"backup-test-{Guid.NewGuid():N}";
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            // Act
            var backupInfo = await backupService.CreateBackupAsync(tenantId, "Test backup for integration test", cancellationToken);

            // Assert
            Assert.NotNull(backupInfo);
            Assert.Equal(tenantId, backupInfo.TenantId);
            Assert.Equal("Test backup for integration test", backupInfo.Reason);
            Assert.NotEmpty(backupInfo.BackupId);
            Assert.True(backupInfo.CreatedAt <= DateTime.UtcNow);
            Assert.Equal(BackupType.BlueprintUpdate, backupInfo.BackupType);
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task BackupService_ListBackups_ReturnsCreatedBackups()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var backupService = _fixture.GetBackupService();
        var systemContext = _fixture.GetSystemContext();

        var tenantId = $"backup-list-test-{Guid.NewGuid():N}";
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            // Create multiple backups
            await backupService.CreateBackupAsync(tenantId, "First backup", cancellationToken);
            await backupService.CreateBackupAsync(tenantId, "Second backup", cancellationToken);

            // Act
            var backups = await backupService.ListBackupsAsync(tenantId, cancellationToken);

            // Assert
            Assert.True(backups.Count >= 2);
            Assert.Contains(backups, b => b.Reason == "First backup");
            Assert.Contains(backups, b => b.Reason == "Second backup");
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task BackupService_GetBackup_ReturnsSpecificBackup()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var backupService = _fixture.GetBackupService();
        var systemContext = _fixture.GetSystemContext();

        var tenantId = $"backup-get-test-{Guid.NewGuid():N}";
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            // Create a backup
            var createdBackup = await backupService.CreateBackupAsync(tenantId, "Specific backup", cancellationToken);

            // Act
            var retrievedBackup = await backupService.GetBackupAsync(tenantId, createdBackup.BackupId, cancellationToken);

            // Assert
            Assert.NotNull(retrievedBackup);
            Assert.Equal(createdBackup.BackupId, retrievedBackup.BackupId);
            Assert.Equal(createdBackup.TenantId, retrievedBackup.TenantId);
            Assert.Equal(createdBackup.Reason, retrievedBackup.Reason);
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task BackupService_DeleteBackup_RemovesBackup()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var backupService = _fixture.GetBackupService();
        var systemContext = _fixture.GetSystemContext();

        var tenantId = $"backup-delete-test-{Guid.NewGuid():N}";
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            // Create and then delete a backup
            var backup = await backupService.CreateBackupAsync(tenantId, "Backup to delete", cancellationToken);
            var backupId = backup.BackupId;

            // Act
            var deleted = await backupService.DeleteBackupAsync(tenantId, backupId, cancellationToken);

            // Assert
            Assert.True(deleted);

            var retrievedBackup = await backupService.GetBackupAsync(tenantId, backupId, cancellationToken);
            Assert.Null(retrievedBackup);
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
    }

    #endregion

    #region TenantContext Blueprint Integration Tests

    [Fact]
    public async Task CreateChildTenantWithNullBlueprint_CreatesNormalTenant()
    {
        // Arrange
        var systemContext = _fixture.GetSystemContext();
        var tenantId = $"no-blueprint-tenant-{Guid.NewGuid():N}";

        try
        {
            // Act
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            var result = await systemContext.CreateChildTenantAsync(session, tenantId, tenantId, null);
            await session.CommitTransactionAsync();

            // Assert
            Assert.Null(result); // No blueprint was applied

            using var session2 = await systemContext.GetAdminSessionAsync();
            session2.StartTransaction();
            var tenantExists = await systemContext.IsChildTenantExistingAsync(session2, tenantId);
            await session2.CommitTransactionAsync();

            Assert.True(tenantExists);
        }
        finally
        {
            // Cleanup
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            try
            {
                await systemContext.DropChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #endregion
}
