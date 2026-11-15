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
///         Readers of an expired entry share one query (single flight). An import lock acquired or released in this
///         process invalidates the entry at once (<see cref="NotifyImportLockChanged" />), so a reader in the same
///         process sees the import with its next resolve. A reader in another process, or one whose importer runs an
///         older package without this signal, may use an answer up to <see cref="TimeToLive" /> old; that is why
///         <c>IsExistingAsync</c> asks the lock collection directly whenever it finds no matching version.
///     </para>
///     <para>
///         The failure paths (the CK cache load retry, the import's own failure classification) and the wait loop
///         (<see cref="CkModelImportInProgress" />) read the lock collection directly, never through this cache.
///     </para>
/// </summary>
internal sealed class CkModelImportLockCache
{
    /// <summary>Default lifetime of a cached answer.</summary>
    internal static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Default bound of the shared query: readers join it, so a stalled read must end on its own, or every later
    ///     resolve of the database would wait for it.
    /// </summary>
    internal static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Entry> _byScope = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ICkMongoDbRepositoryDataSource, Entry> _byInstance = new();

    /// <summary>The cache of this process.</summary>
    internal static CkModelImportLockCache Shared { get; } = new(DefaultTimeToLive, TimeProvider.System);

    internal CkModelImportLockCache(TimeSpan timeToLive, TimeProvider timeProvider, TimeSpan? queryTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeToLive, TimeSpan.Zero);
        TimeToLive = timeToLive;
        _timeProvider = timeProvider;
        QueryTimeout = queryTimeout ?? DefaultQueryTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QueryTimeout, TimeSpan.Zero);
    }

    /// <summary>How long an answer is reused.</summary>
    internal TimeSpan TimeToLive { get; }

    /// <summary>How long the shared query may run before it is cancelled and the next reader starts a new one.</summary>
    internal TimeSpan QueryTimeout { get; }

    /// <summary>
    ///     The names of the models being imported in the database of <paramref name="dataSource" />: the cached answer
    ///     when it is younger than <see cref="TimeToLive" /> and no import lock changed in this process since, otherwise
    ///     a fresh <see cref="ICkMongoDbRepositoryDataSource.GetModelsBeingImportedAsync" />, shared by all readers that
    ///     ask while it runs.
    /// </summary>
    internal Task<IReadOnlyList<string>> GetModelsBeingImportedAsync(ICkMongoDbRepositoryDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var entry = EntryOf(dataSource);
        var snapshot = entry.Snapshot;
        if (snapshot != null && snapshot.Generation == entry.Generation &&
            _timeProvider.GetUtcNow() - snapshot.TakenAt < TimeToLive)
        {
            return Task.FromResult(snapshot.ModelNames);
        }

        return entry.RefreshAsync(dataSource, _timeProvider, TimeToLive, QueryTimeout, cancellationToken);
    }

    /// <summary>
    ///     The slow path of <see cref="GetModelsBeingImportedAsync" /> on its own, as a reader enters it after it found the
    ///     entry expired. Test seam for the interleaving in which another reader refreshed the entry in between.
    /// </summary>
    internal Task<IReadOnlyList<string>> RefreshAsync(ICkMongoDbRepositoryDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        return EntryOf(dataSource).RefreshAsync(dataSource, _timeProvider, TimeToLive, QueryTimeout, cancellationToken);
    }

    /// <summary>An import lock of the database was acquired or released in this process: drop the cached answer.</summary>
    internal void NotifyImportLockChanged(ICkMongoDbRepositoryDataSource dataSource)
    {
        EntryOf(dataSource).Invalidate();
    }

    private Entry EntryOf(ICkMongoDbRepositoryDataSource dataSource)
    {
        return dataSource is ICkModelImportLockScope scope
            ? _byScope.GetOrAdd(scope.ImportLockScopeKey, static _ => new Entry())
            : _byInstance.GetValue(dataSource, static _ => new Entry());
    }

    private sealed record Snapshot(IReadOnlyList<string> ModelNames, DateTimeOffset TakenAt, long Generation);

    private sealed class Entry
    {
        private readonly Lock _gate = new();
        private long _generation;
        private Snapshot? _snapshot;
        private Task<IReadOnlyList<string>>? _inFlight;
        private long _inFlightGeneration;

        public long Generation => Interlocked.Read(ref _generation);

        public Snapshot? Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>
        ///     Joins the running query of this entry, or starts one. A query started before the last lock change is never
        ///     joined: it may have read the old state. The query is not cancelled by one reader; each reader stops waiting
        ///     on its own token. It is cancelled after <paramref name="queryTimeout" />, so a stalled read fails its
        ///     readers and the next reader starts a new query.
        /// </summary>
        public Task<IReadOnlyList<string>> RefreshAsync(ICkMongoDbRepositoryDataSource dataSource,
            TimeProvider timeProvider, TimeSpan timeToLive, TimeSpan queryTimeout, CancellationToken cancellationToken)
        {
            TaskCompletionSource<IReadOnlyList<string>>? owner = null;
            Task<IReadOnlyList<string>> query;
            long generation;
            lock (_gate)
            {
                generation = Generation;
                // Another reader may have refreshed the entry since this one found it expired.
                var snapshot = Snapshot;
                if (snapshot != null && snapshot.Generation == generation &&
                    timeProvider.GetUtcNow() - snapshot.TakenAt < timeToLive)
                {
                    return Task.FromResult(snapshot.ModelNames);
                }

                if (_inFlight == null || _inFlight.IsCompleted || _inFlightGeneration != generation)
                {
                    owner = new TaskCompletionSource<IReadOnlyList<string>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight = owner.Task;
                    _inFlightGeneration = generation;
                }

                query = _inFlight;
            }

            if (owner != null)
            {
                _ = QueryAsync(dataSource, timeProvider, queryTimeout, generation, owner);
            }

            return query.WaitAsync(cancellationToken);
        }

        public void Invalidate()
        {
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _snapshot, null);
        }

        private async Task QueryAsync(ICkMongoDbRepositoryDataSource dataSource, TimeProvider timeProvider,
            TimeSpan queryTimeout, long generation, TaskCompletionSource<IReadOnlyList<string>> owner)
        {
            try
            {
                using var timeout = new CancellationTokenSource(queryTimeout);
                var takenAt = timeProvider.GetUtcNow();
                var modelNames = await dataSource.GetModelsBeingImportedAsync(timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                // Stored with the generation read before the query: if a lock changed meanwhile, the answer is stale
                // at once and the next reader asks again.
                Volatile.Write(ref _snapshot, new Snapshot(modelNames, takenAt, generation));
                owner.SetResult(modelNames);
            }
            catch (Exception exception)
            {
                owner.SetException(exception);
            }
        }
    }
}
