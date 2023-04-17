using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class ResourceStore : IOctoResourceStore
{
    private readonly ICachedCollection<OctoApiResource> _apiResourceCollection;
    private readonly ICachedCollection<OctoApiScope> _apiScopeCollection;
    private readonly ICachedCollection<OctoIdentityResource> _identityResourceCollection;
    private readonly IRepository _repository;

    public ResourceStore(ISystemContext systemContext)
    {
        _repository = systemContext.OctoSystemDatabase;

        _apiResourceCollection = _repository.GetCollection<OctoApiResource>();
        _identityResourceCollection = _repository.GetCollection<OctoIdentityResource>();
        _apiScopeCollection = _repository.GetCollection<OctoApiScope>();
    }

    public async Task CreateApiResourceAsync(OctoApiResource apiResource)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _apiResourceCollection.InsertAsync(session, apiResource);

            await session.CommitTransactionAsync();
        }
    }

    public async Task CreateIdentityResourceAsync(OctoIdentityResource identityResource)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _identityResourceCollection.InsertAsync(session, identityResource);

            await session.CommitTransactionAsync();
        }
    }

    public async Task CreateApiScopeAsync(OctoApiScope apiScope)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _apiScopeCollection.InsertAsync(session, apiScope);

            await session.CommitTransactionAsync();
        }
    }

    public async Task<OctoApiResource> GetOrCreateApiResourceAsync(ApiResource apiResource)
    {
        var res = await GetApiResourceByNameAsync(apiResource.Name);
        if (res == null)
        {
            res = new OctoApiResource
            {
                Description = apiResource.Description,
                Name = apiResource.Name,
                Enabled = apiResource.Enabled,
                DisplayName = apiResource.DisplayName,
                ShowInDiscoveryDocument = apiResource.ShowInDiscoveryDocument,
                Properties = new Dictionary<string, string>(apiResource.Properties),
                UserClaims = new List<string>(apiResource.UserClaims),
                Scopes = apiResource.Scopes
            };

            await CreateApiResourceAsync(res);
        }

        return res;
    }


    public async Task<OctoIdentityResource> GetOrCreateIdentityResourceAsync(IdentityResource identityResource)
    {
        var res = await GetIdentityResourceByNameAsync(identityResource.Name);
        if (res == null)
        {
            res = new OctoIdentityResource
            {
                Description = identityResource.Description,
                Name = identityResource.Name,
                Enabled = identityResource.Enabled,
                DisplayName = identityResource.DisplayName,
                Emphasize = identityResource.Emphasize,
                Required = identityResource.Required,
                ShowInDiscoveryDocument = identityResource.ShowInDiscoveryDocument,
                Properties = new Dictionary<string, string>(identityResource.Properties),
                UserClaims = new List<string>(identityResource.UserClaims)
            };

            await CreateIdentityResourceAsync(res);
        }

        return res;
    }

    public async Task<OctoApiScope> TryCreateApiScopeAsync(ApiScope apiScope)
    {
        var res = (await FindApiScopesByNameAsync(new[] { apiScope.Name })).ToArray();
        if (!res.Any())
        {
            var dbApiScope = new OctoApiScope
            {
                Description = apiScope.Description,
                Name = apiScope.Name,
                Enabled = apiScope.Enabled,
                DisplayName = apiScope.DisplayName,
                Emphasize = apiScope.Emphasize,
                Required = apiScope.Required,
                ShowInDiscoveryDocument = apiScope.ShowInDiscoveryDocument,
                Properties = new Dictionary<string, string>(apiScope.Properties),
                UserClaims = new List<string>(apiScope.UserClaims)
            };

            await CreateApiScopeAsync(dbApiScope);

            return dbApiScope;
        }

        return (OctoApiScope)res.First();
    }


    public async Task DeleteApiResourceAsync(ObjectId resourceId)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _apiResourceCollection.DeleteOneAsync(session, resourceId);
            
            await session.CommitTransactionAsync();
        }
    }

    public async Task DeleteIdentityResourceAsync(ObjectId resourceId)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _identityResourceCollection.DeleteOneAsync(session, resourceId);
            
            await session.CommitTransactionAsync();

        }
    }

    public async Task DeleteApiScopeAsync(ObjectId resourceId)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            await _apiScopeCollection.DeleteOneAsync(session, resourceId);
            
            await session.CommitTransactionAsync();
        }
    }

    public async Task<OctoApiResource?> GetApiResourceByNameAsync(string apiResourceName)
    {
        ArgumentValidation.ValidateString(nameof(apiResourceName), apiResourceName);

        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result =
                await _apiResourceCollection.FindSingleOrDefaultAsync(session, x => x.Name == apiResourceName);

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task<OctoIdentityResource?> GetIdentityResourceByNameAsync(string identityResourceName)
    {
        ArgumentValidation.ValidateString(nameof(identityResourceName), identityResourceName);

        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result =
                await _identityResourceCollection.FindSingleOrDefaultAsync(session,
                    x => x.Name == identityResourceName);

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task UpdateApiScopeAsync(string name, OctoApiScope newApiScope)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();
            
            var apiScope = (await FindApiScopesByNameAsync(new []{name})).FirstOrDefault() as OctoApiScope;
            if (apiScope == null)
            {
                throw new EntityNotFoundException($"API scope with name '{name}' does not exist.");
            }

            await _apiScopeCollection.ReplaceByIdAsync(session, apiScope.Id, newApiScope);

            await session.CommitTransactionAsync();
        }
    }

    public async Task UpdateApiResourceAsync(string apiResourceName, OctoApiResource newApiResource)
    {
        ArgumentValidation.ValidateString(nameof(apiResourceName), apiResourceName);

        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();
            
            var apiResource = (await FindApiResourcesByNameAsync(new []{apiResourceName})).FirstOrDefault() as OctoApiResource;
            if (apiResource == null)
            {
                throw new EntityNotFoundException($"API resource with name '{apiResourceName}' does not exist.");
            }

            await _apiResourceCollection.ReplaceByIdAsync(session, apiResource.Id, newApiResource);
            
            await session.CommitTransactionAsync();
        }
    }


    public async Task<IEnumerable<IdentityResource>> FindIdentityResourcesByScopeNameAsync(
        IEnumerable<string> scopeNames)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result = await _identityResourceCollection.FindManyAsync(session, x => scopeNames.Contains(x.Name));

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task<IEnumerable<ApiScope>> FindApiScopesByNameAsync(IEnumerable<string> scopeNames)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result = await _apiScopeCollection.FindManyAsync(session, x => scopeNames.Contains(x.Name));

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task<IEnumerable<ApiResource>> FindApiResourcesByScopeNameAsync(IEnumerable<string> scopeNames)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result =
                // ReSharper disable once ConvertClosureToMethodGroup
                await _apiResourceCollection.FindManyAsync(session, api => api.Scopes.Any(s=> scopeNames.Contains(s)));

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task<IEnumerable<ApiResource>> FindApiResourcesByNameAsync(IEnumerable<string> apiResourceNames)
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var result =
                await _apiResourceCollection.FindManyAsync(session, x => apiResourceNames.Contains(x.Name));

            await session.CommitTransactionAsync();

            return result;
        }
    }

    public async Task<Resources> GetAllResourcesAsync()
    {
        using (var session = await _repository.StartSessionAsync())
        {
            session.StartTransaction();

            var identityResources = await _identityResourceCollection.GetAsync(session);
            var apiResources = await _apiResourceCollection.GetAsync(session);
            var apiScopes = await _apiScopeCollection.GetAsync(session);

            await session.CommitTransactionAsync();

            return new Resources(identityResources, apiResources, apiScopes);
        }
    }
}
