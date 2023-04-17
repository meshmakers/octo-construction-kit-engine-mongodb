using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine;

internal class CkGraphRuleEngine : ICkGraphRuleEngine
{
    private readonly ICkCache _ckCache;
    private readonly ITenantRepositoryInternal _tenantRepository;

    public CkGraphRuleEngine(ICkCache ckCache, ITenantRepositoryInternal tenantRepository)
    {
        _ckCache = ckCache;
        _tenantRepository = tenantRepository;
    }

    public async Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList)
    {
        return await ValidateAsync(session, entityUpdateInfoList, new List<AssociationUpdateInfo>());
    }


    public async Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        var graphValidationResult = new GraphRuleEngineResult();

        // Validate if the associations are valid to be added/deleted based on the current database content
        var createAssociations = associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Create);
        var deleteAssociations = associationUpdateInfoList.Where(x => x.ModOption == AssociationModOptionsDto.Delete);

        await ValidateAssociationsToCreate(session, createAssociations, graphValidationResult);
        await ValidateAssociationsToDelete(session, deleteAssociations, graphValidationResult);

        // Validate the consistency of the construction kit model
        await ValidateCkModel(session, graphValidationResult, entityUpdateInfoList, associationUpdateInfoList);

        return graphValidationResult;
    }

    // ReSharper disable once UnusedMember.Global
    public async Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        return await ValidateAsync(session, new List<EntityUpdateInfo>(), associationUpdateInfoList);
    }

    private async Task ValidateCkModel(IOctoSession session, GraphRuleEngineResult graphRuleEngineResult,
        IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        await ValidateOrigin(session, entityUpdateInfoList, associationUpdateInfoList);
        await ValidateTarget(session, entityUpdateInfoList, associationUpdateInfoList);

        // Ensure that all associations exists when creating an entity
        // Currently, the only mandatory association has multiplicity of One
        foreach (var entityUpdateInfo in entityUpdateInfoList.Where(x => x.ModOption == EntityModOptions.Create))
        {
            var cacheItem = _ckCache.GetEntityCacheItem(entityUpdateInfo.RtEntity.CkId);

            var inboundAssociationCacheItems =
                cacheItem.InboundAssociations.Values.SelectMany(x =>
                    x.Where(a => a.InboundMultiplicity == Multiplicities.One));
            foreach (var inboundAssociationCacheItem in inboundAssociationCacheItems)
            {
                if (!associationUpdateInfoList.Any(x =>
                        x.ModOption == AssociationModOptionsDto.Create &&
                        x.RoleId == inboundAssociationCacheItem.RoleId))
                {
                    throw new CkModelViolationException(
                        $"Inbound association '{inboundAssociationCacheItem.RoleId}' has multiplicity of 'One', but create statement is missing. Error occurred at CK type '{entityUpdateInfo.RtEntity.CkId}' (from RtId '{entityUpdateInfo.RtEntity.RtId}').");
                }
            }
        }

        // Delete all corresponding associations if an entity is deleted  
        foreach (var entityUpdateInfo in entityUpdateInfoList.Where(x => x.ModOption == EntityModOptions.Delete))
        {
            var result = await _tenantRepository.GetRtAssociationsAsync(session,
                entityUpdateInfo.RtEntity.RtId.ToString(), GraphDirections.Any);
            graphRuleEngineResult.RtAssociationsToDelete.AddRange(result);
        }
    }

    private async Task ValidateTarget(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        var targetList = associationUpdateInfoList.Select(a => a.Target).Distinct();
        foreach (var targetRtId in targetList)
        {
            var targetEntity = await GetEntity(session, entityUpdateInfoList, targetRtId);
            if (targetEntity == null)
            {
                throw new CkModelViolationException($"Target entity '{targetRtId}' does not exist.");
            }

            var targetCacheItem = _ckCache.GetEntityCacheItem(targetEntity.CkId);

            foreach (var associationUpdateInfosByRoleId in associationUpdateInfoList.Where(a => a.Target == targetRtId)
                         .GroupBy(a => a.RoleId))
            {
                var inboundAssociationCacheItem = targetCacheItem.InboundAssociations.Values
                    .SelectMany(x => x.Where(a => a.RoleId == associationUpdateInfosByRoleId.Key)).FirstOrDefault();
                if (inboundAssociationCacheItem == null)
                {
                    throw new CkModelViolationException(
                        $"Inbound association '{associationUpdateInfosByRoleId.Key}' is not allowed at CK type '{targetCacheItem.CkId}' (from RtId '{targetRtId}').");
                }

                foreach (var associationUpdateInfo in associationUpdateInfosByRoleId)
                {
                    var originEntity = await GetEntity(session, entityUpdateInfoList, associationUpdateInfo.Origin);
                    var originCacheItem = _ckCache.GetEntityCacheItem(originEntity.CkId);

                    if (!inboundAssociationCacheItem.AllowedTypes.Contains(originCacheItem))
                    {
                        throw new CkModelViolationException(
                            $"Inbound association '{associationUpdateInfosByRoleId.Key}' does not allow CK type '{targetCacheItem.CkId}' (from RtId '{targetRtId}').");
                    }
                }

                var storedTargetAssociations = await _tenantRepository.GetCurrentRtAssociationMultiplicityAsync(session,
                    targetRtId, associationUpdateInfosByRoleId.Key, GraphDirections.Inbound);

                var createCount =
                    associationUpdateInfosByRoleId.Count(x => x.ModOption == AssociationModOptionsDto.Create);
                var deleteCount =
                    associationUpdateInfosByRoleId.Count(x => x.ModOption == AssociationModOptionsDto.Delete);
                var changeDelta = createCount - deleteCount;

                if (changeDelta < 0)
                {
                    if (storedTargetAssociations == CurrentMultiplicity.One &&
                        inboundAssociationCacheItem.InboundMultiplicity == Multiplicities.One)
                    {
                        throw new CkModelViolationException(
                            $"Inbound association '{associationUpdateInfosByRoleId.Key}' has multiplicity of 'One'. Association deletion violates the model (from RtId '{targetRtId}').");
                    }
                }

                if (changeDelta > 0)
                {
                    if (storedTargetAssociations == CurrentMultiplicity.One &&
                        (inboundAssociationCacheItem.InboundMultiplicity == Multiplicities.One ||
                         inboundAssociationCacheItem.InboundMultiplicity == Multiplicities.ZeroOrOne))
                    {
                        throw new CkModelViolationException(
                            $"Inbound association '{associationUpdateInfosByRoleId.Key}' has multiplicity of 'One'. Adding another association violates the model (from RtId '{targetRtId}').");
                    }
                }
            }
        }
    }

    private async Task ValidateOrigin(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList)
    {
        var originList = associationUpdateInfoList.Select(a => a.Origin).Distinct();
        foreach (var originRtId in originList)
        {
            var originEntity = await GetEntity(session, entityUpdateInfoList, originRtId);
            if (originEntity == null)
            {
                throw new CkModelViolationException($"Origin entity '{originRtId}' does not exist.");
            }

            var originCacheItem = _ckCache.GetEntityCacheItem(originEntity.CkId);

            foreach (var associationUpdateInfosByRoleId in associationUpdateInfoList.Where(a => a.Origin == originRtId)
                         .GroupBy(a => a.RoleId))
            {
                var outboundAssociationCacheItem = originCacheItem.OutboundAssociations.Values
                    .SelectMany(x => x.Where(a => a.RoleId == associationUpdateInfosByRoleId.Key)).FirstOrDefault();
                if (outboundAssociationCacheItem == null)
                {
                    throw new CkModelViolationException(
                        $"Outbound association '{associationUpdateInfosByRoleId.Key}' is not allowed at CK type '{originCacheItem.CkId}' (from RtId '{originRtId}').");
                }

                foreach (var associationUpdateInfo in associationUpdateInfosByRoleId)
                {
                    var targetEntity = await GetEntity(session, entityUpdateInfoList, associationUpdateInfo.Target);
                    var targetCacheItem = _ckCache.GetEntityCacheItem(targetEntity.CkId);

                    if (!outboundAssociationCacheItem.AllowedTypes.Contains(targetCacheItem))
                    {
                        throw new CkModelViolationException(
                            $"Outbound association '{associationUpdateInfosByRoleId.Key}' does not allow CK type '{targetCacheItem.CkId}' (from RtId '{originRtId}').");
                    }
                }

                var storedOriginAssociations = await _tenantRepository.GetCurrentRtAssociationMultiplicityAsync(session,
                    originRtId, associationUpdateInfosByRoleId.Key, GraphDirections.Outbound);

                var createCount =
                    associationUpdateInfosByRoleId.Count(x => x.ModOption == AssociationModOptionsDto.Create);
                var deleteCount =
                    associationUpdateInfosByRoleId.Count(x => x.ModOption == AssociationModOptionsDto.Delete);
                var changeDelta = createCount - deleteCount;

                if (changeDelta < 0)
                {
                    if (storedOriginAssociations == CurrentMultiplicity.One &&
                        outboundAssociationCacheItem.OutboundMultiplicity == Multiplicities.One)
                    {
                        throw new CkModelViolationException(
                            $"Outbound association '{associationUpdateInfosByRoleId.Key}' has multiplicity of 'One'. Association deletion violates the model (from RtId '{originRtId}').");
                    }
                }

                if (changeDelta > 0)
                {
                    if (storedOriginAssociations == CurrentMultiplicity.One &&
                        (outboundAssociationCacheItem.OutboundMultiplicity == Multiplicities.One ||
                         outboundAssociationCacheItem.OutboundMultiplicity == Multiplicities.ZeroOrOne))
                    {
                        throw new CkModelViolationException(
                            $"Outbound association '{associationUpdateInfosByRoleId.Key}' has multiplicity of 'One'. Adding another association violates the model (from RtId '{originRtId}').");
                    }
                }
            }
        }
    }

    private async Task ValidateAssociationsToDelete(IOctoSession session,
        IEnumerable<AssociationUpdateInfo> deleteAssociations, GraphRuleEngineResult graphRuleEngineResult)
    {
        foreach (var d in deleteAssociations)
        {
            var origin = d.Origin;
            var target = d.Target;

            var rtAssociation = await _tenantRepository.GetRtAssociationAsync(session,
                origin,
                target,
                d.RoleId);
            if (rtAssociation == null)
            {
                throw new OperationFailedException(
                    $"Association from '{origin}' to '{target}' in role '{d.RoleId}' does not exist");
            }

            graphRuleEngineResult.RtAssociationsToDelete.Add(rtAssociation);
        }
    }

    private async Task ValidateAssociationsToCreate(IOctoSession session,
        IEnumerable<AssociationUpdateInfo> createAssociations, GraphRuleEngineResult graphRuleEngineResult)
    {
        foreach (var associationUpdateInfo in createAssociations)
        {
            var origin = associationUpdateInfo.Origin;
            var target = associationUpdateInfo.Target;

            var rtAssociation = await _tenantRepository.GetRtAssociationAsync(session,
                origin,
                target,
                associationUpdateInfo.RoleId);
            if (rtAssociation != null)
            {
                throw new OperationFailedException(
                    $"Association from '{origin}' to '{target}' in role '{associationUpdateInfo.RoleId}' already exits.");
            }

            graphRuleEngineResult.RtAssociationsToCreate.Add(new RtAssociation
            {
                OriginRtId = associationUpdateInfo.Origin.RtId.ToObjectId(),
                OriginCkId = associationUpdateInfo.Origin.CkId,
                TargetRtId = associationUpdateInfo.Target.RtId.ToObjectId(),
                TargetCkId = associationUpdateInfo.Target.CkId,
                AssociationRoleId = associationUpdateInfo.RoleId
            });
        }
    }


    private async Task<RtEntity> GetEntity(IOctoSession session, IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        RtEntityId rtEntityId)
    {
        var rtEntity = entityUpdateInfoList.Select(x => x.RtEntity)
            .FirstOrDefault(x => x.RtId == rtEntityId.RtId.ToObjectId());
        // ReSharper disable once ConvertIfStatementToNullCoalescingExpression
        if (rtEntity == null)
        {
            rtEntity = await _tenantRepository.GetRtEntityAsync(session, rtEntityId);
        }

        return rtEntity;
    }
}
