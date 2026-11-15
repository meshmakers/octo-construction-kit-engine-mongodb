using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8992: the process-wide memo of the window phase a downsampling probe found, and the single probe per key.
/// </summary>
public class WindowPhaseCacheTests
{
    private static readonly WindowPhaseCache.Key KeyA = new("\"t\".\"archive_a\"", 900_000, "filter-a");
    private static readonly WindowPhaseCache.Key KeyB = new("\"t\".\"archive_a\"", 900_000, "filter-b");
    private static readonly WindowPhaseCache.Key OtherTable = new("\"t\".\"archive_b\"", 900_000, "filter-a");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static Func<Task<WindowPhaseCache.Probe>> Answer(long? min, long? max, Action? onRun = null) => () =>
    {
        onRun?.Invoke();
        return Task.FromResult(new WindowPhaseCache.Probe(min, max));
    };

    [Fact]
    public async Task ASinglePhase_IsRemembered_UntilItsTimeToLiveEnds()
    {
        var cache = new WindowPhaseCache();

        await cache.GetOrStartProbe(KeyA, Answer(300_000, 300_000), Ttl, () => Now);

        Assert.True(cache.TryGet(KeyA, Now.AddMinutes(4), out var phase));
        Assert.Equal(300_000, phase);
        Assert.False(cache.TryGet(KeyB, Now, out _));
        Assert.False(cache.TryGet(KeyA, Now.AddMinutes(5), out _));
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, 300_000L)]
    public async Task NoWindowOrDifferentPhases_AreNotRemembered(long? min, long? max)
    {
        var cache = new WindowPhaseCache();

        await cache.GetOrStartProbe(KeyA, Answer(min, max), Ttl, () => Now);

        Assert.False(cache.TryGet(KeyA, Now, out _));
    }

    [Fact]
    public async Task ATimeToLiveOfZero_RemembersNothing()
    {
        var cache = new WindowPhaseCache();

        await cache.GetOrStartProbe(KeyA, Answer(0, 0), TimeSpan.Zero, () => Now);

        Assert.False(cache.TryGet(KeyA, Now, out _));
    }

    [Fact]
    public async Task ConcurrentQueries_ShareOneProbePerKey()
    {
        var cache = new WindowPhaseCache();
        var release = new TaskCompletionSource<WindowPhaseCache.Probe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var waits = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrStartProbe(KeyA, () =>
            {
                Interlocked.Increment(ref runs);
                return release.Task;
            }, Ttl, () => Now))
            .ToList();
        var other = cache.GetOrStartProbe(KeyB, Answer(0, 0), Ttl, () => Now);
        Assert.DoesNotContain(waits, w => w.IsCompleted);

        release.SetResult(new WindowPhaseCache.Probe(0, 0));
        await Task.WhenAll(waits);
        await other;

        Assert.Equal(1, runs);
        Assert.Equal(0, cache.InFlightCount);
        Assert.True(cache.TryGet(KeyA, Now, out _));
    }

    [Fact]
    public async Task AFailingProbe_IsReturnedAsAnError_AndRemembersNothing()
    {
        var cache = new WindowPhaseCache();

        var probe = await cache.GetOrStartProbe(KeyA, () => throw new InvalidOperationException("down"), Ttl, () => Now);

        Assert.IsType<InvalidOperationException>(probe.Error);
        Assert.False(probe.IsSinglePhase);
        Assert.False(cache.TryGet(KeyA, Now, out _));
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task ATableDroppedWhileTheProbeRuns_DoesNotGetTheAnswerRemembered()
    {
        var cache = new WindowPhaseCache();
        var release = new TaskCompletionSource<WindowPhaseCache.Probe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = cache.GetOrStartProbe(KeyA, () => release.Task, Ttl, () => Now);

        cache.Forget(KeyA.Table);
        release.SetResult(new WindowPhaseCache.Probe(0, 0));
        await running;

        Assert.False(cache.TryGet(KeyA, Now, out _));
    }

    [Fact]
    public async Task Forget_RemovesEveryEntryOfTheTable_AndOnlyThose()
    {
        var cache = new WindowPhaseCache();
        await cache.GetOrStartProbe(KeyA, Answer(0, 0), Ttl, () => Now);
        await cache.GetOrStartProbe(KeyB, Answer(0, 0), Ttl, () => Now);
        await cache.GetOrStartProbe(OtherTable, Answer(0, 0), Ttl, () => Now);

        cache.Forget(KeyA.Table);

        Assert.False(cache.TryGet(KeyA, Now, out _));
        Assert.False(cache.TryGet(KeyB, Now, out _));
        Assert.True(cache.TryGet(OtherTable, Now, out _));
    }

    [Fact]
    public async Task TheProcessStateForget_OfAnArchiveDrop_ForgetsThePhases()
    {
        var state = new RecomputeProcessState();
        await state.WindowPhases.GetOrStartProbe(KeyA, Answer(0, 0), Ttl, () => Now);

        state.Forget(KeyA.Table);

        Assert.False(state.WindowPhases.TryGet(KeyA, Now, out _));
    }

    [Fact]
    public async Task AWrittenWindowAtAnotherPhase_EndsTheMemo_OneAtTheSamePhaseKeepsIt()
    {
        var cache = new WindowPhaseCache();
        await cache.GetOrStartProbe(KeyA, Answer(0, 0), Ttl, () => Now);
        await cache.GetOrStartProbe(OtherTable, Answer(0, 0), Ttl, () => Now);
        var quarterHour = new DateTime(2026, 11, 15, 10, 15, 0, DateTimeKind.Utc);

        cache.ObserveWrittenWindows(KeyA.Table, [quarterHour, quarterHour.AddMinutes(15)]);
        Assert.True(cache.TryGet(KeyA, Now, out _));

        cache.ObserveWrittenWindows(KeyA.Table, [quarterHour.AddMinutes(5)]);
        Assert.False(cache.TryGet(KeyA, Now, out _));
        Assert.True(cache.TryGet(OtherTable, Now, out _));
    }

    [Fact]
    public async Task Evict_RemovesOneKey()
    {
        var cache = new WindowPhaseCache();
        await cache.GetOrStartProbe(KeyA, Answer(0, 0), Ttl, () => Now);
        await cache.GetOrStartProbe(KeyB, Answer(0, 0), Ttl, () => Now);

        cache.Evict(KeyA);

        Assert.False(cache.TryGet(KeyA, Now, out _));
        Assert.True(cache.TryGet(KeyB, Now, out _));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1_200_000L, 300_000L)]
    [InlineData(-600_000L, 300_000L)]
    public void PhaseOf_IsNeverNegative(long epochMs, long expected)
        => Assert.Equal(expected, WindowPhaseCache.PhaseOf(epochMs, 900_000));
}
