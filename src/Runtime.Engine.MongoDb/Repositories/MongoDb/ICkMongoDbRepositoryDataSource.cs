using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

// ReSharper disable once RedundantUsingDirective - needed for CollectionCleanupResult
namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface ICkMongoDbRepositoryDataSource
{
    IMongoDbDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    IMongoDbDataSourceCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    IMongoDbDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    IMongoDbDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    IMongoDbDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    IMongoDbDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    IMongoDbDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }

    Task UpdateCollectionsAsync(IOctoSession session, bool includeModelsInStateImporting = false, bool skipCleanup = false);
    Task UpdateIndexAsync(IOctoSession session, bool includeModelsInStateImporting,
        CkModelId? scopeToModelId = null, CancellationToken cancellationToken = default);

    Task<IOctoSession> CreateSessionAsync();

    /// <summary>
    /// Acquires a distributed lock for importing a CK model.
    /// The lock prevents multiple services from importing the same model simultaneously.
    /// </summary>
    /// <param name="modelName">The name of the model to lock (without version)</param>
    /// <param name="cancellationToken">Token to cancel the polling loop while waiting for the lock.</param>
    /// <returns>An <see cref="IDistributedLockHandle"/> whose <see cref="IDistributedLockHandle.LockLostToken"/>
    /// fires if ownership is lost (e.g. due to TTL expiry under load) and which releases the lock when disposed.</returns>
    Task<IDistributedLockHandle> AcquireModelImportLockAsync(string modelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up empty collections that were created for abstract CK types.
    /// Only deletes collections that:
    /// 1. Start with "RtEntity_" prefix
    /// 2. Correspond to an abstract CK type
    /// 3. Contain no documents
    /// </summary>
    /// <param name="session">The session to use for database operations</param>
    /// <returns>A result containing information about the cleanup operation</returns>
    Task<CollectionCleanupResult> CleanupEmptyAbstractTypeCollectionsAsync(IOctoSession session);
}
