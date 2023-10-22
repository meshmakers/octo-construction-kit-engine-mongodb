using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

internal sealed class DatabaseContext : RepositoryDataSource, IDatabaseContext
{
    private readonly IRepositoryInternal _repository;
    private readonly IDatabaseCollection<CkId<CkTypeId>, CkType> _ckTypes;
    private readonly IDatabaseCollection<OctoObjectId, CkTypeInheritance> _ckTypeInheritances;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;

    public DatabaseContext(ILoggerFactory loggerFactory, string tenantId, string dataSourceHost, string databaseName, string databaseUser, string? databasePassword,
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
        }), databaseName, tenantId)
    {
    }

    public DatabaseContext(IRepositoryClient repositoryClient, string databaseName, string tenantId)
    : base(tenantId)
    {
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
        
        RtDatabaseAssociations = _repository.GetCollection(new RtAssociationMongoDataSourceMapper());
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
    
    public IDatabaseCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkId<CkTypeId> ckTypeId) where TEntity : RtEntity, new()
    {
        var suffix = ckTypeId.SemanticVersionedFullName.Replace("/", "_");
        var mapper = new RtEntityMongoDataSourceMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    public IDatabaseCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>() where TEntity : RtEntity, new()
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

            var r = await RtDatabaseAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (direction == GraphDirections.Outbound || direction == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, rtEntityId.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, rtEntityId.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, ckRoleId)
            );

            var r = await RtDatabaseAssociations.GetTotalCountAsync(session, filterDefinition);
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

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, string ckTypeId)
    {
        var ckEntity = await GetCkEntityAsync(session, ckTypeId);
        return await GetCkTypeInfoAsync(session, ckEntity);
    }

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkType ckType)
    {
        var filter = Builders<CkType>.Filter.BuildIdFilter(ckType.CkTypeId);

        var aggregate = _ckTypes.Aggregate(session).Match(filter);

        return await AggregateCkTypeInfo(aggregate).SingleOrDefaultAsync();
    }

    public IDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    public IDatabaseCollection<OctoObjectId, RtAssociation> RtDatabaseAssociations { get; }
    public override IDataSourceCollection<OctoObjectId, RtAssociation> RtAssociations => RtDatabaseAssociations;
    public IDatabaseCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    public IDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    public IDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    public IDatabaseCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public IDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    public IDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    public IDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    public IDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        var ckEntities = (await CkTypes.GetAsync(session)).ToList();
        foreach (var ckEntity in ckEntities)
        {
            if (!ckEntity.IsAbstract)
            {
                var suffix = ckEntity.CkTypeId.SemanticVersionedFullName.Replace("/", "_");
                await _repository.CreateCollectionIfNotExistsAsync<RtEntity>(ckEntity.EnableChangeStreamPreAndPostImages, suffix);
            }
        }
    }

    public async Task UpdateIndexAsync(IOctoSession session)
    {
        var ckEntities = (await CkTypes.GetAsync(session)).ToList();

        foreach (var ckEntity in ckEntities)
        {
            var name = ckEntity.CkTypeId.SemanticVersionedFullName.Replace(".", "_");

            var collection = GetRtDatabaseCollection<RtEntity>(ckEntity.CkTypeId);
            await collection.DropIndexAsync(name);
            // TODO: Hard coded database name not possible. Use from configuration
            await collection.DropIndexAsync("OctoSystem");
        }

        foreach (var ckEntity in ckEntities)
        {
            if (ckEntity.Indexes != null)
            {
                foreach (var index in ckEntity.Indexes)
                {
                    if (index.IndexType == IndexTypes.None)
                    {
                        continue;
                    }

                    var collection = GetRtDatabaseCollection<RtEntity>(ckEntity.CkTypeId);

                    var newName = ckEntity.CkTypeId.SemanticVersionedFullName.Replace(".", "_") + "_" + ObjectId.GenerateNewId();

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
                            throw new NotImplementedException($"Index type {index.IndexType} is not implemented.");
                    }
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
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkTypeAssociation>(),
                "baseTypes.originCkTypeId",
                "originCkTypeId",
                "associations.out.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkTypeAssociation>(),
                Constants.IdField,
                "originCkTypeId",
                "associations.out.owned")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkTypeAssociation>(),
                "baseTypes.originCkTypeId",
                "targetCkTypeId",
                "associations.in.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkTypeAssociation>(),
                Constants.IdField,
                "targetCkTypeId",
                "associations.in.owned");
    }

    private async Task<CkType> GetCkEntityAsync(IOctoSession session, string ckTypeId)
    {
        var ckEntity = await CkTypes.DocumentAsync(session, ckTypeId);
        if (ckEntity == null)
        {
            throw new EntityNotFoundException($"'{ckTypeId}' does not exist in database.");
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