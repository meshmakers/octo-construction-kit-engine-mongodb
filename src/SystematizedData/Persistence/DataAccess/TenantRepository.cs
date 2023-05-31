using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Mutation;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class TenantRepository : ITenantRepositoryInternal
{
    private readonly IDatabaseContext _databaseContext;

    public TenantRepository(ICkCache ckCache, IDatabaseContext databaseContext)
    {
        CkCache = ckCache;
        _databaseContext = databaseContext;
    }

    private ICkCache CkCache { get; }

    #region Helper

    private EntityCacheItem GetEntityCacheItem(string ckId)
    {
        var entityCacheItem = CkCache.GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new OperationFailedException($"Type '{ckId}' does not exist.");
        }

        return entityCacheItem;
    }

    #endregion Helper

    #region Transaction Handling

    public async Task<IOctoSession> StartSessionAsync()
    {
        return await _databaseContext.StartSessionAsync();
    }

    public IOctoSession StartSession()
    {
        return _databaseContext.StartSession();
    }

    #endregion Transaction Handling

    #region Data manipulation

    public async Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        MutationHandler mutationHandler =
            new MutationHandler(_databaseContext, CkCache, this, new AutoIncrementModifier(_databaseContext, CkCache, this));
        await mutationHandler.ApplyChanges(session, entityUpdateInfoList, associationUpdateInfoList);
    }

    public async Task ApplyChanges(IOctoSession session,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        await ApplyChanges(session, new List<EntityUpdateInfo>(), associationUpdateInfoList);
    }

    public async Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList)
    {
        await ApplyChanges(session, entityUpdateInfoList, new List<AssociationUpdateInfo>());
    }

    public async Task<AggregatedBulkImportResult> BulkInsertRtEntitiesAsync(IOctoSession session,
        IEnumerable<RtEntity> rtEntityList)
    {
        var results = new List<BulkImportResult>();
        foreach (var groupedEntities in rtEntityList.GroupBy(x => x.CkId))
        {
            if (string.IsNullOrWhiteSpace(groupedEntities.Key))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkId. Please provide a CkId.");
            }

            results.Add(await _databaseContext.GetRtCollection<RtEntity>(groupedEntities.Key)
                .BulkImportAsync(session, groupedEntities));
        }

        return new AggregatedBulkImportResult(results);
    }

    public async Task<BulkImportResult> BulkRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtAssociation> rtAssociations)
    {
        return await _databaseContext.RtAssociations.BulkImportAsync(session, rtAssociations);
    }

    #endregion Data manipulation

    #region Data query

    public async Task<ResultSet<CkAttribute>> GetCkAttributesAsync(IOctoSession session,
        IReadOnlyList<string> attributeIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var resultSet = new List<CkAttribute>();
        long totalCount = 0;

        var statementCreator = new CkAttributeQuery(_databaseContext);
        statementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        statementCreator.AddIdFilter(attributeIds);
        statementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        statementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        statementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await statementCreator.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Result);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<CkAttribute>(resultSet, totalCount);
    }

    public async Task<ResultSet<CkEntity>> GetCkEntityAsync(IOctoSession session, IReadOnlyList<string> ckIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        var resultSet = new List<CkEntity>();
        long totalCount = 0;

        var statementCreator = new CkEntityQuery(_databaseContext);
        statementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        statementCreator.AddIdFilter(ckIds);
        statementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        statementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        statementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await statementCreator.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Result);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<CkEntity>(resultSet, totalCount);
    }

    public async Task<RtEntity?> GetRtEntityAsync(IOctoSession session, RtEntityId rtEntityId)
    {
        return await _databaseContext.GetRtCollection<RtEntity>(rtEntityId.CkId)
            .DocumentAsync(session, rtEntityId.RtId.ToObjectId());
    }

    public async Task<TEntity?> GetRtEntityAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();

        return await _databaseContext.GetRtCollection<TEntity>(ckId)
            .DocumentAsync(session, rtId.ToObjectId());
    }

    public async Task<ResultSet<RtEntity>> GetRtEntitiesByIdAsync(IOctoSession session, string ckId,
        IReadOnlyList<ObjectId> rtIds,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        ArgumentValidation.ValidateString(nameof(ckId), ckId);

        if (!rtIds.Any())
        {
            return new ResultSet<RtEntity>(new List<RtEntity>(), 0);
        }

        var resultSet = new List<RtEntity>();
        long totalCount = 0;
        var entityCacheItem = GetEntityCacheItem(ckId);

        var statementCreator =
            new SingleOriginRtQuery(entityCacheItem, _databaseContext, dataQueryOperation.Language);
        statementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        statementCreator.AddIdFilter(rtIds);
        statementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        statementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        statementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        var tempResultSet = await statementCreator.ExecuteQuery(session, skip, take);
        resultSet.AddRange(tempResultSet.Result);
        totalCount += tempResultSet.TotalCount;

        return new ResultSet<RtEntity>(resultSet, totalCount);
    }

    public async Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(IOctoSession session,
        RtEntityId rtEntityId, string roleId,
        GraphDirections graphDirections)
    {
        long counter = 0;
        if (graphDirections == GraphDirections.Inbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, rtEntityId.RtId.ToObjectId()),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkId, rtEntityId.CkId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, roleId)
            );

            var r = await _databaseContext.RtAssociations.GetTotalCountAsync(session, filterDefinition);
            counter = Math.Max(r, counter);
        }

        if (graphDirections == GraphDirections.Outbound || graphDirections == GraphDirections.Any)
        {
            var filterDefinition = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, rtEntityId.RtId.ToObjectId()),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkId, rtEntityId.CkId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, roleId)
            );

            var r = await _databaseContext.RtAssociations.GetTotalCountAsync(session, filterDefinition);
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

    public async Task<ResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session, ObjectId originRtId,
        string originCkId,
        string roleId,
        string targetCkId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        var result = await GetRtAssociationTargetsAsync(session, new[] { originRtId }, originCkId, roleId, targetCkId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<ResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(IOctoSession session,
        ObjectId originRtId,
        string roleId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        var result = await GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(session, new[] { originRtId }, roleId,
            graphDirection, rtIds, dataQueryOperation, skip, take);

        return result.First().Value;
    }

    public async Task<IMultipleOriginResultSet<RtEntity>> GetRtAssociationTargetsAsync(IOctoSession session,
        IEnumerable<ObjectId> originRtIds, string originCkId, string roleId, string targetCkId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
    {
        ArgumentValidation.ValidateString(nameof(roleId), roleId);
        ArgumentValidation.ValidateString(nameof(targetCkId), targetCkId);

        var entityCacheItem = GetEntityCacheItem(targetCkId);

        var hierarchicalRtStatementCreator =
            new MultipleOriginHierarchicalRtQuery(entityCacheItem, _databaseContext, dataQueryOperation.Language,
                originRtIds,
                originCkId, roleId, graphDirection, targetCkId);

        hierarchicalRtStatementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtStatementCreator.AddIdFilter(rtIds);
        hierarchicalRtStatementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtStatementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtStatementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtStatementCreator.ExecuteQuery(session, skip, take);
    }

    public async Task<IMultipleOriginResultSet<TTargetEntity>> GetRtAssociationTargetsAsync<TOriginEntity, TTargetEntity>(
        IOctoSession session,
        IEnumerable<ObjectId> originRtIds, string roleId,
        GraphDirections graphDirection, IReadOnlyList<ObjectId>? rtIds, DataQueryOperation dataQueryOperation, int? skip = null,
        int? take = null)
        where TOriginEntity : RtEntity
        where TTargetEntity : RtEntity, new()
    {
        ArgumentValidation.ValidateString(nameof(roleId), roleId);

        var originCkId = RtEntityExtensions.GetCkId<TOriginEntity>();
        var targetCkId = RtEntityExtensions.GetCkId<TTargetEntity>();

        var entityCacheItem = GetEntityCacheItem(targetCkId);

        var hierarchicalRtStatementCreator =
            new MultipleOriginHierarchicalRtQuery<TOriginEntity, TTargetEntity>(entityCacheItem, _databaseContext,
                dataQueryOperation.Language,
                originRtIds,
                originCkId, roleId, graphDirection, targetCkId);

        hierarchicalRtStatementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        hierarchicalRtStatementCreator.AddIdFilter(rtIds);
        hierarchicalRtStatementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        hierarchicalRtStatementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        hierarchicalRtStatementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await hierarchicalRtStatementCreator.ExecuteQuery(session, skip, take);
    }

    public async Task<ResultSet<RtEntity>> GetRtEntitiesByTypeAsync(IOctoSession session, string ckId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null)
    {
        return await GetRtEntitiesByTypeAsync<RtEntity>(session, ckId, dataQueryOperation, skip, take);
    }

    public async Task<ResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session,
        DataQueryOperation dataQueryOperation,
        int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        return await GetRtEntitiesByTypeAsync<TEntity>(session, ckId, dataQueryOperation, skip, take);
    }

    private async Task<ResultSet<TEntity>> GetRtEntitiesByTypeAsync<TEntity>(IOctoSession session, string ckId,
        DataQueryOperation dataQueryOperation, int? skip = null, int? take = null) where TEntity : RtEntity, new()
    {
        var entityCacheItem = GetEntityCacheItem(ckId);
        var statementCreator =
            new SingleOriginRtQuery<TEntity>(entityCacheItem, _databaseContext, dataQueryOperation.Language);
        statementCreator.AddFieldFilters(dataQueryOperation.FieldFilters);
        statementCreator.AddTextSearchFilter(dataQueryOperation.TextSearchFilter);
        statementCreator.AddAttributeSearchFilter(dataQueryOperation.AttributeSearchFilter);
        statementCreator.AddSortConstraintsToPipeline(dataQueryOperation.SortOrders);

        return await statementCreator.ExecuteQuery(session, skip, take);
    }

    public async Task<RtAssociation> GetRtAssociationAsync(IOctoSession session, RtEntityId rtEntityIdOrigin,
        RtEntityId rtEntityIdTarget,
        string roleId)
    {
        return await _databaseContext.RtAssociations.FindSingleOrDefaultAsync(session, x =>
            x.OriginRtId == rtEntityIdOrigin.RtId.ToObjectId()
            && x.OriginCkId == rtEntityIdOrigin.CkId
            && x.TargetRtId == rtEntityIdTarget.RtId.ToObjectId()
            && x.TargetCkId == rtEntityIdTarget.CkId
            && x.AssociationRoleId == roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections, string roleId)
    {
        return await GetRtAssociationsAsync(session, ObjectId.Parse(rtId), graphDirections, roleId);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, ObjectId rtId,
        GraphDirections graphDirections, string roleId)
    {
        var associations = new List<RtAssociation>();

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Inbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.TargetRtId == rtId && x.AssociationRoleId == roleId));
        }

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Outbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.OriginRtId == rtId && x.AssociationRoleId == roleId));
        }

        return associations;
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, string rtId,
        GraphDirections graphDirections)
    {
        return await GetRtAssociationsAsync(session, ObjectId.Parse(rtId), graphDirections);
    }

    public async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session, ObjectId rtId,
        GraphDirections graphDirections)
    {
        var associations = new List<RtAssociation>();

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Inbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.TargetRtId == rtId));
        }

        if (graphDirections == GraphDirections.Any || graphDirections == GraphDirections.Outbound)
        {
            associations.AddRange(await _databaseContext.RtAssociations.FindManyAsync(session, x =>
                x.OriginRtId == rtId));
        }

        return associations;
    }

    #endregion Data query

    #region Transient data

    public RtEntity CreateTransientRtEntity(string ckId)
    {
        ArgumentValidation.ValidateString(nameof(ckId), ckId);

        var entityCacheItem = CkCache.GetEntityCacheItem(ckId);
        return CreateTransientRtEntity<RtEntity>(entityCacheItem);
    }

    public RtEntity CreateTransientRtEntity(EntityCacheItem entityCacheItem)
    {
        return CreateTransientRtEntity<RtEntity>(entityCacheItem);
    }

    public TEntity CreateTransientRtEntity<TEntity>() where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();
        if (string.IsNullOrWhiteSpace(ckId))
        {
            throw new InvalidCkIdException($"No Construction Kit Id for type '{typeof(TEntity).FullName}'" +
                                           $" is defined. Is attribute '{typeof(CkIdAttribute).FullName}' missing?");
        }

        var entityCacheItem = CkCache.GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkIdException($"Construction Kit Id '{ckId}' was not found in model cache." +
                                           " Wrong CkId used?");
        }

        return CreateTransientRtEntity<TEntity>(entityCacheItem);
    }

    private TEntity CreateTransientRtEntity<TEntity>(EntityCacheItem entityCacheItem)
        where TEntity : RtEntity, new()
    {
        var rtEntity = new TEntity
        {
            RtId = ObjectId.GenerateNewId(),
            CkId = entityCacheItem.CkId
        };
        foreach (var attributeCacheItem in entityCacheItem.Attributes.Values)
        {
            var value = attributeCacheItem.DefaultValue;
            rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.AttributeValueType, value);
        }

        return rtEntity;
    }

    #endregion Transient data

    #region Large binaries

    public async Task<OctoObjectId> UploadLargeBinaryAsync(string filename, string contentType, Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        var objectId = await _databaseContext.UploadLargeBinaryAsync(filename, contentType, stream, cancellationToken);
        return objectId.ToOctoObjectId();
    }

    public async Task ReplaceLargeBinaryAsync(OctoObjectId largeBinaryId, string filename, string contentType,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentValidation.ValidateString(nameof(filename), filename);
        ArgumentValidation.ValidateString(nameof(contentType), contentType);

        await _databaseContext.ReplaceLargeBinaryAsync(largeBinaryId.ToObjectId(), filename, contentType, stream,
            cancellationToken);
    }

    public async Task DeleteLargeBinaryAsync(OctoObjectId largeBinaryId, CancellationToken cancellationToken = default)
    {
        await _databaseContext.DeleteLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadStreamHandler> DownloadLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _databaseContext.DownloadLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public async Task<IDownloadInfo> GetLargeBinaryAsync(OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = default)
    {
        return await _databaseContext.GetLargeBinaryAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    #endregion Large binaries

    #region Advanced functionality

    public IUpdateStream<RtEntity> SubscribeToRtEntities(string ckId, UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        var collection = _databaseContext.GetRtCollection<RtEntity>(ckId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, pipeline =>
        {
            if (updateStreamFilter.RtId.HasValue)
            {
                var filter = Builders<ChangeStreamDocument<RtEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());
                return pipeline.Match(filter);
            }

            return pipeline;
        }, cancellationToken);
    }

    public IUpdateStream<TEntity> SubscribeToRtEntities<TEntity>(UpdateStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
        where TEntity : RtEntity, new()
    {
        var ckId = RtEntityExtensions.GetCkId<TEntity>();

        var collection = _databaseContext.GetRtCollection<TEntity>(ckId);

        return collection.Subscribe(updateStreamFilter.UpdateTypes, pipeline =>
        {
            if (updateStreamFilter.RtId.HasValue)
            {
                var filter = Builders<ChangeStreamDocument<TEntity>>.Filter.Eq(
                    "fullDocument." + Constants.IdField,
                    updateStreamFilter.RtId.Value.ToObjectId());
                return pipeline.Match(filter);
            }

            return pipeline;
        }, cancellationToken);
    }

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations(string originCkId, string targetCkId,
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default)
    {
        return _databaseContext.RtAssociations.Subscribe(updateStreamFilter.UpdateTypes, pipeline =>
        {
            pipeline = pipeline.Match(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                "fullDocument." + nameof(RtAssociation.OriginCkId).ToCamelCase(), originCkId));
            pipeline = pipeline.Match(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                "fullDocument." + nameof(RtAssociation.TargetCkId).ToCamelCase(), targetCkId));

            if (!string.IsNullOrWhiteSpace(updateStreamFilter.RoleId))
            {
                pipeline = pipeline.Match(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.AssociationRoleId).ToCamelCase(), updateStreamFilter.RoleId));
            }

            if (updateStreamFilter.OriginRtId.HasValue)
            {
                pipeline = pipeline.Match(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.OriginRtId).ToCamelCase(), updateStreamFilter.OriginRtId));
            }

            if (updateStreamFilter.TargetRtId.HasValue)
            {
                pipeline = pipeline.Match(Builders<ChangeStreamDocument<RtAssociation>>.Filter.Eq(
                    "fullDocument." + nameof(RtAssociation.TargetRtId).ToCamelCase(), updateStreamFilter.TargetRtId));
            }

            return pipeline;
        }, cancellationToken);
    }

    public IUpdateStream<RtAssociation> SubscribeToRtAssociations<TOriginEntity, TTargetEntity>(
        UpdateAssociationStreamFilter updateStreamFilter,
        CancellationToken cancellationToken = default) where TOriginEntity : RtEntity, new() where TTargetEntity : RtEntity, new()
    {
        var originCkId = RtEntityExtensions.GetCkId<TOriginEntity>();
        var targetCkId = RtEntityExtensions.GetCkId<TTargetEntity>();

        return SubscribeToRtAssociations(originCkId, targetCkId, updateStreamFilter, cancellationToken);
    }

    public async Task<IEnumerable<AutoCompleteText>> ExtractAutoCompleteValuesAsync(IOctoSession session,
        string ckId,
        string attributeName, string regexFilterValue, int takeCount)
    {
        ArgumentValidation.ValidateString(nameof(ckId), ckId);
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);
        ArgumentValidation.ValidateString(nameof(regexFilterValue), regexFilterValue);

        var entityCacheItem = GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkIdException($"Construction Kit Id '{ckId}' is invalid.");
        }

        if (!entityCacheItem.Attributes.Keys.Contains(attributeName))
        {
            throw new InvalidAttributeException(
                $"Attribute '{attributeName}' does not exist at type '{ckId}'");
        }

        var match = new BsonDocument
        {
            {
                "$match", new BsonDocument
                {
                    {
                        $"attributes.{attributeName.ToCamelCase()}", new BsonDocument
                        {
                            {
                                "$regex", regexFilterValue
                            }
                        }
                    }
                }
            }
        };

        var sortByCount = new BsonDocument
        {
            {
                "$sortByCount", $"$attributes.{attributeName.ToCamelCase()}"
            }
        };

        var limit = new BsonDocument
        {
            {
                "$limit", takeCount
            }
        };

        var collection = _databaseContext.GetRtCollection<RtEntity>(entityCacheItem.CkId);
        var result = collection.Aggregate(session,
            PipelineDefinition<RtEntity, AutoCompleteText>.Create(match, sortByCount, limit));
        return await result.ToListAsync();
    }


    public async Task UpdateAutoCompleteTexts(IOctoSession session, string ckId, string attributeName,
        IEnumerable<string> autoCompleteTexts)
    {
        ArgumentValidation.ValidateString(nameof(ckId), ckId);
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        var ckEntity =
            await _databaseContext.CkEntities.FindSingleOrDefaultAsync(session, x => x.CkId == ckId);
        if (ckEntity == null)
        {
            throw new EntityNotFoundException($"'{ckId}' does not exist in database.");
        }

        var attribute = ckEntity.Attributes.FirstOrDefault(x => x.AttributeName == attributeName);
        if (attribute == null)
        {
            throw new InvalidAttributeException(
                $"Attribute with name '{attributeName}' does not exist on type '{ckId}'");
        }

        attribute.AutoCompleteTexts = autoCompleteTexts.ToList();

        try
        {
            await _databaseContext.CkEntities.ReplaceByIdAsync(session, ckEntity.CkId, ckEntity);
        }
        catch (Exception e)
        {
            throw new OperationFailedException("An error occurred during import: " + e.Message, e);
        }
    }

    #endregion Advanced functionality
}