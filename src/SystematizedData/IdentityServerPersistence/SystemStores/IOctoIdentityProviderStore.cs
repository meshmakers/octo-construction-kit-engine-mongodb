using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoIdentityProviderStore
{
    Task<OctoIdentityProvider?> GetAsync(string id);

    Task<IEnumerable<OctoIdentityProvider>> GetAllAsync();

    Task StoreAsync(OctoIdentityProvider identityProvider);
    Task RemoveAsync(string id);
}
