namespace Persistence.InternalContracts;

public interface ISystemMessageService
{
    Task DistributeTenantModificationPreEventAsync(string tenantId);
    Task DistributeTenantModificationPostEventAsync(string tenantId);
}