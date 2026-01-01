using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for MongoDB blueprint functionality.
/// </summary>
[Collection("Sequential")]
public class BlueprintIntegrationTests(BlueprintFixture fixture)
    : IClassFixture<BlueprintFixture>
{
    private readonly BlueprintFixture _fixture = fixture;

    #region MongoTenantBlueprintHistory Tests

    [Fact]
    public async Task BlueprintHistory_AddEntry_CanBeRetrieved()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = _fixture.GetBlueprintHistory();
        var tenantId = $"test-tenant-{Guid.NewGuid():N}";
        var blueprintId = new BlueprintId("TestBlueprint", "1.0.0");

        var entry = new TenantBlueprintInfo
        {
            BlueprintId = blueprintId,
            AppliedAt = DateTime.UtcNow,
            ApplicationMode = BlueprintApplicationMode.Initial,
            EntitiesCreated = 10,
            EntitiesUpdated = 0,
            EntitiesDeleted = 0
        };

        // Act
        await history.AddEntryAsync(tenantId, entry, cancellationToken);
        var retrieved = await history.GetCurrentAsync(tenantId, cancellationToken);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(blueprintId.Name, retrieved.BlueprintId.Name);
        Assert.Equal(blueprintId.Version, retrieved.BlueprintId.Version);
        Assert.Equal(BlueprintApplicationMode.Initial, retrieved.ApplicationMode);
        Assert.Equal(10, retrieved.EntitiesCreated);
    }

    [Fact]
    public async Task BlueprintHistory_MultipleEntries_ReturnsMostRecent()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = _fixture.GetBlueprintHistory();
        var tenantId = $"test-tenant-{Guid.NewGuid():N}";
        var blueprintId1 = new BlueprintId("TestBlueprint", "1.0.0");
        var blueprintId2 = new BlueprintId("TestBlueprint", "2.0.0");

        var entry1 = new TenantBlueprintInfo
        {
            BlueprintId = blueprintId1,
            AppliedAt = DateTime.UtcNow.AddHours(-1),
            ApplicationMode = BlueprintApplicationMode.Initial,
            EntitiesCreated = 10
        };

        var entry2 = new TenantBlueprintInfo
        {
            BlueprintId = blueprintId2,
            AppliedAt = DateTime.UtcNow,
            ApplicationMode = BlueprintApplicationMode.Update,
            PreviousVersion = blueprintId1,
            EntitiesCreated = 5,
            EntitiesUpdated = 3
        };

        // Act
        await history.AddEntryAsync(tenantId, entry1, cancellationToken);
        await history.AddEntryAsync(tenantId, entry2, cancellationToken);
        var current = await history.GetCurrentAsync(tenantId, cancellationToken);

        // Assert
        Assert.NotNull(current);
        Assert.Equal("2.0.0", current.BlueprintId.Version.ToString());
        Assert.Equal(BlueprintApplicationMode.Update, current.ApplicationMode);
    }

    [Fact]
    public async Task BlueprintHistory_GetHistory_ReturnsAllEntries()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = _fixture.GetBlueprintHistory();
        var tenantId = $"test-tenant-{Guid.NewGuid():N}";

        var entries = new[]
        {
            new TenantBlueprintInfo
            {
                BlueprintId = new BlueprintId("TestBlueprint", "1.0.0"),
                AppliedAt = DateTime.UtcNow.AddHours(-2),
                ApplicationMode = BlueprintApplicationMode.Initial
            },
            new TenantBlueprintInfo
            {
                BlueprintId = new BlueprintId("TestBlueprint", "1.1.0"),
                AppliedAt = DateTime.UtcNow.AddHours(-1),
                ApplicationMode = BlueprintApplicationMode.Update
            },
            new TenantBlueprintInfo
            {
                BlueprintId = new BlueprintId("TestBlueprint", "2.0.0"),
                AppliedAt = DateTime.UtcNow,
                ApplicationMode = BlueprintApplicationMode.Migration
            }
        };

        // Act
        foreach (var entry in entries)
        {
            await history.AddEntryAsync(tenantId, entry, cancellationToken);
        }

        var allHistory = await history.GetHistoryAsync(tenantId, cancellationToken);

        // Assert
        Assert.Equal(3, allHistory.Count);
        // Should be ordered by AppliedAt descending (most recent first)
        Assert.Equal("2.0.0", allHistory[0].BlueprintId.Version.ToString());
        Assert.Equal("1.1.0", allHistory[1].BlueprintId.Version.ToString());
        Assert.Equal("1.0.0", allHistory[2].BlueprintId.Version.ToString());
    }

    [Fact]
    public async Task BlueprintHistory_HasBlueprint_ReturnsTrueWhenExists()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = _fixture.GetBlueprintHistory();
        var tenantId = $"test-tenant-{Guid.NewGuid():N}";

        // Act - Check before any entry
        var hasBlueprint1 = await history.HasBlueprintAsync(tenantId, cancellationToken);

        // Add an entry
        await history.AddEntryAsync(tenantId, new TenantBlueprintInfo
        {
            BlueprintId = new BlueprintId("TestBlueprint", "1.0.0"),
            AppliedAt = DateTime.UtcNow,
            ApplicationMode = BlueprintApplicationMode.Initial
        }, cancellationToken);

        // Check after entry
        var hasBlueprint2 = await history.HasBlueprintAsync(tenantId, cancellationToken);

        // Assert
        Assert.False(hasBlueprint1);
        Assert.True(hasBlueprint2);
    }

    [Fact]
    public async Task BlueprintHistory_GetCurrent_ReturnsNullForNonExistentTenant()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var history = _fixture.GetBlueprintHistory();
        var nonExistentTenantId = $"non-existent-{Guid.NewGuid():N}";

        // Act
        var current = await history.GetCurrentAsync(nonExistentTenantId, cancellationToken);

        // Assert
        Assert.Null(current);
    }

    #endregion

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
