namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

/// <summary>
///     A source that knows which database a tenant's data lives in, supplied at runtime rather than
///     read from the installation's tenant registry (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this seam exists.</b> Resolving a tenant normally goes through the registry in the
///         system database, and that walk is not free of authority: it probes the installation with
///         <c>listDatabases</c> on the <b>admin</b> connection, reads the system CK model, and only
///         then looks up the tenant's row. A process that must do that therefore needs the
///         installation's admin and datasource credentials — which is exactly what an adapter-pool
///         member must not have. A member is handed one tenant at a time by a lease, and the lease
///         already carries that tenant's database name and a credential for it; this seam is what lets
///         the member use them instead of asking the registry.
///     </para>
///     <para>
///         <b>The companion of <see cref="ITenantDatabaseCredentialSource" />, and useless without
///         it.</b> That one answers "which credential opens this database", this one answers "which
///         database holds this tenant". Both are keyed differently on purpose — the credential by
///         database name, because a process may open several, and this one by tenant id, because that
///         is what a caller asks for.
///     </para>
///     <para>
///         🔴 <b>A context built from this source is DETACHED: it manages nothing.</b> The registry
///         walk does more than resolve a name — it imports service-managed CK models, ensures the
///         stream-data model, and stamps the ownership marker. None of that may happen here. Those are
///         writes into a tenant's database made on behalf of the installation that owns it, and a
///         borrowed process running one pipeline is not that installation. A member reads and writes
///         entities; it never manages a tenant's model.
///     </para>
///     <para>
///         <b>Optional by construction.</b> Nothing registers an implementation by default, and a host
///         without one resolves tenants exactly as it did before this interface existed — there is no
///         "unconfigured source" state that could weaken an existing deployment.
///     </para>
/// </remarks>
public interface ITenantLocationSource
{
    /// <summary>
    ///     The database holding <paramref name="tenantId" />'s data, when this source was told where
    ///     that tenant lives.
    /// </summary>
    /// <remarks>
    ///     🔴 Returning <c>false</c> must mean "I was not told about this tenant", never "I was told
    ///     but am unsure". A source that answered with a guess would point a caller at another
    ///     tenant's database — and the credential seam would then be asked to open it, which is the
    ///     one thing the two of them exist to prevent.
    /// </remarks>
    /// <param name="tenantId">The tenant a context is being resolved for.</param>
    /// <param name="databaseName">The database holding that tenant's data.</param>
    /// <returns><c>true</c> when <paramref name="databaseName" /> was set.</returns>
    bool TryGetDatabaseName(string tenantId, out string databaseName);
}
