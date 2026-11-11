namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;

/// <summary>
///     Why a database belongs to another OctoMesh instance on the same MongoDB server: the one answer the
///     destructive and adopting admin guards share — attach (AB#8796), restore (AB#8672) and delete (AB#8807).
/// </summary>
/// <param name="IsSystemDatabase">
///     The database is another instance's system database. Destroying it destroys that whole instance, so no
///     override applies.
/// </param>
/// <param name="IsRegisteredByAnotherInstance">
///     The system database of another instance registers the database: that instance is live and may work on it.
///     Only a database that merely carries another instance's marker (an orphaned marker, nobody else registers it)
///     is open to a deliberate override.
/// </param>
/// <param name="Description">
///     Completes the sentence "Database 'x' ...", names the owner. For the server log, or for callers that may see
///     the owner; never for a reason-free conflict (AB#4763).
/// </param>
/// <param name="ForeignMarker">The ownership marker of the database when it names another instance, else null.</param>
/// <param name="ForeignRegistrations">
///     The system databases of other instances that register the database, normalized and sorted; empty for a system
///     database.
/// </param>
internal sealed record TenantDatabaseForeignOwner(bool IsSystemDatabase, bool IsRegisteredByAnotherInstance,
    string Description, TenantOwnershipRecord? ForeignMarker, IReadOnlyList<string> ForeignRegistrations)
{
    /// <summary>Whether a deliberate administrator override may proceed (orphaned foreign marker only).</summary>
    public bool CanBeOverridden => !IsSystemDatabase && !IsRegisteredByAnotherInstance;
}

/// <summary>
///     Decides whether a database belongs to another OctoMesh instance on the same MongoDB server. A database
///     belongs to another instance when it is that instance's system database, when its ownership marker (AB#4945)
///     names another instance, or when another instance's system database registers it (a tenant from before the
///     marker shipped, or a double registration, AB#8796).
/// </summary>
/// <remarks>
///     Whether a database is a system database at all is decided by the registry snapshot
///     (<see cref="TenantDatabaseRegistrations.IsSystemDatabase" />): the system-database marker first, the parent
///     tenant ids of its registry rows as the fallback. Read-only. Takes one registry snapshot of the server per
///     call (<see cref="TenantDatabaseRegistrationLookup" />), so it belongs on admin paths, not on the per-request
///     resolve.
/// </remarks>
internal static class TenantDatabaseForeignOwnership
{
    /// <summary>
    ///     Returns the foreign owner of <paramref name="databaseName" />, or null when the database does not belong to
    ///     another instance: it does not exist and nobody else registers it, it carries no marker and only this
    ///     instance registers it, or its marker names this instance and no other instance registers it.
    /// </summary>
    /// <param name="ownershipStore">Reads the ownership marker; a read never creates the database.</param>
    /// <param name="registrationLookup">The cross-instance registry lookup of AB#8796.</param>
    /// <param name="ownSystemDatabaseName">The system database of THIS instance (any casing).</param>
    /// <param name="databaseName">The database to check (any casing).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<TenantDatabaseForeignOwner?> FindAsync(TenantOwnershipStore ownershipStore,
        TenantDatabaseRegistrationLookup registrationLookup, string ownSystemDatabaseName, string databaseName,
        CancellationToken cancellationToken = default)
    {
        var ownInstance = TenantDatabaseRegistrationLookup.Normalize(ownSystemDatabaseName);
        var normalizedDatabaseName = TenantDatabaseRegistrationLookup.Normalize(databaseName);
        var registrations = await registrationLookup.GetRegistrationsAsync(cancellationToken).ConfigureAwait(false);

        if (registrations.IsSystemDatabase(normalizedDatabaseName) &&
            !string.Equals(normalizedDatabaseName, ownInstance, StringComparison.Ordinal))
        {
            return new TenantDatabaseForeignOwner(true, false,
                "is the system database of another OctoMesh instance on this MongoDB server", null, []);
        }

        var marker = await ownershipStore.GetAsync(normalizedDatabaseName, cancellationToken).ConfigureAwait(false);

        // Also when the database does not exist (yet): a name another instance has registered is not free.
        var foreignRegistrations = registrations.GetRegisteringSystemDatabases(normalizedDatabaseName)
            .Where(s => !string.Equals(s, ownInstance, StringComparison.Ordinal))
            .ToList();
        var registeredBy = foreignRegistrations.Count > 0
            ? "is registered as a tenant database by another OctoMesh instance (system database " +
              $"'{string.Join("', '", foreignRegistrations)}')"
            : null;

        if (marker != null && !string.Equals(marker.OwnerSystemDatabaseName, ownInstance, StringComparison.Ordinal))
        {
            var ownedBy =
                $"is owned by another OctoMesh instance (owner system database '{marker.OwnerSystemDatabaseName}', " +
                $"tenant '{marker.TenantId}', stamped {marker.AttachedAtUtc:u})";
            return new TenantDatabaseForeignOwner(false, registeredBy != null,
                registeredBy == null ? ownedBy : $"{ownedBy} and {registeredBy}", marker, foreignRegistrations);
        }

        if (registeredBy != null)
        {
            return new TenantDatabaseForeignOwner(false, true, registeredBy, null, foreignRegistrations);
        }

        return null;
    }
}
