using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;

public class ModelValidationException : CkModelException
{
    public ModelValidationException()
    {
    }

    public ModelValidationException(string message) : base(message)
    {
    }

    public ModelValidationException(string message, Exception inner) : base(message, inner)
    {
    }
    
    public static Exception DuplicateAttributeIds(IEnumerable<CkId<CkAttributeId>> duplicateAttributes)
    {
        var attributeIds = string.Join(", ", duplicateAttributes);
        return new ModelValidationException($"Following attribute ids are duplicates: '{attributeIds}'");
    }

    public static Exception UnknownCkIdForInheritance(CkId<CkTypeId> ckId)
    {
        return new ModelValidationException($"CkId '{ckId}' is unknown for inheritance.");
    }


    public static Exception CkIdAlreadyExistsInDatabase(CkId<CkTypeId> ckId)
    {
        return new ModelValidationException($"CkId '{ckId}' already exists in database.");
    }

    public static Exception UnknownAttributeOfCkIdInSource(CkId<CkTypeId> ckId, CkId<CkAttributeId> attributeId)
    {
        return new ModelValidationException($"Attribute Id '{attributeId}' of CkId '{ckId}' does not exist.");
    }

    public static Exception CommonValidationFailed(string error)
    {
        return new ModelValidationException($"Validation of Construction Kit Model failed:" + Environment.NewLine + error);
    }

    public static Exception DuplicateAttributeIdsInCkEntity(CkId<CkTypeId> ckId, IEnumerable<CkId<CkAttributeId>> duplicateAttributeIds)
    {
        var attributeIds = string.Join(", ", duplicateAttributeIds);
        return new ModelValidationException($"CkId '{ckId}' has duplicate attribute IDs: '{attributeIds}'");
    }

    public static Exception DuplicateAttributeNamesInCkEntity(object ckId, IEnumerable<string> select)
    {
        var attributeNames = string.Join(", ", select);
        return new ModelValidationException($"CkId '{ckId}' has duplicate attribute names: '{attributeNames}'");
    }

    public static Exception CkIdUsingSystemReservedAttributeNames(object ckId, IEnumerable<string> systemReservedAttributeNames)
    {
        var attributeNames = string.Join(", ", systemReservedAttributeNames);
        return new ModelValidationException(
            $"CkId '{ckId}' using attribute names that are system reserved: '{attributeNames}'");
    }

    public static Exception CkAssociationRoleNotFound(CkId<CkAssociationRoleId> associationId)
    {
        return new ModelValidationException($"Association role '{associationId}' not found.");
    }

    public static Exception UnknownCkModel(CkModelId modelDependency)
    {
       return new ModelValidationException($"Repository does not contain construction kit model '{modelDependency}'.");
    }


    public static Exception MissingTargetEntity(RtEntityId rtEntityId)
    {
        return new ModelValidationException($"Target entity '{rtEntityId}' does not exist.");
    }
    
    public static Exception MissingOriginEntity(RtEntityId rtEntityId)
    {
        return new ModelValidationException($"Origin entity '{rtEntityId}' does not exist.");
    }

    public static Exception AssociationNotAllowed(CkId<CkAssociationRoleId> roleId, RtEntityId rtEntityId)
    {
        return new ModelValidationException(
            $"CkType '{rtEntityId.CkId}'->RtId '{rtEntityId.RtId}': Inbound association '{roleId}' is not allowed.");
    }
    
    public static Exception InboundAssociationNotAllowedForCkType(CkId<CkAssociationRoleId> roleId, RtEntityId originRtEntityId, CkId<CkTypeId> ckId)
    {
        return new ModelValidationException(
            $"CkType '{originRtEntityId.CkId}'->RtId '{originRtEntityId.RtId}': Inbound association '{roleId}' to CkId '{ckId}' is not allowed.");
    }
    
    public static Exception OutboundAssociationNotAllowedForCkType(CkId<CkAssociationRoleId> roleId, RtEntityId originRtEntityId, CkId<CkTypeId> ckId)
    {
        return new ModelValidationException(
            $"CkType '{originRtEntityId.CkId}'->RtId '{originRtEntityId.RtId}': Outbound association '{roleId}' to CkId '{ckId}' is not allowed.");
    }
    
    public static Exception AssociationCardinalityViolationOnCreate(CkId<CkAssociationRoleId> roleId, Multiplicities multiplicity, RtEntityId rtEntityId)
    {
        return new ModelValidationException(
            $"CkType '{rtEntityId.CkId}'->RtId '{rtEntityId.RtId}': Inbound association '{roleId}' has minimum multiplicity of '{multiplicity}'. There is no create statement for creating this association.");
    }

    
    public static Exception AssociationCardinalityViolationOnDelete(CkId<CkAssociationRoleId> roleId, Multiplicities multiplicity, RtEntityId rtEntityId)
    {
        return new ModelValidationException(
            $"CkType '{rtEntityId.CkId}'->RtId '{rtEntityId.RtId}': Inbound association '{roleId}' has maximum multiplicity of '{multiplicity}'. Association deletion violates the model.");
    }
    
    public static Exception AssociationCardinalityViolationOnModification(CkId<CkAssociationRoleId> roleId, Multiplicities multiplicity, RtEntityId rtEntityId)
    {
        return new ModelValidationException(
            $"CkType '{rtEntityId.CkId}'->RtId '{rtEntityId.RtId}': Inbound association '{roleId}' has maximum multiplicity of '{multiplicity}'. Adding another association violates the model.");
    }
}

