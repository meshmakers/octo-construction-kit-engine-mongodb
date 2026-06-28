using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Default <see cref="IStreamDataRepositoryFactory"/> implementation that builds
/// <see cref="CrateDbStreamDataRepository"/> instances bound to a tenant id. Registered as a
/// singleton in the engine builder; per-tenant repositories are produced on demand.
/// </summary>
internal sealed class CrateDbStreamDataRepositoryFactory : IStreamDataRepositoryFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICkCacheService _ckCacheService;
    private readonly IStreamDataDatabaseClient _databaseClient;
    private readonly IStreamDataDatabaseManagementClient _managementClient;
    private readonly IOptions<StreamDataConfiguration> _configuration;
    private readonly IFormulaEngine _formulaEngine;

    public CrateDbStreamDataRepositoryFactory(
        ILoggerFactory loggerFactory,
        ICkCacheService ckCacheService,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IOptions<StreamDataConfiguration> configuration,
        IFormulaEngine formulaEngine)
    {
        _loggerFactory = loggerFactory;
        _ckCacheService = ckCacheService;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _configuration = configuration;
        _formulaEngine = formulaEngine;
    }

    public IStreamDataRepository Create(
        string tenantId,
        IArchiveRuntimeStore archiveStore,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null)
    {
        return new CrateDbStreamDataRepository(
            _loggerFactory.CreateLogger<CrateDbStreamDataRepository>(),
            _ckCacheService,
            _databaseClient,
            _managementClient,
            _configuration,
            tenantId,
            archiveStore,
            _formulaEngine,
            rollupArchiveStore);
    }
}
