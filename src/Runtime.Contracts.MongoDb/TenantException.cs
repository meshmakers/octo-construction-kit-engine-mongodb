using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

[Serializable]
public class TenantException : PersistenceException
{
    private TenantException()
    {
    }

    private TenantException(string message) : base(message)
    {
    }

    private TenantException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception SystemModelNotFoundInCatalog(CkModelId ckModelId)
    {
        return new TenantException($"System model {ckModelId} not found in any catalog.");
    }

    internal static Exception ErrorDuringSystemModelLoad(OperationResult operationResult)
    {
        return new TenantException($"Error loading system model.{Environment.NewLine}{operationResult.GetMessages()}");
    }

    public static Exception ModelNotFound(CkModelId ckModelId)
    {
        return new TenantException($"Model {ckModelId} not found by repository management.");
    }

    public static Exception ErrorDuringModelLoad(CkModelId ckModelId, OperationResult operationResult)
    {
        return new TenantException($"Error loading model {ckModelId}.{Environment.NewLine}{operationResult.GetMessages()}");
    }

    public static Exception TenantDoesAlreadyExist(string tenantId)
    {
        return new TenantException($"Tenant {tenantId} does already exist.");
    }

    public static Exception TenantDoesNotExist(string tenantId)
    {
        return new TenantException($"Tenant '{tenantId}' does not exist.");
    }

    public static Exception SystemTenantAlreadyExisting()
    {
        return new TenantException("System tenant does already exist.");
    }

    public static Exception TenantDatabaseDoesAlreadyExist(string normalizedDatabaseName)
    {
        return new TenantException($"Tenant database '{normalizedDatabaseName}' does already exist.");
    }

    public static Exception TenantDatabaseDoesNotExist(string databaseName)
    {
        return new TenantException($"Tenant database '{databaseName}' does not exist.");
    }

    public static Exception SystemTenantDatabaseNotExisting()
    {
        return new TenantException("System tenant database does not exist, is not accessible or the system model is missing.");
    }

    public static Exception CannotCreateMongoDbRepositoryClient(string databaseName)
    {
        return new TenantException($"Cannot create MongoDB repository client for database '{databaseName}'.");
    }

    public static Exception DeleteSystemTenantFailed()
    {
       return new TenantException("Deleting system tenant failed.");
    }

    public static Exception CreateSystemTenantFailed(Exception e)
    {
        return new TenantException("Creating system tenant failed.", e);
    }

    public static Exception CannotRegisterBecauseAlreadyRegistered(Type type)
    {
        return new TenantException(
            $"Cannot register type '{type}' because it is already registered. That indicates that BSON class maps where used before initialization of MongoDB client.");
    }

    public static Exception AdminCredentialsMissing()
    {
        return new TenantException("Admin credentials are missing. Please provide admin user and password in the configuration.");
    }

    public static Exception FailedLoadingTenant(string tenantId, OperationResult operationResult)
    {
        return new TenantException($"Failed loading tenant '{tenantId}'.{Environment.NewLine}{operationResult.GetMessages()}");
    }

    public static Exception ModelNotFoundInACatalog(CkModelId ckModelId)
    {
        return new TenantException($"Model {ckModelId} not found in any catalog.");
    }
}
