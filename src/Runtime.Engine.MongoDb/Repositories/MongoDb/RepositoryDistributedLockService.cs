using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class RepositoryDistributedLockService(
    IRepositoryClient repositoryClient,
    IRepositoryInternal repository,
    ILogger logger,
    string id)
    : IDistributedLockHandle
{
    // Polling cadence — with TTL=60s, 90 attempts × 1s = 90s max wait
    // (50% margin over TTL so a stale lock is always reached before the timeout).
    private const int MaxRetryAttempts = 90;
    private const int DelayMilliseconds = 1000;

    // Lock TTL: After this time the lock is considered "stale" and can be claimed by another service.
    // Short TTL drastically reduces stuck-lock time after a crash, but requires the owner-token
    // protection (Fix 2) to be safe against heartbeat hiccups.
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromSeconds(60);

    // Heartbeat interval: extend the lock well before the TTL window closes (4× margin).
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // Process-stable identification of the current holder for diagnostics (Fix 8)
    private static readonly string CurrentProcessHolderInfo =
        $"{Environment.MachineName}/{Environment.ProcessId}";

    private bool _isLockAcquired;
    private readonly Lock _lockObject = new();
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    // Signaled when ownership is lost — exposed via LockLostToken so callers can abort work.
    private readonly CancellationTokenSource _lockLostCts = new();

    // Unique token identifying this acquire (Fix 2). Heartbeat and release filter on it,
    // so a zombie of this service cannot mutate a lock that has already been re-claimed.
    private Guid _ownerToken;

    // Set when the heartbeat detects the lock was stolen — prevents the dispose path
    // from deleting another owner's lock document.
    private volatile bool _lockLost;

    /// <inheritdoc />
    public CancellationToken LockLostToken => _lockLostCts.Token;

    public async Task AcquireLockAsync(CancellationToken cancellationToken = default)
    {
        lock (_lockObject)
        {
            if (_isLockAcquired)
            {
                throw new InvalidOperationException($"Lock '{id}' is already acquired by this service instance.");
            }
        }

        try
        {
            _ownerToken = Guid.NewGuid();
            _lockLost = false;

            logger.LogInformation("Acquiring distributed lock '{LockId}' as {Holder} (token {OwnerToken})",
                id, CurrentProcessHolderInfo, _ownerToken);

            var collection = repository.GetCollection(new LockMongoDataSourceMapper());
            EnsureTtlIndex(collection.GetMongoCollection());

            int attempt = 0;
            bool lockAcquired = false;

            while (!lockAcquired)
            {
                var now = DateTime.UtcNow;
                var expiryTime = now.Add(LockTimeToLive);

                // Try to create a new lock or claim an expired one
                var filter = Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id);

                SysLock? existingLock;
                using (var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false))
                {
                    session.StartTransaction();
                    var existingLocks = await collection.FindManyAsync(session, filter, null, null, 1);
                    await session.CommitTransactionAsync();
                    existingLock = existingLocks.FirstOrDefault();
                }

                if (existingLock == null)
                {
                    // No lock exists - try to create one
                    var newLock = new SysLock
                    {
                        Id = id,
                        CreationDateTime = now,
                        ExpiryDateTime = expiryTime,
                        LastHeartbeat = now,
                        OwnerToken = _ownerToken,
                        HolderInfo = CurrentProcessHolderInfo
                    };

                    try
                    {
                        using var insertSession = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                        insertSession.StartTransaction();
                        await collection.InsertOneAsync(insertSession, newLock);
                        await insertSession.CommitTransactionAsync();
                        lockAcquired = true;
                    }
                    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        // Race condition: Another service just acquired the lock
                        logger.LogDebug("Lock '{LockId}' was just acquired by another service, retrying...", id);
                    }
                    catch (DuplicateKeyException)
                    {
                        // Race condition: Another service just acquired the lock
                        // (MongoDbDataSourceCollection wraps MongoWriteException in DuplicateKeyException)
                        logger.LogDebug("Lock '{LockId}' was just acquired by another service, retrying...", id);
                    }
                }
                else if (!existingLock.ExpiryDateTime.HasValue || existingLock.ExpiryDateTime.Value < now)
                {
                    // Lock is expired or has no ExpiryDateTime - try to claim it
                    logger.LogWarning(
                        "Found stale lock '{LockId}' previously held by {PreviousHolder} (expired at {ExpiryDateTime}, last heartbeat at {LastHeartbeat}), attempting to claim it",
                        id, existingLock.HolderInfo ?? "<unknown>", existingLock.ExpiryDateTime, existingLock.LastHeartbeat);

                    // Filter: Lock exists AND (no ExpiryDateTime OR ExpiryDateTime expired)
                    var replaceFilter = Builders<SysLock>.Filter.And(
                        Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id),
                        Builders<SysLock>.Filter.Or(
                            Builders<SysLock>.Filter.Eq(sysLock => sysLock.ExpiryDateTime, null),
                            Builders<SysLock>.Filter.Lt(sysLock => sysLock.ExpiryDateTime, now)
                        )
                    );

                    var updatedLock = new SysLock
                    {
                        Id = id,
                        CreationDateTime = now,
                        ExpiryDateTime = expiryTime,
                        LastHeartbeat = now,
                        OwnerToken = _ownerToken,
                        HolderInfo = CurrentProcessHolderInfo
                    };

                    using var replaceSession = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                    replaceSession.StartTransaction();
                    var modifiedCount =
                        await collection.ReplaceOneWithModificationCountAsync(replaceSession, replaceFilter, updatedLock);
                    await replaceSession.CommitTransactionAsync();

                    if (modifiedCount > 0)
                    {
                        lockAcquired = true;
                        logger.LogInformation("Successfully claimed stale lock '{LockId}'", id);
                    }
                    else
                    {
                        logger.LogDebug("Failed to claim stale lock '{LockId}', another service claimed it first", id);
                    }
                }
                else
                {
                    // Lock exists and is still valid - log the remaining time
                    var remainingTime = existingLock.ExpiryDateTime.Value - now;
                    logger.LogDebug(
                        "Lock '{LockId}' is held by {Holder}, expires in {RemainingSeconds:F0} seconds (at {ExpiryDateTime:O})",
                        id, existingLock.HolderInfo ?? "<unknown>", remainingTime.TotalSeconds, existingLock.ExpiryDateTime.Value);
                }

                if (!lockAcquired)
                {
                    attempt++;
                    if (attempt >= MaxRetryAttempts)
                    {
                        var expiryInfo = existingLock?.ExpiryDateTime.HasValue == true
                            ? $"Current lock expires at {existingLock.ExpiryDateTime.Value:O}"
                            : "Lock has no expiry time set";
                        throw new TimeoutException(
                            $"Could not acquire distributed lock '{id}' after {MaxRetryAttempts * DelayMilliseconds / 1000} seconds. {expiryInfo}");
                    }

                    logger.LogInformation("Waiting for distributed lock '{LockId}' (attempt {Attempt}/{MaxAttempts})",
                        id, attempt, MaxRetryAttempts);
                    await Task.Delay(DelayMilliseconds, cancellationToken);
                }
            }

            logger.LogInformation("Acquired distributed lock '{LockId}' (token {OwnerToken})", id, _ownerToken);

            lock (_lockObject)
            {
                _isLockAcquired = true;
            }

            // Start heartbeat for long-running operations
            StartHeartbeat();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error acquiring distributed lock '{LockId}'", id);
            throw;
        }
    }

    /// <summary>
    /// Ensures a TTL index on <see cref="SysLock.ExpiryDateTime"/> so MongoDB
    /// automatically reaps expired lock documents within the TTL reaper interval (~60s).
    /// This is the safety net against entries that the application-level reclaim
    /// logic fails to delete (Fix 1).
    ///
    /// Called on every acquire — MongoDB's createIndex is idempotent (no-op when the
    /// same key/name/options already exist), so this stays correct across fresh
    /// database instances (e.g. CI testcontainers spinning up new clusters per class).
    /// </summary>
    private void EnsureTtlIndex(IMongoCollection<SysLock> mongoCollection)
    {
        try
        {
            var indexKeys = Builders<SysLock>.IndexKeys.Ascending(x => x.ExpiryDateTime);
            var indexOptions = new CreateIndexOptions
            {
                ExpireAfter = TimeSpan.Zero,
                Name = "sysLock_ttl_expiryDateTime"
            };
            mongoCollection.Indexes.CreateOne(
                new CreateIndexModel<SysLock>(indexKeys, indexOptions));
        }
        catch (Exception ex)
        {
            // Do not block lock acquisition if the index cannot be created
            // (e.g. permissions); application-level reclaim still handles staleness.
            logger.LogWarning(ex,
                "Failed to ensure TTL index on SysLock collection — relying on application-level reclaim only");
        }
    }

    private void StartHeartbeat()
    {
        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!_heartbeatCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatInterval, _heartbeatCts.Token);

                    if (_heartbeatCts.Token.IsCancellationRequested)
                        break;

                    await UpdateHeartbeatAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping heartbeat
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in heartbeat task for lock '{LockId}'", id);
            }
        }, _heartbeatCts.Token);
    }

    private async Task UpdateHeartbeatAsync()
    {
        try
        {
            // Create a new session for the heartbeat update
            using var heartbeatSession = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
            heartbeatSession.StartTransaction();
            try
            {
                var collection = repository.GetCollection(new LockMongoDataSourceMapper());
                var now = DateTime.UtcNow;
                var newExpiryTime = now.Add(LockTimeToLive);

                // Owner-scoped filter (Fix 2): only the current owner may extend the lock.
                var filter = Builders<SysLock>.Filter.And(
                    Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id),
                    Builders<SysLock>.Filter.Eq(sysLock => sysLock.OwnerToken, _ownerToken));
                var update = Builders<SysLock>.Update
                    .Set(sysLock => sysLock.LastHeartbeat, now)
                    .Set(sysLock => sysLock.ExpiryDateTime, newExpiryTime);

                var result = await collection.GetMongoCollection()
                    .UpdateOneAsync(((IOctoSessionInternal)heartbeatSession).SessionHandle, filter, update);

                await heartbeatSession.CommitTransactionAsync();

                if (result.MatchedCount == 0)
                {
                    // Lock was claimed by another service while we still believed we owned it.
                    // Stop heartbeating, flag the loss so Dispose does not delete the foreign
                    // owner's document, and signal LockLostToken so consumers can abort.
                    _lockLost = true;
                    logger.LogError(
                        "Lost ownership of distributed lock '{LockId}' (token {OwnerToken}): " +
                        "another service claimed it after our TTL expired. Heartbeat stopped. " +
                        "Consumers observing LockLostToken should abort the in-flight operation.",
                        id, _ownerToken);
                    try
                    {
                        _lockLostCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Race with DisposeAsync — already cancelled/disposed, nothing to do.
                    }
                    _heartbeatCts?.Cancel();
                    return;
                }

                logger.LogDebug("Updated heartbeat for lock '{LockId}', new expiry: {ExpiryDateTime:O}",
                    id, newExpiryTime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error updating heartbeat for lock '{LockId}'", id);
                try
                {
                    await heartbeatSession.AbortTransactionAsync();
                }
                catch (Exception abortEx)
                {
                    logger.LogError(abortEx, "Error aborting heartbeat transaction for lock '{LockId}'", id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating session for heartbeat update of lock '{LockId}'", id);
        }
    }

    private async Task StopHeartbeatAsync()
    {
        if (_heartbeatCts != null)
        {
            _heartbeatCts.Cancel();
            if (_heartbeatTask != null)
            {
                try
                {
                    await _heartbeatTask;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error stopping heartbeat task for lock '{LockId}'", id);
                }
            }

            _heartbeatCts.Dispose();
            _heartbeatCts = null;
            _heartbeatTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        logger.LogInformation("Disposing distributed lock service for '{LockId}'", id);

        // Stop heartbeat first
        await StopHeartbeatAsync();

        bool wasLockAcquired;
        lock (_lockObject)
        {
            wasLockAcquired = _isLockAcquired;
        }

        if (wasLockAcquired)
        {
            if (_lockLost)
            {
                // Heartbeat already detected that another service owns the lock now.
                // Deleting by Id would wipe their entry — skip the release entirely.
                logger.LogWarning(
                    "Skipping release of distributed lock '{LockId}' (token {OwnerToken}): ownership was lost.",
                    id, _ownerToken);
                lock (_lockObject)
                {
                    _isLockAcquired = false;
                }
                return;
            }

            try
            {
                logger.LogInformation("Releasing distributed lock '{LockId}' (token {OwnerToken}) on dispose",
                    id, _ownerToken);

                using var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                session.StartTransaction();
                try
                {
                    var collection = repository.GetCollection(new LockMongoDataSourceMapper());

                    // Owner-scoped filter (Fix 2): never delete a foreign owner's lock.
                    var filter = Builders<SysLock>.Filter.And(
                        Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id),
                        Builders<SysLock>.Filter.Eq(sysLock => sysLock.OwnerToken, _ownerToken));
                    await collection.DeleteOneAsync(session, filter);

                    logger.LogInformation("Released distributed lock '{LockId}'", id);
                    await session.CommitTransactionAsync();

                    lock (_lockObject)
                    {
                        _isLockAcquired = false;
                    }
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error releasing distributed lock '{LockId}'", id);
                    try
                    {
                        await session.AbortTransactionAsync();
                    }
                    catch (Exception abortEx)
                    {
                        logger.LogError(abortEx, "Error aborting transaction while releasing lock '{LockId}'", id);
                    }
                    // IMPORTANT: Never throw exceptions in Dispose! Only log.
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Critical error disposing distributed lock '{LockId}'", id);
                // IMPORTANT: Never throw exceptions in Dispose! Only log.
            }
        }

        try
        {
            _lockLostCts.Dispose();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error disposing lock-lost CTS for '{LockId}'", id);
        }
    }
}
