using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Microsoft.Extensions.Logging;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public class InheritanceResolver
{
    private readonly ILogger<InheritanceResolver> _logger;

    public InheritanceResolver(ILogger<InheritanceResolver> logger)
    {
        _logger = logger;
    }


    public CkDependencyGraph ResolveInheritanceAsync(CkAggregatedModelElements aggregatedModelElements, CompilerResult compilerResult)
    {
        _logger.LogInformation("Starting resolving inheritance");

        CkDependencyGraph dependencyGraph = new();

        foreach (var ckEntityKeyValue in aggregatedModelElements.CkEntities)
        {
            var entityGraph = GetOrCreateEntityGraph(dependencyGraph, aggregatedModelElements, ckEntityKeyValue.Key,
                ckEntityKeyValue.Value, compilerResult);
            GetDirectedAggregationsAndAttributes(dependencyGraph, aggregatedModelElements, ckEntityKeyValue.Value,
                entityGraph, compilerResult);
        }

        BuildInheritedAssociations(dependencyGraph, compilerResult);


        return dependencyGraph;
    }

    private CkEntityGraph GetOrCreateEntityGraph(CkDependencyGraph dependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkId<CkTypeId> ckTypeId, CkEntityDto ckEntity, CompilerResult compilerResult)
    {
        if (dependencyGraph.Entities.TryGetValue(ckTypeId, out var entityGraph))
        {
            return entityGraph;
        }

        var baseTypes = GetBaseTypes(aggregatedModelElements, ckTypeId, compilerResult);
        return dependencyGraph.AddEntity(ckTypeId, ckEntity, baseTypes);
    }

    private void GetDirectedAggregationsAndAttributes(CkDependencyGraph ckDependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkEntityDto ckEntityDto,
        CkEntityGraph originEntityGraph, CompilerResult compilerResult)
    {
        for (int i = originEntityGraph.BaseTypes.Count - 1; i >= 0; i--)
        {
            var ckGraphTypeInheritance = originEntityGraph.BaseTypes.ElementAt(i);
            var baseCkEntity = aggregatedModelElements.CkEntities[ckGraphTypeInheritance.BaseCkTypeId];

            if (baseCkEntity.Attributes != null)
            {
                foreach (var entityAttribute in baseCkEntity.Attributes)
                {
                    originEntityGraph.Attributes.Add(entityAttribute);
                }
            }
        }

        // Add the current entity's associations and attributes
        if (ckEntityDto.Associations != null)
        {
            foreach (var entityAssociation in ckEntityDto.Associations)
            {
                var targetCkEntityGraph = GetOrCreateTargetCkEntityGraph(ckDependencyGraph, aggregatedModelElements,
                    originEntityGraph, entityAssociation, compilerResult);

                if (originEntityGraph.Associations.Out.Owned.Any(x =>
                        x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                {
                    compilerResult.AddMessage(MessageCodes.CkTypeIdAssociationNotUnique(originEntityGraph.CkTypeId,
                        entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                    continue;
                }

                targetCkEntityGraph.Associations.In.Owned.Add(entityAssociation);
                originEntityGraph.Associations.Out.Owned.Add(entityAssociation);
            }
        }

        if (ckEntityDto.Attributes != null)
        {
            var duplicateAttributeNames = ckEntityDto.Attributes.GroupBy(a => a.AttributeName).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeNames.Count > 0)
            {
                compilerResult.AddMessage(
                    MessageCodes.CkTypeIdAttributeNameNotUnique(originEntityGraph.CkTypeId, duplicateAttributeNames.Select(a => a.Key)));
                throw ModelValidationException.DuplicateAttributeNamesInCkEntity(originEntityGraph.CkTypeId,
                    duplicateAttributeNames.Select(a => a.Key));
            }

            var duplicateAttributeIds = ckEntityDto.Attributes.GroupBy(a => a.AttributeId).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeIds.Count > 0)
            {
                compilerResult.AddMessage(
                    MessageCodes.CkTypeIdAttributeIdNotUnique(originEntityGraph.CkTypeId, duplicateAttributeNames.Select(a => a.Key)));
                throw ModelValidationException.DuplicateAttributeIdsInCkEntity(originEntityGraph.CkTypeId,
                    duplicateAttributeIds.Select(a => a.Key));
            }

            foreach (var entityAttribute in ckEntityDto.Attributes)
            {
                if (originEntityGraph.Attributes.Any(a => a.AttributeId == entityAttribute.AttributeId))
                {
                    compilerResult.AddMessage(
                        MessageCodes.CkTypeIdAttributeIdNotUniqueByInheritance(originEntityGraph.CkTypeId, entityAttribute.AttributeId));
                    continue;
                }

                if (originEntityGraph.Attributes.Any(a =>
                        string.Compare(a.AttributeName, entityAttribute.AttributeName, StringComparison.OrdinalIgnoreCase) == 0))
                {
                    compilerResult.AddMessage(
                        MessageCodes.CkTypeIdAttributeNameNotUniqueByInheritance(originEntityGraph.CkTypeId,
                            entityAttribute.AttributeName));
                    continue;
                }

                originEntityGraph.Attributes.Add(entityAttribute);
            }
        }
    }

    private CkEntityGraph GetOrCreateTargetCkEntityGraph(CkDependencyGraph ckDependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkEntityGraph entityGraph,
        CkEntityAssociationDto entityAssociation, CompilerResult compilerResult)
    {
        var targetCkEntity = aggregatedModelElements.CkEntities[entityAssociation.TargetCkTypeId];
        if (targetCkEntity == null)
        {
            // TODO: Compiler message and check for more exceptions wihtout message
            throw ModelValidationException.UnknownCkTypeIdForAssociationTarget(entityGraph.CkTypeId,
                entityAssociation.RoleId, entityAssociation.TargetCkTypeId);
        }

        var targetCkEntityGraph = GetOrCreateEntityGraph(ckDependencyGraph, aggregatedModelElements,
            entityAssociation.TargetCkTypeId, targetCkEntity, compilerResult);
        return targetCkEntityGraph;
    }

    private void BuildInheritedAssociations(CkDependencyGraph dependencyGraph, CompilerResult compilerResult)
    {
        var handledInheritanceHashSet = new HashSet<Tuple<CkId<CkTypeId>, CkId<CkTypeId>>>();
        foreach (var graphEntity in dependencyGraph.Entities)
        {
            foreach (var ckGraphTypeInheritance in graphEntity.Value.BaseTypes.Reverse())
            {
                var baseGraphEntity = dependencyGraph.Entities[ckGraphTypeInheritance.BaseCkTypeId];
                var inheritedGraphEntity = dependencyGraph.Entities[ckGraphTypeInheritance.InheritorCkTypeId];

                // Ensure that we don't handle the same inheritance twice
                var tuple = new Tuple<CkId<CkTypeId>, CkId<CkTypeId>>(baseGraphEntity.CkTypeId,
                    inheritedGraphEntity.CkTypeId);
                if (handledInheritanceHashSet.Contains(tuple))
                {
                    continue;
                }

                handledInheritanceHashSet.Add(tuple);

                // Add the owned associations but also the inherited ones
                foreach (var entityAssociation in baseGraphEntity.Associations.In.Owned)
                {
                    if (inheritedGraphEntity.Associations.In.Inherited.Any(x =>
                            x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdInAssociationNotUniqueByInheritance(inheritedGraphEntity.CkTypeId,
                            entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                        continue;
                    }

                    inheritedGraphEntity.Associations.In.Inherited.Add(entityAssociation);
                }

                foreach (var entityAssociation in baseGraphEntity.Associations.In.Inherited)
                {
                    if (inheritedGraphEntity.Associations.In.Inherited.Any(x =>
                            x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdInAssociationNotUniqueByInheritance(inheritedGraphEntity.CkTypeId,
                            entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                        continue;
                    }
                    
                    inheritedGraphEntity.Associations.In.Inherited.Add(entityAssociation);
                }

                foreach (var entityAssociation in baseGraphEntity.Associations.Out.Owned)
                {
                    if (inheritedGraphEntity.Associations.Out.Inherited.Any(x =>
                            x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdOutAssociationNotUniqueByInheritance(inheritedGraphEntity.CkTypeId,
                            entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                        continue;
                    }
                    
                    inheritedGraphEntity.Associations.Out.Inherited.Add(entityAssociation);
                }

                foreach (var entityAssociation in baseGraphEntity.Associations.Out.Inherited)
                {
                    if (inheritedGraphEntity.Associations.Out.Inherited.Any(x =>
                            x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdOutAssociationNotUniqueByInheritance(inheritedGraphEntity.CkTypeId,
                            entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                        continue;
                    }
                    
                    inheritedGraphEntity.Associations.Out.Inherited.Add(entityAssociation);
                }
            }
        }
    }

    private static IList<CkGraphTypeInheritance> GetBaseTypes(CkAggregatedModelElements aggregatedModelElements,
        CkId<CkTypeId> ckTypeId, CompilerResult compilerResult)
    {
        var ckTypeIds = new List<CkGraphTypeInheritance>();

        int i = 0;
        CkId<CkTypeId>? originCkTypeId = ckTypeId;
        while (originCkTypeId != null &&
               aggregatedModelElements.CkEntities.TryGetValue(originCkTypeId.Value, out var ckEntity))
        {
            var targetCkTypeId = ckEntity.DerivedFromCkTypeId;

            if (targetCkTypeId.HasValue)
            {
                ckTypeIds.Add(new CkGraphTypeInheritance(originCkTypeId.Value, targetCkTypeId.Value, i++));
            }

            originCkTypeId = targetCkTypeId;
        }

        if (originCkTypeId != null)
        {
            compilerResult.AddMessage(MessageCodes.UnknownCkTypeIdForInheritance(originCkTypeId.Value));
            throw ModelValidationException.UnknownCkTypeIdForInheritance(originCkTypeId.Value);
        }

        return ckTypeIds;
    }
}