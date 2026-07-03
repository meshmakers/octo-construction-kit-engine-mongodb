namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Carries the state required to drop a tenant's physical database <b>after</b> its metadata
///     records have been deleted and committed.
/// </summary>
/// <remarks>
///     The two-phase delete (delete metadata → commit → drop database) closes a race in which a
///     concurrent tenant-resolve re-creates the tenant database via CK-model auto-import while the
///     tenant record is still visible to other sessions. See
///     <see cref="ITenantContext.DeleteChildTenantMetadataAsync" /> and
///     <see cref="ITenantContext.DropTenantDatabaseAsync" />.
/// </remarks>
/// <param name="DatabaseName">The physical database name of the tenant being deleted.</param>
/// <param name="CorrelationId">
///     The correlation id shared between the pre-delete notification (raised during metadata
///     deletion) and the post-delete notification (raised after the physical database drop).
/// </param>
public sealed record TenantDeletionHandle(string DatabaseName, Guid CorrelationId);
