using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface ISystemContext
{
    IRepository OctoSystemDatabase { get; }
    Task<IOctoSession> StartSystemSessionAsync();

    Task CreateSystemDatabaseAsync();
    Task UpdateSystemSchemaAsync(IOctoSession systemSession);
    Task ClearSystemDatabaseAsync();
    Task DropSystemDatabaseAsync();
    Task<bool> IsSystemDatabaseExistingAsync();
    Task<bool> IsTenantExistingAsync(IOctoSession systemSession, string tenantId);

    Task<PagedResult<OctoTenant>> GetTenantsAsync(IOctoSession systemSession, int? skip = null, int? take = null);
    Task<OctoTenant> GetTenantAsync(IOctoSession systemSession, string tenantId);

    Task CreateTenantAsync(IOctoSession systemSession, string databaseName, string tenantId);
    Task AttachTenantAsync(IOctoSession systemSession, string databaseName, string tenantId);
    Task DetachTenantAsync(IOctoSession systemSession, string tenantId);
    Task ClearTenantAsync(IOctoSession systemSession, string tenantId);
    Task DropTenantAsync(IOctoSession systemSession, string tenantId);
    Task UpdateTenantSystemCkModelAsync(IOctoSession systemSession, string tenantId);
    Task ImportCkModelAsTextAsync(IOctoSession systemSession, string tenantId, ScopeIds scopeId, string jsonText);

    Task ImportCkModelAsync(IOctoSession systemSession, string tenantId, ScopeIds scopeId, string filePath,
        CancellationToken? cancellationToken);

    Task<TValueType?> GetConfigurationAsync<TValueType>(IOctoSession systemSession, string key, TValueType defaultValue)
        where TValueType : struct;

    Task<string?> GetConfigurationAsync(IOctoSession systemSession, string key, string? defaultValue = null);

    Task SetConfigurationAsync<TValueType>(IOctoSession systemSession, string key, TValueType value)
        where TValueType : struct;

    Task SetConfigurationAsync(IOctoSession systemSession, string key, string value);

    /// <summary>
    /// Gets or creates a tenant context for the given tenant id.
    /// </summary>
    /// <param name="tenantId">Identifier of tenant</param>
    /// <returns>Interface to access the content of the tenant</returns>
    Task<ITenantContext> CreateOrGetTenantContextAsync(string tenantId);

    bool TryGetCkCache(string tenantId, out ICkCache? ckCache);
}
