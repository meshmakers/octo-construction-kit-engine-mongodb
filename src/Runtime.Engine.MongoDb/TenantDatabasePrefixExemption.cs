namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     Lets an existing tenant keep its database name under a tenant-database prefix (AB#8797).
/// </summary>
/// <remarks>
///     The prefix rule refuses NEW database names that do not carry the prefix. Some engine operations drop a
///     registered tenant and register it again under the name it already had - clear (drop + create) and a restore
///     over an existing tenant (drop + attach). A tenant registered before the prefix was configured has a name
///     without it and must keep working (no rename, no migration), so these operations run the re-registration
///     inside <see cref="Begin" /> for exactly that name. The scope flows with the async call
///     (<see cref="AsyncLocal{T}" />) and covers no other name and no other call.
/// </remarks>
internal static class TenantDatabasePrefixExemption
{
    private static readonly AsyncLocal<string?> ExemptDatabaseName = new();

    /// <summary>
    ///     Exempts the given database name from the prefix rule until the returned scope is disposed.
    /// </summary>
    /// <param name="databaseName">The name the tenant was registered with before the operation.</param>
    public static IDisposable Begin(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var previous = ExemptDatabaseName.Value;
        ExemptDatabaseName.Value = Normalize(databaseName);
        return new Scope(previous);
    }

    /// <summary>
    ///     Whether the (normalized) database name is exempt in the current async flow.
    /// </summary>
    public static bool IsExempt(string normalizedDatabaseName) =>
        ExemptDatabaseName.Value is { } exempt && string.Equals(exempt, normalizedDatabaseName, StringComparison.Ordinal);

    private static string Normalize(string databaseName) => databaseName.Trim().ToLowerInvariant();

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ExemptDatabaseName.Value = previous;
        }
    }
}
