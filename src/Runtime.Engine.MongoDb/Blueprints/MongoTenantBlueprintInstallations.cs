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
/// MongoDB implementation of <see cref="ITenantBlueprintInstallations"/>.
/// Stores the live set of installed blueprints per tenant in the system
/// database, separate from the append-only <see cref="MongoTenantBlueprintHistory"/>.
/// </summary>
internal class MongoTenantBlueprintInstallations : ITenantBlueprintInstallations
{
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private readonly ILogger<MongoTenantBlueprintInstallations> _logger;

    public MongoTenantBlueprintInstallations(
        IAdminRepositoryAccess adminRepositoryAccess,
        IOptions<OctoSystemConfiguration> systemConfiguration,
        ILogger<MongoTenantBlueprintInstallations> logger)
    {
        _adminRepositoryAccess = adminRepositoryAccess;
        _systemConfiguration = systemConfiguration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlueprintInstallation>> GetInstalledAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Listing blueprint installations for tenant {TenantId}", tenantId);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintInstallation>.Filter.Eq(x => x.TenantId, tenantId);

        var cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        var documents = await cursor.ToListAsync(cancellationToken);

        return documents.Select(MapToInstallation).ToList();
    }

    /// <inheritdoc />
    public async Task<BlueprintInstallation?> GetByBlueprintNameAsync(
        string tenantId,
        string blueprintName,
        CancellationToken cancellationToken = default)
    {
        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintInstallation>.Filter.And(
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.BlueprintName, blueprintName));

        var cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        var document = await cursor.FirstOrDefaultAsync(cancellationToken);

        return document != null ? MapToInstallation(document) : null;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        string tenantId,
        BlueprintInstallation installation,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Upserting blueprint installation for tenant {TenantId}: {BlueprintId} (dependency={IsDependency})",
            tenantId, installation.BlueprintId, installation.IsDependency);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintInstallation>.Filter.And(
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.BlueprintName, installation.BlueprintId.Name));

        var existing = await (await collection.FindAsync(filter, cancellationToken: cancellationToken))
            .FirstOrDefaultAsync(cancellationToken);

        var document = MapFromInstallation(tenantId, installation, existing?.Id);

        await collection.ReplaceOneAsync(
            filter,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(
        string tenantId,
        string blueprintName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Removing blueprint installation for tenant {TenantId}: {BlueprintName}",
            tenantId, blueprintName);

        var collection = GetMongoCollection();
        var filter = Builders<RtBlueprintInstallation>.Filter.And(
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<RtBlueprintInstallation>.Filter.Eq(x => x.BlueprintName, blueprintName));

        var result = await collection.DeleteOneAsync(filter, cancellationToken);
        return result.DeletedCount > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string TenantId, BlueprintInstallation Installation)>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var collection = GetMongoCollection();
        var cursor = await collection.FindAsync(
            Builders<RtBlueprintInstallation>.Filter.Empty,
            cancellationToken: cancellationToken);
        var documents = await cursor.ToListAsync(cancellationToken);

        return documents
            .Select(d => (d.TenantId, MapToInstallation(d)))
            .ToList();
    }

    private IMongoCollection<RtBlueprintInstallation> GetMongoCollection()
    {
        // Match TenantContext's normalisation: SystemDatabaseName must be
        // lower-cased before it reaches MongoDB. On macOS the filesystem is
        // case-insensitive but MongoDB stores DB names case-sensitively;
        // passing the raw default ("OctoSystem") when an existing "octosystem"
        // DB is around triggers WriteError 13297.
        var databaseName = _systemConfiguration.Value.SystemDatabaseName.ToLower();
        var repositoryClient = _adminRepositoryAccess.GetRepositoryClient(databaseName);
        var repository = repositoryClient.GetRepository(databaseName);
        var mapper = new RtBlueprintInstallationMongoDataSourceMapper();
        var dataSourceCollection = repository.GetCollection(mapper);
        return dataSourceCollection.GetMongoCollection();
    }

    private static BlueprintInstallation MapToInstallation(RtBlueprintInstallation document)
    {
        return new BlueprintInstallation
        {
            BlueprintId = new BlueprintId(document.BlueprintName, document.BlueprintVersion),
            InstalledAt = document.InstalledAt,
            LastUpdatedAt = document.LastUpdatedAt,
            SeedDataChecksum = document.SeedDataChecksum,
            ResolvedDependencies = document.ResolvedDependencies
                .Select(s => new BlueprintId(s))
                .ToList(),
            IsDependency = document.IsDependency
        };
    }

    private static RtBlueprintInstallation MapFromInstallation(
        string tenantId,
        BlueprintInstallation installation,
        string? existingId)
    {
        return new RtBlueprintInstallation
        {
            Id = existingId ?? Guid.NewGuid().ToString(),
            TenantId = tenantId,
            BlueprintName = installation.BlueprintId.Name,
            BlueprintVersion = installation.BlueprintId.Version.ToString(),
            InstalledAt = installation.InstalledAt,
            LastUpdatedAt = installation.LastUpdatedAt,
            SeedDataChecksum = installation.SeedDataChecksum,
            ResolvedDependencies = installation.ResolvedDependencies
                .Select(b => b.FullName)
                .ToList(),
            IsDependency = installation.IsDependency
        };
    }
}
