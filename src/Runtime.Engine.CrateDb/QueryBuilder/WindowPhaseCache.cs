namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// What a process remembers about the window phase of time-range archives (AB#8992): the answer of the window-phase
/// probe of a downsampling query (AB#8962), and the one probe per key that is running. Held by
/// <see cref="RecomputeProcessState"/>, i.e. by the singleton repository factory, because the repositories live only as
/// long as one request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key.</b> The schema-qualified archive table, the window length and the probe's source filters without the time
/// bounds (ckTypeId, rtIds, field filters). The filters stay in the key: two series of one archive may be stored at
/// different phases, and snapping one onto the other's phase would drop its windows.
/// </para>
/// <para>
/// <b>What is remembered.</b> Only a single phase. "No window in range" depends on the range and "different phases" may
/// end with the next range, so both are asked again.
/// </para>
/// <para>
/// <b>Single flight.</b> At most one probe runs per key; concurrent queries share it. A probe that outlives the short
/// wait of its query keeps running (bounded by its own budget) and fills the cache for the next query, so it holds at
/// most one pooled connection per key. Only a single phase holds for any range; "no window" and "different phases" are
/// answers about the probe's own range. A query with another range that joins a probe therefore takes only a single
/// phase from it, and otherwise starts its own probe after it (still one at a time per key). A phase taken from another
/// range is marked (<see cref="Probe.OfOtherRange"/>) and treated like a remembered one: not proven for the caller's range.
/// </para>
/// <para>
/// <b>Staleness.</b> An entry ends after its time to live, when the archive's tables are dropped by this process
/// (<see cref="Forget"/>, from <see cref="RecomputeProcessState.Forget"/>) and when this process writes a window of
/// another phase into the table (<see cref="ObserveWrittenWindows"/>). A phase that changes in another process without a
/// table drop is seen only after the time to live. A probe that answers after this process wrote a window of another
/// phase into the table does not get its answer remembered (it may have read the table before the write).
/// </para>
/// </remarks>
internal sealed class WindowPhaseCache
{
    /// <summary>More entries are not kept: the cache is cleared rather than grown without bound.</summary>
    internal const int MaxEntries = 10_000;

    /// <summary>The cache key, see the class remarks.</summary>
    internal readonly record struct Key(string Table, long WindowMilliseconds, string Filter);

    /// <summary>
    /// The answer of one probe: the smallest and the largest phase (<c>window_start</c> in epoch milliseconds modulo
    /// the window length), both null when the range holds no window; <see cref="Error"/> when the probe failed.
    /// </summary>
    /// <param name="OfOtherRange">
    /// True when the phase was found by a probe of another range (a joined probe): like a remembered phase it is not
    /// proven for the caller's range, so the caller treats it like one (the check after an empty result).
    /// </param>
    internal readonly record struct Probe(long? MinPhase, long? MaxPhase, Exception? Error = null, bool OfOtherRange = false)
    {
        /// <summary>True when every window in range sits at one phase.</summary>
        public bool IsSinglePhase => Error is null && MinPhase is { } min && MaxPhase is { } max && min == max;
    }

    private readonly record struct Entry(long Phase, DateTimeOffset ExpiresAt);

    private sealed class Flight(string range)
    {
        /// <summary>The probe statement, time bounds included: the range the answer is about.</summary>
        public string Range { get; } = range;

        public Task<Probe> Task { get; set; } = System.Threading.Tasks.Task.FromResult(default(Probe));

        /// <summary>Phases of the windows this process wrote into the table while the probe ran.</summary>
        public HashSet<long> WrittenPhases { get; } = [];
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Dictionary<Key, Flight> _inFlight = new();

    /// <summary>Number of remembered phases (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Number of running probes (tests).</summary>
    internal int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>The remembered phase of <paramref name="key"/>, when there is one that has not expired.</summary>
    public bool TryGet(Key key, DateTimeOffset now, out long phase)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    phase = entry.Phase;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        phase = 0;
        return false;
    }

