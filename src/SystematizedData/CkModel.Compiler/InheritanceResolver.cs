using Meshmakers.Octo.Common.Shared;
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


    public CkDependencyGraph ResolveInheritanceAsync(CkAggregatedModelElements aggregatedModelElements)
    {
        _logger.LogInformation("Starting resolving inheritance");

        CkDependencyGraph dependencyGraph = new();

        foreach (var ckEntityKeyValue in aggregatedModelElements.CkEntities)
        {
            var entityGraph = GetOrCreateEntityGraph(dependencyGraph, aggregatedModelElements, ckEntityKeyValue.Key,
                ckEntityKeyValue.Value);
            GetDirectedAggregationsAndAttributes(dependencyGraph, aggregatedModelElements, ckEntityKeyValue.Value,
                entityGraph);
        }

        BuildIncomingAssociations(dependencyGraph);


        return dependencyGraph;
    }

    private CkEntityGraph GetOrCreateEntityGraph(CkDependencyGraph dependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkId<CkTypeId> ckTypeId, CkEntityDto ckEntity)
    {
        if (dependencyGraph.Entities.TryGetValue(ckTypeId, out var entityGraph))
        {
            return entityGraph;
        }

        var baseTypes = GetBaseTypes(aggregatedModelElements, ckTypeId);
        return dependencyGraph.AddEntity(ckTypeId, ckEntity, baseTypes);
    }

    private void GetDirectedAggregationsAndAttributes(CkDependencyGraph ckDependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkEntityDto ckEntityDto,
        CkEntityGraph originEntityGraph)
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
                    originEntityGraph, entityAssociation);

                targetCkEntityGraph.Associations.In.Owned.Add(entityAssociation);
                originEntityGraph.Associations.Out.Owned.Add(entityAssociation);
            }
        }

        if (ckEntityDto.Attributes != null)
        {
            foreach (var entityAttribute in ckEntityDto.Attributes)
            {
                originEntityGraph.Attributes.Add(entityAttribute);
            }
        }
    }

    private CkEntityGraph GetOrCreateTargetCkEntityGraph(CkDependencyGraph ckDependencyGraph,
        CkAggregatedModelElements aggregatedModelElements, CkEntityGraph entityGraph,
        CkEntityAssociationDto entityAssociation)
    {
        var targetCkEntity = aggregatedModelElements.CkEntities[entityAssociation.TargetCkTypeId];
        if (targetCkEntity == null)
        {
            throw ModelValidationException.UnknownCkTypeIdForAssociationTarget(entityGraph.CkTypeId,
                entityAssociation.RoleId, entityAssociation.TargetCkTypeId);
        }

        var targetCkEntityGraph = GetOrCreateEntityGraph(ckDependencyGraph, aggregatedModelElements,
            entityAssociation.TargetCkTypeId, targetCkEntity);
        return targetCkEntityGraph;
    }

    private void BuildIncomingAssociations(CkDependencyGraph dependencyGraph)
    {
        var handledInheritanceHashSet = new HashSet<Tuple<CkId<CkTypeId>, CkId<CkTypeId>>>();
        foreach (var graphEntity in dependencyGraph.Entities)
        {
            foreach (var ckGraphTypeInheritance in graphEntity.Value.BaseTypes.Reverse())
            {
                var baseGraphEntity = dependencyGraph.Entities[ckGraphTypeInheritance.BaseCkTypeId];
                var inheritedGraphEntity = dependencyGraph.Entities[ckGraphTypeInheritance.InheritorCkTypeId];

                var tuple = new Tuple<CkId<CkTypeId>, CkId<CkTypeId>>(baseGraphEntity.CkTypeId,
                    inheritedGraphEntity.CkTypeId);
                if (handledInheritanceHashSet.Contains(tuple))
                {
                    continue;
                }

                handledInheritanceHashSet.Add(tuple);

                foreach (var entityAssociation in baseGraphEntity.Associations.In.Owned)
                {
                    inheritedGraphEntity.Associations.In.Inherited.Add(entityAssociation);
                }

                foreach (var entityAssociation in baseGraphEntity.Associations.Out.Owned)
                {
                    inheritedGraphEntity.Associations.Out.Inherited.Add(entityAssociation);
                }
            }

            // var list = graphEntity.Value.Associations.Out.Inherited.Concat(graphEntity.Value.Associations.Out.Owned);
            // foreach (var entityAssociation in list)
            // {
            //     if (dependencyGraph.Entities.TryGetValue(entityAssociation.TargetCkTypeId, out var targetEntity))
            //     {
            //         targetEntity.Associations.In.Owned.Add(entityAssociation);
            //     }
            //     else
            //     {
            //         throw ModelValidationException.UnknownCkTypeIdForAssociationTarget(graphEntity.Key,
            //             entityAssociation.RoleId, entityAssociation.TargetCkTypeId);
            //     }
            // }
        }
    }

    private static IList<CkGraphTypeInheritance> GetBaseTypes(CkAggregatedModelElements aggregatedModelElements,
        CkId<CkTypeId> ckTypeId)
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
            throw ModelValidationException.UnknownCkTypeIdForInheritance(originCkTypeId.Value);
        }

        return ckTypeIds;
    }
}