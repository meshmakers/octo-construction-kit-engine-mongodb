using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoIdentityProviderStore
{
    Task<RtIdentityProvider?> GetAsync(string id);

    Task<IEnumerable<RtIdentityProvider>> GetAllAsync();

    Task StoreAsync(RtIdentityProvider identityProvider);
    Task RemoveAsync(string id);
}
