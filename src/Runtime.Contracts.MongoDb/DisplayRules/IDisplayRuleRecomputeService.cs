namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;

/// <summary>
///     Operator-triggered repair of the engine-computed display fields (rtDisplayName /
///     rtDisplayDescription, AB#5945). Enqueues the same durable, idempotent backfill sweep tasks a CK
///     model import enqueues for changed rules (AB#4812), so the display-rule sweep background service
///     recomputes the stored values. Safe to call repeatedly: re-enqueueing resets a pending task and
///     the sweep writes only entities whose stored value differs from the rule's result.
/// </summary>
public interface IDisplayRuleRecomputeService
{
    /// <summary>
    ///     Enqueues recompute sweeps for one tenant.
    /// </summary>
    /// <param name="tenantId">The tenant whose entities are recomputed</param>
    /// <param name="ckTypeId">
    ///     Optional CK type id (versioned or not, e.g. <c>Meshmakers.Accounting/FiscalYear</c>); the sweep
    ///     covers the type's subtree. When omitted, every available type that declares a display rule is
    ///     enqueued (inheriting types are covered by the declaring type's polymorphic sweep).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The enqueued sweep keys (fully versioned CK type ids)</returns>
    /// <exception cref="ArgumentException">The given CK type does not exist in the tenant or has no display rule</exception>
    Task<IReadOnlyList<string>> EnqueueRecomputeAsync(string tenantId, string? ckTypeId = null,
        CancellationToken cancellationToken = default);
}
