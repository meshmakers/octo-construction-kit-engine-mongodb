using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.Contracts;

namespace Persistence.InternalContracts;

public interface ITenantContextInternal : ITenantContext
{
    Task<ITenantRepositoryInternal> CreateOrGetTenantRepositoryInternalAsync(string tenantId);
}