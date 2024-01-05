using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal sealed class MongoDbRepositoryDataSource : RepositoryDataSource, IMongoDbRepositoryDataSource
{
    private readonly ICkCacheService _ckCacheService;
    private readonly IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> _ckTypeInheritances;
    private readonly IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> _ckTypes;
    private readonly IRepositoryInternal _repository;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;

    public MongoDbRepositoryDataSource(ILoggerFactory loggerFactory, ICkCacheService ckCacheService, string tenantId, string dataSourceHost,
        string databaseName,
        string databaseUser, string? databasePassword,
        string authenticationDatabaseName, bool useTls, bool allowInsecureTls)
        : this(new MongoRepositoryClient(loggerFactory.CreateLogger<MongoRepositoryClient>(), new MongoConnectionOptions
        {
            MongoDbHost = dataSourceHost,
            MongoDbUsername = databaseUser,
            MongoDbPassword = databasePassword,
            DatabaseName = databaseName,
            AuthenticationSource = authenticationDatabaseName,
            UseTls = useTls,
            AllowInsecureTls = allowInsecureTls
        }), ckCacheService, databaseName, tenantId)
    {
    }

    public MongoDbRepositoryDataSource(IRepositoryClient repositoryClient, ICkCacheService ckCacheService, string databaseName,
        string tenantId)
        : base(tenantId)
    {
        ArgumentValidation.ValidateString(databaseName, nameof(databaseName));

        _repositoryClient = repositoryClient;
        _repository = (IRepositoryInternal)_repositoryClient.GetRepository(databaseName);
        _ckCacheService = ckCacheService;

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


    public IOctoSession StartSession()
    {
        var session = _repositoryClient.StartSession();
        return session;
    }

    public override IDataSourceCollection<OctoObjectId, TEntity> GetRtCollection<TEntity>(CkId<CkTypeId> ckTypeId)
    {
        return GetRtDatabaseCollection<TEntity>(ckTypeId);
    }

    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkId<CkTypeId> ckTypeId)
        where TEntity : RtEntity, new()
    {
        if (!_ckCacheService.TryGetCkType(TenantId, ckTypeId, out var ckTypeGraph))
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(TenantId, ckTypeId);
        }

        if (ckTypeGraph.DefiningCollectionRootCkTypeId == null)
        {
            throw OperationFailedException.CkTypeHasNoDefiningCollectionRoot(ckTypeId);
        }

        var suffix = ckTypeGraph.DefiningCollectionRootCkTypeId.Value.GetCkTypeCollectionName();
        var mapper = new RtEntityMongoDataSourceMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>() where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        return GetRtDatabaseCollection<TEntity>(ckTypeId);
    }

    public override async Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(
        IOctoSession session, RtEntityId rtEntityId, CkId<CkAssociationRoleId> ckRoleId,
        GraphDirections direction)
    {
        long counter = 0;
        if (direction == GraphDirections.Inbound || direction == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, rtEntityId.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, ckRoleId)
            );

            var r = await RtMongoDbDataSourceAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (direction == GraphDirections.Outbound || direction == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, rtEntityId.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, ckRoleId)
            );

            var r = await RtMongoDbDataSourceAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (counter >= 2)
        {
            return CurrentMultiplicity.Many;
        }

        if (counter == 1)
        {
            return CurrentMultiplicity.One;
        }

        return CurrentMultiplicity.Zero;
    }


    public async Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session)
    {
        var aggregate = _ckTypes.Aggregate(session);

        return await AggregateCkTypeInfo(aggregate).ToListAsync();
    }

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkId<CkTypeId> ckTypeId)
    {
        var ckEntity = await GetCkTypeAsync(session, ckTypeId);
        return await GetCkTypeInfoAsync(session, ckEntity);
    }

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkType ckType)
    {
        var filter = Builders<CkType>.Filter.BuildIdFilter(ckType.CkTypeId);

        var aggregate = _ckTypes.Aggregate(session).Match(filter);

        return await AggregateCkTypeInfo(aggregate).SingleOrDefaultAsync();
    }

    public IDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, RtAssociation> RtMongoDbDataSourceAssociations { get; }
    public override IDataSourceCollection<OctoObjectId, RtAssociation> RtAssociations => RtMongoDbDataSourceAssociations;
    public IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    public IMongoDbDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    public IMongoDbDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    public IMongoDbDataSourceCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public IDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    public IDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    public IDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        await _repository.CreateCollectionIfNotExistsAsync(new RtAssociationMongoDataSourceMapper(), true);

        var ckTypes = (await CkTypes.FindManyAsync(session, t => t.IsCollectionRoot)).ToList();
        foreach (var ckType in ckTypes)
        {
            var suffix = ckType.CkTypeId.GetCkTypeCollectionName();
            await _repository.CreateCollectionIfNotExistsAsync(new RtEntityMongoDataSourceMapper<RtEntity>(),
                ckType.EnableChangeStreamPreAndPostImages, suffix);
        }
    }

    public async Task UpdateIndexAsync(IOctoSession session)
    {
        var ckTypes = (await CkTypes.FindManyAsync(session, t => t.IsCollectionRoot)).ToList();

        foreach (var ckType in ckTypes)
        {
            var name = ckType.CkTypeId.GetCkTypeCollectionName();

            var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
            var collection = _repository.GetCollection(mapper, name);
            await collection.DropIndexAsync(name);
        }

        foreach (var ckType in ckTypes)
        {
            if (ckType.Indexes == null)
            {
                continue;
            }

            foreach (var index in ckType.Indexes)
            {
                if (index.IndexType == IndexTypes.None)
                {
                    continue;
                }

                var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
                var collection = _repository.GetCollection(mapper, ckType.CkTypeId.GetCkTypeCollectionName());

                var newName = ckType.CkTypeId.GetCkTypeCollectionName();

                switch (index.IndexType)
                {
                    case IndexTypes.Ascending:
                        await collection.CreateAscendingIndexAsync(newName,
                            index.Fields.SelectMany(x => x.AttributeNames));
                        break;
                    case IndexTypes.Text:
                        await collection.CreateTextIndexAsync(newName, index.Language ?? "en", index.Fields);
                        break;
                    default:
                        throw OperationFailedException.IndexTypeNotImplemented(index.IndexType);
                }
            }
        }
    }

    private IAggregateFluent<CkTypeInfo> AggregateCkTypeInfo(IAggregateFluent<CkType> aggregate)
    {
        return aggregate.GraphLookup(_ckTypeInheritances.GetMongoCollection(),
                x => x.BaseCkTypeId,
                x => x.InheritorCkTypeId,
                x => x.CkTypeId,
                (CkTypeInfo x) => x.BaseTypes, (CkBaseTypeInfo i) => i.BaseTypeDepthIndex)
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypeAssociations.CollectionName,
                "baseTypes.originCkTypeId",
                "originCkTypeId",
                "associations.out.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypeAssociations.CollectionName,
                Constants.IdField,
                "originCkTypeId",
                "associations.out.owned")
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypeAssociations.CollectionName,
                "baseTypes.originCkTypeId",
                "targetCkTypeId",
                "associations.in.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypeAssociations.CollectionName,
                Constants.IdField,
                "targetCkTypeId",
                "associations.in.owned");
    }

    private async Task<CkType> GetCkTypeAsync(IOctoSession session, CkId<CkTypeId> ckTypeId)
    {
        var ckEntity = await CkTypes.DocumentAsync(session, ckTypeId);
        if (ckEntity == null)
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(TenantId, ckTypeId);
        }

        return ckEntity;
    }

    #region Large Binaries

    public async Task<ObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default)
    {
        return await _repository.UploadLargeBinaryAsync(filename, contentType, stream, cancellationToken);
    }

    public async Task ReplaceLargeBinaryAsync(ObjectId largeBinaryId, string filename, string contentType,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        await _repository.ReplaceLargeBinaryAsync(largeBinaryId, filename, contentType, stream, cancellationToken);
    }

    public async Task DeleteLargeBinaryAsync(ObjectId largeBinaryId, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteLargeBinaryAsync(largeBinaryId, cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.DownloadLargeBinaryAsync(largeBinaryId, cancellationToken);
    }

    public async Task<IDownloadInfo> GetLargeBinaryAsync(ObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetLargeBinaryAsync(largeBinaryId, cancellationToken);
    }

    #endregion Large Binaries
}