using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;

public class ModelValidationException : Exception
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
    
    internal static Exception DuplicateAttributeIds(IEnumerable<CkId<CkAttributeId>> duplicateAttributes)
    {
        var attributeIds = string.Join(", ", duplicateAttributes);
        return new ModelValidationException($"Following attribute ids are duplicates: '{attributeIds}'");
    }

    internal static Exception UnknownCkIdForInheritance(CkId<CkTypeId> ckId)
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

    public static Exception CkAssociationRoleNotFound(CkId<CkAssociationId> associationId)
    {
        return new ModelValidationException($"Association role '{associationId}' not found.");
    }

    public static Exception UnknownCkModel(CkModelId modelDependency)
    {
       return new ModelValidationException($"Repository does not contain construction kit model '{modelDependency}'.");
    }
}

