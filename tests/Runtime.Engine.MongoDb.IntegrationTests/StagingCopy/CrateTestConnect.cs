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
    ///     Opens <paramref name="connections" /> physical connections of the pool under test and gives them back, so the
    ///     measured part finds them idle in the pool and opens no new connection (AB#9250).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Why: on a Windows workstation a new connection through the Docker Desktop port proxy sometimes takes several
    ///         seconds while CrateDB itself is idle (2026-11-18, AC4 run 2: <c>TenParallelQueries_..._DoNotExhaustAPoolOf32</c>
    ///         failed with an Npgsql connect timeout inside <c>NpgsqlConnector.ConnectAsync</c>, while direct TCP connects to the
    ///         same port took 0-46 ms). With the 3 s <see cref="PoolWaitSeconds" /> such a connect fails the test although the
    ///         pool behaves correctly.
    ///     </para>
    ///     <para>
    ///         The proof stays as strict: <paramref name="open" /> must open through the same data source (same access, same
    ///         tenant) as the measured part, so the connections land in the pool the test measures. The tests prove that a pool
    ///         is not drained and that failed or cancelled statements give their connection back; that is measured as the wait
    ///         for a free slot, which keeps its 3 s. A slot that is not given back still leaves the pool short and fails the test
    ///         as before; only the connect of a new physical connection is taken out of the measured part.
    ///     </para>
    ///     <para>
    ///         <paramref name="connections" /> must be at least the number of connections the measured part holds at once and
    ///         at most the pool size, so the measured part never has to connect: the ten-parallel probe test warms 30 (its
    ///         refresh runs 3 x 10 queries at once) in a pool of 32, the other tests warm their whole pool of 1 or 2. Npgsql
    ///         closes an idle pooled connection only after <c>ConnectionIdleLifetime</c> (300 s by default), far longer than
    ///         a test. A test that breaks a connection on purpose (a discarded slot) still connects anew afterwards.
    ///     </para>
    ///     <para>
    ///         The connects run one after the other; one that times out is retried until <see cref="ReadinessBudget" />,
    ///         then the test fails with a message that names the container and the slow connection setup.
    ///     </para>
    /// </remarks>
    public static async Task WarmUpAsync(CrateStagingCopyFixture fixture,
        Func<CancellationToken, Task<NpgsqlConnection>> open, int connections, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var opened = new List<NpgsqlConnection>();
        Exception? last = null;
        var failures = 0;
        try
        {
            // One connect after the other: many connects at once through the Docker Desktop port proxy each took longer
            // than 3 s (2026-11-18, AC4 run 4: 1 of 30 within 30 s, without any other load), single ones did not.
            while (opened.Count < connections && watch.Elapsed < ReadinessBudget)
            {
                try
                {
                    opened.Add(await open(cancellationToken));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && MayBeUnreachable(ex))
                {
                    last = ex;
                    failures++;
                }
            }
        }
        finally
        {
            // back to the pool: the measured part leases exactly these connections
            foreach (var connection in opened)
            {
                await connection.DisposeAsync();
            }
        }

        if (opened.Count < connections)
        {
            throw new InvalidOperationException(
                $"{fixture.Describe()}: could open only {opened.Count} of {connections} connections within " +
                $"{ReadinessBudget.TotalSeconds:0} s ({failures} connects took longer than {PoolWaitSeconds} s; " +
                "slow connection setup through the Docker Desktop port proxy?). " +
                $"Last error: {last?.GetType().Name}: {last?.Message}", last);
        }
    }

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
