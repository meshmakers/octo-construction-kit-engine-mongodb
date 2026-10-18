using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
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
    private readonly IArchiveAuditTrail? _archiveAuditTrail;
    private readonly IOptions<CrateRecomputeOptions>? _recomputeOptions;
    private readonly TimeProvider? _timeProvider;

    // The factory is a singleton, the repositories it creates are not (one per tenant context, i.e.
    // per request / orchestrator tick). What a recompute remembers "per process" therefore lives here.
    private readonly RecomputeProcessState _recomputeProcessState = new();

    public CrateDbStreamDataRepositoryFactory(
        ILoggerFactory loggerFactory,
        ICkCacheService ckCacheService,
        IStreamDataDatabaseClient databaseClient,
        IStreamDataDatabaseManagementClient managementClient,
        IOptions<StreamDataConfiguration> configuration,
        IFormulaEngine formulaEngine,
        IArchiveAuditTrail? archiveAuditTrail = null,
        IOptions<CrateRecomputeOptions>? recomputeOptions = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _ckCacheService = ckCacheService;
        _databaseClient = databaseClient;
        _managementClient = managementClient;
        _configuration = configuration;
        _formulaEngine = formulaEngine;
        _archiveAuditTrail = archiveAuditTrail;
        _recomputeOptions = recomputeOptions;
    }

    public IStreamDataRepository Create(
        string tenantId,
        IArchiveRuntimeStore archiveStore,
        IRollupArchiveRuntimeStore? rollupArchiveStore = null,
        IArchiveRecomputeStateStore? recomputeStateStore = null)
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
            rollupArchiveStore,
            recomputeStateStore,
            _archiveAuditTrail,
            _recomputeOptions,
            _recomputeProcessState,
            _timeProvider);
    }

    /// <inheritdoc />
    public Task<StagingCopyReadiness> CheckStagingCopyReadinessAsync(CancellationToken cancellationToken = default)
        => StagingCopyReadinessProbe.CheckAsync(
            _loggerFactory.CreateLogger<CrateDbStreamDataRepositoryFactory>(),
            StagingCopyReadinessProbe.ClusterConnectionLabel,
            _databaseClient as ICrateCopyClient,
            cancellationToken);

    public async Task DeleteArchiveTablesAsync(string tenantId, IReadOnlyList<OctoObjectId> archiveRtIds)
    {
        foreach (var archiveRtId in archiveRtIds)
        {
            await ArchiveTableDrop.DropAsync(_managementClient, tenantId, archiveRtId.ToString(), _recomputeProcessState);
        }
    }
}
