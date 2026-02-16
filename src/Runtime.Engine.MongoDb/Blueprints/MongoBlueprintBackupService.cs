using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using IBlueprintBackupService = Meshmakers.Octo.Runtime.Contracts.Blueprints.ITenantBackupService;
using IMongoBackupService = Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.ITenantBackupService;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;

/// <summary>
/// MongoDB implementation of <see cref="IBlueprintBackupService"/>.
/// Adapts the existing MongoDB backup functionality for blueprint updates.
/// </summary>
internal class MongoBlueprintBackupService : IBlueprintBackupService
{
    private const string BackupDirectory = "blueprints/backups";

    private readonly ISystemContext _systemContext;
    private readonly IMongoBackupService _mongoBackupService;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly ILogger<MongoBlueprintBackupService> _logger;

    public MongoBlueprintBackupService(
        ISystemContext systemContext,
        IMongoBackupService mongoBackupService,
        IAdminRepositoryAccess adminRepositoryAccess,
        IOptions<OctoSystemConfiguration> systemConfiguration,
        ILogger<MongoBlueprintBackupService> logger)
    {
        _systemContext = systemContext;
        _mongoBackupService = mongoBackupService;
        _adminRepositoryAccess = adminRepositoryAccess;
        _systemConfiguration = systemConfiguration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BackupInfo> CreateBackupAsync(
        string tenantId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating backup for tenant {TenantId}: {Reason}", tenantId, reason);

        // Generate backup ID and path
        var backupId = Guid.NewGuid().ToString();
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archiveFileName = $"{tenantId}_{timestamp}_{backupId}.gz";
        var archiveFilePath = Path.Combine(GetBackupDirectory(), archiveFileName);

        // Ensure backup directory exists
        Directory.CreateDirectory(GetBackupDirectory());

        // Get current blueprint version (if any)
        string? blueprintVersion = null;
        try
        {
            var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
            if (tenantContext != null)
            {
                // Note: Blueprint version tracking would come from ITenantBlueprintHistory
                // For now, we'll leave it null
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine current blueprint version for tenant {TenantId}", tenantId);
        }

        // Perform the backup using the existing MongoDB backup service
        var result = await _mongoBackupService.BackupTenantAsync(
            tenantId,
            archiveFilePath,
            detachTenant: false,
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException($"Backup failed: {result.Error}");
        }

        // Get file size
        long? sizeBytes = null;
        if (File.Exists(archiveFilePath))
        {
            sizeBytes = new FileInfo(archiveFilePath).Length;
        }

        // Create backup info
        var backupInfo = new BackupInfo
        {
            BackupId = backupId,
            TenantId = tenantId,
            CreatedAt = DateTime.UtcNow,
            Reason = reason,
            BlueprintVersion = blueprintVersion,
            StorageLocation = archiveFilePath,
            SizeBytes = sizeBytes,
            BackupType = BackupType.BlueprintUpdate
        };

        // Store backup metadata in MongoDB
        await SaveBackupInfoAsync(backupInfo, cancellationToken);

        _logger.LogInformation("Backup created successfully: {BackupId}", backupId);

        return backupInfo;
    }

    /// <inheritdoc />
    public async Task<BackupRestoreResult> RestoreBackupAsync(
        string tenantId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restoring backup {BackupId} for tenant {TenantId}", backupId, tenantId);

        var result = new BackupRestoreResult();

        // Get backup info
        var backupInfo = await GetBackupAsync(tenantId, backupId, cancellationToken);
        if (backupInfo == null)
        {
            result.Success = false;
            result.Errors.Add($"Backup not found: {backupId}");
            return result;
        }

        if (string.IsNullOrEmpty(backupInfo.StorageLocation))
        {
            result.Success = false;
            result.Errors.Add("Backup storage location is not available");
            return result;
        }

        if (!File.Exists(backupInfo.StorageLocation))
        {
            result.Success = false;
            result.Errors.Add($"Backup file not found: {backupInfo.StorageLocation}");
            return result;
        }

        // Get tenant context to determine database name
        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
        var databaseName = tenantContext?.DatabaseName ?? tenantId.ToLower();

        // Perform the restore
        var restoreResult = await _mongoBackupService.RestoreTenantAsync(
            tenantId,
            databaseName,
            backupInfo.StorageLocation,
            sourceDatabaseName: null,
            dropExistingTenant: true,
            attachTenant: true,
            timeout: TimeSpan.FromMinutes(30),
            cancellationToken: cancellationToken);

        if (restoreResult.Success)
        {
            result.Success = true;
            result.RestoredBackup = backupInfo;
            _logger.LogInformation("Backup {BackupId} restored successfully", backupId);
        }
        else
        {
            result.Success = false;
            result.Errors.Add(restoreResult.Error ?? "Unknown restore error");
            _logger.LogError("Failed to restore backup {BackupId}: {Error}", backupId, restoreResult.Error);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing backups for tenant {TenantId}", tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBackupInfo>.Filter.Eq(x => x.TenantId, tenantId);
        var sort = Builders<RtBackupInfo>.Sort.Descending(x => x.CreatedAt);

        var cursor = await collection.FindAsync(filter, new FindOptions<RtBackupInfo>
        {
            Sort = sort
        }, cancellationToken);

        var documents = await cursor.ToListAsync(cancellationToken);

        return documents.Select(MapToBackupInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<BackupInfo?> GetBackupAsync(
        string tenantId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting backup {BackupId} for tenant {TenantId}", backupId, tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBackupInfo>.Filter.And(
            Builders<RtBackupInfo>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<RtBackupInfo>.Filter.Eq(x => x.BackupId, backupId));

        var cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        var document = await cursor.FirstOrDefaultAsync(cancellationToken);

        return document != null ? MapToBackupInfo(document) : null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteBackupAsync(
        string tenantId,
        string backupId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting backup {BackupId} for tenant {TenantId}", backupId, tenantId);

        // Get backup info first to delete the file
        var backupInfo = await GetBackupAsync(tenantId, backupId, cancellationToken);
        if (backupInfo == null)
        {
            return false;
        }

        // Delete the backup file if it exists
        if (!string.IsNullOrEmpty(backupInfo.StorageLocation) && File.Exists(backupInfo.StorageLocation))
        {
            try
            {
                File.Delete(backupInfo.StorageLocation);
                _logger.LogDebug("Deleted backup file: {Path}", backupInfo.StorageLocation);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete backup file: {Path}", backupInfo.StorageLocation);
            }
        }

        // Delete from MongoDB
        var collection = GetMongoCollection();
        var filter = Builders<RtBackupInfo>.Filter.And(
            Builders<RtBackupInfo>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<RtBackupInfo>.Filter.Eq(x => x.BackupId, backupId));

        var result = await collection.DeleteOneAsync(filter, cancellationToken);

        return result.DeletedCount > 0;
    }

    private async Task SaveBackupInfoAsync(BackupInfo info, CancellationToken cancellationToken)
    {
        var collection = GetMongoCollection();
        var document = MapFromBackupInfo(info);

        await collection.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    private IMongoCollection<RtBackupInfo> GetMongoCollection()
    {
        var databaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var repositoryClient = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var repository = repositoryClient.GetRepository(databaseName);
        var mapper = new RtBackupInfoMongoDataSourceMapper();
        var dataSourceCollection = repository.GetCollection(mapper);

        return dataSourceCollection.GetMongoCollection();
    }

    private string GetBackupDirectory()
    {
        // Use a directory relative to the current directory or a configured path
        return BackupDirectory;
    }

    private static BackupInfo MapToBackupInfo(RtBackupInfo document)
    {
        return new BackupInfo
        {
            BackupId = document.BackupId,
            TenantId = document.TenantId,
            CreatedAt = document.CreatedAt,
            Reason = document.Reason,
            BlueprintVersion = document.BlueprintVersion,
            StorageLocation = document.StorageLocation,
            SizeBytes = document.SizeBytes,
            EntityCount = document.EntityCount ?? 0,
            BackupType = Enum.TryParse<BackupType>(document.BackupType, out var backupType)
                ? backupType
                : BackupType.Full
        };
    }

    private static RtBackupInfo MapFromBackupInfo(BackupInfo info)
    {
        return new RtBackupInfo
        {
            Id = Guid.NewGuid().ToString(),
            BackupId = info.BackupId,
            TenantId = info.TenantId,
            CreatedAt = info.CreatedAt,
            Reason = info.Reason,
            BlueprintVersion = info.BlueprintVersion,
            StorageLocation = info.StorageLocation,
            SizeBytes = info.SizeBytes,
            EntityCount = info.EntityCount,
            BackupType = info.BackupType.ToString()
        };
    }
}
