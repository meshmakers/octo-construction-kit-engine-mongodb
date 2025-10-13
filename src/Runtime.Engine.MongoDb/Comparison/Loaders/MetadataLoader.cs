using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loads tenant metadata directly from MongoDB
/// </summary>
internal class MetadataLoader
{
    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILoggerFactory _loggerFactory;

    public MetadataLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _loggerFactory = loggerFactory;
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
        ITenantContext tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

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
        // TODO: Implement proper entity counting per CkType
        return await Task.FromResult(new Dictionary<string, long>());
    }

    private async Task<long> LoadTotalAssociationCountAsync(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);
        var session = await dataSource.GetSessionAsync();
        var count = await dataSource.RtMongoDbDataSourceAssociations.GetTotalCountAsync(session);
        return count;
    }

    private IMongoDbRepositoryDataSource GetMongoDbRepositoryDataSource(ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        IRepositoryClient repositoryClient = _adminRepositoryAccess.GetRepositoryClient(tenantContext.DatabaseName);
        var dataSource = new MongoDbRepositoryDataSource(
            _loggerFactory.CreateLogger<MongoDbRepositoryDataSource>(),
            repositoryClient,
            tenantContext.DatabaseName,
            tenantContext.TenantId);

        return dataSource;
    }
}
