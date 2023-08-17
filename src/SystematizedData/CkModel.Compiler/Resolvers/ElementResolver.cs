using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;

public class ElementResolver : IElementResolver
{
    public CkModelGraph Resolve(CkModelRoot ckModelRoot, CompilerResult validationResult)
    {
        var ckModelGraph = new CkModelGraph();
        
        if (ckModelRoot.CkAttributes != null)
        {
            foreach (var ckAttribute in ckModelRoot.CkAttributes)
            {
                var ckAttributeId = new CkId<CkAttributeId>(ckModelRoot.ModelId, ckAttribute.AttributeId);
                if (ckModelGraph.Attributes.ContainsKey(ckAttributeId))
                {
                    validationResult.AddMessage(MessageCodes.AttributeIdNotUnique(ckAttributeId));
                    continue;
                }
                ckModelGraph.GetOrCreateAttribute(ckAttributeId, ckAttribute);
            }
        }
        
        if (ckModelRoot.CkAssociationRoles != null)
        {
            foreach (var ckAssociationRole in ckModelRoot.CkAssociationRoles)
            {
                var ckAssociationId = new CkId<CkAssociationRoleId>(ckModelRoot.ModelId, ckAssociationRole.RoleId);
                if (ckModelGraph.AssociationRoles.ContainsKey(ckAssociationId))
                {
                    validationResult.AddMessage(MessageCodes.AssociationRoleIdNotUnique(ckAssociationId));
                    continue;
                }
                ckModelGraph.GetOrCreateAssociationRoles(ckAssociationId, ckAssociationRole);
            }
        }
        
        if (ckModelRoot.CkEntities != null)
        {
            foreach (var ckEntity in ckModelRoot.CkEntities)
            {
                var ckTypeId = new CkId<CkTypeId>(ckModelRoot.ModelId, ckEntity.TypeId);
                if (ckModelGraph.Entities.ContainsKey(ckTypeId))
                {
                    validationResult.AddMessage(MessageCodes.TypeIdNotUnique(ckTypeId));
                    continue;
                }
                ckModelGraph.GetOrCreateEntity(ckTypeId, ckEntity);
            }
        }

        return ckModelGraph;
    }
}