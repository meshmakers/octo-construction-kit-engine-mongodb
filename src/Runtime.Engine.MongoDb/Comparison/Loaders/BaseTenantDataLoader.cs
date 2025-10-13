using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;

/// <summary>
///     Base class for tenant data loaders providing common data source access functionality
/// </summary>
internal abstract class BaseTenantDataLoader
{
    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;
    private readonly ILoggerFactory _loggerFactory;

    protected BaseTenantDataLoader(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess,
        ILoggerFactory loggerFactory)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    ///     Gets the system context for accessing tenant information
    /// </summary>
    protected ISystemContext SystemContext => _systemContext;

    /// <summary>
    ///     Creates a MongoDB repository data source for the specified tenant
    /// </summary>
    /// <param name="tenantContext">The tenant context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>MongoDB repository data source</returns>
    protected IMongoDbRepositoryDataSource GetMongoDbRepositoryDataSource(ITenantContext tenantContext,
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
