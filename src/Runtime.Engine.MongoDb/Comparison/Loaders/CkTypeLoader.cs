using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loader for CkType data from tenants
/// </summary>
internal class CkTypeLoader : BaseTenantDataLoader
{
    public CkTypeLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
        : base(systemContext, adminRepositoryAccess, loggerFactory)
    {
    }

    /// <summary>
    ///     Loads all CkTypes for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="options">Comparison options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of CkTypes</returns>
    public async Task<List<CkType>> LoadAsync(string tenantId, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        ITenantContext tenantContext = await SystemContext.FindTenantContextAsync(tenantId);
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Load all CkTypes
        var ckTypes = await dataSource.CkTypes.FindManyAsync(session,
            FilterDefinition<CkType>.Empty);

        await session.CommitTransactionAsync();

        return ckTypes.ToList();
    }
}
