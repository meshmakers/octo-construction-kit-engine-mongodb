using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>
///     While a tenant restore raises a service-managed model of its still unattached database, the CK migrations of that
///     import resolve the tenant by its id (<c>ISystemContext.TryFindTenantContextAsync</c>). The scope makes exactly that
///     tenant id resolve to the restored database, in exactly the async flow of that restore, and only until the scope
///     ends. Every other tenant id, every other async flow (another restore, a request) and every flow that outlives the
///     scope resolve through the registry as before.
/// </summary>
/// <remarks>
///     An <see cref="AsyncLocal{T}" /> value flows into tasks started inside the scope, also into one that keeps running
///     after the scope ended. The value is therefore a holder that <see cref="Dispose" /> deactivates: such a leaked flow
///     sees an inactive holder and resolves through the registry. The scope is opened only after the system model check
///     admitted every model of the dump.
/// </remarks>
internal sealed class RestoringTenantScope : IDisposable
{
    private static readonly AsyncLocal<RestoringTenantScope?> Current = new();

    private readonly RestoringTenantScope? _previous;
    private volatile bool _active = true;

    private RestoringTenantScope(string tenantId, string databaseName, RestoringTenantScope? previous)
    {
        TenantId = tenantId;
        DatabaseName = databaseName;
        _previous = previous;
    }

    /// <summary>The normalized id of the tenant being restored.</summary>
    public string TenantId { get; }

    /// <summary>The restored, still unattached database of that tenant.</summary>
    public string DatabaseName { get; }

    /// <summary>Opens the scope for <paramref name="tenantId" /> in the current async flow.</summary>
    public static RestoringTenantScope Begin(string tenantId, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var scope = new RestoringTenantScope(tenantId.NormalizeString(), databaseName.Trim().ToLowerInvariant(),
            Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>
    ///     The restored database of <paramref name="tenantId" /> when the current async flow restores exactly that tenant
    ///     and its scope is still open; otherwise false.
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
