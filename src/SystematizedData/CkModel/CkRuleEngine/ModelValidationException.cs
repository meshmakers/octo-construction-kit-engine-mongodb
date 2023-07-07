using Meshmakers.Octo.Common.Shared;

namespace CkModel.CkRuleEngine;

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
    
    internal static Exception DuplicateAttributeIds(IEnumerable<string> duplicateAttributes)
    {
        var attributeIds = string.Join(", ", duplicateAttributes);
        return new ModelValidationException($"Following attribute ids are duplicates: '{attributeIds}'");
    }

    internal static Exception UnknownCkIdForInheritance(CkTypeId ckId)
    {
        return new ModelValidationException($"CkId '{ckId}' is unknown for inheritance.");
    }


    public static Exception CkIdAlreadyExistsInDatabase(CkTypeId ckId)
    {
        return new ModelValidationException($"CkId '{ckId}' already exists in database.");
    }

    public static Exception UnknownAttributeOfCkIdInSource(CkTypeId ckId, string attributeId)
    {
        return new ModelValidationException($"Attribute Id '{attributeId}' of CkId '{ckId}' does not exist.");
    }

    public static Exception CommonValidationFailed(string error)
    {
        return new ModelValidationException($"Validation of Construction Kit Model failed:" + Environment.NewLine + error);
    }

    public static Exception DuplicateAttributeIdsInCkEntity(CkTypeId ckId, IEnumerable<string> duplicateAttributeIds)
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
}

