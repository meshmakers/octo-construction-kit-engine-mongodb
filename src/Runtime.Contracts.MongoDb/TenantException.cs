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

    /// <summary>
    /// True when this exception represents a resource conflict (the tenant or its database already
    /// exists) rather than a generic bad request. Lets the REST layer map it to 409 Conflict — and,
    /// for the database case, signal that a previous deletion may still be completing so the caller
    /// can retry (AB#4348).
    /// </summary>
    public bool IsConflict { get; private init; }

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

    /// <summary>
    /// The requested tenant id cannot be used.
    /// </summary>
    /// <remarks>
    /// The message is deliberately uniform and carries no reason. Tenant ids are a flat,
    /// platform-wide namespace while a caller only ever sees their own subtree, so the same text has
    /// to cover "taken by a tenant you cannot see", "reserved for the system tenant" and "a deletion
    /// is still completing" — telling them apart would turn this into an existence oracle for the
    /// whole platform (AB#4763). The concrete reason goes to the server log only.
    /// </remarks>
    public static Exception TenantIdNotAvailable(string tenantId)
    {
        return new TenantException($"Tenant ID '{tenantId}' is already in use.") { IsConflict = true };
    }

    public static Exception TenantDoesNotExist(string tenantId)
    {
        return new TenantException($"Tenant '{tenantId}' does not exist.");
    }

    public static Exception SystemTenantAlreadyExisting()
    {
        return new TenantException("System tenant does already exist.");
    }

    /// <summary>
    /// The requested database name cannot be used.
    /// </summary>
    /// <remarks>
    /// Uniform and reason-free for the same purpose as <see cref="TenantIdNotAvailable" />: the
    /// existence check spans the whole MongoDB cluster, so distinguishing "another tenant's
    /// database", "the system database", "a non-tenant database such as admin or a job store" and
    /// "an orphan of a previous deletion" would let any caller enumerate the cluster (AB#4763).
    /// Operators find the real reason in the server log.
    /// </remarks>
    public static Exception DatabaseNameNotAvailable(string databaseName)
    {
        return new TenantException($"Database name '{databaseName}' is not available.") { IsConflict = true };
    }

    /// <summary>
    /// The system database is present but does not carry a usable System CK model, so the system
    /// tenant cannot be bootstrapped over it.
    /// </summary>
    /// <remarks>
    /// Deliberately explicit rather than generic: this path has no untrusted caller, it fails host
    /// startup, and the operator diagnosing it needs the actual cause. Re-creating the system tenant
    /// over an existing database used to drop it (AB#4762).
    /// </remarks>
    public static Exception SystemTenantDatabaseNotBootstrappable(string databaseName)
    {
        return new TenantException(
            $"Cannot create the system tenant: database '{databaseName}' already exists but does not " +
            "contain a usable System CK model. Refusing to re-create the system tenant over it, because " +
            "that would drop the database. Repair the System CK model of that database instead.");
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
