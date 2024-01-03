using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class InvalidCkTypeIdException : OperationFailedException
{
    private InvalidCkTypeIdException()
    {
    }

    private InvalidCkTypeIdException(string message) : base(message)
    {
    }

    private InvalidCkTypeIdException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception CkTypeIdNotFound(string tenantId, CkId<CkTypeId> ckTypeId)
    {
        return new InvalidCkTypeIdException($"Construction Kit Type Id '{ckTypeId}' is invalid for tenant '{tenantId}'.");
    }
}