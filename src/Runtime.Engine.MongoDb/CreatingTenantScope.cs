using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     While <c>CreateChildTenantAsync</c> applies a blueprint to the tenant it has just created, the tenant's registry
///     entry is still part of the caller's uncommitted transaction (AB#8796), so a lookup by id in another session
///     (<c>ISystemContext.TryFindTenantContextAsync</c>, used by the blueprint's CK model install) does not find it
///     (AB#10060). The scope makes exactly that tenant id resolve to its new database, in exactly the async flow of that
///     creation, and only until the scope ends. Every other tenant id, every other async flow (another creation, a
///     request) and every flow that outlives the scope resolve through the registry as before.
/// </summary>
/// <remarks>
///     The same pattern as <see cref="Services.RestoreSystemModelCheck.RestoringTenantScope" />: an
///     <see cref="AsyncLocal{T}" /> value flows into tasks started inside the scope, also into one that keeps running
///     after the scope ended. The value is therefore a holder that <see cref="Dispose" /> deactivates: such a leaked flow
///     sees an inactive holder and resolves through the registry. The transaction boundary of the creation does not
///     change: a failed blueprint still rolls the whole creation back.
/// </remarks>
internal sealed class CreatingTenantScope : IDisposable
{
    private static readonly AsyncLocal<CreatingTenantScope?> Current = new();

    private readonly CreatingTenantScope? _previous;
    private volatile bool _active = true;

    private CreatingTenantScope(string tenantId, string databaseName, CreatingTenantScope? previous)
    {
        TenantId = tenantId;
        DatabaseName = databaseName;
        _previous = previous;
    }

    /// <summary>The normalized id of the tenant being created.</summary>
    public string TenantId { get; }

    /// <summary>The new database of that tenant.</summary>
    public string DatabaseName { get; }

    /// <summary>Opens the scope for <paramref name="tenantId" /> in the current async flow.</summary>
    public static CreatingTenantScope Begin(string tenantId, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var scope = new CreatingTenantScope(tenantId.NormalizeString(), databaseName.Trim().ToLowerInvariant(),
            Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    ///     The new database of <paramref name="tenantId" /> when the current async flow creates exactly that tenant and
    ///     its scope is still open; otherwise false.
    /// </summary>
    public static bool TryGetDatabaseName(string tenantId, out string databaseName)
    {
        var scope = Current.Value;
        if (scope is { _active: true } &&
            string.Equals(scope.TenantId, tenantId.NormalizeString(), StringComparison.Ordinal))
        {
            databaseName = scope.DatabaseName;
            return true;
        }

        databaseName = string.Empty;
        return false;
    }

    /// <summary>Ends the scope; flows that captured it see it inactive from now on.</summary>
    public void Dispose()
    {
        _active = false;
        if (ReferenceEquals(Current.Value, this))
        {
            Current.Value = _previous;
        }
    }
}
