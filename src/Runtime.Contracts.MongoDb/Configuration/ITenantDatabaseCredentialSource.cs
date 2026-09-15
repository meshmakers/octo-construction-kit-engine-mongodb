namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

/// <summary>
///     A source of database credentials that are valid for <b>one named database</b> and are supplied
///     at runtime rather than by the process's own configuration (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this seam exists.</b> <see cref="OctoSystemConfiguration.DatabaseUser" /> is a
///         format over the database name and <see cref="OctoSystemConfiguration.DatabaseUserPassword" />
///         is one installation-wide value, so a process that holds them can open <i>every</i> tenant's
///         database. That is correct for a platform service and wrong for an adapter-pool member, which
///         executes work for tenants other than the one that owns it and is handed exactly one of them
///         at a time by a lease. The Communication Operator therefore withholds the cluster's
///         data-store credentials from a pool, and the lease carries a credential for the borrowing
///         tenant's database instead — which needs somewhere to be applied.
///     </para>
///     <para>
///         <b>Per database, not per process.</b> A member that swapped its process-wide credential for
///         the borrower's would present the borrower's datasource user to the installation's tenant
///         registry as well, which is neither authorised nor meant. The database name is therefore the
///         argument: a source answers for the one database its credential belongs to and stays silent
///         for every other, so the fallback to the configured credential is what happens everywhere
///         else — including in every host that never registers a source at all.
///     </para>
///     <para>
///         <b>Optional by construction.</b> Nothing registers an implementation by default. A host
///         without one behaves exactly as it did before this interface existed; there is no
///         "unconfigured source" state that could weaken an existing deployment.
///     </para>
/// </remarks>
public interface ITenantDatabaseCredentialSource
{
    /// <summary>
    ///     The credential to open <paramref name="databaseName" /> with, when this source holds one
    ///     for exactly that database.
    /// </summary>
    /// <remarks>
    ///     🔴 Returning <c>false</c> must mean "I have nothing for this database", never "I have
    ///     something but cannot decide". An implementation that answered for a database it was not
    ///     given a credential for would authenticate one tenant's connection as another's.
    /// </remarks>
    /// <param name="databaseName">The database a connection is being built for.</param>
    /// <param name="user">The database user to authenticate as.</param>
    /// <param name="password">That user's password.</param>
    /// <returns><c>true</c> when both out parameters were set.</returns>
    bool TryGetCredential(string databaseName, out string user, out string password);
}
