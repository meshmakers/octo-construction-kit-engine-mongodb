using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Persistence.DataAccess;
using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using Meshmakers.Octo.Backend.Persistence.MongoDb;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using MongoDB.Driver;
using NLog;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class PersistentGrantStore : IOctoPersistentGrantStore
{
    private const int TokenCleanupBatchSize = 50;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ICachedCollection<OctoPersistedGrant> _persistentGrantCollection;
    private readonly IRepository _repository;

    public PersistentGrantStore(ISystemContext systemContext)
    {
        _repository = systemContext.SystemDatabase;
        _persistentGrantCollection = _repository.GetCollection<OctoPersistedGrant>();
    }

    public async Task StoreAsync(PersistedGrant grant)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var persistedGrant = (OctoPersistedGrant)await GetAsync(session, grant.Key);
        if (persistedGrant == null)
        {
            var appGrant = GetApplicationPersistedGrant(grant);

            await _persistentGrantCollection.InsertAsync(session, appGrant);
        }
        else
        {
            var appGrant = GetApplicationPersistedGrant(grant);

            await _persistentGrantCollection.ReplaceByIdAsync(session, persistedGrant.Id, appGrant);
        }

        await session.CommitTransactionAsync();
    }

    public async Task<PersistedGrant> GetAsync(string key)
    {
        ArgumentValidation.ValidateString(nameof(key), key);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _persistentGrantCollection.FindSingleOrDefaultAsync(session, x => x.Key == key);

        await session.CommitTransactionAsync();
        return result;
    }


    public async Task<IEnumerable<PersistedGrant>> GetAllAsync(PersistedGrantFilter filter)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _persistentGrantCollection.FindManyAsync(session,
            grant => grant.SubjectId == filter.SubjectId && grant.SessionId == filter.SessionId &&
                     grant.ClientId == filter.ClientId && grant.Type == filter.Type);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task RemoveAsync(string key)
    {
        ArgumentValidation.ValidateString(nameof(key), key);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        await _persistentGrantCollection.DeleteOneAsync(session, x => x.Key == key);

        await session.CommitTransactionAsync();
    }

    public async Task RemoveAllAsync(PersistedGrantFilter filter)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        await _persistentGrantCollection.DeleteOneAsync(session, grant =>
            grant.SubjectId == filter.SubjectId && grant.SessionId == filter.SessionId &&
            grant.ClientId == filter.ClientId && grant.Type == filter.Type);

        await session.CommitTransactionAsync();
    }

    /// <summary>
    ///     Method to clear expired persisted grants.
    /// </summary>
    /// <returns></returns>
    public async Task RemoveExpiredGrantsAsync()
    {
        try
        {
            Logger.Trace("Querying for expired grants to remove");

            var session = await _repository.StartSessionAsync();
            session.StartTransaction();

            await RemoveGrantsAsync(session);

            await session.CommitTransactionAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Exception removing expired grants: {exception}", ex.Message);
        }
    }

    private static OctoPersistedGrant GetApplicationPersistedGrant(PersistedGrant grant)
    {
        var appGrant = grant as OctoPersistedGrant;
        if (appGrant == null)
        {
            appGrant = new OctoPersistedGrant
            {
                ClientId = grant.ClientId,
                Key = grant.Key,
                Data = grant.Data,
                SubjectId = grant.SubjectId,
                Type = grant.Type,
                CreationTime = grant.CreationTime,
                Expiration = grant.Expiration
            };
        }

        return appGrant;
    }

    public async Task RemoveAllAsync(string subjectId, string clientId, string type)
    {
        ArgumentValidation.ValidateString(nameof(subjectId), subjectId);
        ArgumentValidation.ValidateString(nameof(clientId), clientId);
        ArgumentValidation.ValidateString(nameof(type), type);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        await _persistentGrantCollection.DeleteOneAsync(session, grant =>
            grant.SubjectId == subjectId && grant.ClientId == clientId && grant.Type == type);

        await session.CommitTransactionAsync();
    }

    public async Task<PersistedGrant> GetAsync(IOctoSession session, string key)
    {
        return await _persistentGrantCollection.FindSingleOrDefaultAsync(session, x => x.Key == key);
    }

    /// <summary>
    ///     Removes the stale persisted grants.
    /// </summary>
    /// <returns></returns>
    private async Task RemoveGrantsAsync(IOctoSession session)
    {
        var found = int.MaxValue;

        while (found >= TokenCleanupBatchSize)
        {
            var query = await _persistentGrantCollection.FindManyAsync(session,
                grant => grant.Expiration < DateTime.UtcNow,
                0, TokenCleanupBatchSize);
            var expiredGrants = query.OrderBy(x => x.Key)
                .ToList();

            found = expiredGrants.Count;
            Logger.Info($"Removing {found} grants");

            if (found > 0)
            {
                try
                {
                    foreach (var persistedGrant in expiredGrants)
                    {
                        await _persistentGrantCollection.DeleteOneAsync(session, persistedGrant.Id);
                    }
                }
                catch (MongoException ex)
                {
                    // we get this if/when someone else already deleted the records
                    // we want to essentially ignore this, and keep working
                    Logger.Debug($"Concurrency exception removing expired grants: {ex.Message}");
                }
            }
        }
    }
}
