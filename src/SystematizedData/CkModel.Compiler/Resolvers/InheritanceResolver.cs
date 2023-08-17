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
    private readonly HashSet<CkId<CkTypeId>> _handledTypesHashSet;

    public InheritanceResolver(ILogger<InheritanceResolver> logger)
    {
        _handledTypesHashSet = new HashSet<CkId<CkTypeId>>();
        _logger = logger;
    }

    public CkModelGraph Resolve(CkAggregatedModelElements aggregatedModelElements, CkModelGraph modelGraph, CompilerResult compilerResult)
    {
        _logger.LogInformation("Starting resolving inheritance");

        foreach (var ckTypeKeyValue in aggregatedModelElements.CkTypes)
        {
            var typeGraph = GetOrCreateTypeGraph(modelGraph, aggregatedModelElements, ckTypeKeyValue.Key, compilerResult);
            GetDirectedAggregationsAndAttributes(modelGraph, aggregatedModelElements, ckTypeKeyValue.Value,
                typeGraph, compilerResult);
        }

        BuildInheritedAssociations(modelGraph, compilerResult);


        return modelGraph;
    }

    private CkTypeGraph GetOrCreateTypeGraph(CkModelGraph modelGraph,
        CkAggregatedModelElements aggregatedModelElements, CkId<CkTypeId> ckTypeId, CompilerResult compilerResult)
    {
        if (!aggregatedModelElements.CkTypes.TryGetValue(ckTypeId, out var ckType))
        {
            compilerResult.AddMessage(MessageCodes.CkTypeIdUnknown(ckTypeId));
            throw ModelValidationException.UnknownCkTypeId(ckTypeId);
        }

        if (!modelGraph.Types.TryGetValue(ckTypeId, out var typeGraph))
        {
            typeGraph = modelGraph.GetOrCreateType(ckTypeId, ckType);
        }

        if (!_handledTypesHashSet.Contains(ckTypeId))
        {
            var baseTypes = GetBaseTypes(aggregatedModelElements, ckTypeId, compilerResult);
            typeGraph.AddBaseTypes(baseTypes);
            _handledTypesHashSet.Add(ckTypeId);
        }

        return typeGraph;
    }

    private void GetDirectedAggregationsAndAttributes(CkModelGraph ckModelGraph,
        CkAggregatedModelElements aggregatedModelElements, CkTypeDto ckTypeDto,
        CkTypeGraph originTypeGraph, CompilerResult compilerResult)
    {
        for (int i = originTypeGraph.BaseTypes.Count - 1; i >= 0; i--)
        {
            var ckGraphTypeInheritance = originTypeGraph.BaseTypes.ElementAt(i);
            var baseCkType = aggregatedModelElements.CkTypes[ckGraphTypeInheritance.BaseCkTypeId];

            if (baseCkType.Attributes != null)
            {
                foreach (var typeAttribute in baseCkType.Attributes)
                {
                    originTypeGraph.Attributes.Add(typeAttribute);
                }
            }
        }

        // Add the current type's associations and attributes
        if (ckTypeDto.Associations != null)
        {
            foreach (var typeAssociation in ckTypeDto.Associations)
            {
                var targetCkTypeGraph = GetOrCreateTargetCkTypeGraph(ckModelGraph, aggregatedModelElements,
                    originTypeGraph, typeAssociation, compilerResult);

                // Check if there is a duplicate association defined at the same type.
                if (originTypeGraph.Associations.Out.Owned.Any(x =>
                        x.RoleId == typeAssociation.RoleId && x.TargetCkTypeId == typeAssociation.TargetCkTypeId))
                {
                    compilerResult.AddMessage(MessageCodes.CkTypeIdAssociationNotUnique(originTypeGraph.CkTypeId,
                        typeAssociation.RoleId, typeAssociation.TargetCkTypeId));
                    continue;
                }

                // Check if there is the same association role defined in a base type with a target to the same target type inheritance chain
                var duplicateTypeAssociations = originTypeGraph.BaseTypes.SelectMany(i =>
                {
                    var baseCkTypeGraph = GetOrCreateTypeGraph(ckModelGraph, aggregatedModelElements,
                        i.BaseCkTypeId, compilerResult);

                    return baseCkTypeGraph.Associations.Out.Owned.Where(x =>
                        x.RoleId == typeAssociation.RoleId && originTypeGraph.BaseTypes.Any(y =>
                            y.BaseCkTypeId == x.TargetCkTypeId)).Select(s => new { BaseCkTypeGraph = baseCkTypeGraph, s.TargetCkTypeId });
                }).ToList();

                if (duplicateTypeAssociations.Any())
                {
                    foreach (var duplicateTypeAssociation in duplicateTypeAssociations)
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdMultipleOutgoingAssociationRepresentingSameRole(
                            originTypeGraph.CkTypeId,
                            typeAssociation.RoleId, typeAssociation.TargetCkTypeId,
                            duplicateTypeAssociation.BaseCkTypeGraph.CkTypeId, duplicateTypeAssociation.TargetCkTypeId));
                    }

                    continue;
                }


                targetCkTypeGraph.Associations.In.Owned.Add(typeAssociation);
                originTypeGraph.Associations.Out.Owned.Add(typeAssociation);
            }
        }

        if (ckTypeDto.Attributes != null)
        {
            var duplicateAttributeNames = ckTypeDto.Attributes.GroupBy(a => a.AttributeName).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeNames.Count > 0)
            {
                compilerResult.AddMessage(
                    MessageCodes.CkTypeIdAttributeNameNotUnique(originTypeGraph.CkTypeId, duplicateAttributeNames.Select(a => a.Key)));
                throw ModelValidationException.DuplicateAttributeNamesInCkType(originTypeGraph.CkTypeId,
                    duplicateAttributeNames.Select(a => a.Key));
            }

            var duplicateAttributeIds = ckTypeDto.Attributes.GroupBy(a => a.AttributeId).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeIds.Count > 0)
            {
                compilerResult.AddMessage(
                    MessageCodes.CkTypeIdAttributeIdNotUnique(originTypeGraph.CkTypeId, duplicateAttributeNames.Select(a => a.Key)));
                throw ModelValidationException.DuplicateAttributeIdsInCkType(originTypeGraph.CkTypeId,
                    duplicateAttributeIds.Select(a => a.Key));
            }

            foreach (var typeAttribute in ckTypeDto.Attributes)
            {
                if (originTypeGraph.Attributes.Any(a => a.AttributeId == typeAttribute.AttributeId))
                {
                    compilerResult.AddMessage(
                        MessageCodes.CkTypeIdAttributeIdNotUniqueByInheritance(originTypeGraph.CkTypeId, typeAttribute.AttributeId));
                    continue;
                }

                if (originTypeGraph.Attributes.Any(a =>
                        string.Compare(a.AttributeName, typeAttribute.AttributeName, StringComparison.OrdinalIgnoreCase) == 0))
                {
                    compilerResult.AddMessage(
                        MessageCodes.CkTypeIdAttributeNameNotUniqueByInheritance(originTypeGraph.CkTypeId,
                            typeAttribute.AttributeName));
                    continue;
                }

                originTypeGraph.Attributes.Add(typeAttribute);
            }
        }
    }

    private CkTypeGraph GetOrCreateTargetCkTypeGraph(CkModelGraph ckModelGraph,
        CkAggregatedModelElements aggregatedModelElements, CkTypeGraph typeGraph,
        CkTypeAssociationDto typeAssociation, CompilerResult compilerResult)
    {
        if (!aggregatedModelElements.CkTypes.ContainsKey(typeAssociation.TargetCkTypeId))
        {
            compilerResult.AddMessage(MessageCodes.CkTypeIdUnknownTargetCkTypeIdForAssociation(typeGraph.CkTypeId,
                typeAssociation.RoleId, typeAssociation.TargetCkTypeId));
            throw ModelValidationException.UnknownCkTypeIdForAssociationTarget(typeGraph.CkTypeId,
                typeAssociation.RoleId, typeAssociation.TargetCkTypeId);
        }

        var targetCkTypeGraph = GetOrCreateTypeGraph(ckModelGraph, aggregatedModelElements,
            typeAssociation.TargetCkTypeId, compilerResult);
        return targetCkTypeGraph;
    }

    private void BuildInheritedAssociations(CkModelGraph modelGraph, CompilerResult compilerResult)
    {
        var handledInheritanceHashSet = new HashSet<Tuple<CkId<CkTypeId>, CkId<CkTypeId>>>();
        foreach (var graphType in modelGraph.Types)
        {
            foreach (var ckGraphTypeInheritance in graphType.Value.BaseTypes.Reverse())
            {
                var baseGraphType = modelGraph.Types[ckGraphTypeInheritance.BaseCkTypeId];
                var inheritedGraphType = modelGraph.Types[ckGraphTypeInheritance.InheritorCkTypeId];

                // Ensure that we don't handle the same inheritance twice
                var tuple = new Tuple<CkId<CkTypeId>, CkId<CkTypeId>>(baseGraphType.CkTypeId,
                    inheritedGraphType.CkTypeId);
                if (handledInheritanceHashSet.Contains(tuple))
                {
                    continue;
                }

                handledInheritanceHashSet.Add(tuple);

                // Add the owned associations but also the inherited ones
                foreach (var typeAssociation in baseGraphType.Associations.In.Owned)
                {
                    inheritedGraphType.Associations.In.Inherited.Add(typeAssociation);
                }

                foreach (var typeAssociation in baseGraphType.Associations.In.Inherited)
                {
                    inheritedGraphType.Associations.In.Inherited.Add(typeAssociation);
                }

                foreach (var typeAssociation in baseGraphType.Associations.Out.Owned)
                {
                    if (inheritedGraphType.Associations.Out.Inherited.Any(x =>
                            x.RoleId == typeAssociation.RoleId && x.TargetCkTypeId == typeAssociation.TargetCkTypeId))
                    {
                        compilerResult.AddMessage(MessageCodes.CkTypeIdOutAssociationNotUniqueByInheritance(inheritedGraphType.CkTypeId,
                            typeAssociation.RoleId, typeAssociation.TargetCkTypeId));
                        continue;
                    }

                    inheritedGraphType.Associations.Out.Inherited.Add(typeAssociation);
                }

                foreach (var typeAssociation in baseGraphType.Associations.Out.Inherited)
                {
                    inheritedGraphType.Associations.Out.Inherited.Add(typeAssociation);
                }
            }
        }
    }

    private static IList<CkGraphTypeInheritance> GetBaseTypes(CkAggregatedModelElements aggregatedModelElements,
        CkId<CkTypeId> ckTypeId, CompilerResult compilerResult)
    {
        var ckTypeIds = new List<CkGraphTypeInheritance>();

        int i = 0;
        CkId<CkTypeId>? currentCkTypeId = ckTypeId;
        CkId<CkTypeId>? lastCkTypeId = ckTypeId;
        while (currentCkTypeId != null &&
               aggregatedModelElements.CkTypes.TryGetValue(currentCkTypeId.Value, out var currentCkType))
        {
            var baseCkTypeId = currentCkType.DerivedFromCkTypeId;

            if (i != 0)
            {
                if (currentCkType.IsFinal)
                {
                    compilerResult.AddMessage(MessageCodes.DerivedFromCkTypeIdThatIsFinal(currentCkTypeId.Value, lastCkTypeId.Value));
                    throw ModelValidationException.DerivedFromCkTypeIdThatIsFinal(currentCkTypeId.Value, lastCkTypeId.Value);
                }
            }

            if (baseCkTypeId.HasValue)
            {
                ckTypeIds.Add(new CkGraphTypeInheritance(currentCkTypeId.Value, baseCkTypeId.Value, i++));
            }

            lastCkTypeId = currentCkTypeId;
            currentCkTypeId = baseCkTypeId;
        }

        if (currentCkTypeId != null)
        {
            compilerResult.AddMessage(MessageCodes.UnknownCkTypeIdForInheritance(currentCkTypeId.Value));
            throw ModelValidationException.UnknownCkTypeIdForInheritance(currentCkTypeId.Value);
        }
        
        if (!ckTypeIds.Any())
        {
            if (!CompilerStatics.WhiteListedCkTypeIds.Any(x => x.ModelId.ModelId == ckTypeId.ModelId.ModelId
                                                               && x.Key.TypeId == ckTypeId.Key.TypeId))
            {
                compilerResult.AddMessage(
                    MessageCodes.InheritanceMissing(ckTypeId.Key.TypeId));
                throw ModelValidationException.InheritanceMissing(ckTypeId.Key.TypeId);
            }
        }

        return ckTypeIds;
    }
}