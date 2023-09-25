using Duende.IdentityServer.Stores;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoClientStore : IClientStore
{
    Task<IEnumerable<RtClient>> GetClients();

    Task CreateAsync(RtClient client);

    Task UpdateAsync(string clientId, RtClient client);

    Task DeleteAsync(string clientId);
}
