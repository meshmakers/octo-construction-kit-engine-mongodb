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
    : IAsyncDisposable
{
    private const int MaxRetryAttempts = 60;
    private const int DelayMilliseconds = 2000;

    // Lock TTL: After this time the lock is considered "stale" and can be claimed by another service
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromMinutes(10);

    // Heartbeat interval for long-running operations
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    private bool _isLockAcquired;
    private readonly Lock _lockObject = new();
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    public async Task AcquireLockAsync()
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
            logger.LogInformation("Acquiring distributed lock '{LockId}'", id);

            var collection = repository.GetCollection(new LockMongoDataSourceMapper());

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
                        Id = id, CreationDateTime = now, ExpiryDateTime = expiryTime, LastHeartbeat = now
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
                        "Found stale lock '{LockId}' (expired at {ExpiryDateTime}, last heartbeat at {LastHeartbeat}), attempting to claim it",
                        id, existingLock.ExpiryDateTime, existingLock.LastHeartbeat);

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
                        Id = id, CreationDateTime = now, ExpiryDateTime = expiryTime, LastHeartbeat = now
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
                        "Lock '{LockId}' is held by another service, expires in {RemainingSeconds:F0} seconds (at {ExpiryDateTime:O})",
                        id, remainingTime.TotalSeconds, existingLock.ExpiryDateTime.Value);
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
                    await Task.Delay(DelayMilliseconds);
                }
            }

            logger.LogInformation("Acquired distributed lock '{LockId}'", id);

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

                var filter = Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id);
                var update = Builders<SysLock>.Update
                    .Set(sysLock => sysLock.LastHeartbeat, now)
                    .Set(sysLock => sysLock.ExpiryDateTime, newExpiryTime);

                await collection.GetMongoCollection()
                    .UpdateOneAsync(((IOctoSessionInternal)heartbeatSession).SessionHandle, filter, update);

                await heartbeatSession.CommitTransactionAsync();

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
            try
            {
                logger.LogInformation("Releasing distributed lock '{LockId}' on dispose", id);

                using var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                session.StartTransaction();
                try
                {
                    var collection = repository.GetCollection(new LockMongoDataSourceMapper());

                    var filter = Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id);
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
    }
}
