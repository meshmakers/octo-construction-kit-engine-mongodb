using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Driver;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Mutation;

public class BulkRtMutation
{
    private readonly IDatabaseContext _databaseContext;
    private readonly ICkCache _ckCache;
    private readonly ITenantRepositoryInternal _tenantRepository;
    private readonly IAutoIncrementModifier _autoIncrementModifier;

    internal BulkRtMutation(IDatabaseContext databaseContext,
        ICkCache ckCache,
        ITenantRepositoryInternal tenantRepository,
        IAutoIncrementModifier autoIncrementModifier)
    {
        _databaseContext = databaseContext;
        _ckCache = ckCache;
        _tenantRepository = tenantRepository;
        _autoIncrementModifier = autoIncrementModifier;
    }
    
    public async Task ApplyChanges(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        var ckEntityRuleEngine = new CkEntityRuleEngine(_ckCache, _tenantRepository);
        var entityValidatorResult = await ckEntityRuleEngine.ValidateAsync(entityUpdateInfoList);

        var ckGraphRuleEngine = new CkGraphRuleEngine(_ckCache, _tenantRepository);
        var graphValidationResult =
            await ckGraphRuleEngine.ValidateAsync(session, entityUpdateInfoList, associationUpdateInfoList);

        await ApplyRtEntityChangesAsync(session, entityValidatorResult);
        await ApplyRtAssociationChangesAsync(session, graphValidationResult);
    }
    
    private async Task ApplyRtEntityChangesAsync(IOctoSession session,
        CkEntityRuleEngineResult ckEntityRuleEngineResult)
    {
        if (ckEntityRuleEngineResult.RtEntitiesToDelete.Any())
        {
            await DeleteRtEntityAsync(session, ckEntityRuleEngineResult.RtEntitiesToDelete);
        }

        if (ckEntityRuleEngineResult.RtEntitiesToUpdate.Any())
        {
            await UpdateRtEntities(session, ckEntityRuleEngineResult.RtEntitiesToUpdate);
        }

        if (ckEntityRuleEngineResult.RtEntitiesToCreate.Any())
        {
            await InsertRtEntitiesAsync(session, ckEntityRuleEngineResult.RtEntitiesToCreate);
        }
    }

    
    
    private async Task InsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList,
        bool disableAutoIncrement = false)
    {
        var rtEntities = rtEntityList.ToList();
        rtEntities.ForEach(x => x.RtCreationDateTime = DateTime.Now);
        rtEntities.ForEach(x => x.RtChangedDateTime = x.RtCreationDateTime);
        rtEntities.ForEach(x =>
            {
                if (string.IsNullOrWhiteSpace(x.CkTypeId.FullName)) {
                    x.CkTypeId = x.GetCkTypeId();
                }
            }
        );

        foreach (var rtEntityGrouping in rtEntities.GroupBy(x => x.GetCkTypeId()))
        {
            if (string.IsNullOrWhiteSpace(rtEntityGrouping.Key.FullName))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkTypeId. Please provide a CkTypeId.");
            }

            var ckTypeId = rtEntityGrouping.Key;

            if (!disableAutoIncrement)
            {
                await _autoIncrementModifier.RunAutoIncrementAsync(session, ckTypeId, rtEntityGrouping);
                
                await _databaseContext.GetRtCollection<RtEntity>(ckTypeId)
                    .InsertMultipleAsync(session, rtEntityGrouping);
            }

        }
    }
    
    private async Task UpdateRtEntities(IOctoSession session, IReadOnlyList<RtEntity> rtEntities)
    {
        foreach (var rtEntityGrouping in rtEntities.GroupBy(x => x.GetCkTypeId()))
        {
            if (string.IsNullOrWhiteSpace(rtEntityGrouping.Key.FullName))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot update RtEntity without CkTypeId. Please provide a CkTypeId.");
            }

            await UpdateRtEntitiesByCkId<RtEntity>(session, rtEntityGrouping.Key, rtEntityGrouping);
        }
    }

    private async Task UpdateRtEntitiesByCkId<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntityGrouping)
        where TEntity : RtEntity, new()
    {
        var collection = _databaseContext.GetRtCollection<TEntity>(ckTypeId);

        foreach (var document in rtEntityGrouping.AsParallel())
        {
            var updateDefList = new List<UpdateDefinition<TEntity>>();
            foreach (var keyValuePair in document.Attributes)
            {
                updateDefList.Add(Builders<TEntity>.Update.Set(
                    $"{Constants.AttributesName}.{keyValuePair.Key.ToCamelCase()}", keyValuePair.Value));
            }

            if (updateDefList.Any())
            {
                document.RtChangedDateTime = DateTime.Now;

                var updateDefinition = Builders<TEntity>.Update.Combine(updateDefList);
                await collection.UpdateOneAsync(session, document.RtId, updateDefinition);
            }
        }
    }
    
    private async Task DeleteRtEntityAsync(IOctoSession session, IReadOnlyList<RtEntity> rtEntities)
    {
        foreach (var rtEntityGrouping in rtEntities.GroupBy(x => x.GetCkTypeId()))
        {
            if (string.IsNullOrWhiteSpace(rtEntityGrouping.Key.FullName))
            {
                throw OperationFailedException.CreateWithMessage(
                    "Cannot delete RtEntity without CkTypeId. Please provide a CkTypeId.");
            }

            await DeleteRtEntityAsync<RtEntity>(session, rtEntityGrouping.Key, rtEntityGrouping);
        }
    }
    
    private async Task DeleteRtEntityAsync<TEntity>(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntities)
        where TEntity : RtEntity, new()
    {
        var collection = _databaseContext.GetRtCollection<TEntity>(ckTypeId);
        
        foreach (var rtEntity in rtEntities.AsParallel())
        {
            await collection.DeleteOneAsync(session, rtEntity.RtId);
        }
    }
    
    private async Task ApplyRtAssociationChangesAsync(IOctoSession session,
        GraphRuleEngineResult graphRuleEngineResult)
    {
        if (graphRuleEngineResult.RtAssociationsToDelete.Any())
        {
            await DeleteRtAssociationsAsync(session, graphRuleEngineResult.RtAssociationsToDelete);
        }
        
        if (graphRuleEngineResult.RtAssociationsToCreate.Any())
        {
            await InsertRtAssociationsAsync(session, graphRuleEngineResult.RtAssociationsToCreate);
        }
    }

    private async Task InsertRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations)
    {
        await _databaseContext.RtAssociations.InsertMultipleAsync(session, rtAssociations);
    }
    
    private async Task DeleteRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations)
    {
        await _databaseContext.RtAssociations.DeleteManyAsync(session, rtAssociations.Select(x => x.AssociationId));
    }


}