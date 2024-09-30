
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Exception thrown by the <see cref="IDatabaseCkModelRepository"/> when an error occurs.
/// </summary>
public class DatabaseCkModelRepositoryException : CkModelException
{
    public DatabaseCkModelRepositoryException()
    {
    }

    public DatabaseCkModelRepositoryException(string message) : base(message)
    {
    }

    public DatabaseCkModelRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception CkEnumNotFound(CkId<CkEnumId> ckEnumId)
    {
        return new DatabaseCkModelRepositoryException($"Enum '{ckEnumId}' not found.");
    }

    public static Exception CkEnumValueIsSystem(CkId<CkEnumId> ckEnumId, int valueKey)
    {
        return new DatabaseCkModelRepositoryException($"Enum '{ckEnumId}' value '{valueKey}' is a system value and cannot be modified.");
    }

    public static Exception CkEnumValueAlreadyExists(CkId<CkEnumId> ckEnumId, int valueKey)
    {
        return new DatabaseCkModelRepositoryException($"Enum '{ckEnumId}' with key '{valueKey}' already exists.");
    }

    public static Exception ErrorDuringUpdateOfCkEnumExtensions(CkId<CkEnumId> ckEnumId, Exception exception)
    {
        return new DatabaseCkModelRepositoryException($"Error during update of extensions for enum '{ckEnumId}'.", exception);
    }

    public static Exception CkEnumNotExtensible(CkId<CkEnumId> ckEnumId)
    {
        return new DatabaseCkModelRepositoryException($"Enum '{ckEnumId}' is not extensible.");
    }
}
