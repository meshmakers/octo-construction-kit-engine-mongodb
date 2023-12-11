using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public interface ISystemMessageService : IHostedService
{
    Task DistributeTenantModificationPreEventAsync(string tenantId);
    Task DistributeTenantModificationPostEventAsync(string tenantId);
}