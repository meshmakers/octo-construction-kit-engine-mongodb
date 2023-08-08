using System.Security.Cryptography;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

public class CkModelGraph
{
    private CkModelGraph()
    {
        CkEntities = new();
        CkAttributes = new();
        CkAssociationRoles = new();
    }

    public Dictionary<CkId<CkTypeId>, CkEntity> CkEntities { get; }
    public Dictionary<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public Dictionary<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }

    public static CkModelGraph Create(CkModelRoot ckModelRoot, ValidationResult validationResult)
    {
        var ckModelGraph = new CkModelGraph();
        
        if (ckModelRoot.CkAttributes != null)
        {
            foreach (var ckAttribute in ckModelRoot.CkAttributes)
            {
                var ckId = new CkId<CkAttributeId>(ckModelRoot.ModelId, ckAttribute.AttributeId);
                if (ckModelGraph.CkAttributes.ContainsKey(ckId))
                {
                    validationResult.AddMessage(MessageCodes.AttributeIdNotUnique(ckId));
                    continue;
                }
                ckModelGraph.CkAttributes.Add(ckId, ckAttribute);
            }
        }
        
        if (ckModelRoot.CkAssociationRoles != null)
        {
            foreach (var ckAssociationRole in ckModelRoot.CkAssociationRoles)
            {
                var ckId = new CkId<CkAssociationRoleId>(ckModelRoot.ModelId, ckAssociationRole.RoleId);
                if (ckModelGraph.CkAssociationRoles.ContainsKey(ckId))
                {
                    validationResult.AddMessage(MessageCodes.AssociationRoleIdNotUnique(ckId));
                    continue;
                }
                ckModelGraph.CkAssociationRoles.Add(ckId, ckAssociationRole);
            }
        }
        
        if (ckModelRoot.CkEntities != null)
        {
            foreach (var ckEntity in ckModelRoot.CkEntities)
            {
                var ckId = new CkId<CkTypeId>(ckModelRoot.ModelId, ckEntity.TypeId);
                if (ckModelGraph.CkEntities.ContainsKey(ckId))
                {
                    validationResult.AddMessage(MessageCodes.TypeIdNotUnique(ckId));
                    continue;
                }
                ckModelGraph.CkEntities.Add(ckId, ckEntity);
            }
        }

        return ckModelGraph;
    }
}