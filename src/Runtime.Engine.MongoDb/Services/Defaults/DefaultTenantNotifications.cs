using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;

public class DefaultTenantNotifications : ITenantNotifications
{
    public Task NotifyPreTenantCreateAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantCreateAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantDeleteAsync(string tenantId, Guid correlationId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }
}