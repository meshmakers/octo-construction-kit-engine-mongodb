namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Registry of the non-CK collections the engine's own plumbing creates in the SYSTEM database —
///     the only collections that may exist there before the system tenant has been bootstrapped
///     (AB#4854). A system database containing nothing but these is an "infrastructure-only shell"
///     (typically materialized by an index creation or a setup-retry record on a virgin server) and
///     must be treated as bootstrappable, not as an existing tenant database worth protecting.
///     The stores reference these constants for their collection names, so a rename that would let
///     the classification drift out of sync becomes a compile error.
/// </summary>
internal static class InfrastructureCollections
{
    public const string TenantLifecycle = "tenant_lifecycle";
    public const string TenantSetupRetry = "tenant_setup_retry";
    public const string DisplayRuleSweep = "display_rule_sweep";
    public const string SysLock = "SysLock";

    private static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        TenantLifecycle,
        TenantSetupRetry,
        DisplayRuleSweep,
        SysLock,
    };

    /// <summary>
    ///     Whether the given collection is engine infrastructure. MongoDB's own bookkeeping
    ///     collections (<c>system.*</c>, e.g. <c>system.views</c>) count as infrastructure too —
    ///     they never represent tenant data. Comparison is Ordinal; Mongo collection names are
    ///     case-sensitive.
    /// </summary>
    public static bool IsInfrastructure(string collectionName)
    {
        return Names.Contains(collectionName)
               || collectionName.StartsWith("system.", StringComparison.Ordinal);
    }
}