    /// <summary>
    /// The running probe of <paramref name="key"/>, or a new one started with <paramref name="run"/>. A single phase the
    /// probe finds is remembered for <paramref name="timeToLive"/> (not at all when it is not positive), unless the key
    /// was forgotten or a window of another phase was written while the probe ran. A running probe of another
    /// <paramref name="range"/> is joined for its single phase only; otherwise this range is probed after it. The task
    /// never faults: a failure is returned in <see cref="Probe.Error"/>.
    /// </summary>
    /// <param name="range">The probe statement with its time bounds; equal ranges share one answer.</param>
    public Task<Probe> GetOrStartProbe(Key key, string range, Func<Task<Probe>> run, TimeSpan timeToLive,
        Func<DateTimeOffset> now)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var running))
            {
                return running.Range == range
                    ? running.Task
                    : FollowAsync(running.Task, key, range, run, timeToLive, now);
            }

            // Registered before it starts, so a probe that completes at once still finds itself and removes its entry.
            var flight = new Flight(range);
            _inFlight[key] = flight;
            flight.Task = RunAsync(key, flight, run, timeToLive, now);
            return flight.Task;
        }
    }

    /// <summary>Waits for the probe of another range; takes its answer only when it is a single phase.</summary>
    private async Task<Probe> FollowAsync(Task<Probe> other, Key key, string range, Func<Task<Probe>> run,
        TimeSpan timeToLive, Func<DateTimeOffset> now)
    {
        var probe = await other.ConfigureAwait(false);
        return probe.IsSinglePhase
            ? probe with { OfOtherRange = true }
            : await GetOrStartProbe(key, range, run, timeToLive, now).ConfigureAwait(false);
    }

    private async Task<Probe> RunAsync(Key key, Flight flight, Func<Task<Probe>> run, TimeSpan timeToLive,
        Func<DateTimeOffset> now)
    {
        Probe probe;
        try
        {
            // Off the caller's thread and lock: run() opens a connection.
            probe = await System.Threading.Tasks.Task.Run(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            probe = new Probe(null, null, ex);
        }

        lock (_gate)
        {
            // A Forget while the probe ran removed the flight: its answer may describe a table that is gone.
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
            {
                _inFlight.Remove(key);
                if (probe.IsSinglePhase && timeToLive > TimeSpan.Zero
                    && flight.WrittenPhases.All(phase => phase == probe.MinPhase))
                {
                    if (_entries.Count >= MaxEntries)
                    {
                        _entries.Clear();
                    }

                    _entries[key] = new Entry(probe.MinPhase!.Value, now() + timeToLive);
                }
            }
        }

        return probe;
    }

    /// <summary>Removes the remembered phase of <paramref name="key"/> (it proved wrong).</summary>
    public void Evict(Key key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    /// <summary>Forgets everything about <paramref name="qualifiedTable"/>, running probes included: its tables are gone.</summary>
    public void Forget(string qualifiedTable)
    {
        lock (_gate)
        {
            RemoveWhere(_entries, k => k.Table == qualifiedTable);
            RemoveWhere(_inFlight, k => k.Table == qualifiedTable);
        }
    }

    /// <summary>
    /// Called with the window starts this process wrote into <paramref name="qualifiedTable"/>: a remembered phase that
    /// one of them does not have is removed, so the next query probes again.
    /// </summary>
    public void ObserveWrittenWindows(string qualifiedTable, IEnumerable<DateTime> windowStarts)
    {
        lock (_gate)
        {
            // Called on every time-range write: nothing to scan in a process that never ran a downsampling probe.
            if (_entries.Count == 0 && _inFlight.Count == 0)
            {
                return;
            }

            var keys = _entries.Where(e => e.Key.Table == qualifiedTable).ToList();
            var flights = _inFlight.Where(f => f.Key.Table == qualifiedTable).ToList();
            if (keys.Count == 0 && flights.Count == 0)
            {
                return;
            }

            var startsMs = windowStarts
                .Select(s => (long)(s.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds)
                .ToList();
            foreach (var (key, entry) in keys)
            {
                if (startsMs.Any(ms => PhaseOf(ms, key.WindowMilliseconds) != entry.Phase))
                {
                    _entries.Remove(key);
                }
            }

            // A running probe may have read the table before these windows: its answer is remembered only when it
            // matches every phase written meanwhile.
            foreach (var (key, flight) in flights)
            {
                flight.WrittenPhases.UnionWith(startsMs.Select(ms => PhaseOf(ms, key.WindowMilliseconds)));
            }
        }
    }

    /// <summary>The phase of an instant in epoch milliseconds: the remainder modulo the window length, never negative.</summary>
    internal static long PhaseOf(long epochMilliseconds, long windowMilliseconds)
    {
        var phase = epochMilliseconds % windowMilliseconds;
        return phase < 0 ? phase + windowMilliseconds : phase;
    }

    private static void RemoveWhere<TValue>(Dictionary<Key, TValue> dictionary, Func<Key, bool> predicate)
    {
        foreach (var key in dictionary.Keys.Where(predicate).ToList())
        {
            dictionary.Remove(key);
        }
    }
}
