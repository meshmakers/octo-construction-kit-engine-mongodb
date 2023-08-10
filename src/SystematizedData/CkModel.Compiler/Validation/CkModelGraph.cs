using System.Security.Cryptography;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
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

    public Dictionary<CkId<CkTypeId>, CkEntityDto> CkEntities { get; }
    public Dictionary<CkId<CkAttributeId>, CkAttributeDto> CkAttributes { get; }
    public Dictionary<CkId<CkAssociationRoleId>, CkAssociationRoleDto> CkAssociationRoles { get; }

    public static CkModelGraph Create(CkModelRoot ckModelRoot, ValidationResult validationResult)
    {
        var ckModelGraph = new CkModelGraph();
        
        if (ckModelRoot.CkAttributes != null)
        {
            foreach (var ckAttribute in ckModelRoot.CkAttributes)
            {
                var ckAttributeId = new CkId<CkAttributeId>(ckModelRoot.ModelId, ckAttribute.AttributeId);
                if (ckModelGraph.CkAttributes.ContainsKey(ckAttributeId))
                {
                    validationResult.AddMessage(MessageCodes.AttributeIdNotUnique(ckAttributeId));
                    continue;
                }
                ckModelGraph.CkAttributes.Add(ckAttributeId, ckAttribute);
            }
        }
        
        if (ckModelRoot.CkAssociationRoles != null)
        {
            foreach (var ckAssociationRole in ckModelRoot.CkAssociationRoles)
            {
                var ckAssociationId = new CkId<CkAssociationRoleId>(ckModelRoot.ModelId, ckAssociationRole.RoleId);
                if (ckModelGraph.CkAssociationRoles.ContainsKey(ckAssociationId))
                {
                    validationResult.AddMessage(MessageCodes.AssociationRoleIdNotUnique(ckAssociationId));
                    continue;
                }
                ckModelGraph.CkAssociationRoles.Add(ckAssociationId, ckAssociationRole);
            }
        }
        
        if (ckModelRoot.CkEntities != null)
        {
            foreach (var ckEntity in ckModelRoot.CkEntities)
            {
                var ckTypeId = new CkId<CkTypeId>(ckModelRoot.ModelId, ckEntity.TypeId);
                if (ckModelGraph.CkEntities.ContainsKey(ckTypeId))
                {
                    validationResult.AddMessage(MessageCodes.TypeIdNotUnique(ckTypeId));
                    continue;
                }
                ckModelGraph.CkEntities.Add(ckTypeId, ckEntity);
            }
        }

        return ckModelGraph;
    }
}