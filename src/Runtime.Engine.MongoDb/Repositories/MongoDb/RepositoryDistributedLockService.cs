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
    private bool _isLockAcquired;

    public async Task AcquireLockAsync()
    {
        var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        try
        {
            logger.LogInformation("Acquiring distributed lock '{LockId}'", id);

            var collection = repository.GetCollection(new LockMongoDataSourceMapper());

            var filter = Builders<SysLock>.Filter.And(
                Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id)
            );

            var update = Builders<SysLock>.Update
                .Set(sysLock => sysLock.Id, id)
                .Set(sysLock => sysLock.CreationDateTime, DateTime.UtcNow);

            SysLock? result = null;
            int attempt = 0;
            do
            {
                if (result != null)
                {
                    attempt++;
                    if (attempt >= MaxRetryAttempts)
                    {
                        throw new Exception(
                            $"Could not acquire distributed lock '{id}' after {MaxRetryAttempts * DelayMilliseconds} milliseconds.");
                    }

                    logger.LogInformation("Waiting for distributed lock '{LockId}'", id);
                    await Task.Delay(DelayMilliseconds);
                }

                result = await collection.FindOneAndUpsertAsync(session, filter, update, ReturnDocument.Before);
            } while (result != null);

            logger.LogInformation("Acquired distributed lock '{LockId}'", id);
            await session.CommitTransactionAsync();

            _isLockAcquired = true;
        }
        catch (MongoCommandException ex)
        {
            logger.LogError(ex, "MongoDB command error while acquiring distributed lock '{LockId}', code {Code}", id,
                ex.Code);
            await session.AbortTransactionAsync();
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error acquiring distributed lock '{LockId}'", id);
            await session.AbortTransactionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        logger.LogInformation("Disposing distributed lock service");

        if (_isLockAcquired)
        {
            logger.LogInformation("Releasing distributed lock on dispose");

            var session = await repositoryClient.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            try
            {
                logger.LogInformation("Releasing distributed lock");

                var collection = repository.GetCollection(new LockMongoDataSourceMapper());

                var filter = Builders<SysLock>.Filter.And(
                    Builders<SysLock>.Filter.Eq(sysLock => sysLock.Id, id)
                );
                await collection.DeleteOneAsync(session, filter);

                logger.LogInformation("Released distributed lock");
                await session.CommitTransactionAsync();

                _isLockAcquired = false;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error releasing distributed lock");
                await session.AbortTransactionAsync();
                throw;
            }
        }
    }
}
