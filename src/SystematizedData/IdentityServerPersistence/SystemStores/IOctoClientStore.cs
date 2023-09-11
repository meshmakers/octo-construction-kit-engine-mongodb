using Duende.IdentityServer.Stores;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoClientStore : IClientStore
{
    Task<IEnumerable<RtSystemIdentityClient>> GetClients();

    Task CreateAsync(RtSystemIdentityClient client);

    Task UpdateAsync(string clientId, RtSystemIdentityClient client);

    Task DeleteAsync(string clientId);
}
