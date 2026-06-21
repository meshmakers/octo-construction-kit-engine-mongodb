using System.Collections.Concurrent;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Thread-safe in-memory ring buffer of the most-recent <see cref="SlowQueryEntry"/> instances
/// observed by <see cref="MongoCommandObservability"/>. Written from the MongoDB driver's
/// command-event callbacks (non-blocking, lock-free enqueue) and read by the Refinery Studio
/// Diagnostics REST endpoint.
/// </summary>
/// <remarks>
/// Lifetime / scope:
/// <list type="bullet">
///   <item>Registered as a DI singleton — one buffer per service process, shared between admin
///         and user MongoDB connections.</item>
///   <item>Memory-only: contents are wiped on service restart. The Studio surface communicates
///         this as "since service start" so users aren't surprised by gaps.</item>
///   <item>Ring capacity defaults to 1000 entries; configurable via
///         <c>OctoSystemConfiguration.SlowQueryBufferSize</c>. At ~3 KB per entry that's roughly
///         3 MB of resident memory — small enough to be irrelevant at typical service heap sizes.</item>
///   <item>Capacity of 0 disables capture (sized-out ring). Capacity comes from the configured
///         value at construction time and is fixed for the lifetime of the buffer; rebuilding
///         the DI graph on config change applies a new size.</item>
/// </list>
/// </remarks>
public sealed class SlowQueriesBuffer
{
    private readonly ConcurrentQueue<SlowQueryEntry> _queue = new();
    private readonly int _capacity;

    /// <summary>
    /// Interlocked-tracked entry count. We maintain this ourselves because
    /// <c>ConcurrentQueue.Count</c> is O(N) and only approximate under contention — calling
    /// it in the hot Add()-trim loop would add measurable overhead on the MongoDB driver's
    /// callback threads.
    /// </summary>
    private long _count;

    /// <summary>Total entries the buffer will retain before FIFO-dropping.</summary>
    public int Capacity => _capacity;

    public SlowQueriesBuffer(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                "Slow-queries buffer capacity cannot be negative.");
        }

        _capacity = capacity;
    }

    /// <summary>
    /// Adds <paramref name="entry"/> to the buffer. If the buffer is at capacity, the oldest
    /// entry is discarded first. Safe to call concurrently from any thread (intended for the
    /// MongoDB driver's command-event thread pool).
    /// </summary>
    public void Add(SlowQueryEntry entry)
    {
        if (_capacity == 0)
        {
            return;
        }

        _queue.Enqueue(entry);
        var current = Interlocked.Increment(ref _count);

        // Trim using our own Interlocked counter — no O(N) ConcurrentQueue.Count scan.
        // Under contention this loop may briefly leave the queue at Capacity+k for a small k
        // (another writer increments between our TryDequeue and Interlocked.Decrement), but
        // converges back deterministically as soon as contention drops.
        while (current > _capacity)
        {
            if (_queue.TryDequeue(out _))
            {
                current = Interlocked.Decrement(ref _count);
            }
            else
            {
                // Another writer's Dequeue raced us; the queue is empty even though _count
                // hadn't caught up. Stop — the next Add will rebalance.
                break;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the buffer's contents, newest first, optionally filtered and
    /// capped. The snapshot is consistent at a single point in time; concurrent writes after
    /// this call do not affect the returned list.
    /// </summary>
    /// <param name="predicate">Optional filter applied before reversal and limit.</param>
    /// <param name="limit">Optional max number of entries to return (after filtering).</param>
    public IReadOnlyList<SlowQueryEntry> GetSnapshot(
        Func<SlowQueryEntry, bool>? predicate = null,
        int? limit = null)
    {
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                "Slow-queries snapshot limit cannot be negative. Pass null for no limit, or 0 for an empty result.");
        }

        // ConcurrentQueue.ToArray() takes an internal lock-free snapshot — perfect for our
        // use case. The array is ordered oldest → newest; we reverse for the UI surface.
        var snapshot = _queue.ToArray();
        IEnumerable<SlowQueryEntry> view = snapshot;

        if (predicate is not null)
        {
            view = view.Where(predicate);
        }

        view = view.Reverse();

        if (limit.HasValue)
        {
            view = view.Take(limit.Value);
        }

        return view.ToList();
    }

    /// <summary>
    /// Approximate current entry count from the Interlocked tracker. May be briefly stale
    /// (off by one or two) under heavy concurrent Add() contention but converges as soon as
    /// contention drops. For a deterministic point-in-time count, use <c>GetSnapshot().Count</c>.
    /// </summary>
    public int Count => (int)Interlocked.Read(ref _count);

    /// <summary>
    /// Same point-in-time snapshot as <see cref="GetSnapshot"/>, but aggregated by
    /// <see cref="SlowQueryEntry.Fingerprint"/>. Groups are returned ordered by
    /// <see cref="SlowQueryGroup.LastSeen"/> descending (most-recent activity first).
    /// </summary>
    /// <param name="predicate">Optional filter applied to the entries before grouping.</param>
    /// <param name="limit">Optional max number of groups (after sorting).</param>
    public IReadOnlyList<SlowQueryGroup> GetGroupedSnapshot(
        Func<SlowQueryEntry, bool>? predicate = null,
        int? limit = null)
    {
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                "Slow-queries grouped-snapshot limit cannot be negative. Pass null for no limit, or 0 for an empty result.");
        }

        var snapshot = _queue.ToArray();
        IEnumerable<SlowQueryEntry> view = snapshot;
        if (predicate is not null)
        {
            view = view.Where(predicate);
        }

        var groups = view
            .GroupBy(e => e.Fingerprint, StringComparer.Ordinal)
            .Select(g =>
            {
                // Materialize once; we need multiple aggregations + the representative pick.
                var entries = g.ToList();
                var ordered = entries.OrderByDescending(e => e.Timestamp).ToList();
                var representative = ordered[0];
                return new SlowQueryGroup(
                    Fingerprint: g.Key,
                    CommandName: representative.CommandName,
                    Target: representative.Target,
                    Database: representative.Database,
                    Count: entries.Count,
                    FirstSeen: entries.Min(e => e.Timestamp),
                    LastSeen: ordered[0].Timestamp,
                    MinDurationMs: entries.Min(e => e.DurationMs),
                    MaxDurationMs: entries.Max(e => e.DurationMs),
                    AvgDurationMs: entries.Average(e => e.DurationMs),
                    Representative: representative);
            })
            .OrderByDescending(g => g.LastSeen);

        IEnumerable<SlowQueryGroup> final = groups;
        if (limit.HasValue)
        {
            final = final.Take(limit.Value);
        }

        return final.ToList();
    }
}
