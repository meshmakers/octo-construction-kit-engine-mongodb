using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     The write rule of synced master data. An entity that carries <see cref="AttributeName" /> is a copy that a
///     cross-tenant master-data sync wrote into this tenant; the tenant named in it owns the entity.
/// </summary>
/// <remarks>
///     Invariant, enforced in the write funnel of every tenant repository (insert, update, replace, delete and archive,
///     by id, by filter and in <c>ApplyChanges</c>): only a session from
///     <see cref="TenantRepositorySyncWriterExtensions.GetSyncWriterSessionAsync" /> writes a copy or the origin, and only
///     for the copies of the source tenant it declared. Every other session - also a system session - is refused when it
///     changes, replaces, deletes or archives a copy, or sets or changes the origin. A sync-writer session in turn never
///     touches an entity without the origin and inserts only entities that carry its own origin.
///     Not covered: the bulk import (<c>BulkInsertRtEntitiesAsync</c>: RT import, blueprint seeds, restore keep copies
///     as they are), the <c>*ForMigrationAsync</c> maintenance methods, and the repositories the engine maintenance
///     services get from the <c>IRuntimeRepositoryProvider</c> (CK model migrations, blueprints, secret maintenance).
///     Associations are not refused: a local entity may link to a copy.
/// </remarks>
public static class SyncOriginWriteRules
{
    /// <summary>The attribute that marks a synced copy (the System record <c>SyncOrigin</c> on <c>System/Entity</c>).</summary>
    public const string AttributeName = "RtSyncOrigin";

    /// <summary>The member of the origin record that names the source tenant.</summary>
    public const string SourceTenantIdMember = "SourceTenantId";

    /// <summary>Stable code of a refusal, in the parameter <c>code</c> of the operation message. Safe to match on.</summary>
    public const string ErrorCode = "SYNC_OWNED_ENTITY";

    /// <summary>
    ///     The number of the operation message of a refusal: the number of the other write refusals of the write funnel.
    ///     Match on <see cref="ErrorCode" /> to tell this refusal apart.
    /// </summary>
    public const int MessageNumber = 144;

    /// <summary>Whether <paramref name="exception" /> is a refusal of this rule.</summary>
    public static bool IsRefusal(Exception? exception)
    {
        return exception is RuntimeRepositoryException repositoryException &&
               repositoryException.OperationResult.Messages.Any(m =>
                   m.MessageNumber == MessageNumber &&
                   m.Parameters.TryGetValue("code", out var code) && code == ErrorCode);
    }
}

/// <summary>
///     Opens the sessions of the cross-tenant master-data sync. Implemented by the tenant repository; never reachable from
///     API input.
/// </summary>
public interface ISyncWriterSessionFactory
{
    /// <summary>
    ///     A session acting for <paramref name="securityContext" /> that writes the copies of
    ///     <paramref name="sourceTenantId" /> (see <see cref="SyncOriginWriteRules" />).
    /// </summary>
    Task<IOctoSession> GetSyncWriterSessionAsync(RtSecurityContext securityContext, string sourceTenantId);
}

/// <summary>Sync-writer sessions for <see cref="ITenantRepository" />.</summary>
public static class TenantRepositorySyncWriterExtensions
{
    /// <summary>
    ///     A session of the master-data sync of <paramref name="sourceTenantId" /> (see <see cref="SyncOriginWriteRules" />).
    ///     Fails for a repository that cannot open one; there is no fallback to a plain session.
    /// </summary>
    public static Task<IOctoSession> GetSyncWriterSessionAsync(this ITenantRepository repository,
        RtSecurityContext securityContext, string sourceTenantId)
    {
        return repository is ISyncWriterSessionFactory factory
            ? factory.GetSyncWriterSessionAsync(securityContext, sourceTenantId)
            : throw new NotSupportedException(
                $"The repository of tenant '{repository.TenantId}' cannot open a sync-writer session.");
    }
}
