using System.Threading.Tasks;
using Duende.IdentityServer.Stores;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public interface IOctoPersistentGrantStore : IPersistedGrantStore
{
    /// <summary>
    ///     Method to clear expired persisted grants.
    /// </summary>
    /// <returns></returns>
    public Task RemoveExpiredGrantsAsync();
}
