using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     A data source that names the tenant database it reads, so that every data source instance of the same database
///     (they are created per call; the import uses the admin one, readers the user one) shares one entry of the
///     <see cref="CkModelImportLockCache" />.
/// </summary>
internal interface ICkModelImportLockScope
{
    /// <summary>Identifies the database: the MongoDB servers and the database name.</summary>
    string ImportLockScopeKey { get; }
}

/// <summary>
///     AB#8947: the CK models whose import lock is held, as the hot dependency resolve
///     (<see cref="DatabaseCkModelRepository" />.<c>IsExistingAsync</c>) needs it, without a query on the lock collection
///     on every call. Since AB#8677 every resolve outside a transaction asked the lock collection first; the resolve runs
///     on every tenant lookup of every request (<c>SystemContext.IsSystemTenantExistingAsync</c>), which made it one
///     MongoDB round-trip per request in every service.
///     <para>
///         One entry per tenant database and process holds the last answer for at most <see cref="TimeToLive" />.
///         An import lock acquired or released in this process invalidates the entry at once
///         (<see cref="NotifyImportLockChanged" />), so a reader in the same process sees the import with its next
///         resolve. A reader in another process sees it at most <see cref="TimeToLive" /> late; the importer therefore
///         waits <see cref="TimeToLive" /> after acquiring the lock before it removes the installed version
///         (<see cref="WaitUntilForeignSnapshotsExpiredAsync" />), so no reader can still trust a snapshot taken before
///         the import began when the model disappears.
///     </para>
///     <para>
///         The failure paths (the CK cache load retry, the import's own failure classification) and the wait loop
///         (<see cref="CkModelImportInProgress" />) read the lock collection directly, never through this cache.
///     </para>
/// </summary>
internal sealed class CkModelImportLockCache
{
    /// <summary>Default lifetime of a cached answer: the worst-case detection delay in another process.</summary>
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Entry> _byScope = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ICkMongoDbRepositoryDataSource, Entry> _byInstance = new();

    /// <summary>The cache of this process.</summary>
    internal static CkModelImportLockCache Shared { get; } = new(DefaultTimeToLive, TimeProvider.System);

    internal CkModelImportLockCache(TimeSpan timeToLive, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeToLive, TimeSpan.Zero);
        TimeToLive = timeToLive;
        _timeProvider = timeProvider;
    }

    /// <summary>How long an answer is reused; also the worst-case delay before another process sees an import.</summary>
    internal TimeSpan TimeToLive { get; }

    /// <summary>
    ///     The names of the models being imported in the database of <paramref name="dataSource" />: the cached answer
    ///     when it is younger than <see cref="TimeToLive" /> and no import lock changed in this process since, otherwise
    ///     a fresh <see cref="ICkMongoDbRepositoryDataSource.GetModelsBeingImportedAsync" />.
    /// </summary>
    internal async Task<IReadOnlyList<string>> GetModelsBeingImportedAsync(ICkMongoDbRepositoryDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var entry = EntryOf(dataSource);
        var now = _timeProvider.GetUtcNow();
        var snapshot = entry.Snapshot;
        var generation = entry.Generation;
        if (snapshot != null && snapshot.Generation == generation && now - snapshot.TakenAt < TimeToLive)
        {
            return snapshot.ModelNames;
        }

        // The generation is read before the query: an import lock that changes while the query runs makes the
        // stored answer stale at once (its generation no longer matches), so the next reader asks again.
        var modelNames = await dataSource.GetModelsBeingImportedAsync(cancellationToken).ConfigureAwait(false);
        entry.Snapshot = new Snapshot(modelNames, now, generation);
        return modelNames;
    }

    /// <summary>An import lock of the database was acquired or released in this process: drop the cached answer.</summary>
    internal void NotifyImportLockChanged(ICkMongoDbRepositoryDataSource dataSource)
    {
        EntryOf(dataSource).Invalidate();
    }

    /// <summary>
    ///     Waits until <see cref="TimeToLive" /> has passed since <paramref name="lockAcquiredAt" />: by then every
    ///     snapshot another process took before the lock was acquired has expired, and its next resolve of the model
    ///     sees the import.
    /// </summary>
    internal Task WaitUntilForeignSnapshotsExpiredAsync(DateTimeOffset lockAcquiredAt,
        CancellationToken cancellationToken = default)
    {
        var remaining = lockAcquiredAt + TimeToLive - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero
            ? Task.Delay(remaining, _timeProvider, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>The current time of the cache's clock.</summary>
    internal DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private Entry EntryOf(ICkMongoDbRepositoryDataSource dataSource)
    {
        return dataSource is ICkModelImportLockScope scope
            ? _byScope.GetOrAdd(scope.ImportLockScopeKey, static _ => new Entry())
            : _byInstance.GetValue(dataSource, static _ => new Entry());
    }

    private sealed record Snapshot(IReadOnlyList<string> ModelNames, DateTimeOffset TakenAt, long Generation);

    private sealed class Entry
    {
        private long _generation;
        private Snapshot? _snapshot;

        public long Generation => Interlocked.Read(ref _generation);

        public Snapshot? Snapshot
        {
            get => Volatile.Read(ref _snapshot);
            set => Volatile.Write(ref _snapshot, value);
        }

        public void Invalidate()
        {
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _snapshot, null);
        }
    }
}
