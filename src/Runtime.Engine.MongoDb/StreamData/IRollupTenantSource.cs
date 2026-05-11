using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Returns the set of tenant ids the rollup orchestrator background service should tick on each
/// pass. Decouples the orchestrator host from any particular tenant-discovery mechanism
/// (config-driven, Mongo-driven via <c>ISystemContext</c>, RabbitMQ-event-cached) so deployments
/// can pick the one that fits their multi-tenancy strategy. Rollup-archives concept §5, §12.
/// </summary>
public interface IRollupTenantSource
{
    /// <summary>
    /// Returns the tenant ids that should be considered by the next orchestrator tick. Order is
    /// implementation-defined; callers must not rely on it. Returning an empty list is valid and
    /// makes the tick a no-op.
    /// </summary>
    Task<IReadOnlyList<string>> GetTenantIdsAsync(CancellationToken cancellationToken);
}
