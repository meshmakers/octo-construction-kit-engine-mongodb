using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loads tenant metadata directly from MongoDB
/// </summary>
internal class MetadataLoader : BaseTenantDataLoader
{
    public MetadataLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
        : base(systemContext, adminRepositoryAccess, loggerFactory)
    {
    }

    /// <summary>
    ///     Loads complete metadata for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="options">options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Populated tenant metadata</returns>
    public async Task<TenantMetadata> LoadAsync(string tenantId, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        ITenantContext tenantContext = await SystemContext.FindTenantContextAsync(tenantId);

        var databaseName = tenantContext.DatabaseName;


        long ckModelCount = 0;
        long totalRtEntityCount = 0;
        Dictionary<string, long>? rtEntityCountByCkType = null;
        long totalAssociationCount = 0;

        if (options.Areas.HasFlag(ComparisonAreas.CkModels))
        {
            ckModelCount = await LoadCkModelCountAsync(tenantContext, cancellationToken);
        }

        if (options.Areas.HasFlag(ComparisonAreas.RtEntities))
        {
            rtEntityCountByCkType = await LoadRtEntityCountByCkTypeAsync(tenantContext, cancellationToken);
            totalRtEntityCount = await LoadTotalRtEntityCountAsync(tenantContext, cancellationToken);
        }
        else
        {
            rtEntityCountByCkType = new Dictionary<string, long>();
        }

        if (options.Areas.HasFlag(ComparisonAreas.Associations))
        {
            totalAssociationCount = await LoadTotalAssociationCountAsync(tenantContext, cancellationToken);
        }


        return new TenantMetadata
        {
            TenantId = tenantId,
            DatabaseName = databaseName,
            TotalRtEntityCount = totalRtEntityCount,
            RtEntityCountByCkType = rtEntityCountByCkType,
            TotalAssociationCount = totalAssociationCount,
            CkModelCount = ckModelCount,
        };
    }

    private async Task<long> LoadCkModelCountAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var r = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);
        using var s = await tenantContext.GetAdminSessionAsync();
        s.StartTransaction();
        var count = await r.CkModels.GetTotalCountAsync(s, FilterDefinition<CkModel>.Empty);
        await s.CommitTransactionAsync();
        return count;
    }

    private async Task<long> LoadTotalRtEntityCountAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        Dictionary<string, long> countsByType = await LoadRtEntityCountByCkTypeAsync(tenantContext, cancellationToken);
        return countsByType.Values.Sum();
    }

    private async Task<Dictionary<string, long>> LoadRtEntityCountByCkTypeAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);
        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Load all CkTypes to know which ones are collection roots
        var ckTypes = await dataSource.CkTypes.FindManyAsync(session, FilterDefinition<CkType>.Empty);

        var result = new Dictionary<string, long>();

        // Count entities for each collection root
        foreach (CkType ckType in ckTypes.Where(t => t.IsCollectionRoot))
        {
            MongoDbRepositoryDataSource mongoDataSource = (MongoDbRepositoryDataSource)dataSource;
            IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection =
                mongoDataSource.GetRtDatabaseCollectionByCkType<RtEntity>(ckType);

            // For abstract types, count all entities in collection
            // For concrete types, count only entities with matching CkTypeId
            FilterDefinition<RtEntity> filter = ckType.IsAbstract
                ? FilterDefinition<RtEntity>.Empty
                : Builders<RtEntity>.Filter.Eq(e => e.CkTypeId, ckType.CkTypeId);

            long count = await collection.GetTotalCountAsync(session, filter);
            result[ckType.CkTypeId.ToString()] = count;
        }

        await session.CommitTransactionAsync();
        return result;
    }

    private async Task<long> LoadTotalAssociationCountAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);
        var session = await dataSource.GetSessionAsync();
        var count = await dataSource.RtMongoDbDataSourceAssociations.GetTotalCountAsync(session);
        return count;
    }
}
