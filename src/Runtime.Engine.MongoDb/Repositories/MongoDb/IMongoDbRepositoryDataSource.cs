using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface IMongoDbRepositoryDataSource : ICkMongoDbRepositoryDataSource, IRepositoryDataSource
{
    IMongoDbDataSourceCollection<OctoObjectId, RtAssociation> RtMongoDbDataSourceAssociations { get; }

    /// <summary>
    ///     Returns the data source access object for the given entity type
    /// </summary>
    /// <param name="ckTypeGraph">Construction kit type graph</param>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkTypeGraph ckTypeGraph)
        where TEntity : RtEntity, new();

    Task<IOctoSession> GetSessionAsync();
    IOctoSession GetSession();

    /// <summary>
    ///     Creates indexes for the RtAssociations collection if they don't already exist.
    ///     This method is typically called during migrations to ensure optimal query performance.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateRtAssociationIndexesAsync();
}
