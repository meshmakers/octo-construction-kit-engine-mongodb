using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;

/// <summary>
/// MongoDB implementation of <see cref="IRuntimeRepositoryProvider"/> that retrieves
/// runtime repositories for tenants using the system context.
/// </summary>
/// <remarks>
/// This implementation uses the existing <see cref="ISystemContext"/> infrastructure
/// to retrieve tenant repositories, leveraging the built-in caching mechanisms
/// and multi-tenant architecture.
/// </remarks>
public class MongoRuntimeRepositoryProvider : IRuntimeRepositoryProvider
{
    private readonly ISystemContext _systemContext;
    private readonly ILogger<MongoRuntimeRepositoryProvider> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="MongoRuntimeRepositoryProvider"/>
    /// </summary>
    /// <param name="systemContext">The system context for accessing tenant repositories</param>
    /// <param name="logger">Logger instance</param>
    public MongoRuntimeRepositoryProvider(
        ISystemContext systemContext,
        ILogger<MongoRuntimeRepositoryProvider> logger)
    {
        _systemContext = systemContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IRuntimeRepository?> GetRepositoryAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            var repository = await _systemContext.TryFindTenantRepositoryAsync(tenantId);

            if (repository == null)
            {
                _logger.LogDebug("No repository found for tenant {TenantId}", tenantId);
            }

            return repository;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving repository for tenant {TenantId}", tenantId);
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsRepositoryAvailable(string tenantId)
    {
        // We need to check synchronously, so we try to find the tenant context first
        // This is a quick check that doesn't require async database operations
        try
        {
            var task = _systemContext.TryFindTenantContextAsync(tenantId);
            task.Wait();
            return task.Result != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetSchemaVersionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, string>();

        try
        {
            var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
            if (tenantContext == null)
            {
                _logger.LogDebug("No tenant context found for tenant {TenantId}", tenantId);
                return versions;
            }

            var repository = tenantContext.GetTenantRepository();
            var session = await repository.GetSessionAsync().ConfigureAwait(false);

            // Get all available CK models from the schema
            var queryOptions = RtEntityQueryOptions.Create();
            var ckModels = await repository.GetCkModelsAsync(session, null, queryOptions).ConfigureAwait(false);

            foreach (var model in ckModels.Items)
            {
                // Only include available models (not importing or failed)
                if (model.ModelState == ModelState.Available)
                {
                    // Use the version from the CkModelId
                    versions[model.Id.Name] = model.Id.Version.ToString();
                    _logger.LogDebug("Found schema version {Version} for CK model {ModelName} in tenant {TenantId}",
                        model.Id.Version, model.Id.Name, tenantId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting schema versions for tenant {TenantId}", tenantId);
        }

        return versions;
    }
}
