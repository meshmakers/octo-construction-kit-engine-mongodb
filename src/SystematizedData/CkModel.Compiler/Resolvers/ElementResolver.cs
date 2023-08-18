using System.Text.RegularExpressions;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

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
                
                if (!Regex.IsMatch(ckAttribute.AttributeId.AttributeId, CompilerStatics.AllowedCharactersInNamesRegex))
                {
                    validationResult.AddMessage(MessageCodes.CkAttributeIdContainsInvalidCharacters(ckAttribute.AttributeId.AttributeId));
                    continue;
                }
                
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
                var ckAssociationId = new CkId<CkAssociationRoleId>(ckModelRoot.ModelId, ckAssociationRole.AssociationRoleId);
                if (!Regex.IsMatch(ckAssociationRole.AssociationRoleId.RoleId, CompilerStatics.AllowedCharactersInNamesRegex))
                {
                    validationResult.AddMessage(MessageCodes.CkAssociationIdContainsInvalidCharacters(ckAssociationRole.AssociationRoleId.RoleId));
                    continue;
                }
                if (ckModelGraph.AssociationRoles.ContainsKey(ckAssociationId))
                {
                    validationResult.AddMessage(MessageCodes.AssociationRoleIdNotUnique(ckAssociationId));
                    continue;
                }
                ckModelGraph.GetOrCreateAssociationRoles(ckAssociationId, ckAssociationRole);
            }
        }
        
        if (ckModelRoot.CkTypes != null)
        {
            foreach (var ckType in ckModelRoot.CkTypes)
            {
                var ckTypeId = new CkId<CkTypeId>(ckModelRoot.ModelId, ckType.TypeId);
                if (!Regex.IsMatch(ckType.TypeId.TypeId, CompilerStatics.AllowedCharactersInNamesRegex))
                {
                    validationResult.AddMessage(MessageCodes.CkTypeIdContainsInvalidCharacters(ckType.TypeId.TypeId));
                    continue;
                }
                if (ckModelGraph.Types.ContainsKey(ckTypeId))
                {
                    validationResult.AddMessage(MessageCodes.TypeIdNotUnique(ckTypeId));
                    continue;
                }
                ckModelGraph.GetOrCreateType(ckTypeId, ckType);
            }
        }

        return ckModelGraph;
    }
}