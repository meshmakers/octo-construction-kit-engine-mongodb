using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Npgsql;
using Polly.CircuitBreaker;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     Connection settings of the CrateDB tests that build their own connection pools (AB#9250), and the translation of an
///     unreachable container into a failure that names it.
/// </summary>
/// <remarks>
///     Npgsql's <c>Timeout</c> is one setting for two waits: the connect of a new physical connection and the wait for a
///     free pool slot. The pool tests (<c>CrateConnectionPoolReturnTests</c>, <c>DownsamplingPhaseProbePoolIntegrationTests</c>)
///     prove that a pool is not drained, so they need the short wait (<see cref="PoolWaitSeconds" />); a longer one would let
///     a drained pool pass. They wait for the container before the measured part instead
///     (<see cref="CrateStagingCopyFixture.EnsureReachableAsync" />). Both are test settings: the circuit breaker of
///     <c>CrateResiliencePipeline</c> is product code and unchanged.
/// </remarks>
internal static class CrateTestConnect
{
    /// <summary>Longest wait of <see cref="CrateStagingCopyFixture.EnsureReachableAsync" /> before a test fails.</summary>
    public static readonly TimeSpan ReadinessBudget = TimeSpan.FromSeconds(30);

    /// <summary>Longest single attempt (connect and <c>SELECT 1</c>) of the readiness wait.</summary>
    public static readonly TimeSpan ReadinessAttemptBudget = TimeSpan.FromSeconds(10);

    /// <summary>Budget of the probe that decides whether a failure of the measured part was an unreachable container.</summary>
    public static readonly TimeSpan FailureProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Connect timeout and pool-slot wait of the pool tests, 3 s on purpose: an exhausted pool must fail within one such
    ///     wait (<c>AnExhaustedPool_FailsFast_WithTheDriversRealError</c> asserts less than 10 s), and a drained or leaked
    ///     slot must fail the tests within seconds (<c>TenParallelQueries_..._DoNotExhaustAPoolOf32</c>: a query waiting on a
    ///     drained pool fails after 3 s). A longer value would hide exactly what they prove.
    /// </summary>
    public const int PoolWaitSeconds = 3;

    /// <summary>
    ///     Runs <paramref name="action" />. When it fails with an error that can mean "no connection" (an Npgsql timeout or
    ///     socket error, or the <see cref="BrokenCircuitException" /> the resilience pipeline throws after such failures), the
    ///     container is probed once: if it does not answer, the failure names the container; if it answers, the original error
    ///     is rethrown unchanged, so a slow query or a drained pool is never reported as an unreachable container.
    /// </summary>
    public static async Task<T> NamingAnUnreachableContainerAsync<T>(CrateStagingCopyFixture fixture,
        Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (MayBeUnreachable(ex))
        {
            var probe = await fixture.TryConnectOnceAsync(FailureProbeBudget, cancellationToken);
            if (probe is null)
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            throw new InvalidOperationException(
                $"{fixture.Describe()} was not reachable during the test (connect timeout {PoolWaitSeconds} s; " +
                $"a probe after the failure got no answer within {FailureProbeBudget.TotalSeconds:0} s either: " +
                $"{probe.GetType().Name}: {probe.Message}). Original error: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>True when the exception or one of its inner exceptions can mean that no connection could be made.</summary>
    internal static bool MayBeUnreachable(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case BrokenCircuitException:
                case SocketException:
                case NpgsqlException { InnerException: TimeoutException or SocketException }:
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(MayBeUnreachable):
                    return true;
            }
        }

        return false;
    }
}
