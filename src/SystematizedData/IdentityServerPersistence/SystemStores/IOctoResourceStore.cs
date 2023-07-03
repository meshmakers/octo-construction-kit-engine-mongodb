using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoResourceStore : IResourceStore
{
    Task CreateApiResourceAsync(OctoApiResource apiResource);
    Task CreateIdentityResourceAsync(OctoIdentityResource identityResource);
    Task CreateApiScopeAsync(OctoApiScope apiScope);
    Task<OctoIdentityResource> GetOrCreateIdentityResourceAsync(IdentityResource identityResource);
    Task<OctoApiScope> TryCreateApiScopeAsync(ApiScope apiScope);
    Task<OctoApiResource> GetOrCreateApiResourceAsync(ApiResource apiResource);
    Task DeleteApiResourceAsync(OctoObjectId resourceId);
    Task DeleteIdentityResourceAsync(OctoObjectId resourceId);
    Task DeleteApiScopeAsync(OctoObjectId resourceId);

    Task<OctoApiResource?> GetApiResourceByNameAsync(string apiResourceName);
    Task<OctoIdentityResource?> GetIdentityResourceByNameAsync(string identityResourceName);
    Task UpdateApiScopeAsync(string name, OctoApiScope newApiScope);
    Task UpdateApiResourceAsync(string apiResourceName, OctoApiResource newApiResource);
}
