using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class InvalidAttributeException : OperationFailedException
{
    private InvalidAttributeException()
    {
    }

    private InvalidAttributeException(string message) : base(message)
    {
    }

    private InvalidAttributeException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception SortDefinitionContainsInvalidAttribute(string attributeName, string rtEntityName)
    {
        return new InvalidAttributeException($"Sort definition contains attribute '{attributeName}', but attribute does not exist on type '{rtEntityName}'");
    }

    public static Exception AttributeNotFound(CkId<CkTypeId> ckTypeId, string attributeName)
    {
        return new InvalidAttributeException($"Attribute with name '{attributeName}' does not exist at CkTypeId '{ckTypeId}'.");
    }

    public static Exception AutoIncrementReferenceNotFound(CkId<CkTypeId> ckTypeId, string? autoIncrementReference)
    {
        return new InvalidAttributeException($"Auto increment reference '{autoIncrementReference}' does not exist at CkTypeId '{ckTypeId}'.");
    }
}