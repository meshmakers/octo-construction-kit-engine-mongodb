using System.Diagnostics;
using System.Globalization;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     The suite's progress channel, and the bounded runner every teardown goes through (AB#5436).
///     <para>
///         <b>Why the progress channel exists.</b> At the default console verbosity <c>dotnet test</c>
///         prints nothing at all for a passing test — only <c>[FAIL]</c> and <c>[SKIP]</c> lines reach
///         the log. This suite reports its last failing test very early (the stream-data collections
///         abort on the first assertion and finish within the first couple of minutes) and then keeps
///         running for the rest of its wall clock, so the CI log falls silent for the remainder:
///         19 m 37 s of silence in build 48277, and the very same silence in the GREEN builds 48116
///         (520 s) and 48223 (409 s) — 83-94 % of the run in every single case. From outside, that is
///         indistinguishable from a hung build, and it has been read that way. The silence was never a
///         hang: the .trx of 48116 accounts for 1817 s of test time across its 482 results (slowest
///         single test 81 s), which is exactly where the wall clock went.
///     </para>
///     <para>
///         <b>Why diagnostic messages and not the console.</b> <c>Console.WriteLine</c> from a fixture
///         is captured as test output and ends up in the .trx, not in the build log — which is why
///         "Using shared Testcontainer MongoDB at ..." was never visible in CI. xUnit diagnostic
///         messages are printed by the runner as <c>[xUnit.net HH:MM:SS.ff]</c> lines, the same channel
///         the <c>[FAIL]</c> lines use, and they need <c>diagnosticMessages: true</c> in
///         <c>xunit.runner.json</c>.
///     </para>
/// </summary>
internal static class RunProgress
{
    /// <summary>How long any single teardown step may take before the run walks away from it.</summary>
    internal static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Wall clock of the whole test process; every message carries it.</summary>
    private static readonly Stopwatch RunClock = Stopwatch.StartNew();

    /// <summary>
    ///     The diagnostic sink hangs off the <c>AsyncLocal</c> test context, so a timer thread — the
    ///     heartbeat — sees only xUnit's idle context, whose sink is <c>null</c>, and its messages
    ///     would vanish. Captured once from the assembly fixture, which does run on a context that
    ///     carries the sink, and reused from everywhere afterwards. It is the same sink instance the
    ///     per-test contexts inherit, so using it always instead of <see cref="TestContext.Current" />
    ///     cannot duplicate a line.
    /// </summary>
    private static ITestContext? _sinkCarrier;

    private static int _fixturesReady;
    private static int _fixturesTornDown;

    /// <summary>Called once, from the assembly fixture, before any collection runs.</summary>
    internal static void CaptureDiagnosticSink()
    {
        _sinkCarrier ??= TestContext.Current;
    }

    /// <summary>Writes one <c>[xUnit.net ...] octo-progress ...</c> line into the build log.</summary>
    internal static void Report(string message)
    {
        var carrier = _sinkCarrier ?? TestContext.Current;
        carrier.SendDiagnosticMessage(
            string.Format(CultureInfo.InvariantCulture, "octo-progress {0:hh\\:mm\\:ss} {1}", RunClock.Elapsed,
                message));
    }

    /// <summary>
    ///     The heartbeat line. Its whole job is to make the difference between "still working" and
    ///     "hung" readable from the build log without waiting for the run to end.
    /// </summary>
    internal static void ReportHeartbeat()
    {
        Report(string.Format(CultureInfo.InvariantCulture,
            "still running ({0} fixtures ready, {1} torn down) — passing tests print nothing, so silence here is normal",
            Volatile.Read(ref _fixturesReady), Volatile.Read(ref _fixturesTornDown)));
    }

    internal static void FixtureReady()
    {
        Interlocked.Increment(ref _fixturesReady);
    }

    internal static void FixtureTornDown()
    {
        Interlocked.Increment(ref _fixturesTornDown);
    }

    /// <summary>
    ///     Runs a teardown step under a time budget and reports what happened. Never throws and never
    ///     waits longer than <paramref name="timeout" />: cleaning up a throwaway tenant, a database or
    ///     a service provider must not be able to hold the run open, and the run's red/green verdict is
    ///     already decided by then. A step that fails or overruns is reported loudly instead of
    ///     escalated — swallowing it is the point, so the reported line is the only trace and is
    ///     deliberately hard to miss.
    /// </summary>
    internal static async Task RunBoundedAsync(string what, Func<Task> action, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TeardownTimeout;
        var watch = Stopwatch.StartNew();

        // Reported before the attempt, not only after it: the whole point is that the log says what
        // the run is waiting for while it is waiting, instead of leaving a reader to guess.
        Report($"{what}: starting");

        // Task.Run so that a synchronous block inside the action still hits the timeout instead of
        // parking this thread before Task.WhenAny is ever reached.
        var work = Task.Run(action);

        // The timer is cancelled as soon as the work wins, so a run does not accumulate one pending
        // 60-second delay per fixture.
        using var expiry = new CancellationTokenSource();
        var completed = await Task.WhenAny(work, Task.Delay(budget, expiry.Token)).ConfigureAwait(false) == work;
        if (completed)
        {
            await expiry.CancelAsync().ConfigureAwait(false);
        }

        if (!completed)
        {
            Report(string.Format(CultureInfo.InvariantCulture,
                "{0}: STILL RUNNING after {1:F0}s — abandoned, the run continues", what, budget.TotalSeconds));

            // Nobody awaits it again, so observe the outcome here: an unobserved faulted task would
            // otherwise surface much later, attributed to whatever test happens to be running.
            _ = work.ContinueWith(
                t => Report(string.Format(CultureInfo.InvariantCulture,
                    "{0}: the abandoned attempt ended as {1} after {2:F0}s", what,
                    t.IsFaulted ? Describe(t.Exception) : t.Status.ToString(), watch.Elapsed.TotalSeconds)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        if (work.IsFaulted)
        {
            Report(string.Format(CultureInfo.InvariantCulture, "{0}: FAILED after {1:F1}s — {2}", what,
                watch.Elapsed.TotalSeconds, Describe(work.Exception)));
            return;
        }

        Report(string.Format(CultureInfo.InvariantCulture, "{0}: done in {1:F1}s", what,
            watch.Elapsed.TotalSeconds));
    }

    /// <summary>
    ///     Invariant seconds. The build log is English, so a German agent locale must not turn "4.8s"
    ///     into "4,8s".
    /// </summary>
    internal static string Seconds(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Describe(AggregateException? exception)
    {
        var inner = exception?.InnerException ?? exception;
        return inner is null ? "unknown failure" : $"{inner.GetType().Name}: {inner.Message}";
    }
}
