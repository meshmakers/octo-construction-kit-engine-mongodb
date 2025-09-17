using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.Repositories;

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
        IEnumerable<RtOriginTargetPair> rtOriginTargetPair)
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
        var ckTypes = (await CkTypes.FindManyAsync(session, t => t.IsCollectionRoot)).ToList();
        foreach (var ckType in ckTypes)
        {
            _logger.LogDebug("Creating type root collection for '{CkTypeId}'", ckType.CkTypeId);
            var suffix = ckType.CkTypeId.GetCkTypeCollectionName();
            await _repository.CreateCollectionIfNotExistsAsync(new RtEntityMongoDataSourceMapper<RtEntity>(),
                ckType.EnableChangeStreamPreAndPostImages, suffix);
        }

        _logger.LogDebug("Type root collections created for tenant '{TenantId}'", TenantId);
    }

    public async Task UpdateIndexAsync(IOctoSession session)
    {
        var aggregate = _ckTypes.Aggregate(session);
        aggregate = aggregate.Match(x => x.IsCollectionRoot == true);

        var ckTypeInfoList = await AggregateCkTypeInfo(aggregate).ToListAsync();
        var ckTypeInfos = ckTypeInfoList.ToList();

        foreach (var ckTypeInfo in ckTypeInfos)
        {
            await CreateIndexIfNotExists(ckTypeInfo);
        }

        // Create indexes for RtAssociations collection
        await CreateRtAssociationIndexesAsync();
    }

    private async Task CreateIndexIfNotExists(CkTypeInfo ckTypeInfo)
    {
        var name = ckTypeInfo.CkTypeId.GetCkTypeCollectionName();
        var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
        var collection = _repository.GetCollection(mapper, name);

        _logger.LogDebug("Updating indexes for '{CkTypeId}'", ckTypeInfo.CkTypeId);

        // We need to merge text indexes from inherited types, because MongoDB does not support more than one text index
        Dictionary<CkType, List<CkTypeIndex>> regularIndices = new();
        CkTypeIndex? textIndex = null;

        // Analyze the base type first
        AnalyseIndex(ckTypeInfo, regularIndices, ref textIndex);

        // Then analyze the inherited types, to merge text indexes
        var inheritTypes = ckTypeInfo.Inheritances.ToDictionary(k => k.CkTypeId, v => v);
        foreach (var ckInheritedTypeInfo in ckTypeInfo.InheritedTypes.OrderByDescending(x => x.BaseTypeDepthIndex))
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
            _logger.LogDebug("Dropping all indexes for '{CkTypeId}'", ckTypeInfo.CkTypeId);
            await collection.DropAllIndexesAsync(name);
            return;
        }

        // Now, we compare the existing indexes with the defined indexes in the CK model.
        var repositoryIndices = await collection.GetIndexListAsync(name);

        foreach (var keyValuePair in regularIndices)
        {
            int uniqueIndexNumber = 0;

            foreach (CkTypeIndex ckTypeIndex in keyValuePair.Value)
            {
                await CreateIndexIfNotExists(keyValuePair.Key, ckTypeIndex, repositoryIndices, collection,
                    uniqueIndexNumber);
                uniqueIndexNumber++;
            }

            // Let's create the text index if it exists.
            if (keyValuePair.Key == ckTypeInfo)
            {
                if (textIndex != null)
                {
                    await CreateIndexIfNotExists(keyValuePair.Key, textIndex, repositoryIndices, collection,
                        uniqueIndexNumber);
                }
                else
                {
                    var repositoryTextIndex = repositoryIndices.SingleOrDefault(i => i.IndexType == IndexTypes.Text);
                    if (repositoryTextIndex != null)
                    {
                        _logger.LogDebug("Dropping text index '{IndexName}' for '{CkTypeId}'",
                            repositoryTextIndex.Name, keyValuePair.Key.CkTypeId);
                        await collection.DropIndexAsync(repositoryTextIndex.Name);
                    }
                }
            }
        }
    }

    private async Task CreateIndexIfNotExists(CkType ckType, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection,
        int uniqueIndexNumber)
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

        await CreateIndexIfNotExists(ckType.CkTypeId.GetCkTypeCollectionName(), ckTypeIndex, repositoryIndices,
            collection, uniqueIndexNumber);
    }

    private async Task CreateIndexIfNotExists<TKey, TDocument>(string collectionName, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<TKey, TDocument> collection, int uniqueIndexNumber)
        where TDocument : class, new()
        where TKey : notnull
    {
        var indexName = collectionName + "_" + uniqueIndexNumber;

        // We check if the index already exists in the repository,
        // by comparing type, the fields' weight and the attribute paths
        // The fields are compared case-insensitive, so we use the attribute names directly.
        var repositoryIndexList = repositoryIndices.Where(i =>
            i.CompareToInSequence(ckTypeIndex));

        var repositoryIndex = repositoryIndices.SingleOrDefault(i =>
            i.CompareToInSequence(ckTypeIndex));

        // If found, we skip the creation of the index.
        if (repositoryIndex != null)
        {
            _logger.LogDebug("Index '{IndexName}' already exists for '{CollectionName}', skipping creation",
                indexName, collectionName);
            repositoryIndices.Remove(repositoryIndex);
            return;
        }

        // If the index does not exist, we create it.
        await collection.DropIndexAsync(indexName);

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
            default:
                throw OperationFailedException.IndexTypeNotSupported(ckTypeIndex.IndexType);
        }
    }

    private void AnalyseIndex(CkType ckTypeInfo, Dictionary<CkType, List<CkTypeIndex>> regularIndices,
        ref CkTypeIndex? textIndex)
    {
        var ckTypeIndices = new List<CkTypeIndex>();

        if (ckTypeInfo.IsCollectionRoot)
        {
            ckTypeIndices.Add(new()
            {
                IndexType = IndexTypes.Ascending,
                Fields = [new CkIndexFields { AttributeNames = [nameof(RtEntity.RtWellKnownName).ToCamelCase()] }]
            });
            ckTypeIndices.Add(
                new()
                {
                    IndexType = IndexTypes.Ascending,
                    Fields =
                    [
                        new CkIndexFields
                        {
                            AttributeNames = [nameof(RtEntity.CkTypeId).ToCamelCase(), Constants.IdField]
                        }
                    ]
                });
        }

        ;
        regularIndices.Add(ckTypeInfo, ckTypeIndices);

        if (ckTypeInfo.Indexes != null)
        {
            ckTypeIndices.AddRange(
                ckTypeInfo.Indexes.Where(i => i.IndexType == IndexTypes.Ascending).ToList());

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

        var ckTypeIndices = new List<CkTypeIndex>();
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });
        ckTypeIndices.Add(new()
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
        });

        int uniqueIndexNumber = 0;

        foreach (CkTypeIndex ckTypeIndex in ckTypeIndices)
        {
            await CreateIndexIfNotExists(collection.CollectionName, ckTypeIndex, existingIndexes, collection,
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
