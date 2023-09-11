using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Persistence.Contracts;

public interface ITenantContext 
{
    string TenantId { get; }

    Task<IOctoSession> StartSystemSessionAsync();

    Task<ITenantContext> CreateChildTenantContextAsync(string tenantId);

    Task CreateChildTenantAsync(IOctoSession systemSession, string databaseName, string tenantId);

    Task AttachChildTenantAsync(IOctoSession systemSession, string databaseName, string tenantId);
    
    Task DetachChildTenantAsync(IOctoSession systemSession, string tenantId);
    
    Task ClearChildTenantAsync(IOctoSession systemSession, string tenantId);

    Task DropChildTenantAsync(IOctoSession systemSession, string tenantId);

    Task<bool> IsChildTenantExistingAsync(IOctoSession systemSession, string tenantId);

    Task<PagedResult<OctoTenant>> GetChildTenantsAsync(IOctoSession systemSession, int? skip = null,
        int? take = null);

    Task<OctoTenant> GetChildTenantAsync(IOctoSession systemSession, string tenantId);

    ITenantCkModelRepository CreateTenantCkModelRepository();

    ITenantRepository CreateOrGetTenantRepository();

    Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSession systemSession, string key,
        TValueType? defaultValue) where
        TValueType : class;

    Task<string?> GetConfigurationAsync(IOctoSession systemSession, string key, string? defaultValue = null);

    Task SetConfigurationAsync<TValueType>(IOctoSession systemSession, string key, TValueType value)
        where TValueType : struct;

    Task SetConfigurationAsync(IOctoSession systemSession, string key, string value);
    
    Task SetConfigurationAsync(IOctoSession systemSession, string key, object value);
}