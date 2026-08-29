namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     Caller-scoped session overloads for <see cref="ITenantRepository" />. The security context is used
///     for RtCreatedBy stamping and data-level permission enforcement (AB#4969); repositories that do not
///     implement <see cref="ISecureSessionFactory" /> fall back to a system session.
/// </summary>
public static class TenantRepositorySecurityExtensions
{
    /// <summary>
    ///     Gets a session for the tenant acting for the given caller.
    /// </summary>
    /// <param name="repository">The tenant repository</param>
    /// <param name="securityContext">Identity of the caller the session acts for</param>
    public static IOctoSession GetSession(this ITenantRepository repository, RtSecurityContext securityContext)
    {
        return repository is ISecureSessionFactory factory
            ? factory.GetSession(securityContext)
            : repository.GetSession();
    }

    /// <summary>
    ///     Gets a session for the tenant acting for the given caller.
    /// </summary>
    /// <param name="repository">The tenant repository</param>
    /// <param name="securityContext">Identity of the caller the session acts for</param>
    public static Task<IOctoSession> GetSessionAsync(this ITenantRepository repository,
        RtSecurityContext securityContext)
    {
        return repository is ISecureSessionFactory factory
            ? factory.GetSessionAsync(securityContext)
            : repository.GetSessionAsync();
    }
}
