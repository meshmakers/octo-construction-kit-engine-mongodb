using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Loader for association data from tenants
/// </summary>
internal class AssociationLoader : BaseTenantDataLoader
{
    public AssociationLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
        : base(systemContext, adminRepositoryAccess, loggerFactory)
    {
    }

    /// <summary>
    ///     Loads all associations for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant identifier</param>
    /// <param name="options">Comparison options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Association data containing all associations</returns>
    public async Task<AssociationData> LoadAsync(string tenantId, TenantComparisonOptions options,
        CancellationToken cancellationToken = default)
    {
        ITenantContext tenantContext = await SystemContext.FindTenantContextAsync(tenantId);
        var dataSource = GetMongoDbRepositoryDataSource(tenantContext, cancellationToken);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Load all associations from the tenant
        var associations = await dataSource.RtMongoDbDataSourceAssociations.FindManyAsync(session,
            FilterDefinition<RtAssociation>.Empty);

        await session.CommitTransactionAsync();

        return new AssociationData { Associations = associations.ToList() };
    }
}
