using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;

/// <summary>
/// MongoDB implementation of <see cref="ITenantBlueprintHistory"/>.
/// Stores blueprint application history in the system tenant database.
/// </summary>
internal class MongoTenantBlueprintHistory : ITenantBlueprintHistory
{
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly ILogger<MongoTenantBlueprintHistory> _logger;

    public MongoTenantBlueprintHistory(
        IAdminRepositoryAccess adminRepositoryAccess,
        IOptions<OctoSystemConfiguration> systemConfiguration,
        ILogger<MongoTenantBlueprintHistory> logger)
    {
        _adminRepositoryAccess = adminRepositoryAccess;
        _systemConfiguration = systemConfiguration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantBlueprintInfo>> GetHistoryAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting blueprint history for tenant {TenantId}", tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintHistory>.Filter.Eq(x => x.TenantId, tenantId);
        var sort = Builders<RtBlueprintHistory>.Sort.Descending(x => x.AppliedAt);

        var cursor = await collection.FindAsync(filter, new FindOptions<RtBlueprintHistory>
        {
            Sort = sort
        }, cancellationToken);

        var documents = await cursor.ToListAsync(cancellationToken);

        return documents.Select(MapToTenantBlueprintInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<TenantBlueprintInfo?> GetCurrentAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting current blueprint for tenant {TenantId}", tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintHistory>.Filter.Eq(x => x.TenantId, tenantId);
        var sort = Builders<RtBlueprintHistory>.Sort.Descending(x => x.AppliedAt);

        var options = new FindOptions<RtBlueprintHistory>
        {
            Sort = sort,
            Limit = 1
        };

        var cursor = await collection.FindAsync(filter, options, cancellationToken);
        var document = await cursor.FirstOrDefaultAsync(cancellationToken);

        return document != null ? MapToTenantBlueprintInfo(document) : null;
    }

    /// <inheritdoc />
    public async Task AddEntryAsync(
        string tenantId,
        TenantBlueprintInfo info,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Adding blueprint history entry for tenant {TenantId}: {BlueprintId}",
            tenantId, info.BlueprintId);

        var collection = GetMongoCollection();
        var document = MapFromTenantBlueprintInfo(tenantId, info);

        await collection.InsertOneAsync(document, cancellationToken: cancellationToken);

        _logger.LogDebug("Blueprint history entry added successfully");
    }

    /// <inheritdoc />
    public async Task<bool> HasBlueprintAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Checking if tenant {TenantId} has a blueprint", tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintHistory>.Filter.Eq(x => x.TenantId, tenantId);

        var count = await collection.CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, cancellationToken);

        return count > 0;
    }

    private IMongoCollection<RtBlueprintHistory> GetMongoCollection()
    {
        // Lower-case SystemDatabaseName to match TenantContext's normalisation
        // (and MongoTenantBlueprintInstallations.GetMongoCollection). Without
        // this, MongoDB rejects collection access with WriteError 13297 when
        // an existing "octosystem" DB conflicts with the raw "OctoSystem"
        // default casing.
        var databaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var repositoryClient = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var repository = repositoryClient.GetRepository(databaseName);
        var mapper = new RtBlueprintHistoryMongoDataSourceMapper();
        var dataSourceCollection = repository.GetCollection(mapper);

        return dataSourceCollection.GetMongoCollection();
    }

    private static TenantBlueprintInfo MapToTenantBlueprintInfo(RtBlueprintHistory document)
    {
        BlueprintId? previousVersion = null;
        if (!string.IsNullOrEmpty(document.PreviousBlueprintId) && !string.IsNullOrEmpty(document.PreviousVersion))
        {
            previousVersion = new BlueprintId(document.PreviousBlueprintId, document.PreviousVersion);
        }

        return new TenantBlueprintInfo
        {
            BlueprintId = new BlueprintId(document.BlueprintId, document.BlueprintVersion),
            AppliedAt = document.AppliedAt,
            ApplicationMode = Enum.Parse<BlueprintApplicationMode>(document.ApplicationMode),
            PreviousVersion = previousVersion,
            EntitiesCreated = document.EntitiesCreated,
            EntitiesUpdated = document.EntitiesUpdated,
            EntitiesDeleted = document.EntitiesDeleted,
            SeedDataChecksum = document.SeedDataChecksum
        };
    }

    private static RtBlueprintHistory MapFromTenantBlueprintInfo(string tenantId, TenantBlueprintInfo info)
    {
        return new RtBlueprintHistory
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            BlueprintId = info.BlueprintId.Name,
            BlueprintVersion = info.BlueprintId.Version.ToString(),
            AppliedAt = info.AppliedAt,
            ApplicationMode = info.ApplicationMode.ToString(),
            PreviousBlueprintId = info.PreviousVersion?.Name,
            PreviousVersion = info.PreviousVersion?.Version.ToString(),
            EntitiesCreated = info.EntitiesCreated,
            EntitiesUpdated = info.EntitiesUpdated,
            EntitiesDeleted = info.EntitiesDeleted,
            SeedDataChecksum = info.SeedDataChecksum
        };
    }
}
