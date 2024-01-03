using System.Linq.Expressions;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class EntityNotFoundException : OperationFailedException
{
    private EntityNotFoundException()
    {
    }

    private EntityNotFoundException(string message) : base(message)
    {
    }

    private EntityNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception FilterNotMatching<TDocument>(string filter) where TDocument : class, new()
    {
        return new EntityNotFoundException( $"Operation failed because filter '{filter}' did not match any documents for type {nameof(TDocument)}.");
    }

    public static Exception NoDataMatched()
    {
        return new EntityNotFoundException("Operation failed because no data matched.");
    }

    public static Exception IdNotFound(string typeName, string id)
    {
        return new EntityNotFoundException($"Operation failed because ID '{id}' is not existing for document type {typeName}.");
    }
}