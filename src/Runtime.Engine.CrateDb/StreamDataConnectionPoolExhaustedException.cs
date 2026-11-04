namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// No CrateDB connection of the tenant's pool became free within the pool's wait time, so the statement
/// was never sent (AB#8266).
/// </summary>
/// <remarks>
/// The driver reports this as a plain <c>NpgsqlException</c> with an inner <see cref="TimeoutException"/>, which
/// the resilience pipeline would treat as a transient connection failure: it retried the wait, and the call
/// ended only when the call-wide timeout ran out, as a misleading <see cref="StreamDataStatementTimeoutException"/>
/// after 35 s. A full pool does not get emptier by waiting again, and it is a matter of one tenant, not of the
/// cluster, so this exception is neither retried nor counted by the shared circuit breaker. The pool is per
/// tenant (<c>StreamData:MaxPoolSize</c>); the message names the tenant and the pool size.
/// </remarks>
public sealed class StreamDataConnectionPoolExhaustedException : Exception
{
    internal StreamDataConnectionPoolExhaustedException(string tenantId, Exception innerException)
        : base(
            FormattableString.Invariant(
                $"No CrateDB connection of tenant '{tenantId}' became free in time; the tenant's connection pool is exhausted and the statement was not sent. Driver: {innerException.Message}"),
            innerException)
    {
        TenantId = tenantId;
    }

    /// <summary>The tenant whose connection pool is exhausted.</summary>
    public string TenantId { get; }
}
