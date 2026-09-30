using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

[assembly: AssemblyFixture(typeof(RunHeartbeatFixture))]

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Prints one <see cref="RunProgress" /> line every <see cref="Interval" /> for as long as the
///     assembly runs, so the build log can never go quiet for longer than that (AB#5436).
///     <para>
///         This is the piece that makes a genuine hang distinguishable from the suite's normal silence:
///         passing tests print nothing, so the log used to stand still for 83-94 % of the run — 19 m 37 s
///         in build 48277 — while the tests were simply working. A heartbeat that keeps ticking says
///         "working"; a heartbeat that stops says "hung", and the last <c>octo-progress</c> line before
///         it stopped says where.
///     </para>
///     <para>
///         An assembly fixture is created before any collection and disposed after the last one, which
///         is exactly the window that needs covering. It also captures the diagnostic sink for
///         <see cref="RunProgress" />, because the timer thread itself only ever sees xUnit's idle test
///         context.
///     </para>
/// </summary>
public sealed class RunHeartbeatFixture : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Task _beat;
    private readonly CancellationTokenSource _stop = new();

    public RunHeartbeatFixture()
    {
        RunProgress.CaptureDiagnosticSink();
        RunProgress.Report("test run started");
        _beat = BeatAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _beat;
        }
        catch (OperationCanceledException)
        {
            // Expected: the heartbeat is cancelled, not awaited for a result.
        }

        _stop.Dispose();
        RunProgress.Report("test run finished");
    }

    private static async Task BeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RunProgress.ReportHeartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended; nothing to report.
        }
    }
}
