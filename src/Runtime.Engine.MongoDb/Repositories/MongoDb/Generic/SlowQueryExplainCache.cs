using System.Collections.Concurrent;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Per-process cache of <see cref="SlowQueryExplain"/> results, keyed by
/// <see cref="SlowQueryExplainKey"/>. Acts as the dedup gate for explain capture
/// (<see cref="ShouldCapture"/> consults the cached entry's age vs the configured cooldown)
/// AND as the read-side join target — <see cref="SlowQueriesBuffer.GetSnapshot"/> looks up
/// each entry's key to stamp the latest known explain at read time.
/// </summary>
/// <remarks>
/// Lifetime / scope:
/// <list type="bullet">
///   <item>Registered as a DI singleton — one cache per service process, mirrors the
///         <see cref="SlowQueriesBuffer"/> shape so the two correlate naturally.</item>
///   <item>Memory-only: contents are wiped on service restart. The Studio surface communicates
///         buffer + cache lifetime together as "since service start".</item>
///   <item>Capacity defaults to 5000 keys (configurable via
///         <c>OctoSystemConfiguration.SlowQueryExplainCacheCapacity</c>). At ~5 KB per stored
///         preview that bounds resident memory at ~25 MB. Capacity of 0 disables capture.</item>
///   <item>Cooldown defaults to 300 s (configurable via
///         <c>SlowQueryExplainCooldownSeconds</c>). Within the cooldown,
///         <see cref="ShouldCapture"/> returns false even when a fresh slow-query fires for
///         the same key — prevents replay storms on a hot endpoint.</item>
/// </list>
/// </remarks>
public sealed class SlowQueryExplainCache
{
    private readonly ConcurrentDictionary<SlowQueryExplainKey, CacheEntry> _entries = new();
    private readonly ConcurrentQueue<SlowQueryExplainKey> _evictionOrder = new();
    private readonly int _capacity;
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _clock;
    private long _count;

    /// <summary>Total distinct keys the cache will retain before FIFO-evicting.</summary>
    public int Capacity => _capacity;

    /// <summary>Minimum interval between captures for the same key.</summary>
    public TimeSpan Cooldown => _cooldown;

    public SlowQueryExplainCache(int capacity, TimeSpan cooldown)
        : this(capacity, cooldown, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Test-friendly constructor — the clock can be replaced to make cooldown enforcement
    /// deterministic in unit tests without sleeping.
    /// </summary>
    internal SlowQueryExplainCache(int capacity, TimeSpan cooldown, Func<DateTimeOffset> clock)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                "Slow-query explain cache capacity cannot be negative.");
        }

        if (cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), cooldown,
                "Slow-query explain cooldown cannot be negative.");
        }

        _capacity = capacity;
        _cooldown = cooldown;
        _clock = clock;
    }

    /// <summary>
    /// Returns the cached explain for <paramref name="key"/>, or <c>null</c> if none has been
    /// captured yet. Lookup is lock-free.
    /// </summary>
    public SlowQueryExplain? TryGet(SlowQueryExplainKey key)
        => _entries.TryGetValue(key, out var entry) ? entry.Explain : null;

    /// <summary>
    /// <c>true</c> when the cache is enabled (capacity &gt; 0) AND either no entry exists for
    /// <paramref name="key"/> or the existing entry is older than the cooldown. Callers schedule
    /// a fresh capture in that case; otherwise they silently skip the round-trip.
    /// </summary>
    public bool ShouldCapture(SlowQueryExplainKey key)
    {
        if (_capacity == 0)
        {
            return false;
        }

        if (!_entries.TryGetValue(key, out var entry))
        {
            return true;
        }

        return _clock() - entry.Explain.CapturedAt >= _cooldown;
    }

    /// <summary>
    /// Stores <paramref name="explain"/> as the latest known result for <paramref name="key"/>.
    /// Inserts under capacity → may FIFO-evict the oldest distinct key. Replaces in place when
    /// the key is already known (no eviction churn for the hot path).
    /// </summary>
    public void Set(SlowQueryExplainKey key, SlowQueryExplain explain)
    {
        if (_capacity == 0)
        {
            return;
        }

        var isNew = !_entries.ContainsKey(key);
        _entries[key] = new CacheEntry(explain);

        if (!isNew)
        {
            return;
        }

        _evictionOrder.Enqueue(key);
        var current = Interlocked.Increment(ref _count);

        // Trim using the eviction-order queue. Like SlowQueriesBuffer.Add, the loop may
        // briefly leave the cache at Capacity+k under contention (another writer increments
        // between our TryDequeue + Remove and Interlocked.Decrement), but converges as soon
        // as contention drops.
        while (current > _capacity)
        {
            if (_evictionOrder.TryDequeue(out var oldest))
            {
                _entries.TryRemove(oldest, out _);
                current = Interlocked.Decrement(ref _count);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Approximate distinct-key count from the Interlocked tracker. May be briefly stale under
    /// heavy concurrent <see cref="Set"/> contention but converges as soon as contention drops.
    /// </summary>
    public int Count => (int)Interlocked.Read(ref _count);

    private sealed record CacheEntry(SlowQueryExplain Explain);
}
