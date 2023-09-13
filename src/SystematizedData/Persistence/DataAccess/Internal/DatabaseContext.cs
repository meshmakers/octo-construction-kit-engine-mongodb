using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

internal sealed class DatabaseContext : IDatabaseContext
{
    private readonly IRepositoryInternal _repository;
    private readonly IDatabaseCollection<DatabaseEntities.CkModel> _ckModels;
    private readonly IDatabaseCollection<CkType> _ckTypes;
    private readonly IDatabaseCollection<CkTypeInheritance> _ckTypeInheritances;
    private readonly IDatabaseCollection<CkAttribute> _ckAttributes;
    
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;

    public DatabaseContext(ILoggerFactory loggerFactory, string dataSourceHost, string databaseName, string databaseUser, string? databasePassword,
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
        }), databaseName)
    {
    }

    public DatabaseContext(IRepositoryClient repositoryClient, string databaseName)
    {
        ArgumentValidation.ValidateString(databaseName, nameof(databaseName));

        _repositoryClient = repositoryClient;
        _repository = (IRepositoryInternal)_repositoryClient.GetRepository(databaseName);

        _ckModels = _repository.GetCollection<DatabaseEntities.CkModel>();
        CkModels = _ckModels;
        CkModelsInternal = _ckModels;
        
        _ckTypes = _repository.GetCollection<CkType>();
        CkTypes = _ckTypes;
        CkTypesInternal = _ckTypes;
        
        CkRecords = _repository.GetCollection<CkRecord>();
        
        CkEnums = _repository.GetCollection<CkEnum>();
        _ckAttributes = _repository.GetCollection<CkAttribute>();
        CkAttributes = _ckAttributes;
        CkAttributesInternal = _ckAttributes;
        
        CkAssociationRoles = _repository.GetCollection<CkAssociationRole>();
        CkTypeAssociations = _repository.GetCollection<CkTypeAssociation>();
        
        _ckTypeInheritances = _repository.GetCollection<CkTypeInheritance>();
        CkTypeInheritances = _ckTypeInheritances;
        
        CkRecordInheritances = _repository.GetCollection<CkRecordInheritance>();
        RtAssociations = _repository.GetCollection<RtAssociation>();
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

    public IDatabaseCollection<TEntity> GetRtCollection<TEntity>(CkId<CkTypeId> ckTypeId) where TEntity : RtEntity, new()
    {
        var suffix = ckTypeId.SemanticVersionedFullName.Replace("/", "_");
        return _repository.GetCollection<TEntity>(suffix);
    }

    public IDatabaseCollection<TEntity> GetRtCollection<TEntity>() where TEntity : RtEntity, new()
    {
        var ckTypeId = RtEntityExtensions.GetCkTypeId<TEntity>();
        return GetRtCollection<TEntity>(ckTypeId);
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

    public ICkDatabaseCollection<DatabaseEntities.CkModel> CkModels { get; }
    public IDatabaseCollection<DatabaseEntities.CkModel> CkModelsInternal { get; }
    public ICkDatabaseCollection<CkType> CkTypes { get; }
    public IDatabaseCollection<CkType> CkTypesInternal { get; }
    public ICkDatabaseCollection<CkRecord> CkRecords { get; }
    public ICkDatabaseCollection<CkEnum> CkEnums { get; }
    public ICkDatabaseCollection<CkAttribute> CkAttributes { get; }
    public IDatabaseCollection<CkAttribute> CkAttributesInternal { get; }
    public ICkDatabaseCollection<CkAssociationRole> CkAssociationRoles { get; }
    public ICkDatabaseCollection<CkTypeAssociation> CkTypeAssociations { get; }
    public ICkDatabaseCollection<CkTypeInheritance> CkTypeInheritances { get; }
    public ICkDatabaseCollection<CkRecordInheritance> CkRecordInheritances { get; }
    public IDatabaseCollection<RtAssociation> RtAssociations { get; }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        var ckEntities = (await _ckTypes.GetAsync(session)).ToList();
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
        var ckEntities = (await _ckTypes.GetAsync(session)).ToList();

        foreach (var ckEntity in ckEntities)
        {
            var name = ckEntity.CkTypeId.SemanticVersionedFullName.Replace(".", "_");

            var collection = GetRtCollection<RtEntity>(ckEntity.CkTypeId);
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

                    var collection = GetRtCollection<RtEntity>(ckEntity.CkTypeId);

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
        var ckEntity = await _ckTypes.DocumentAsync(session, ckTypeId);
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