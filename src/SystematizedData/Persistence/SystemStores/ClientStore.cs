using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class ClientStore : IOctoClientStore
{
    private readonly ICachedCollection<OctoClient> _clientCollection;
    private readonly IRepository _repository;

    public ClientStore(ISystemContext systemContext)
    {
        _repository = systemContext.OctoSystemDatabase;
        _clientCollection = _repository.GetCollection<OctoClient>();
    }

    public async Task CreateAsync(OctoClient octoClient)
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

    public async Task<Client?> FindClientByIdAsync(string clientId)
    {
        ArgumentValidation.ValidateString(nameof(clientId), clientId);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _clientCollection.FindSingleOrDefaultAsync(session, x => x.ClientId == clientId);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task<IEnumerable<OctoClient>> GetClients()
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _clientCollection.GetAsync(session);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task UpdateAsync(string clientId, OctoClient client)
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

    private async Task<OctoClient> GetClientByClientId(IOctoSession session, string clientId)
    {
        var client = await _clientCollection.FindSingleOrDefaultAsync(session, x => x.ClientId == clientId);
        return client;
    }

    public async Task<IReadOnlyCollection<string>> GetKnownOriginsAsync()
    {
        var clients = await GetClients();
        var origins = clients.SelectMany(x => x.AllowedCorsOrigins);
        return new List<string>(origins);
    }
}
