using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Default implementation of <see cref="IIndexUsageService"/>. Resolves the tenant's
/// <c>IMongoDatabase</c> through <see cref="ISystemContext"/> + <see cref="IAdminRepositoryAccess"/>
/// and delegates the projection to <see cref="IndexUsageCollector.CollectAsync"/>. Kept
/// <c>internal</c> so the engine retains the freedom to swap the resolution path (e.g. add
/// caching, switch to a tenant-pool client) without breaking consumers — they only see
/// <see cref="IIndexUsageService"/>.
/// </summary>
internal sealed class IndexUsageService : IIndexUsageService
{
    private readonly ISystemContext _systemContext;
    private readonly IAdminRepositoryAccess _adminRepositoryAccess;

    public IndexUsageService(ISystemContext systemContext, IAdminRepositoryAccess adminRepositoryAccess)
    {
        _systemContext = systemContext;
        _adminRepositoryAccess = adminRepositoryAccess;
    }

    public async Task<IReadOnlyList<IndexUsageEntry>> CollectAsync(
        string tenantId,
        int minAgeDays,
        long lowUsageOpsThreshold,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // FindTenantContextAsync throws if the tenant doesn't exist — same contract every
        // other tenant-scoped engine entry point uses, surfaced by the controller as a
        // 404/500 (controller's HandleException path). Done deliberately rather than the
        // Try-variant: a 404 with a clear error beats an empty 200 here.
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);

        var client = _adminRepositoryAccess.GetRepositoryClient(tenantContext.DatabaseName);
        var repository = (IRepositoryInternal)client.GetRepository(tenantContext.DatabaseName);

        return await IndexUsageCollector
            .CollectAsync(repository.Database, minAgeDays, lowUsageOpsThreshold, now, cancellationToken)
            .ConfigureAwait(false);
    }
}
