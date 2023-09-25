using AutoMapper;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class IdentityProviderStore : IOctoIdentityProviderStore
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IMapper _mapper;

    public IdentityProviderStore(ITenantRepository tenantRepository, IMapper mapper)
    {
        _tenantRepository = tenantRepository;
        _mapper = mapper;
    }

    public async Task<RtIdentityProvider?> GetAsync(string id)
    {
        ArgumentValidation.ValidateString(nameof(id), id);

        var session = await _tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var result = await _tenantRepository.GetRtEntityByRtIdAsync<RtIdentityProvider>(session, new OctoObjectId(id));

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task<IEnumerable<RtIdentityProvider>> GetAllAsync()
    {
        var session = await _tenantRepository.GetSessionAsync();
        session.StartTransaction();

        DataQueryOperation dataQueryOperation = new();
       
        var result = await _tenantRepository.GetRtEntitiesByTypeAsync<RtIdentityProvider>(session, dataQueryOperation);
        await session.CommitTransactionAsync();
        
        return result.Items;
    }

    public async Task StoreAsync(RtIdentityProvider identityProvider)
    {
        var session = await _tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var result = await _tenantRepository.GetRtEntityByRtIdAsync<RtIdentityProvider>(session, identityProvider.RtId);
        if (result == null)
        {
            await _tenantRepository.InsertOneRtEntityAsync(session, identityProvider);
        }
        else
        {
            await _tenantRepository.ReplaceOneRtEntityByIdAsync(session, identityProvider.RtId, identityProvider);
        }

        await session.CommitTransactionAsync();
    }

    public async Task RemoveAsync(string id)
    {
        ArgumentValidation.ValidateString(nameof(id), id);

        var session = await _tenantRepository.GetSessionAsync();
        session.StartTransaction();

        await _tenantRepository.DeleteOneRtEntityByRtIdAsync<RtIdentityProvider>(session, new OctoObjectId(id));

        await session.CommitTransactionAsync();
    }
}
