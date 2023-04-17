using System.Collections.Generic;
using System.Threading.Tasks;
using Duende.IdentityServer.Stores;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public interface IOctoClientStore : IClientStore
{
    Task<IEnumerable<OctoClient>> GetClients();

    Task CreateAsync(OctoClient client);

    Task UpdateAsync(string clientId, OctoClient client);

    Task DeleteAsync(string clientId);
}
