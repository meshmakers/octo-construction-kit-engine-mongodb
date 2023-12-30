using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public class DefaultTenantNotifications : ITenantNotifications
{
    public Task NotifyPreTenantCreateAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantCreateAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantUpdateAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantUpdateAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPreTenantDeleteAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }

    public Task NotifyPosTenantDeleteAsync(string tenantId)
    {
        // Intentionally left blank
        return Task.CompletedTask;
    }
}