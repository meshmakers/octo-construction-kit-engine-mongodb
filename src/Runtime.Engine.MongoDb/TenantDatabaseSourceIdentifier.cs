using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
/// Identifies the database source for a CK-model repository operation. <see cref="TenantId"/>
/// is required by audit-trail consumers (e.g. <c>ICkModelImportAuditTrail</c>) to route
/// notifications to the correct tenant event log; <c>null</c> denotes the system tenant.
/// </summary>
public record TenantDatabaseSourceIdentifier(
    IOctoSession? Session,
    ICkMongoDbRepositoryDataSource MongoDbRepositoryDataSource,
    string? TenantId = null);
