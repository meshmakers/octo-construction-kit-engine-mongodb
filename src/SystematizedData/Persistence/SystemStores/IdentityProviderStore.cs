using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class IdentityProviderStore : IOctoIdentityProviderStore
{
    private readonly ICachedCollection<OctoIdentityProvider> _providerCollection;
    private readonly IRepository _repository;

    public IdentityProviderStore(ISystemContext systemContext)
    {
        _repository = systemContext.OctoSystemDatabase;

        _providerCollection = _repository.GetCollection<OctoIdentityProvider>();
    }

    public async Task<OctoIdentityProvider?> GetAsync(string id)
    {
        ArgumentValidation.ValidateString(nameof(id), id);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _providerCollection.DocumentAsync(session, id);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task<IEnumerable<OctoIdentityProvider>> GetAllAsync()
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _providerCollection.GetAsync(session);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task StoreAsync(OctoIdentityProvider identityProvider)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var persistentProvider = await GetAsync(identityProvider.Alias);
        if (persistentProvider == null)
        {
            await _providerCollection.InsertAsync(session, identityProvider);
        }
        else
        {
            await _providerCollection.ReplaceByIdAsync(session, identityProvider.Id, identityProvider);
        }

        await session.CommitTransactionAsync();
    }

    public async Task RemoveAsync(string id)
    {
        ArgumentValidation.ValidateString(nameof(id), id);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        await _providerCollection.DeleteOneAsync(session, id);

        await session.CommitTransactionAsync();
    }
}
