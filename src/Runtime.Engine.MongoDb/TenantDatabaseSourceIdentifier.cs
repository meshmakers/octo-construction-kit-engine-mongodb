using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
/// Identifies the database source for a CK-model repository operation. <see cref="TenantId"/>
/// is required by audit-trail consumers (e.g. <c>ICkModelImportAuditTrail</c>) to route
/// notifications to the correct tenant event log; <c>null</c> denotes the system tenant.
/// <para>
/// <see cref="IncludeResolveFailedModels"/> (CK v2 F1.0-S2, AB#5901) lets the dependency resolver
/// see <c>ResolveFailed</c> models next to <c>Available</c> ones. Only the post-import re-validation
/// (<c>DatabaseCkModelRepository.ValidateDependencies</c>) sets it — the CK cache, imports and every
/// other lookup keep resolving <c>Available</c> models only.
/// </para>
/// </summary>
public record TenantDatabaseSourceIdentifier(
    IOctoSession? Session,
    ICkMongoDbRepositoryDataSource MongoDbRepositoryDataSource,
    string? TenantId = null,
    bool IncludeResolveFailedModels = false);
