using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

internal sealed class DatabaseContext : IDatabaseContext
{
    private readonly IRepositoryInternal _repository;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;

    public DatabaseContext(string dataSourceHost, string databaseName, string databaseUser, string? databasePassword,
        string authenticationDatabaseName, bool useTls, bool allowInsecureTls)
        : this(new MongoRepositoryClient(new MongoConnectionOptions
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

        CkModels = _repository.GetCollection<DatabaseEntities.CkModel>();
        CkEntities = _repository.GetCollection<CkEntity>();
        CkAttributes = _repository.GetCollection<CkAttribute>();
        CkAssociationRoles = _repository.GetCollection<CkAssociationRole>();
        CkEntityAssociations = _repository.GetCollection<CkEntityAssociation>();
        CkEntityInheritances = _repository.GetCollection<CkEntityInheritance>();
        RtAssociations = _repository.GetCollection<RtAssociation>();
    }

    public async Task<IOctoSession> StartSessionAsync()
    {
        var session = await _repositoryClient.StartSessionAsync();
        return session;
    }


    public IOctoSession StartSession()
    {
        var session = _repositoryClient.StartSession();
        return session;
    }

    public ICachedCollection<TEntity> GetRtCollection<TEntity>(CkId<CkTypeId> ckId) where TEntity : RtEntity, new()
    {
        var suffix = ckId.SemanticVersionedFullName.Replace("/", "_");
        return _repository.GetCollection<TEntity>(suffix);
    }

    public ICachedCollection<TEntity> GetRtCollection<TEntity>() where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        return GetRtCollection<TEntity>(ckId);
    }

    public async Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session)
    {
        var aggregate = CkEntities.Aggregate(session);

        return await AggregateCkTypeInfo(aggregate).ToListAsync();
    }

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, string ckId)
    {
        var ckEntity = await GetCkEntityAsync(session, ckId);
        return await GetCkTypeInfoAsync(session, ckEntity);
    }

    public async Task<CkTypeInfo> GetCkTypeInfoAsync(IOctoSession session, CkEntity ckId)
    {
        var filter = Builders<CkEntity>.Filter.BuildIdFilter(ckId.CkId);

        var aggregate = CkEntities.Aggregate(session).Match(filter);

        return await AggregateCkTypeInfo(aggregate).SingleOrDefaultAsync();
    }

    public ICachedCollection<DatabaseEntities.CkModel> CkModels { get; }
    public ICachedCollection<CkEntity> CkEntities { get; }
    public ICachedCollection<CkAttribute> CkAttributes { get; }
    public ICachedCollection<CkAssociationRole> CkAssociationRoles { get; }
    public ICachedCollection<CkEntityAssociation> CkEntityAssociations { get; }
    public ICachedCollection<CkEntityInheritance> CkEntityInheritances { get; }
    public ICachedCollection<RtAssociation> RtAssociations { get; }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        var ckEntities = (await CkEntities.GetAsync(session)).ToList();
        foreach (var ckEntity in ckEntities)
        {
            if (!ckEntity.IsAbstract)
            {
                var suffix = ckEntity.CkId.SemanticVersionedFullName.Replace("/", "_");
                await _repository.CreateCollectionIfNotExistsAsync<RtEntity>(ckEntity.EnableChangeStreamPreAndPostImages, suffix);
            }
        }
    }

    public async Task UpdateIndexAsync(IOctoSession session)
    {
        var ckEntities = (await CkEntities.GetAsync(session)).ToList();

        foreach (var ckEntity in ckEntities)
        {
            var name = ckEntity.CkId.SemanticVersionedFullName.Replace(".", "_");

            var collection = GetRtCollection<RtEntity>(ckEntity.CkId);
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

                    var collection = GetRtCollection<RtEntity>(ckEntity.CkId);

                    var newName = ckEntity.CkId.SemanticVersionedFullName.Replace(".", "_") + "_" + ObjectId.GenerateNewId();

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

    private IAggregateFluent<CkTypeInfo> AggregateCkTypeInfo(IAggregateFluent<CkEntity> aggregate)
    {
        return aggregate.GraphLookup(CkEntityInheritances.GetMongoCollection(),
                x => x.OriginCkId,
                x => x.TargetCkId,
                x => x.CkId,
                (CkTypeInfo x) => x.BaseTypes, (CkBaseTypeInfo i) => i.BaseTypeDepthIndex)
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkEntityAssociation>(),
                "baseTypes.originCkId",
                "originCkId",
                "associations.out.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkEntityAssociation>(),
                Constants.IdField,
                "originCkId",
                "associations.out.owned")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkEntityAssociation>(),
                "baseTypes.originCkId",
                "targetCkId",
                "associations.in.inherited")
            .Lookup<CkTypeInfo, CkTypeInfo>(_repository.GetCollectionName<CkEntityAssociation>(),
                Constants.IdField,
                "targetCkId",
                "associations.in.owned");
    }

    private async Task<CkEntity> GetCkEntityAsync(IOctoSession session, string ckId)
    {
        var ckEntity = await CkEntities.DocumentAsync(session, ckId);
        if (ckEntity == null)
        {
            throw new EntityNotFoundException($"'{ckId}' does not exist in database.");
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