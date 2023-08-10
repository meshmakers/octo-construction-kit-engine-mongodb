using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

public class CkModelValidator : ICkModelValidator
{
    private readonly IDependencyResolver _dependencyResolver;

    public CkModelValidator(IDependencyResolver dependencyResolver)
    {
        _dependencyResolver = dependencyResolver;
    }

    public async Task<ValidationResult> ValidateAsync(CkModelRoot model)
    {
        ValidationResult validationResult = new();

        // By creating the model graph, a validation is done if association roles, attributes and entities are unique.
        CkModelGraph modelGraph = CkModelGraph.Create(model, validationResult);
        
        // Before the checks, we need to build a cache of the model.
        // We check if the can retrieve the model from one of the model repository sources (e.g. database).
        // We combine all entities, attributes and association roles into one list.
        CkAggregatedModelElements aggregatedModelElements = new();
        
        if (model.CkDependencies != null)
        {
            aggregatedModelElements = await _dependencyResolver.ResolveDependenciesAsync(model.CkDependencies, validationResult);
        }
        
        // We suppose that the dependent models are already validated and we can use them.
        // So we check the current to be validated model against the dependent models.
        
        // Check: Ensure that the model forces no circular dependencies.
        if (aggregatedModelElements.CkModelDependencies.Any(x => x.Key.ModelId == model.ModelId.ModelId))
        {
            var dependentModels = aggregatedModelElements.CkModelDependencies.Keys.Where(x => x.ModelId == model.ModelId.ModelId);
            
            validationResult.AddMessage(
                MessageCodes.CircularDependency(model.ModelId.ModelId, dependentModels.Select(x => x.ModelId).ToList()));
        }

        // Check: There are only a few places, where elements of other models are used.
        // 1. entities.attributes.id -> Reference to a defined attribute.
        // 2. entities.ckDerivedId -> Reference to a defined entity.
        // 3. entities.associations.roleId -> Reference to a defined association role.
        // 4. entities.associations.targetCkTypeId -> Reference to a defined entity.
        ValidateReferences(modelGraph, aggregatedModelElements, validationResult);

        // Check: Inheritance.
        // 1. entities.ckDerivedId -> Only one entity cannot have a derived entity: System.Entity.
        // 2. entities.attributes -> It is not possible that an entity has an attribute, which is defined in a base entity.
        // 3. entities.attributes -> It is not possible that an entity has an attribute name, that is defined in a base entity.
        // 4. entities.associations -> It is not possible that an entity has an association, which is defined in a base entity.
        // 5. entities.isFinal -> It is not possible that an entity is final, but has a derived entity.
        
        foreach (var ckEntityKeyValue in modelGraph.CkEntities)
        {
            // Check 1.
            if (ckEntityKeyValue.Value.DerivedFromCkTypeId == null)
            {
                if (!CompilerStatics.WhiteListedCkTypeIds.Any(x => x.ModelId.ModelId == ckEntityKeyValue.Key.ModelId.ModelId
                                                               && x.Key.TypeId == ckEntityKeyValue.Key.Key.TypeId))
                {
                    validationResult.AddMessage(
                        MessageCodes.InheritanceMissing(ckEntityKeyValue.Key));
                }
            }
            
            // Check 2.
            
            
        }
        

            
        return validationResult;
    }

    private static void ValidateReferences(CkModelGraph modelGraph, CkAggregatedModelElements aggregatedModelElements, ValidationResult validationResult)
    {
        foreach (var ckEntityKeyValue in modelGraph.CkEntities)
        {
            if (ckEntityKeyValue.Value.Attributes != null)
            {
                // Check 1.
                foreach (var ckEntityAttribute in ckEntityKeyValue.Value.Attributes)
                {
                    if (!aggregatedModelElements.CkAttributes.ContainsKey(ckEntityAttribute.AttributeId) &&
                        !modelGraph.CkAttributes.ContainsKey(ckEntityAttribute.AttributeId))
                    {
                        validationResult.AddMessage(
                            MessageCodes.UnknownAttributeOfCkTypeIdInSource(ckEntityKeyValue.Key, ckEntityAttribute.AttributeId));
                    }
                }
            }

            // Check 2.
            if (ckEntityKeyValue.Value.DerivedFromCkTypeId != null)
            {
                if (!aggregatedModelElements.CkEntities.ContainsKey(ckEntityKeyValue.Value.DerivedFromCkTypeId.Value) &&
                    !modelGraph.CkEntities.ContainsKey(ckEntityKeyValue.Value.DerivedFromCkTypeId.Value))
                {
                    validationResult.AddMessage(
                        MessageCodes.UnknownCkDerivedIdOfCkTypeIdInSource(ckEntityKeyValue.Value.DerivedFromCkTypeId, ckEntityKeyValue.Key));
                }
            }

            if (ckEntityKeyValue.Value.Associations != null)
            {
                foreach (var ckEntityAssociation in ckEntityKeyValue.Value.Associations)
                {
                    // Check 3.
                    if (!aggregatedModelElements.CkAssociationRoles.ContainsKey(ckEntityAssociation.RoleId) &&
                        !modelGraph.CkAssociationRoles.ContainsKey(ckEntityAssociation.RoleId))
                    {
                        validationResult.AddMessage(
                            MessageCodes.UnknownAssociationRoleOfCkTypeIdInSource(ckEntityKeyValue.Key, ckEntityAssociation.RoleId));
                    }

                    // Check 4.
                    if (!aggregatedModelElements.CkEntities.ContainsKey(ckEntityAssociation.TargetCkTypeId) &&
                        !modelGraph.CkEntities.ContainsKey(ckEntityAssociation.TargetCkTypeId))
                    {
                        validationResult.AddMessage(
                            MessageCodes.UnknownTargetCkTypeIdOfCkTypeIdInSource(ckEntityKeyValue.Key, ckEntityAssociation.TargetCkTypeId));
                    }
                }
            }
        }
    }
}