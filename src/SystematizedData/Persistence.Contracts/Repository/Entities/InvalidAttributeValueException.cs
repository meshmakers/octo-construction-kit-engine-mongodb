using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class InvalidAttributeValueException : Exception
{
    public InvalidAttributeValueException()
    {
    }

    public InvalidAttributeValueException(string message) : base(message)
    {
    }

    public InvalidAttributeValueException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception CannotBeNull(OctoObjectId rtId, CkId<CkTypeId> ckId, string attributeName)
    {
        return new InvalidAttributeValueException($"Attribute value cannot be null for {ckId}.{rtId} at attribute with name {attributeName}");
    }
}