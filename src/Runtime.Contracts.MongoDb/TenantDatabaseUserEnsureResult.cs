namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Outcome of <see cref="ISystemContext.EnsureTenantDatabaseUserAsync" /> (AB#8177).
/// </summary>
public enum TenantDatabaseUserEnsureResult
{
    /// <summary>The tenant is not in the registry (e.g. deleted meanwhile); nothing was touched.</summary>
    TenantNotRegistered = 0,

    /// <summary>The database user already existed.</summary>
    AlreadyPresent = 1,

    /// <summary>The database user was missing and has been created.</summary>
    Created = 2
}
