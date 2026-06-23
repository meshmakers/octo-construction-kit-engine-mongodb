namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Stage 3 / AB#4224 entry point for the unused-index analysis. Resolves the tenant's MongoDB
/// database from a tenant id, runs <c>$indexStats</c> across every non-system collection via
/// <see cref="IndexUsageCollector"/>, and returns the pre-classified entries ready to render.
/// </summary>
/// <remarks>
/// Live-query design: each call hits MongoDB. <c>$indexStats</c> is metadata-only and returns
/// in tens of milliseconds for a typical tenant, so no background polling or persistence layer
/// is needed in this iteration. <see cref="IndexUsageEntry.SinceUtc"/> resets on
/// <c>mongod</c> restart — the <paramref name="minAgeDays"/> filter on the caller side
/// shields the operator from false-positive Unused flags right after a Mongo restart.
/// </remarks>
public interface IIndexUsageService
{
    /// <summary>
    /// Collects index-usage entries for every non-system collection in the tenant's database.
    /// </summary>
    /// <param name="tenantId">Tenant id (route-validated by the caller).</param>
    /// <param name="minAgeDays">Indexes younger than this are pre-classified as <see cref="IndexUsageStatus.Used"/>
    /// regardless of ops — no signal exists yet. Caller-tunable via query param.</param>
    /// <param name="lowUsageOpsThreshold">Strict less-than cutoff between
    /// <see cref="IndexUsageStatus.LowUsage"/> and <see cref="IndexUsageStatus.Used"/>.
    /// Caller-tunable via query param.</param>
    /// <param name="now">Reference moment for age computation. Injected so the asset-repo
    /// controller can keep timestamps consistent across the request and tests can pin them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<IndexUsageEntry>> CollectAsync(
        string tenantId,
        int minAgeDays,
        long lowUsageOpsThreshold,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
