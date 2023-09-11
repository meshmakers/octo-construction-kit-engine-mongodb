using Duende.IdentityServer.Models;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.Contracts;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class ClientStore : IOctoClientStore
{
    private readonly ICachedCollection<OctoClient> _clientCollection;
    private readonly IRepository _repository;

    public ClientStore(ITenantContext tenantContext)
    {
        tenantContext.CreateOrGetTenantRepositoryAsync()
        _repository = systemContext.SystemDatabase;
        _clientCollection = _repository.GetCollection<OctoClient>();
    }

    public async Task CreateAsync(RtSystemIdentityClient octoClient)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        await _clientCollection.InsertAsync(session, octoClient);

        await session.CommitTransactionAsync();
    }

    public async Task DeleteAsync(string clientId)
    {
        ArgumentValidation.ValidateString(nameof(clientId), clientId);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var client = await GetClientByClientId(session, clientId);
        if (client == null)
        {
            throw new EntityNotFoundException($"Client id '{clientId}' does not exist.");
        }

        await _clientCollection.DeleteOneAsync(session, client.Id);

        await session.CommitTransactionAsync();
    }

    public async Task<Client> FindClientByIdAsync(string clientId)
    {
        ArgumentValidation.ValidateString(nameof(clientId), clientId);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _clientCollection.FindSingleOrDefaultAsync(session, x => x.ClientId == clientId);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task<IEnumerable<RtSystemIdentityClient>> GetClients()
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _clientCollection.GetAsync(session);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task UpdateAsync(string clientId, RtSystemIdentityClient client)
    {
        ArgumentValidation.ValidateString(nameof(clientId), clientId);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var dbClient = await GetClientByClientId(session, clientId);
        if (dbClient == null)
        {
            throw new EntityNotFoundException($"Client id '{clientId}' does not exist.");
        }

        await _clientCollection.ReplaceByIdAsync(session, dbClient.Id, client);

        await session.CommitTransactionAsync();
    }

    private async Task<RtSystemIdentityClient> GetClientByClientId(IOctoSession session, string clientId)
    {
        var client = await _clientCollection.FindSingleOrDefaultAsync(session, x => x.ClientId == clientId);
        return client;
    }
}
