namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public interface ISystemMessageService
{
    Task DistributeTenantModificationPreEventAsync(string tenantId);
    Task DistributeTenantModificationPostEventAsync(string tenantId);
}