using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public interface IOctoIdentityProviderStore
{
    Task<OctoIdentityProvider?> GetAsync(string id);

    Task<IEnumerable<OctoIdentityProvider>> GetAllAsync();

    Task StoreAsync(OctoIdentityProvider identityProvider);
    Task RemoveAsync(string id);
}
