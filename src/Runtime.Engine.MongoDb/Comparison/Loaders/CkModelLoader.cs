using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loads CkModel information directly from MongoDB
/// </summary>
internal class CkModelLoader : BaseTenantDataLoader
{
    public CkModelLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
        : base(systemContext, adminRepositoryAccess, loggerFactory)
    {
    }

    /// <summary>
    ///     Loads all available CkModels for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="options">Comparison options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of CkModels</returns>
    public async Task<List<CkModel>> LoadAsync(string tenantId, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        ITenantContext tenantContext = await SystemContext.FindTenantContextAsync(tenantId);
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Load all available CkModels
        var ckModels = await dataSource.CkModels.FindManyAsync(session,
            m => m.ModelState == ModelState.Available);

        await session.CommitTransactionAsync();

        return ckModels.ToList();
    }
}
