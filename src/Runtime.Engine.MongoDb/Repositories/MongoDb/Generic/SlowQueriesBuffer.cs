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

        // Trim until back at capacity. Under contention this loop may temporarily over-trim
        // (another concurrent writer enqueues a new entry mid-trim), but the result is always
        // a buffer with ≤ Capacity entries — never blown out, never blocking.
        while (_queue.Count > _capacity && _queue.TryDequeue(out _))
        {
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
        // ConcurrentQueue.ToArray() takes an internal lock-free snapshot — perfect for our
        // use case. The array is ordered oldest → newest; we reverse for the UI surface.
        var snapshot = _queue.ToArray();
        IEnumerable<SlowQueryEntry> view = snapshot;

        if (predicate is not null)
        {
            view = view.Where(predicate);
        }

        view = view.Reverse();

        if (limit.HasValue && limit.Value >= 0)
        {
            view = view.Take(limit.Value);
        }

        return view.ToList();
    }

    /// <summary>Approximate current entry count. May be slightly stale under concurrent writers.</summary>
    public int Count => _queue.Count;
}
