using Npgsql;
using Polly.CircuitBreaker;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     Connect timeouts of the CrateDB tests that build their own connection pools (AB#9250), and the translation of an
///     unreachable container into a failure that names it.
/// </summary>
/// <remarks>
///     Npgsql's <c>Timeout</c> is one setting for two waits: the connect of a new physical connection and the wait for a
///     free pool slot. Tests that measure the pool wait need it short; tests that only need a working pool need it long
///     enough for a loaded container. Both are test settings: the circuit breaker of <c>CrateResiliencePipeline</c> is
///     product code and unchanged.
/// </remarks>
internal static class CrateTestConnect
{
    /// <summary>Longest wait of <see cref="CrateStagingCopyFixture.EnsureReachableAsync" /> before a test fails.</summary>
    public static readonly TimeSpan ReadinessBudget = TimeSpan.FromSeconds(30);

    /// <summary>Connect timeout of one readiness attempt.</summary>
    public const int ReadinessAttemptConnectTimeoutSeconds = 10;

    /// <summary>
    ///     Connect timeout for tests whose assertions do not depend on the pool wait
    ///     (<c>DownsamplingPhaseProbePoolIntegrationTests</c>; its probes bound themselves by their own budgets of 0.5-30 s,
    ///     independent of this value). It only has to let the first connects through on a loaded machine: 3 s failed
    ///     4 of 4 runs on a Windows workstation while another lane built Docker images (2026-11-17, Npgsql connect timeout,
    ///     surfacing as <c>BrokenCircuitException</c>).
    /// </summary>
    public const int LoadedConnectTimeoutSeconds = 15;

    /// <summary>
    ///     Connect timeout for tests that measure the wait for a pool slot (<c>CrateConnectionPoolReturnTests</c>). It stays
    ///     at 3 s on purpose: <c>AnExhaustedPool_FailsFast_WithTheDriversRealError</c> asserts that an exhausted pool fails
    ///     after one wait of this length (less than 10 s), and a leaked slot must fail the other tests within seconds. A
    ///     longer value would hide exactly what they measure. These tests wait for the container with
    ///     <see cref="CrateStagingCopyFixture.EnsureReachableAsync" /> before the measured part instead.
    /// </summary>
    public const int PoolWaitSeconds = 3;

    /// <summary>
    ///     Runs <paramref name="action" /> and turns a failure to reach the container (an Npgsql connect timeout, or the
    ///     <see cref="BrokenCircuitException" /> the resilience pipeline throws after such failures) into a failure that
    ///     names the container and the connect timeout. Every other failure passes through unchanged.
    /// </summary>
    public static async Task<T> NamingTheContainerAsync<T>(CrateStagingCopyFixture fixture, int connectTimeoutSeconds,
        Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsConnectFailure(ex))
        {
            throw new InvalidOperationException(
                $"{fixture.Describe(connectTimeoutSeconds)} was not reachable during the test: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    /// <summary>True when the exception or one of its inner exceptions says that no connection could be made.</summary>
    internal static bool IsConnectFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case BrokenCircuitException:
                    return true;
                case NpgsqlException { InnerException: TimeoutException }
                    when !current.Message.Contains("pool", StringComparison.OrdinalIgnoreCase):
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(IsConnectFailure):
                    return true;
            }
        }

        return false;
    }
}
