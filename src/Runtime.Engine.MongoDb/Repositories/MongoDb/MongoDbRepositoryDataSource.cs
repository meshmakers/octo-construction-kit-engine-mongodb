using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal sealed class MongoDbRepositoryDataSource : RepositoryDataSource, IMongoDbRepositoryDataSource
{
    private readonly IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> _ckTypeInheritances;
    private readonly IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> _ckTypes;
    private readonly IRepositoryInternal _repository;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;
    private readonly ILogger<MongoDbRepositoryDataSource> _logger;
    private readonly IndexStateService _indexStateService;

    public MongoDbRepositoryDataSource(ILogger<MongoDbRepositoryDataSource> logger,
        IUserRepositoryAccess repositoryAccess, string databaseName,
        string tenantId)
        : this(logger, repositoryAccess.GetRepositoryClient(databaseName), databaseName, tenantId)
    {
    }

    internal MongoDbRepositoryDataSource(ILogger<MongoDbRepositoryDataSource> logger,
        IRepositoryClient repositoryClient, string databaseName,
        string tenantId)
        : base(tenantId, new MongoLinkedBinaryDataSource(repositoryClient, databaseName))
    {
        _logger = logger;
        ArgumentValidation.ValidateString(databaseName, nameof(databaseName));

        _repositoryClient = repositoryClient;
        _repository = (IRepositoryInternal)_repositoryClient.GetRepository(databaseName);

        CkModels = _repository.GetCollection(new CkModelMongoDataSourceMapper());

        _ckTypes = _repository.GetCollection(new CkTypeMongoDataSourceMapper());
        CkTypes = _ckTypes;

        CkRecords = _repository.GetCollection(new CkRecordMongoDataSourceMapper());

        CkEnums = _repository.GetCollection(new CkEnumMongoDataSourceMapper());
        CkAttributes = _repository.GetCollection(new CkAttributeMongoDataSourceMapper());

        CkAssociationRoles = _repository.GetCollection(new CkAssociationRoleMongoDataSourceMapper());
        CkTypeAssociations = _repository.GetCollection(new CkTypeAssociationMongoDataSourceMapper());

        _ckTypeInheritances = _repository.GetCollection(new CkTypeInheritanceMongoDataSourceMapper());
        CkTypeInheritances = _ckTypeInheritances;

        CkRecordInheritances = _repository.GetCollection(new CkRecordInheritanceMongoDataSourceMapper());

        RtMongoDbDataSourceAssociations = _repository.GetCollection(new RtAssociationMongoDataSourceMapper());

        // Initialize index state service
        _indexStateService = new IndexStateService(_ckTypes, logger);
    }

    public async Task<IOctoSession> GetSessionAsync()
    {
        var session = await _repositoryClient.GetSessionAsync();
        return session;
    }


    public IOctoSession GetSession()
    {
        var session = _repositoryClient.GetSession();
        return session;
    }

    public override IDataSourceCollection<OctoObjectId, TEntity> GetRtCollection<TEntity>(CkTypeGraph ckTypeGraph)
    {
        return GetRtDatabaseCollection<TEntity>(ckTypeGraph);
    }


    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkTypeGraph ckTypeGraph)
        where TEntity : RtEntity, new()
    {
        if (ckTypeGraph.DefiningCollectionRootCkTypeId == null)
        {
            throw OperationFailedException.CkTypeHasNoDefiningCollectionRoot(ckTypeGraph.CkTypeId);
        }

        var suffix = ckTypeGraph.DefiningCollectionRootCkTypeId.GetCkTypeCollectionName();
        var mapper = new RtEntityMongoDataSourceMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    public override async Task<IReadOnlyList<RtAssociationsMultiplicityResult>> GetRtAssociationsMultiplicityAsync(
        IOctoSession session, IEnumerable<RtEntityRoleIdDirectionPair> entityRoleIdDirectionPairs)
    {
        List<FilterDefinition<RtAssociation>> filters = new();
        IEnumerable<RtEntityRoleIdDirectionPair> rtEntityRoleIdDirectionPairs = entityRoleIdDirectionPairs.ToList();
        foreach (var pair in rtEntityRoleIdDirectionPairs)
        {
            if (pair.Direction == GraphDirections.Inbound || pair.Direction == GraphDirections.Any)
            {
                filters.Add(Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, pair.RtEntityId.RtId),
                    Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, pair.RtEntityId.CkTypeId),
                    Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.CkRoleId)
                ));
            }

            if (pair.Direction == GraphDirections.Outbound || pair.Direction == GraphDirections.Any)
            {
                filters.Add(Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, pair.RtEntityId.RtId),
                    Builders<RtAssociation>.Filter.Eq(x => x.OriginCkTypeId, pair.RtEntityId.CkTypeId),
                    Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.CkRoleId)
                ));
            }
        }

        if (filters.Count == 0)
        {
            return [];
        }

        var orFilter = Builders<RtAssociation>.Filter.Or(filters);

        var aggregate = RtMongoDbDataSourceAssociations.Aggregate(session);
        var aggregateFluent = aggregate.Match(orFilter);

        var result = await aggregateFluent.ToListAsync();

        var multiplicityResults = new List<RtAssociationsMultiplicityResult>();

        foreach (var pair in rtEntityRoleIdDirectionPairs)
        {
            var count = result.Count(x => x.AssociationRoleId == pair.CkRoleId &&
                                          (((pair.Direction == GraphDirections.Inbound ||
                                             pair.Direction == GraphDirections.Any) &&
                                            x.TargetRtId == pair.RtEntityId.RtId) ||
                                           (pair.Direction == GraphDirections.Outbound ||
                                            pair.Direction == GraphDirections.Any) &&
                                           x.OriginRtId == pair.RtEntityId.RtId));

            CurrentMultiplicity multiplicity = count switch
            {
                >= 2 => CurrentMultiplicity.Many,
                1 => CurrentMultiplicity.One,
                _ => CurrentMultiplicity.Zero
            };

            multiplicityResults.Add(new RtAssociationsMultiplicityResult(pair, multiplicity));
        }


        return multiplicityResults;
    }

    public override async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtOriginTargetPair> rtOriginTargetPair, RtAssociationQueryOptions queryOptions)
    {
        List<FilterDefinition<RtAssociation>> filters = new();
        foreach (var pair in rtOriginTargetPair)
        {
            var filter = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, pair.Origin.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.OriginCkTypeId, pair.Origin.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, pair.Target.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, pair.Target.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.AssociationRoleId)
            );

            filters.Add(filter);
        }

        if (filters.Count == 0)
        {
            return [];
        }

        var orFilter = Builders<RtAssociation>.Filter.Or(filters);

        var aggregate = RtMongoDbDataSourceAssociations.Aggregate(session);
        var aggregateFluent = aggregate.Match(orFilter);

        return await aggregateFluent.ToListAsync();
    }

    public IMongoDbDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, RtAssociation> RtMongoDbDataSourceAssociations { get; }

    public override IDataSourceCollection<OctoObjectId, RtAssociation> RtAssociations =>
        RtMongoDbDataSourceAssociations;

    public IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    public IMongoDbDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    public IMongoDbDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    public IMongoDbDataSourceCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public IMongoDbDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        _logger.LogDebug("Creating collections for tenant '{TenantId}'", TenantId);
        await _repository.CreateCollectionIfNotExistsAsync(CkModels.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkTypes.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkRecords.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkEnums.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkAttributes.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkTypeAssociations.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkAssociationRoles.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkTypeAssociations.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkTypeInheritances.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(CkRecordInheritances.MongoDataSourceMapper, false);
        await _repository.CreateCollectionIfNotExistsAsync(RtMongoDbDataSourceAssociations.MongoDataSourceMapper, true);

        _logger.LogDebug("Creating type root collections for tenant '{TenantId}'", TenantId);
        var ckTypes =
            (await CkTypes.FindManyAsync(session, t => t.IsCollectionRoot && t.ModelState == ModelState.Available))
            .ToList();
        foreach (var ckType in ckTypes)
        {
            _logger.LogDebug("Creating type root collection for '{CkTypeId}'", ckType.CkTypeId);
            var suffix = ckType.CkTypeId.GetCkTypeCollectionName();
            await _repository.CreateCollectionIfNotExistsAsync(new RtEntityMongoDataSourceMapper<RtEntity>(),
                ckType.EnableChangeStreamPreAndPostImages, suffix);
        }

        _logger.LogDebug("Type root collections created for tenant '{TenantId}'", TenantId);
    }

    public async Task UpdateIndexAsync(IOctoSession session, bool includeModelsInStateImporting)
    {
        _logger.LogInformation("Updating indexes of tenant '{TenantId}'", TenantId);

        await using var lockService =
            new RepositoryDistributedLockService(_repositoryClient, _repository, _logger, "index_update_lock");
        await lockService.AcquireLockAsync();

        var aggregate = _ckTypes.Aggregate(session);
        aggregate = aggregate.Match(x => x.IsCollectionRoot == true && includeModelsInStateImporting
            ? x.ModelState == ModelState.Available || x.ModelState == ModelState.Importing
            : x.ModelState == ModelState.Available);

        var ckTypeInfoList = await AggregateCkTypeInfo(aggregate).ToListAsync();
        var collectionRootTypes = ckTypeInfoList.ToList();

        // Pre-fetch all base types for all collection roots to avoid long transactions
        var baseTypesMap =
            await CollectBaseTypesForCollectionRoots(session, collectionRootTypes, includeModelsInStateImporting);

        foreach (var ckTypeInfo in collectionRootTypes)
        {
            var baseTypes = baseTypesMap.TryGetValue(ckTypeInfo.CkTypeId, out var types) ? types : [];
            await UpdateIndexesForCollectionRoot(ckTypeInfo, baseTypes);
        }

        // Create indexes for RtAssociations collection
        await CreateRtAssociationIndexesAsync();

        _logger.LogInformation("Updating indexes of tenant '{TenantId}' completed", TenantId);
    }

    private async Task<Dictionary<CkId<CkTypeId>, List<CkType>>> CollectBaseTypesForCollectionRoots(
        IOctoSession session, IEnumerable<CkTypeInfo> collectionRoots, bool includeModelsInStateImporting)
    {
        var result = new Dictionary<CkId<CkTypeId>, List<CkType>>();
        var collectionRootsList = collectionRoots.ToList();

        if (!collectionRootsList.Any())
        {
            return result;
        }

        // Bulk fetch ALL inheritances in one query
        var allInheritances = await _ckTypeInheritances.FindManyAsync(session, x => true);
        var inheritanceDict = allInheritances.ToLookup(x => x.InheritorCkTypeId, x => x);

        // Collect all type IDs that we might need (all collection roots + their potential base types)
        var allTypeIds = new HashSet<CkId<CkTypeId>>(collectionRootsList.Select(x => x.CkTypeId));

        // Add all potential base type IDs from inheritances
        foreach (var inheritance in allInheritances)
        {
            allTypeIds.Add(inheritance.BaseCkTypeId);
            allTypeIds.Add(inheritance.InheritorCkTypeId);
        }

        // Bulk fetch ALL types that we might need in one query
        var allTypes = await _ckTypes.FindManyAsync(session,
            x => allTypeIds.Contains(x.CkTypeId) && includeModelsInStateImporting
                ? (x.ModelState == ModelState.Available || x.ModelState == ModelState.Importing)
                : x.ModelState == ModelState.Available);
        var typeDict = allTypes.ToDictionary(x => x.CkTypeId, x => x);

        // Build inheritance chains in memory using the fetched data
        foreach (var collectionRoot in collectionRootsList)
        {
            var baseTypes = new List<CkType>();
            var currentTypeId = collectionRoot.CkTypeId;

            // Traverse up the inheritance chain using in-memory data
            while (true)
            {
                var inheritances = inheritanceDict[currentTypeId];
                var inheritance = inheritances.FirstOrDefault();

                if (inheritance != null)
                {
                    if (typeDict.TryGetValue(inheritance.BaseCkTypeId, out var baseType))
                    {
                        baseTypes.Add(baseType);
                        currentTypeId = inheritance.BaseCkTypeId;
                    }
                    else
                    {
                        _logger.LogWarning("Base type '{BaseCkTypeId}' not found for '{InheritorCkTypeId}'",
                            inheritance.BaseCkTypeId, inheritance.InheritorCkTypeId);
                        break;
                    }
                }
                else
                {
                    // No more base types
                    break;
                }
            }

            // Reverse to get most base first
            baseTypes.Reverse();
            result[collectionRoot.CkTypeId] = baseTypes;
        }

        return result;
    }

    private async Task UpdateIndexesForCollectionRoot(CkTypeInfo collectionRootType, List<CkType> baseTypes)
    {
        var name = collectionRootType.CkTypeId.GetCkTypeCollectionName();
        var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
        var collection = _repository.GetCollection(mapper, name);

        _logger.LogDebug("Updating indexes for '{CkTypeId}'", collectionRootType.CkTypeId);

        // Begin tracking index operations
        _indexStateService.BeginTracking();

        try
        {
            // We need to merge text indexes from inherited types, because MongoDB does not support more than one text index
            Dictionary<CkType, List<CkTypeIndex>> regularIndices = new();
            CkTypeIndex? textIndex = null;

            // Process base types from most base to most derived (already collected and ordered)
            foreach (var baseType in baseTypes)
            {
                _logger.LogDebug("Analyzing base type '{CkTypeId}' for collection root '{CollectionRootCkTypeId}'",
                    baseType.CkTypeId, collectionRootType.CkTypeId);
                AnalyseIndex(baseType, regularIndices, ref textIndex);
            }

            // Analyze the collection root type itself
            AnalyseIndex(collectionRootType, regularIndices, ref textIndex);

            // Then analyze the inherited types (descendants), to merge text indexes
            var inheritTypes = collectionRootType.Inheritances.ToDictionary(k => k.CkTypeId, v => v);
            foreach (var ckInheritedTypeInfo in collectionRootType.InheritedTypes.OrderByDescending(x =>
                         x.BaseTypeDepthIndex))
            {
                if (!inheritTypes.TryGetValue(ckInheritedTypeInfo.InheritorCkTypeId, out var inheritCkTypeInfo))
                {
                    _logger.LogWarning("Inherited type '{CkTypeId}' not found in inheritances for '{BaseCkTypeId}'",
                        ckInheritedTypeInfo.InheritorCkTypeId, ckInheritedTypeInfo.BaseCkTypeId);
                    continue;
                }

                AnalyseIndex(inheritCkTypeInfo, regularIndices, ref textIndex);
            }

            // When there is no index defined, we drop all indexes for the collection in case
            // an index was removed in the CK model.
            if (regularIndices.Count == 0 && textIndex == null)
            {
                _logger.LogDebug("Dropping all indexes for '{CkTypeId}'", collectionRootType.CkTypeId);
                await collection.DropAllIndexesAsync(name);

                // Clear index states for affected types
                using var session = await GetSessionAsync();
                var affectedTypeIds = new List<CkId<CkTypeId>> { collectionRootType.CkTypeId };
                affectedTypeIds.AddRange(regularIndices.Keys.Select(k => k.CkTypeId));
                await _indexStateService.ClearIndexStatesForCollectionAsync(session, collection.CollectionName,
                    affectedTypeIds);
                return;
            }

            // Now, we compare the existing indexes with the defined indexes in the CK model.
            var repositoryIndices = await collection.GetIndexListAsync();

            foreach (var keyValuePair in regularIndices)
            {
                int uniqueIndexNumber = 0;

                foreach (CkTypeIndex ckTypeIndex in keyValuePair.Value)
                {
                    await PrepareAndCreateIndex(keyValuePair.Key, ckTypeIndex, repositoryIndices, collection,
                        uniqueIndexNumber, collectionRootType);
                    uniqueIndexNumber++;
                }

                // Let's create the text index if it exists.
                if (keyValuePair.Key == collectionRootType)
                {
                    if (textIndex != null)
                    {
                        await PrepareAndCreateIndex(keyValuePair.Key, textIndex, repositoryIndices, collection,
                            uniqueIndexNumber, collectionRootType);
                    }
                    else
                    {
                        var repositoryTextIndex =
                            repositoryIndices.SingleOrDefault(i => i.IndexType == IndexTypes.Text);
                        if (repositoryTextIndex != null)
                        {
                            _logger.LogDebug("Dropping text index '{IndexName}' for '{CkTypeId}'",
                                repositoryTextIndex.Name, keyValuePair.Key.CkTypeId);
                            await DropIndexWithTracking(collection, repositoryTextIndex.Name,
                                keyValuePair.Key.CkTypeId);
                            // Remove from repositoryIndices so it won't be processed again in the cleanup loop
                            repositoryIndices.Remove(repositoryTextIndex);
                        }
                    }
                }
            }

            // Drop any remaining indexes that are no longer needed
            foreach (var repositoryIndex in repositoryIndices)
            {
                _logger.LogInformation("Dropping obsolete index '{IndexName}' for '{CollectionName}'",
                    repositoryIndex.Name, collection.CollectionName);
                await collection.DropIndexAsync(repositoryIndex.Name);
            }
        }
        finally
        {
            // Always save tracked index states before ending tracking
            try
            {
                using var updateSession = await GetSessionAsync();
                await _indexStateService.BulkUpdateIndexStatesAsync(updateSession);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist index states for collection of CkTypeId '{CkTypeId}'",
                    collectionRootType.CkTypeId);
            }
            finally
            {
                // Always end tracking
                _indexStateService.EndTracking();
            }
        }
    }


    /// <summary>
    /// Prepares index configuration by deduplicating attribute paths and then delegates
    /// to the actual index creation method.
    /// </summary>
    /// <param name="indexDefiningType">The type that defines this index</param>
    /// <param name="ckTypeIndex">The index configuration</param>
    /// <param name="repositoryIndices">Existing indexes in the repository</param>
    /// <param name="collection">The MongoDB collection</param>
    /// <param name="uniqueIndexNumber">Unique number for index naming</param>
    /// <param name="collectionRootType">Optional collection root type info</param>
    private async Task PrepareAndCreateIndex(CkType indexDefiningType, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection,
        int uniqueIndexNumber, CkTypeInfo? collectionRootType = null)
    {
        if (ckTypeIndex.IndexType == IndexTypes.None)
        {
            return;
        }

        // Ensure that attributes are not multiple times in the index. If an attribute is defined multiple times, we remove duplicates.
        HashSet<string> attributePaths = new();
        foreach (CkIndexFields fields in ckTypeIndex.Fields.OrderBy(f => f.Weight))
        {
            var fieldAttributePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string attributeName in fields.AttributeNames)
            {
                if (attributePaths.Add(attributeName))
                {
                    fieldAttributePaths.Add(attributeName);
                }
            }

            fields.AttributeNames = fieldAttributePaths.ToList();
        }

        await CreateOrUpdateIndexWithTracking(collection.CollectionName, ckTypeIndex, repositoryIndices,
            collection, uniqueIndexNumber, collectionRootType, indexDefiningType);
    }

    /// <summary>
    /// Creates or updates an index in MongoDB. Compares with existing indexes to determine if creation
    /// is needed, handles name conflicts by dropping and recreating, and actually performs the index
    /// creation in the database.
    /// </summary>
    /// <param name="collectionName">Name of the MongoDB collection</param>
    /// <param name="ckTypeIndex">The index configuration to create</param>
    /// <param name="repositoryIndices">Existing indexes in the repository (modified to track processed indexes)</param>
    /// <param name="collection">The MongoDB collection interface</param>
    /// <param name="uniqueIndexNumber">Unique number for index naming</param>
    /// <param name="collectionRootType">Optional collection root type info (required for Unique/UniqueNotDeleted)</param>
    /// <param name="indexDefiningType">Optional type that defines this index (required for Unique/UniqueNotDeleted)</param>
    private async Task<bool> CreateOrUpdateIndex<TKey, TDocument>(string collectionName, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<TKey, TDocument> collection, int uniqueIndexNumber,
        CkTypeInfo? collectionRootType = null, CkType? indexDefiningType = null)
        where TDocument : class, new()
        where TKey : notnull
    {
        // For system indexes (like RtAssociation indexes) where no defining type exists,
        // fall back to collection name. Otherwise, use the type name for backward compatibility.
        var indexName = indexDefiningType != null
            ? indexDefiningType.CkTypeId.GetCkTypeCollectionName() + "_" + uniqueIndexNumber
            : collectionName + "_" + uniqueIndexNumber;

        // We check if the index already exists in the repository,
        // by comparing type, the fields' weight and the attribute paths
        // The fields are compared case-insensitive, so we use the attribute names directly.
        var repositoryIndex = repositoryIndices.SingleOrDefault(i =>
            i.CompareToInSequence(ckTypeIndex));

        // If found, check if the name matches what we expect
        if (repositoryIndex != null)
        {
            // Remove the matching index from the list so it won't be dropped later
            repositoryIndices.Remove(repositoryIndex);

            // If the name doesn't match, we need to drop the old one and create with the correct name
            if (repositoryIndex.Name != indexName)
            {
                _logger.LogInformation(
                    "Index with correct definition but wrong name: '{ActualName}' should be '{ExpectedName}'. Recreating with correct name",
                    repositoryIndex.Name, indexName);

                // Drop the incorrectly named index
                await collection.DropIndexAsync(repositoryIndex.Name);

                // Fall through to create the index with the correct name
            }
            else
            {
                _logger.LogDebug("Index '{IndexName}' already exists for '{CollectionName}', skipping creation",
                    indexName, collectionName);
                return false; // Index already exists with correct name, not created
            }
        }

        // Check if there's an existing index with this name (regardless of configuration)
        var existingIndexWithSameName = repositoryIndices.FirstOrDefault(i => i.Name == indexName);
        if (existingIndexWithSameName != null)
        {
            repositoryIndices.Remove(existingIndexWithSameName);

            _logger.LogInformation("Index '{IndexName}' exists with different configuration, recreating",
                indexName);
            // Index exists but has wrong configuration - drop it first
            await collection.DropIndexAsync(indexName);
        }

        switch (ckTypeIndex.IndexType)
        {
            case IndexTypes.Ascending:
                await collection.CreateAscendingIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames));
                break;
            case IndexTypes.Text:
                await collection.CreateTextIndexAsync(indexName, ckTypeIndex.Language ?? "en",
                    ckTypeIndex.Fields);
                break;
            case IndexTypes.Unique:
                var uniqueTypeIds = CollectTypeIdsForIndex(collectionRootType, indexDefiningType, "Unique");

                await collection.CreateUniqueIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames),
                    uniqueTypeIds);
                break;
            case IndexTypes.UniqueNotDeleted:
                var typeIds = CollectTypeIdsForIndex(collectionRootType, indexDefiningType, "UniqueNotDeleted");

                await collection.CreateUniqueNotDeletedIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames),
                    typeIds);
                break;
            default:
                throw OperationFailedException.IndexTypeNotSupported(ckTypeIndex.IndexType);
        }

        return true; // Index was created
    }

    /// <summary>
    /// Wrapper for CreateOrUpdateIndex that adds index state tracking
    /// </summary>
    private async Task CreateOrUpdateIndexWithTracking<TKey, TDocument>(string collectionName, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<TKey, TDocument> collection, int uniqueIndexNumber,
        CkTypeInfo? collectionRootType = null, CkType? indexDefiningType = null)
        where TDocument : class, new()
        where TKey : notnull
    {
        // Generate index name using the same logic as CreateOrUpdateIndex for consistency
        var indexName = indexDefiningType != null
            ? indexDefiningType.CkTypeId.GetCkTypeCollectionName() + "_" + uniqueIndexNumber
            : collectionName + "_" + uniqueIndexNumber;

        try
        {
            // Call the original method and check if index was actually created
            var wasCreated = await CreateOrUpdateIndex(collectionName, ckTypeIndex, repositoryIndices, collection,
                uniqueIndexNumber, collectionRootType, indexDefiningType);

            // Only track if the index was actually created (not if it already existed)
            if (wasCreated && indexDefiningType != null)
            {
                _indexStateService.TrackIndexOperation(indexDefiningType.CkTypeId, indexName, collection.CollectionName,
                    IndexState.Applied);
            }
        }
        catch (MongoCommandException ex)
        {
            // Track failed index creation
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (indexDefiningType != null && indexName != null)
            {
                _indexStateService.TrackIndexOperation(indexDefiningType.CkTypeId, indexName, collection.CollectionName,
                    IndexState.Failed, ex.Message);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Index creation failed but cannot track (indexDefiningType={TypeId}, indexName={IndexName})",
                    indexDefiningType?.CkTypeId.ToString() ?? "NULL", indexName ?? "NULL");
            }

            _logger.LogWarning(ex,
                "Failed to create index '{IndexName}' of type {IndexType} for '{CollectionName}': {ErrorMessage}",
                indexName, ckTypeIndex.IndexType, collection.CollectionName, ex.Message);

            // Don't rethrow - continue with other indexes
        }
    }

    /// <summary>
    /// Wrapper for DropIndexAsync that adds index state tracking
    /// </summary>
    private async Task DropIndexWithTracking<TKey, TDocument>(IMongoDbDataSourceCollection<TKey, TDocument> collection,
        string indexName, CkId<CkTypeId> typeId)
        where TDocument : class, new()
        where TKey : notnull
    {
        await collection.DropIndexAsync(indexName);
        _indexStateService.RemoveIndexState(typeId, indexName, collection.CollectionName);
    }

    private List<RtCkId<CkTypeId>> CollectTypeIdsForIndex(CkTypeInfo? collectionRootType, CkType? indexDefiningType,
        string indexTypeName)
    {
        if (collectionRootType == null || indexDefiningType == null)
        {
            throw DatabaseCkModelRepositoryException.IndexTypeNeedsCkTypeInfoAndIndexDefiningTypes(indexTypeName);
        }

        // Collect type IDs: the index-defining type + types that derive from it
        var typeIds = new List<RtCkId<CkTypeId>> { indexDefiningType.CkTypeId.ToRtCkId() };

        // Find all types that derive from the index-defining type (not the collection root)
        var derivedTypeIds = GetDerivedTypeIds(indexDefiningType.CkTypeId.ToRtCkId(), collectionRootType);
        typeIds.AddRange(derivedTypeIds);

        return typeIds;
    }

    private IEnumerable<RtCkId<CkTypeId>> GetDerivedTypeIds(RtCkId<CkTypeId> baseRtCkTypeId, CkTypeInfo ckTypeInfo)
    {
        // Get all inheritance relationships where the base type is our target type
        var directDerived = ckTypeInfo.InheritedTypes
            .Where(inheritance => inheritance.BaseCkTypeId.Equals(baseRtCkTypeId))
            .Select(inheritance => inheritance.InheritorCkTypeId.ToRtCkId())
            .ToList();

        var allDerived = new HashSet<RtCkId<CkTypeId>>(directDerived);

        // Recursively find derived types of the derived types
        foreach (var derivedType in directDerived)
        {
            var nestedDerived = GetDerivedTypeIds(derivedType, ckTypeInfo);
            foreach (var nestedType in nestedDerived)
            {
                allDerived.Add(nestedType);
            }
        }

        return allDerived;
    }

    private void AnalyseIndex(CkType ckTypeInfo, Dictionary<CkType, List<CkTypeIndex>> regularIndices,
        ref CkTypeIndex? textIndex)
    {
        var indices = new List<CkTypeIndex>();
        regularIndices.Add(ckTypeInfo, indices);

        if (ckTypeInfo.Indexes != null)
        {
            // Add regular indexes (Ascending, Unique, UniqueNotDeleted)
            indices.AddRange(
                ckTypeInfo.Indexes.Where(i =>
                    i.IndexType == IndexTypes.Ascending ||
                    i.IndexType == IndexTypes.Unique ||
                    i.IndexType == IndexTypes.UniqueNotDeleted).ToList());

            // Handle text indexes separately (only one text index allowed per collection)
            foreach (var index in ckTypeInfo.Indexes.Where(i => i.IndexType == IndexTypes.Text))
            {
                if (textIndex == null)
                {
                    textIndex = index;
                    continue;
                }

                if (textIndex.Language != index.Language)
                {
                    _logger.LogWarning(
                        "Text index for '{CkTypeId}' has different language '{Language}' than existing text index '{ExistingLanguage}'",
                        ckTypeInfo.CkTypeId.ToString(), index.Language, textIndex.Language);
                }

                textIndex.Fields = textIndex.Fields.Union(index.Fields).ToList();
            }
        }
    }

    public async Task<IOctoSession> CreateSessionAsync()
    {
        return await _repositoryClient.GetSessionAsync();
    }

    public async Task CreateRtAssociationIndexesAsync()
    {
        _logger.LogDebug("Creating indexes for RtAssociations collection");

        var collection = RtMongoDbDataSourceAssociations;

        // Get existing indexes to check if they already exist
        var existingIndexes = await collection.GetIndexListAsync();

        var ckTypeIndices = new List<CkTypeIndex>
        {
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
        };

        int uniqueIndexNumber = 0;

        foreach (CkTypeIndex ckTypeIndex in ckTypeIndices)
        {
            _ = await CreateOrUpdateIndex(collection.CollectionName, ckTypeIndex, existingIndexes, collection,
                uniqueIndexNumber++);
        }

        // We cleanup old indexes that are not defined anymore
        foreach (var repositoryIndex in existingIndexes)
        {
            _logger.LogDebug("Dropping old index '{IndexName}' for RtAssociations collection",
                repositoryIndex.Name);
            await collection.DropIndexAsync(repositoryIndex.Name);
        }
    }

    private IAggregateFluent<CkTypeInfo> AggregateCkTypeInfo(IAggregateFluent<CkType> aggregate)
    {
        return aggregate.GraphLookup(_ckTypeInheritances.GetMongoCollection(),
                x => x.InheritorCkTypeId,
                x => x.BaseCkTypeId,
                x => x.CkTypeId,
                (CkTypeInfo x) => x.InheritedTypes, (CkInheritedTypeInfo i) => i.BaseTypeDepthIndex)
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypes.CollectionName,
                "inheritedTypes.inheritorCkTypeId",
                "_id",
                "Inheritances");
    }
}
