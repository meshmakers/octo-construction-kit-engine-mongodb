using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loader for RtEntity data from tenants
/// </summary>
internal class RtEntityLoader : BaseTenantDataLoader
{
    public RtEntityLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
        : base(systemContext, adminRepositoryAccess, loggerFactory)
    {
    }

    /// <summary>
    ///     Loads RtEntities grouped by CkTypeId for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="options">Comparison options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of RtEntity lists grouped by CkTypeId</returns>
    public async Task<Dictionary<string, List<RtEntity>>> LoadAsync(string tenantId,
        TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        ITenantContext tenantContext = await SystemContext.FindTenantContextAsync(tenantId);
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Load all CkTypes to know which ones are collection roots
        var ckTypes = await dataSource.CkTypes.FindManyAsync(session,
            FilterDefinition<CkType>.Empty);

        var result = new Dictionary<string, List<RtEntity>>();

        // Process each CkType that is a collection root
        foreach (CkType ckType in ckTypes.Where(t => t.IsCollectionRoot))
        {
            string ckTypeIdString = ckType.CkTypeId.ToString();
            var entities = await LoadEntitiesForCkTypeAsync(dataSource, session, ckType, options, cancellationToken);
            result[ckTypeIdString] = entities;
        }

        await session.CommitTransactionAsync();

        return result;
    }

    private async Task<List<RtEntity>> LoadEntitiesForCkTypeAsync(
        IMongoDbRepositoryDataSource dataSource,
        dynamic session,
        CkType ckType,
        TenantComparisonOptions options,
        CancellationToken cancellationToken)
    {
        // Cast to MongoDbRepositoryDataSource to access internal method
        MongoDbRepositoryDataSource mongoDataSource = (MongoDbRepositoryDataSource)dataSource;

        // Get the MongoDB collection for this entity type
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection =
            mongoDataSource.GetRtDatabaseCollectionByCkType<RtEntity>(ckType);

        // Build filter for entities of this CkType
        FilterDefinition<RtEntity> filter = Builders<RtEntity>.Filter.Eq(e => e.CkTypeId, ckType.CkTypeId);

        // Apply MaxEntitiesPerType limit if specified
        int? limit = options.MaxEntitiesPerType;

        // Load entities from MongoDB
        List<RtEntity> entities = await collection.FindManyAsync(session, filter);

        return entities;
    }
}
