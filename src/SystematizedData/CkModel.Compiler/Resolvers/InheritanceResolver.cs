using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Microsoft.Extensions.Logging;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;

public class InheritanceResolver : IInheritanceResolver
{
    private readonly ILogger<InheritanceResolver> _logger;
    private readonly HashSet<CkId<CkTypeId>> _handledEntitiesHashSet;

    public InheritanceResolver(ILogger<InheritanceResolver> logger)
    {
        _handledEntitiesHashSet = new HashSet<CkId<CkTypeId>>();
        _logger = logger;
    }

    public CkModelGraph Resolve(CkAggregatedModelElements aggregatedModelElements, CkModelGraph modelGraph, CompilerResult compilerResult)
    {
        _logger.LogInformation("Starting resolving inheritance");

        foreach (var ckEntityKeyValue in aggregatedModelElements.CkEntities)
        {
            var entityGraph = GetOrCreateTypeGraph(modelGraph, aggregatedModelElements, ckEntityKeyValue.Key, compilerResult);
            GetDirectedAggregationsAndAttributes(modelGraph, aggregatedModelElements, ckEntityKeyValue.Value,
                entityGraph, compilerResult);
        }

        BuildInheritedAssociations(modelGraph, compilerResult);


        return modelGraph;
    }

    private CkEntityGraph GetOrCreateTypeGraph(CkModelGraph modelGraph,
        CkAggregatedModelElements aggregatedModelElements, CkId<CkTypeId> ckTypeId, CompilerResult compilerResult)
    {
        if (!aggregatedModelElements.CkEntities.TryGetValue(ckTypeId, out var ckEntity))
        {
            compilerResult.AddMessage(MessageCodes.CkTypeIdUnknown(ckTypeId));
            throw ModelValidationException.UnknownCkTypeId(ckTypeId);
        }

        if (!modelGraph.Entities.TryGetValue(ckTypeId, out var entityGraph))
        {
            entityGraph = modelGraph.GetOrCreateEntity(ckTypeId, ckEntity);
        }

        if (!_handledEntitiesHashSet.Contains(ckTypeId))
        {
            var baseTypes = GetBaseTypes(aggregatedModelElements, ckTypeId, compilerResult);
            entityGraph.AddBaseTypes(baseTypes);
            _handledEntitiesHashSet.Add(ckTypeId);
        }

        return entityGraph;
    }

    private void GetDirectedAggregationsAndAttributes(CkModelGraph ckModelGraph,
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
                var targetCkEntityGraph = GetOrCreateTargetCkEntityGraph(ckModelGraph, aggregatedModelElements,
                    originEntityGraph, entityAssociation, compilerResult);

                // Check if there is a duplicate association defined at the same entity.
                if (originEntityGraph.Associations.Out.Owned.Any(x =>
                        x.RoleId == entityAssociation.RoleId && x.TargetCkTypeId == entityAssociation.TargetCkTypeId))
                {
                    compilerResult.AddMessage(MessageCodes.CkTypeIdAssociationNotUnique(originEntityGraph.CkTypeId,
                        entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
                    continue;
                }

                // Check if there is the same association role defined in a base type with a target to the same target type inheritance chain
                var duplicateEntityAssociations = originEntityGraph.BaseTypes.SelectMany(i =>
                {
                    var baseCkTypeGraph = GetOrCreateTypeGraph(ckModelGraph, aggregatedModelElements,
                        i.BaseCkTypeId, compilerResult);

                    return baseCkTypeGraph.Associations.Out.Owned.Where(x =>
                        x.RoleId == entityAssociation.RoleId && originEntityGraph.BaseTypes.Any(y =>
                            y.BaseCkTypeId == x.TargetCkTypeId)).Select(s => new { BaseCkTypeGraph = baseCkTypeGraph, s.TargetCkTypeId });
                }).ToList();

                if (duplicateEntityAssociations.Any())
                {
                    foreach (var duplicateEntityAssociation in duplicateEntityAssociations)
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdMultipleOutgoingAssociationRepresentingSameRole(
                            originEntityGraph.CkTypeId,
                            entityAssociation.RoleId, entityAssociation.TargetCkTypeId,
                            duplicateEntityAssociation.BaseCkTypeGraph.CkTypeId, duplicateEntityAssociation.TargetCkTypeId));
                    }

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

    private CkEntityGraph GetOrCreateTargetCkEntityGraph(CkModelGraph ckModelGraph,
        CkAggregatedModelElements aggregatedModelElements, CkEntityGraph entityGraph,
        CkEntityAssociationDto entityAssociation, CompilerResult compilerResult)
    {
        if (!aggregatedModelElements.CkEntities.ContainsKey(entityAssociation.TargetCkTypeId))
        {
            compilerResult.AddMessage(MessageCodes.CkTypeIdUnknownTargetCkTypeIdForAssociation(entityGraph.CkTypeId,
                entityAssociation.RoleId, entityAssociation.TargetCkTypeId));
            throw ModelValidationException.UnknownCkTypeIdForAssociationTarget(entityGraph.CkTypeId,
                entityAssociation.RoleId, entityAssociation.TargetCkTypeId);
        }

        var targetCkEntityGraph = GetOrCreateTypeGraph(ckModelGraph, aggregatedModelElements,
            entityAssociation.TargetCkTypeId, compilerResult);
        return targetCkEntityGraph;
    }

    private void BuildInheritedAssociations(CkModelGraph modelGraph, CompilerResult compilerResult)
    {
        var handledInheritanceHashSet = new HashSet<Tuple<CkId<CkTypeId>, CkId<CkTypeId>>>();
        foreach (var graphEntity in modelGraph.Entities)
        {
            foreach (var ckGraphTypeInheritance in graphEntity.Value.BaseTypes.Reverse())
            {
                var baseGraphEntity = modelGraph.Entities[ckGraphTypeInheritance.BaseCkTypeId];
                var inheritedGraphEntity = modelGraph.Entities[ckGraphTypeInheritance.InheritorCkTypeId];

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
                    inheritedGraphEntity.Associations.In.Inherited.Add(entityAssociation);
                }

                foreach (var entityAssociation in baseGraphEntity.Associations.In.Inherited)
                {
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