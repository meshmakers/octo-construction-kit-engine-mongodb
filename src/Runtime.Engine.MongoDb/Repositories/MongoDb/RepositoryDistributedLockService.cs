using Meshmakers.Octo.Runtime.Contracts;
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

    // Lock TTL: Nach dieser Zeit wird der Lock als "stale" betrachtet und kann übernommen werden
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromMinutes(10);

    // Heartbeat interval für lang laufende Operations
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

                // Versuche, einen neuen Lock zu erstellen oder einen abgelaufenen zu übernehmen
                var filter = Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id);
                var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                session.StartTransaction();
                var existingLocks = await collection.FindManyAsync(session, filter, null, null, 1);
                await session.CommitTransactionAsync();
                var existingLock = existingLocks.FirstOrDefault();

                if (existingLock == null)
                {
                    // Kein Lock existiert - versuche ihn zu erstellen
                    var newLock = new SysLock
                    {
                        Id = id, CreationDateTime = now, ExpiryDateTime = expiryTime, LastHeartbeat = now
                    };

                    try
                    {
                        session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                        session.StartTransaction();
                        await collection.InsertOneAsync(session, newLock);
                        await session.CommitTransactionAsync();
                        lockAcquired = true;
                    }
                    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        // Race condition: Ein anderer Service hat den Lock gerade erstellt
                        logger.LogDebug("Lock '{LockId}' was just acquired by another service, retrying...", id);
                    }
                }
                else if (existingLock.ExpiryDateTime.HasValue && existingLock.ExpiryDateTime.Value < now)
                {
                    // Lock ist abgelaufen - versuche ihn zu übernehmen
                    logger.LogWarning(
                        "Found stale lock '{LockId}' (expired at {ExpiryDateTime}, last heartbeat at {LastHeartbeat}), attempting to claim it",
                        id, existingLock.ExpiryDateTime, existingLock.LastHeartbeat);

                    var replaceFilter = Builders<SysLock>.Filter.And(
                        Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id),
                        Builders<SysLock>.Filter.Lt(sysLock => sysLock.ExpiryDateTime, now) // Nur wenn noch abgelaufen
                    );

                    var updatedLock = new SysLock
                    {
                        Id = id, CreationDateTime = now, ExpiryDateTime = expiryTime, LastHeartbeat = now
                    };

                    session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
                    session.StartTransaction();
                    var modifiedCount =
                        await collection.ReplaceOneWithModificationCountAsync(session, replaceFilter, updatedLock);
                    await session.CommitTransactionAsync();

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

            // Starte Heartbeat für lang laufende Operations
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
            // Erstelle eine neue Session für das Heartbeat-Update
            var heartbeatSession = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
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
                await heartbeatSession.AbortTransactionAsync();
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

        // Stoppe Heartbeat zuerst
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

                var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
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
                    // WICHTIG: Keine Exception werfen in Dispose! Nur loggen.
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Critical error disposing distributed lock '{LockId}'", id);
                // WICHTIG: Keine Exception werfen in Dispose! Nur loggen.
            }
        }
    }
}
