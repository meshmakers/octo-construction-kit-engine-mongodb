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

    /// <summary>
    /// Returns the data source access object for the given CK type id by constructing the collection name
    /// directly from the type id, without requiring a CkTypeGraph from the CK cache.
    /// This is intended for migration scenarios where the source type may no longer exist in the CK cache.
    /// </summary>
    /// <param name="rtCkTypeId">The CK type id used to derive the collection name</param>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollectionByTypeId<TEntity>(
        RtCkId<CkTypeId> rtCkTypeId) where TEntity : RtEntity, new();

    /// <summary>
    /// Returns the data source access object for a collection identified by its suffix.
    /// This is intended for migration scenarios where the collection name is already known.
    /// </summary>
    /// <param name="suffix">The collection suffix (without the "RtEntity_" prefix)</param>
    /// <typeparam name="TEntity">The type of entity derived from &lt;see cref="RtEntity"/&gt;</typeparam>
    /// <returns></returns>
    IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollectionByCollectionSuffix<TEntity>(
        string suffix) where TEntity : RtEntity, new();

    /// <summary>
    /// Drops the collection for the specified CK type id.
    /// The collection name is derived directly from the type id without CK cache validation.
    /// </summary>
    /// <param name="rtCkTypeId">The CK type id whose collection should be dropped</param>
    Task DropRtDatabaseCollectionByTypeIdAsync(RtCkId<CkTypeId> rtCkTypeId);

    /// <summary>
    /// Searches all RtEntity collections for documents whose ckTypeId field matches the specified value.
    /// This is used for migrating derived types that are stored in a parent type's collection
    /// (not in their own collection).
    /// </summary>
    /// <param name="session">The session object</param>
    /// <param name="ckTypeIdValue">The ckTypeId field value to search for (e.g. "System.Communication/Adapter")</param>
    /// <returns>Tuple of (collection name, list of matching entities)</returns>
    Task<(string CollectionName, IReadOnlyList<TEntity> Entities)> FindEntitiesInAllCollectionsByCkTypeIdAsync<TEntity>(
        IOctoSession session, string ckTypeIdValue) where TEntity : RtEntity, new();

    Task<IOctoSession> GetSessionAsync();
    IOctoSession GetSession();

    /// <summary>
    ///     Creates indexes for the RtAssociations collection if they don't already exist.
    ///     This method is typically called during migrations to ensure optimal query performance.
    /// </summary>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CreateRtAssociationIndexesAsync();
}
