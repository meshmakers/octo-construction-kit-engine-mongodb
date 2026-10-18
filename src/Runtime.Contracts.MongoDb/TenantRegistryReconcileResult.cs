namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     A tenant registry row that the reconcile inserted (or, in a dry run, would insert) into the system
///     registry (AB#6941).
/// </summary>
/// <param name="TenantId">Normalized tenant id.</param>
/// <param name="DatabaseName">Normalized database name.</param>
/// <param name="ParentTenantId">The tenant whose own registry names this tenant as a child.</param>
public sealed record TenantRegistryReconcileEntry(string TenantId, string DatabaseName, string ParentTenantId);

/// <summary>
///     A tenant the reconcile found but did not touch, with the reason (AB#6941).
/// </summary>
/// <param name="TenantId">Tenant id as named by the parent-local registry.</param>
/// <param name="ParentTenantId">The tenant whose own registry names this tenant as a child.</param>
/// <param name="Reason">Why the row was left alone.</param>
public sealed record TenantRegistryReconcileConflict(string TenantId, string ParentTenantId, string Reason);

/// <summary>
///     Outcome of <see cref="ISystemContext.ReconcileTenantRegistryAsync" /> (AB#6941).
/// </summary>
public sealed class TenantRegistryReconcileResult
{
    /// <summary>True when the missing rows were written; false for a dry run.</summary>
    public bool Applied { get; init; }

    /// <summary>Rows inserted (apply) or that would be inserted (dry run).</summary>
    public IReadOnlyList<TenantRegistryReconcileEntry> Inserted { get; init; } = [];

    /// <summary>Tenants of the tree whose system registry row already matches.</summary>
    public int AlreadyPresent { get; init; }

    /// <summary>Tenants left untouched because the registries disagree or a registry is unreadable.</summary>
    public IReadOnlyList<TenantRegistryReconcileConflict> Conflicts { get; init; } = [];

    /// <summary>Tenants visited by the walk (the system tenant is not counted).</summary>
    public int Visited { get; init; }

    /// <summary>One-line human-readable summary.</summary>
    public string Summary =>
        $"{(Applied ? "Applied" : "Dry run")}: {Visited} tenant(s) walked, " +
        $"{Inserted.Count} {(Applied ? "inserted" : "to insert")}, {AlreadyPresent} already present, " +
        $"{Conflicts.Count} conflict(s)";
}
