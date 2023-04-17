using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public interface IOctoResourceStore : IResourceStore
{
    Task CreateApiResourceAsync(OctoApiResource apiResource);
    Task CreateIdentityResourceAsync(OctoIdentityResource identityResource);
    Task CreateApiScopeAsync(OctoApiScope apiScope);
    Task<OctoIdentityResource> GetOrCreateIdentityResourceAsync(IdentityResource identityResource);
    Task<OctoApiScope> TryCreateApiScopeAsync(ApiScope apiScope);
    Task<OctoApiResource> GetOrCreateApiResourceAsync(ApiResource apiResource);
    Task DeleteApiResourceAsync(ObjectId resourceId);
    Task DeleteIdentityResourceAsync(ObjectId resourceId);
    Task DeleteApiScopeAsync(ObjectId resourceId);

    Task<OctoApiResource?> GetApiResourceByNameAsync(string apiResourceName);
    Task<OctoIdentityResource?> GetIdentityResourceByNameAsync(string identityResourceName);
    Task UpdateApiScopeAsync(string name, OctoApiScope newApiScope);
    Task UpdateApiResourceAsync(string apiResourceName, OctoApiResource newApiResource);
}
