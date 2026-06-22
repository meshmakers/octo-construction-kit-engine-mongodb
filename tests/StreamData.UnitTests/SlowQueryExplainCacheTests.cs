using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class SlowQueryExplainCacheTests
{
    private static readonly DateTimeOffset Origin = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static SlowQueryExplainKey Key(string fp = "aaaa") => new(fp, "find", "rt_entities", "tenant_a");

    private static SlowQueryExplain Success(DateTimeOffset capturedAt) => new(
        CapturedAt: capturedAt,
        Status: SlowQueryExplainStatus.Success,
        WinningStage: "COLLSCAN",
        HasCollScan: true,
        IndexNames: Array.Empty<string>(),
        RawExplainPreview: null,
        ErrorMessage: null);

    [Fact]
    public void TryGet_NoEntry_ReturnsNull()
    {
        var cache = new SlowQueryExplainCache(capacity: 10, cooldown: TimeSpan.FromMinutes(5));

        Assert.Null(cache.TryGet(Key()));
    }

    [Fact]
    public void Set_AndTryGet_RoundTrip()
    {
        var cache = new SlowQueryExplainCache(10, TimeSpan.FromMinutes(5));
        var key = Key();
        var explain = Success(Origin);

        cache.Set(key, explain);

        Assert.Same(explain, cache.TryGet(key));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ShouldCapture_NoEntry_True()
    {
        var cache = new SlowQueryExplainCache(10, TimeSpan.FromMinutes(5));

        Assert.True(cache.ShouldCapture(Key()));
    }

    [Fact]
    public void ShouldCapture_WithinCooldown_False()
    {
        var now = Origin;
        var cache = new SlowQueryExplainCache(10, TimeSpan.FromMinutes(5), () => now);
        var key = Key();
        cache.Set(key, Success(now));

        // Advance just under the cooldown — must still suppress.
        now = Origin.AddMinutes(4).AddSeconds(59);

        Assert.False(cache.ShouldCapture(key));
    }

    [Fact]
    public void ShouldCapture_AfterCooldown_True()
    {
        var now = Origin;
        var cache = new SlowQueryExplainCache(10, TimeSpan.FromMinutes(5), () => now);
        var key = Key();
        cache.Set(key, Success(now));

        now = Origin.AddMinutes(5).AddSeconds(1);

        Assert.True(cache.ShouldCapture(key));
    }

    [Fact]
    public void ShouldCapture_ZeroCapacity_AlwaysFalse()
    {
        var cache = new SlowQueryExplainCache(capacity: 0, cooldown: TimeSpan.FromMinutes(5));

        Assert.False(cache.ShouldCapture(Key()));
    }

    [Fact]
    public void Set_AtZeroCapacity_NoEntryStored()
    {
        var cache = new SlowQueryExplainCache(0, TimeSpan.FromMinutes(5));

        cache.Set(Key(), Success(Origin));

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet(Key()));
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsOldestFifo()
    {
        var cache = new SlowQueryExplainCache(capacity: 2, cooldown: TimeSpan.Zero);
        var k1 = Key("aaaa");
        var k2 = Key("bbbb");
        var k3 = Key("cccc");

        cache.Set(k1, Success(Origin));
        cache.Set(k2, Success(Origin));
        cache.Set(k3, Success(Origin));

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryGet(k1));     // oldest evicted
        Assert.NotNull(cache.TryGet(k2));
        Assert.NotNull(cache.TryGet(k3));
    }

    [Fact]
    public void Set_ReplaceExistingKey_NoEvictionChurn()
    {
        var cache = new SlowQueryExplainCache(capacity: 2, cooldown: TimeSpan.Zero);
        var k1 = Key("aaaa");
        var k2 = Key("bbbb");

        cache.Set(k1, Success(Origin));
        cache.Set(k2, Success(Origin));
        // Replace k1 — should NOT evict anyone since it's already in the cache.
        cache.Set(k1, Success(Origin.AddSeconds(10)));

        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.TryGet(k1));
        Assert.NotNull(cache.TryGet(k2));
        Assert.Equal(Origin.AddSeconds(10), cache.TryGet(k1)!.CapturedAt);
    }

    [Fact]
    public async Task Set_ConcurrentWritersForSameKey_CountStaysOne()
    {
        // Pins the race fix from PR #102 review: with ContainsKey + indexer-assign, two
        // writers could both see "absent" and both Interlocked.Increment _count, leaving
        // Count = 2 for a single distinct key. TryAdd narrows new-key detection to a single
        // atomic CAS — only one of the concurrent writers wins the +1.
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var key = Key("same_key");
        const int writers = 32;
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            cache.Set(key, Success(Origin.AddSeconds(i)));
        })).ToArray();

        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, cache.Count);
        Assert.NotNull(cache.TryGet(key));
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlowQueryExplainCache(capacity: -1, cooldown: TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Constructor_NegativeCooldown_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlowQueryExplainCache(capacity: 10, cooldown: TimeSpan.FromMinutes(-1)));
    }
}
