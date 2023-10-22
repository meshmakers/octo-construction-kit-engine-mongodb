
namespace Meshmakers.Octo.SystematizedData.Persistence;

[Serializable]
public class TenantRepositoryException : Runtime.Contracts.PersistenceException
{
    private TenantRepositoryException()
    {
    }

    private TenantRepositoryException(string message) : base(message)
    {
    }

    private TenantRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception NoFilterDefinitions()
    {
        return new TenantRepositoryException("No filter definitions defined.");
    }

    public static Exception EntityFilterReturnNotExactlyOne()
    {
        return new TenantRepositoryException("Entity filter returns not exactly one entity.");
    }
}
