using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal sealed class MongoDbRepositoryDataSource : RepositoryDataSource, IMongoDbRepositoryDataSource,
    ICkModelImportLockScope
{
    /// <summary>Prefix of the lock id of a CK model import lock; the model name follows it.</summary>
    internal const string ModelImportLockIdPrefix = "ck_model_import_";

    /// <summary>The distributed lock every index update of the tenant takes (CK import, update, tiebreak upgrade).</summary>
    internal const string IndexUpdateLockId = "index_update_lock";

    /// <summary>
    ///     AB#9302: how long a section of the tiebreak index upgrade (after the first) waits for
    ///     <see cref="IndexUpdateLockId" />: it yielded the lock to a CK import between two collections and waits until
    ///     that import's index update is done, not the 90 s a CK import waits.
    /// </summary>
    internal static readonly TimeSpan UpgradeSectionMaxWait = TimeSpan.FromHours(1);

    /// <summary>
    ///     AB#9302: after a section, while a waiter marker is fresh, the upgrade gives the waiter this long to take the
    ///     lock (it polls once per second) before it acquires the lock for the next collection itself.
    /// </summary>
    internal static readonly TimeSpan YieldToWaiterMaxWait = TimeSpan.FromSeconds(3);

    private readonly IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> _ckTypeInheritances;
    private readonly IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> _ckTypes;
    private readonly IRepositoryInternal _repository;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly IRepositoryClient _repositoryClient;
    private readonly ILogger<MongoDbRepositoryDataSource> _logger;
    private readonly IndexStateService _indexStateService;
    private readonly ICkCacheService? _ckCacheService;
    private readonly Lazy<string> _importLockScopeKey;

    public MongoDbRepositoryDataSource(ILogger<MongoDbRepositoryDataSource> logger,
        IUserRepositoryAccess repositoryAccess, string databaseName,
        string tenantId, ICkCacheService? ckCacheService = null)
        : this(logger, repositoryAccess.GetRepositoryClient(databaseName), databaseName, tenantId, ckCacheService)
    {
    }

    /// <param name="logger">Logger</param>
    /// <param name="repositoryClient">Repository client of the tenant database</param>
    /// <param name="databaseName">Tenant database name</param>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="ckCacheService">
    ///     CK cache used to normalise legacy strings in Secret slots on every runtime-entity collection
    ///     read (AB#5533). Null disables the normalisation (tooling without a CK cache).
    /// </param>
    internal MongoDbRepositoryDataSource(ILogger<MongoDbRepositoryDataSource> logger,
        IRepositoryClient repositoryClient, string databaseName,
        string tenantId, ICkCacheService? ckCacheService = null)
        : base(tenantId, new MongoLinkedBinaryDataSource(repositoryClient, databaseName))
    {
        _logger = logger;
        _ckCacheService = ckCacheService;
        ArgumentValidation.ValidateString(databaseName, nameof(databaseName));

        _repositoryClient = repositoryClient;
        _repository = (IRepositoryInternal)_repositoryClient.GetRepository(databaseName);

        CkModels = _repository.GetCollection(new CkModelMongoDataSourceMapper());

        _ckTypes = _repository.GetCollection(new CkTypeMongoDataSourceMapper());
        CkTypes = _ckTypes;

        CkRecords = _repository.GetCollection(new CkRecordMongoDataSourceMapper());

        CkEnums = _repository.GetCollection(new CkEnumMongoDataSourceMapper());
        CkAttributes = _repository.GetCollection(new CkAttributeMongoDataSourceMapper());

        CkAssociationRoles = _repository.GetCollection(new CkAssociationRoleMongoDataSourceMapper());
        CkTypeAssociations = _repository.GetCollection(new CkTypeAssociationMongoDataSourceMapper());

        _ckTypeInheritances = _repository.GetCollection(new CkTypeInheritanceMongoDataSourceMapper());
        CkTypeInheritances = _ckTypeInheritances;

        CkRecordInheritances = _repository.GetCollection(new CkRecordInheritanceMongoDataSourceMapper());

        // CK v2 (AB#5667)
        CkInterfaces = _repository.GetCollection(new CkInterfaceMongoDataSourceMapper());
        CkTypeInterfaceImplementations =
            _repository.GetCollection(new CkTypeInterfaceImplementationMongoDataSourceMapper());

        RtMongoDbDataSourceAssociations = _repository.GetCollection(new RtAssociationMongoDataSourceMapper());

        // Initialize index state service
        _indexStateService = new IndexStateService(_ckTypes, logger);

        _importLockScopeKey = new Lazy<string>(() =>
        {
            var database = _repository.GetCollection(new LockMongoDataSourceMapper()).GetMongoCollection().Database;
            var servers = string.Join(",", database.Client.Settings.Servers.Select(server => server.ToString()));
            return $"{servers}/{database.DatabaseNamespace.DatabaseName}";
        });
    }

    /// <inheritdoc />
    public string ImportLockScopeKey => _importLockScopeKey.Value;

    public async Task<IOctoSession> GetSessionAsync()
    {
        var session = await _repositoryClient.GetSessionAsync();
        return session;
    }

    public async Task<IOctoSession> GetSessionAsync(RtSecurityContext securityContext)
    {
        var session = await _repositoryClient.GetSessionAsync(securityContext);
        return session;
    }

    public IOctoSession GetSession()
    {
        var session = _repositoryClient.GetSession();
        return session;
    }

    public IOctoSession GetSession(RtSecurityContext securityContext)
    {
        var session = _repositoryClient.GetSession(securityContext);
        return session;
    }

    public override IDataSourceCollection<OctoObjectId, TEntity> GetRtCollection<TEntity>(CkTypeGraph ckTypeGraph)
    {
        return GetRtDatabaseCollection<TEntity>(ckTypeGraph);
    }


    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollection<TEntity>(CkTypeGraph ckTypeGraph)
        where TEntity : RtEntity, new()
    {
        if (ckTypeGraph.DefiningCollectionRootCkTypeId == null)
        {
            throw OperationFailedException.CkTypeHasNoDefiningCollectionRoot(ckTypeGraph.CkTypeId);
        }

        var suffix = ckTypeGraph.DefiningCollectionRootCkTypeId.ToRtCkId().GetCkTypeCollectionName();
        var mapper = CreateRtEntityMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    /// <summary>
    ///     Mapper for runtime-entity collections whose reads normalise legacy strings in Secret slots
    ///     (AB#5533) - every read path of the returned collection (by id, by ids, find, migration reads,
    ///     change streams) hands out <see cref="RtSecretValue.LegacyPlaintext" />, never a string.
    /// </summary>
    private RtEntityMongoDataSourceMapper<TEntity> CreateRtEntityMapper<TEntity>() where TEntity : RtEntity, new()
    {
        return new RtEntityMongoDataSourceMapper<TEntity>(_ckCacheService, TenantId);
    }

    /// <inheritdoc />
    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollectionByTypeId<TEntity>(
        RtCkId<CkTypeId> rtCkTypeId) where TEntity : RtEntity, new()
    {
        var suffix = rtCkTypeId.GetCkTypeCollectionName();
        var mapper = CreateRtEntityMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    /// <inheritdoc />
    public IMongoDbDataSourceCollection<OctoObjectId, TEntity> GetRtDatabaseCollectionByCollectionSuffix<TEntity>(
        string suffix) where TEntity : RtEntity, new()
    {
        var mapper = CreateRtEntityMapper<TEntity>();
        return _repository.GetCollection(mapper, suffix);
    }

    /// <inheritdoc />
    public async Task DropRtDatabaseCollectionByTypeIdAsync(RtCkId<CkTypeId> rtCkTypeId)
    {
        var suffix = rtCkTypeId.GetCkTypeCollectionName();
        var collectionName = $"RtEntity_{suffix}";
        await _repository.DropCollectionAsync(collectionName);
    }

    /// <inheritdoc />
    public async Task<(string CollectionName, IReadOnlyList<TEntity> Entities)> FindEntitiesInAllCollectionsByCkTypeIdAsync<TEntity>(
        IOctoSession session, string ckTypeIdValue) where TEntity : RtEntity, new()
    {
        const string rtEntityPrefix = "RtEntity_";
        var allCollections = await _repository.ListCollectionNamesAsync(rtEntityPrefix);

        _logger.LogDebug(
            "FindEntitiesInAllCollectionsByCkTypeIdAsync: Searching {Count} collections for ckTypeId '{CkTypeId}'",
            allCollections.Count, ckTypeIdValue);

        foreach (var collectionName in allCollections)
        {
            var suffix = collectionName.Substring(rtEntityPrefix.Length);
            var mapper = CreateRtEntityMapper<TEntity>();
            var collection = _repository.GetCollection(mapper, suffix);

            var filter = Builders<TEntity>.Filter.Eq("ckTypeId", ckTypeIdValue);
            var entities = await collection.FindManyAsync(session, filter).ConfigureAwait(false);

            _logger.LogDebug(
                "FindEntitiesInAllCollectionsByCkTypeIdAsync: Collection '{CollectionName}' has {Count} matches",
                collectionName, entities.Count);

            if (entities.Count > 0)
            {
                return (collectionName, entities.ToList());
            }
        }

        _logger.LogWarning(
            "FindEntitiesInAllCollectionsByCkTypeIdAsync: No entities found with ckTypeId '{CkTypeId}' in any collection",
            ckTypeIdValue);

        return (string.Empty, Array.Empty<TEntity>());
    }

    /// <inheritdoc />
    public async Task<string?> FindRtCollectionNameByRtIdAsync(IOctoSession session, OctoObjectId rtId)
    {
        const string rtEntityPrefix = "RtEntity_";
        var allCollections = await _repository.ListCollectionNamesAsync(rtEntityPrefix);
        var filter = Builders<RtEntity>.Filter.Eq("_id", rtId);
        foreach (var collectionName in allCollections)
        {
            var collection = _repository.GetCollection(CreateRtEntityMapper<RtEntity>(),
                collectionName.Substring(rtEntityPrefix.Length));
            if (await collection.GetTotalCountAsync(session, filter, 1).ConfigureAwait(false) > 0)
            {
                return collectionName;
            }
        }

        return null;
    }

    public override async Task<IReadOnlyList<RtAssociationsMultiplicityResult>> GetRtAssociationsMultiplicityAsync(
        IOctoSession session, IEnumerable<RtEntityRoleIdDirectionPair> entityRoleIdDirectionPairs)
    {
        // Only Zero/One/Many is needed, so each count is capped at 2 — the server stops at the
        // second match instead of walking the whole index range. Materializing the matching
        // documents here scaled O(edge count) per association write and saturated MongoDB on
        // high-frequency targets (e.g. a pipeline with 650k execution edges).
        var multiplicityResults = new List<RtAssociationsMultiplicityResult>();

        foreach (var pair in entityRoleIdDirectionPairs)
        {
            List<FilterDefinition<RtAssociation>> filters = new();

            if (pair.Direction == GraphDirections.Inbound || pair.Direction == GraphDirections.Any)
            {
                filters.Add(Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, pair.RtEntityId.RtId),
                    Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, pair.RtEntityId.CkTypeId),
                    Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.CkRoleId)
                ));
            }

            if (pair.Direction == GraphDirections.Outbound || pair.Direction == GraphDirections.Any)
            {
                filters.Add(Builders<RtAssociation>.Filter.And(
                    Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, pair.RtEntityId.RtId),
                    Builders<RtAssociation>.Filter.Eq(x => x.OriginCkTypeId, pair.RtEntityId.CkTypeId),
                    Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.CkRoleId)
                ));
            }

            if (filters.Count == 0)
            {
                multiplicityResults.Add(new RtAssociationsMultiplicityResult(pair, CurrentMultiplicity.Zero));
                continue;
            }

            var count = await RtMongoDbDataSourceAssociations.GetTotalCountAsync(session,
                Builders<RtAssociation>.Filter.Or(filters), 2);

            CurrentMultiplicity multiplicity = count switch
            {
                >= 2 => CurrentMultiplicity.Many,
                1 => CurrentMultiplicity.One,
                _ => CurrentMultiplicity.Zero
            };

            multiplicityResults.Add(new RtAssociationsMultiplicityResult(pair, multiplicity));
        }

        return multiplicityResults;
    }

    public override async Task<IReadOnlyList<RtAssociation>> GetRtAssociationsAsync(IOctoSession session,
        IEnumerable<RtOriginTargetPair> rtOriginTargetPair, RtAssociationBaseQueryOptions queryOptions)
    {
        List<FilterDefinition<RtAssociation>> filters = new();
        foreach (var pair in rtOriginTargetPair)
        {
            var filter = Builders<RtAssociation>.Filter.And(
                Builders<RtAssociation>.Filter.Eq(x => x.OriginRtId, pair.Origin.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.OriginCkTypeId, pair.Origin.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetRtId, pair.Target.RtId),
                Builders<RtAssociation>.Filter.Eq(x => x.TargetCkTypeId, pair.Target.CkTypeId),
                Builders<RtAssociation>.Filter.Eq(x => x.AssociationRoleId, pair.AssociationRoleId)
            );

            filters.Add(filter);
        }

        if (filters.Count == 0)
        {
            return [];
        }

        var orFilter = Builders<RtAssociation>.Filter.Or(filters);

        var aggregate = RtMongoDbDataSourceAssociations.Aggregate(session);
        var aggregateFluent = aggregate.Match(orFilter);

        return await aggregateFluent.ToListAsync();
    }

    public IMongoDbDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, RtAssociation> RtMongoDbDataSourceAssociations { get; }

    public override IDataSourceCollection<OctoObjectId, RtAssociation> RtAssociations =>
        RtMongoDbDataSourceAssociations;

    public IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    public IMongoDbDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    public IMongoDbDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    public IMongoDbDataSourceCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    public IMongoDbDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    public IMongoDbDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }
    public IMongoDbDataSourceCollection<CkId<CkInterfaceId>, CkInterface> CkInterfaces { get; }

    public IMongoDbDataSourceCollection<OctoObjectId, CkTypeInterfaceImplementation> CkTypeInterfaceImplementations
    {
        get;
    }

    public async Task UpdateCollectionsAsync(IOctoSession session, bool includeModelsInStateImporting = false,
        bool skipCleanup = false)
    {
        _logger.LogDebug("Creating collections for tenant '{TenantId}'", TenantId);
        await Task.WhenAll(
            _repository.CreateCollectionIfNotExistsAsync(CkModels.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkTypes.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkRecords.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkEnums.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkAttributes.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkTypeAssociations.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkAssociationRoles.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkTypeInheritances.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkRecordInheritances.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkInterfaces.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(CkTypeInterfaceImplementations.MongoDataSourceMapper, false),
            _repository.CreateCollectionIfNotExistsAsync(RtMongoDbDataSourceAssociations.MongoDataSourceMapper, true));

        _logger.LogDebug("Creating type root collections for tenant '{TenantId}'", TenantId);
        // Mirrors UpdateIndexAsync: when called from inside an active import, the new CkTypes
        // are still in ModelState.Importing — they only flip to Available after the transaction
        // commits. Opt in via `includeModelsInStateImporting: true` so those types' collections
        // are created (with the right changeStreamPreAndPostImages option) during the import.
        var ckTypes =
            (await CkTypes.FindManyAsync(session, t => t.IsCollectionRoot &&
                (includeModelsInStateImporting
                    ? (t.ModelState == ModelState.Available || t.ModelState == ModelState.Importing)
                    : t.ModelState == ModelState.Available)))
            .ToList();

        // Build set of valid collection suffixes for collection roots
        var validCollectionSuffixes = new HashSet<string>(
            ckTypes.Select(t => t.CkTypeId.ToRtCkId().GetCkTypeCollectionName()));

        // CK v2 F1.0-S2 (AB#5901, G-M2): collection roots of ResolveFailed models are kept too (not created, not
        // dropped by the cleanup below). Since ResolveFailed models recover by re-validation, dropping their empty
        // collections would leave a recovered model without collection, indexes and change-stream options.
        var resolveFailedRoots = await CkTypes.FindManyAsync(session,
            t => t.IsCollectionRoot && t.ModelState == ModelState.ResolveFailed);
        validCollectionSuffixes.UnionWith(
            resolveFailedRoots.Select(t => t.CkTypeId.ToRtCkId().GetCkTypeCollectionName()));

        foreach (var ckType in ckTypes)
        {
            _logger.LogDebug("Creating type root collection for '{CkTypeId}'", ckType.CkTypeId);
            var suffix = ckType.CkTypeId.ToRtCkId().GetCkTypeCollectionName();
            var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
            await _repository.CreateCollectionIfNotExistsAsync(mapper,
                ckType.EnableChangeStreamPreAndPostImages, suffix);
            // Reconcile the option on collections that already existed from a prior import,
            // since CreateCollectionIfNotExistsAsync no longer mutates existing collections.
            await _repository.ReconcileChangeStreamPreAndPostImagesAsync(mapper,
                ckType.EnableChangeStreamPreAndPostImages, suffix);
        }

        _logger.LogDebug("Type root collections created for tenant '{TenantId}'", TenantId);

        if (!skipCleanup)
        {
            // Cleanup: Remove empty collections that do not belong to a collection root
            await CleanupEmptyAbstractTypeCollectionsInternalAsync(validCollectionSuffixes);
        }
    }

    private async Task CleanupEmptyAbstractTypeCollectionsInternalAsync(HashSet<string> validCollectionSuffixes)
    {
        const string rtEntityPrefix = "RtEntity_";

        // Get all RtEntity collections
        var allCollections = await _repository.ListCollectionNamesAsync(rtEntityPrefix);

        // Find collections that don't correspond to collection roots
        foreach (var collectionName in allCollections)
        {
            var suffix = collectionName.Substring(rtEntityPrefix.Length);

            // Skip if this is a valid collection root
            if (validCollectionSuffixes.Contains(suffix))
            {
                continue;
            }

            // Check if this collection has any documents (fast check using Find+Limit(1)
            // instead of CountDocumentsAsync which is very slow on large collections)
            var hasDocuments = await _repository.CollectionHasDocumentsAsync(collectionName);

            if (hasDocuments)
            {
                _logger.LogWarning(
                    "Collection '{CollectionName}' is not a valid collection root but contains documents - skipping cleanup",
                    collectionName);
                continue;
            }

            // Delete the empty orphaned collection
            _logger.LogInformation("Deleting empty orphaned collection '{CollectionName}'", collectionName);
            await _repository.DropCollectionAsync(collectionName);
        }
    }

    /// <summary>
    ///     AB#9302, tests only: called at each stage of a lock section of the tiebreak index upgrade (lock acquired,
    ///     about to be released, released, yield to a waiter checked).
    /// </summary>
    internal Func<IndexUpgradeSectionEvent, Task>? UpgradeSectionObserver { get; set; }

    // AB#9302: the section of the collection run in progress (tests only: the TrackingStarted stage).
    private int? _upgradeSection;

    public Task UpdateIndexAsync(IOctoSession session, bool includeModelsInStateImporting,
        CkModelId? scopeToModelId = null, CancellationToken cancellationToken = default) =>
        UpdateIndexCoreAsync(session, includeModelsInStateImporting, scopeToModelId, upgradeTiebreakIndexes: false,
            cancellationToken);

    /// <inheritdoc />
    public Task UpgradeTiebreakIndexesAsync(IOctoSession session, CancellationToken cancellationToken = default) =>
        UpdateIndexCoreAsync(session, false, null, upgradeTiebreakIndexes: true, cancellationToken);

    /// <summary>
    ///     The index update of every collection root (or of those of one model). With
    ///     <paramref name="upgradeTiebreakIndexes" /> a CK Ascending index in the legacy shape is rebuilt in the
    ///     tiebreak shape (AB#8960, <see cref="TiebreakIndexShape" />): the new index is built first, the legacy one is
    ///     dropped after it, collection by collection, each collection in a lock section of its own
    ///     (<see cref="UpgradeTiebreakIndexesPerCollectionAsync" />, AB#9302). Without it (CK import,
    ///     <c>UpdateIndexesAsync</c>) a legacy index is kept as it is - a model import must not rebuild the indexes of
    ///     large collections while a tenant is resolved - and only indexes that do not exist yet are created, in the
    ///     tiebreak shape; that update holds <see cref="IndexUpdateLockId" /> for the whole tenant, as before.
    /// </summary>
    private async Task UpdateIndexCoreAsync(IOctoSession session, bool includeModelsInStateImporting,
        CkModelId? scopeToModelId, bool upgradeTiebreakIndexes, CancellationToken cancellationToken)
    {
        if (upgradeTiebreakIndexes)
        {
            await UpgradeTiebreakIndexesPerCollectionAsync(session, cancellationToken);
            return;
        }

        if (scopeToModelId != null)
        {
            _logger.LogInformation(
                "Updating indexes of tenant '{TenantId}' scoped to model '{ModelId}'", TenantId, scopeToModelId);
        }
        else
        {
            _logger.LogInformation("Updating indexes of tenant '{TenantId}'", TenantId);
        }

        // AB#9302: the waiter marker lets a running tiebreak upgrade yield the lock to this update between two
        // collections.
        await using var lockService = new RepositoryDistributedLockService(_repositoryClient, _repository, _logger,
            IndexUpdateLockId, markWaiters: true);
        await lockService.AcquireLockAsync(cancellationToken);

        // Abort the index update if either the caller cancels or the lock is lost mid-flight.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lockService.LockLostToken);
        var effectiveToken = linkedCts.Token;

        var plan = await ReadIndexUpdatePlanAsync(session, includeModelsInStateImporting, scopeToModelId,
            effectiveToken);

        foreach (var ckTypeInfo in plan.CollectionRoots)
        {
            effectiveToken.ThrowIfCancellationRequested();
            await UpdateIndexesForCollectionRoot(ckTypeInfo, plan.BaseTypesOf(ckTypeInfo), plan.Attributes,
                plan.Records, upgradeTiebreakIndexes: false, lockService.LockLostToken);
        }

        // Create indexes for RtAssociations collection (model-independent, only needed for full updates)
        if (scopeToModelId == null)
        {
            effectiveToken.ThrowIfCancellationRequested();
            await CreateModelIndependentIndexesAsync(effectiveToken);
        }

        _logger.LogInformation("Updating indexes of tenant '{TenantId}' completed", TenantId);
    }

    /// <summary>
    ///     AB#9302: the tiebreak index upgrade holds <see cref="IndexUpdateLockId" /> only while it works on one
    ///     collection. Each lock section re-reads the CK metadata (a CK import between two sections may change, add or
    ///     remove collection roots and index definitions; a stale definition would drop an index the import just
    ///     built), takes the next collection root it has not processed in this run and upgrades it - so a collection an
    ///     import created meanwhile is upgraded in the same run. A last section creates the model-independent indexes.
    ///     <para>
    ///         Invariant: a section never drops or replaces an index of a collection that holds a type of a model in state
    ///         <see cref="ModelState.Importing" /> (root, base chain or descendant; <see cref="InvolvesImportingModel" />).
    ///         A CK import releases the lock after its index update and only then makes its model Available; a section
    ///         that ran in between would not see that model's index definitions and drop its indexes. Such a root is
    ///         skipped, retried once after every other collection, and left for the next run if it is still involved
    ///         (named in the log). The section reads the metadata with the Importing models only to recognise them.
    ///     </para>
    ///     Between two sections the upgrade yields the lock to a waiting index update (a fresh waiter marker,
    ///     <see cref="RepositoryDistributedLockService.HasWaitersAsync" />) and then waits up to
    ///     <see cref="UpgradeSectionMaxWait" /> for it. The caller's token stops before the next section; a lost lock
    ///     aborts the current collection (<see cref="UpdateIndexesForCollectionRoot" />). Every index state tracking scope
    ///     lies inside one section.
    /// </summary>
    private async Task UpgradeTiebreakIndexesPerCollectionAsync(IOctoSession session,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Upgrading the indexes of tenant '{TenantId}' to the paging tiebreak shape (AB#8960), one collection per " +
            "lock section (AB#9302)", TenantId);

        // By collection name: a CK import between two sections can raise the model version (new type ids, same collection).
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var left = new List<string>();
        var retrying = false;
        var tenantStopwatch = Stopwatch.StartNew();
        var longest = TimeSpan.Zero;
        string? longestCollection = null;
        var section = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? collectionName;
            await using (var lockService = new RepositoryDistributedLockService(_repositoryClient, _repository,
                             _logger, IndexUpdateLockId, LogLevel.Debug, markWaiters: true))
            {
                if (section == 0)
                {
                    await lockService.AcquireLockAsync(cancellationToken);
                }
                else
                {
                    await lockService.AcquireLockAsync(UpgradeSectionMaxWait, cancellationToken);
                }

                var lockLostToken = lockService.LockLostToken;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockLostToken);
                var plan = await ReadIndexUpdatePlanAsync(session, true, null, linkedCts.Token);

                // The next root: first every root not processed yet, then once more the roots skipped because a model of
                // their collection was being imported.
                CkTypeInfo? next = null;
                while (next == null)
                {
                    // In the retry also a root that appeared meanwhile (a collection an import created) is taken.
                    var candidate = plan.CollectionRoots.FirstOrDefault(r =>
                        !processed.Contains(CollectionNameOf(r)) && !left.Contains(CollectionNameOf(r)) &&
                        (retrying || !skipped.Contains(CollectionNameOf(r))));
                    if (candidate == null)
                    {
                        if (!retrying && skipped.Count > 0)
                        {
                            retrying = true;
                            continue;
                        }

                        // A skipped root that is gone from the metadata (its model was removed) needs nothing.
                        skipped.Clear();
                        break;
                    }

                    var candidateName = CollectionNameOf(candidate);
                    if (InvolvesImportingModel(candidate, plan.BaseTypesOf(candidate)))
                    {
                        if (retrying)
                        {
                            skipped.Remove(candidateName);
                            left.Add(candidateName);
                            await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.LeftForNextRun, candidateName,
                                section, lockLostToken);
                        }
                        else
                        {
                            skipped.Add(candidateName);
                            _logger.LogInformation(
                                "Tiebreak index upgrade of tenant '{TenantId}': collection '{CollectionName}' holds a " +
                                "type of a model that is being imported; it is retried after the other collections",
                                TenantId, candidateName);
                        }

                        continue;
                    }

                    skipped.Remove(candidateName);
                    next = candidate;
                }

                collectionName = next != null ? CollectionNameOf(next) : null;

                _logger.LogDebug("Acquired '{LockId}' of tenant '{TenantId}' for {Target}", IndexUpdateLockId, TenantId,
                    collectionName != null ? $"collection '{collectionName}'" : "the model-independent indexes");
                await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.Acquired, collectionName, section,
                    lockLostToken);

                if (next != null)
                {
                    processed.Add(collectionName!);
                    _logger.LogInformation(
                        "Tiebreak index upgrade of tenant '{TenantId}': collection '{CollectionName}' started " +
                        "({Number} of {Count})", TenantId, collectionName, processed.Count,
                        processed.Count + plan.CollectionRoots.Count(r => !processed.Contains(CollectionNameOf(r))));
                    var stopwatch = Stopwatch.StartNew();
                    _upgradeSection = section;
                    try
                    {
                        await UpdateIndexesForCollectionRoot(next, plan.BaseTypesOf(next), plan.Attributes,
                            plan.Records, upgradeTiebreakIndexes: true, lockLostToken);
                    }
                    finally
                    {
                        _upgradeSection = null;
                    }

                    stopwatch.Stop();

                    // A lock lost after the collection's last index operation still ends the run here.
                    lockLostToken.ThrowIfCancellationRequested();
                    if (stopwatch.Elapsed > longest)
                    {
                        longest = stopwatch.Elapsed;
                        longestCollection = collectionName;
                    }

                    _logger.LogInformation(
                        "Tiebreak index upgrade of tenant '{TenantId}': collection '{CollectionName}' finished in " +
                        "{Seconds:F1} s", TenantId, collectionName, stopwatch.Elapsed.TotalSeconds);
                }
                else
                {
                    await CreateModelIndependentIndexesAsync(lockLostToken);
                }

                await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.Releasing, collectionName, section,
                    lockLostToken);
            }

            _logger.LogDebug("Released '{LockId}' of tenant '{TenantId}' after {Target}", IndexUpdateLockId, TenantId,
                collectionName != null ? $"collection '{collectionName}'" : "the model-independent indexes");
            await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.Released, collectionName, section,
                CancellationToken.None);
            section++;

            if (collectionName == null)
            {
                break;
            }

            var waiterSeen = await YieldToWaitersAsync(cancellationToken);
            await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.Yielded, collectionName, section - 1,
                CancellationToken.None, waiterSeen);
        }

        if (left.Count > 0)
        {
            _logger.LogWarning(
                "Tiebreak index upgrade of tenant '{TenantId}': {Count} collection(s) left for the next run, a model of " +
                "theirs was still being imported: {Collections}", TenantId, left.Count, string.Join(", ", left));
        }

        _logger.LogInformation(
            "Upgrading the indexes of tenant '{TenantId}' to the paging tiebreak shape completed: {Count} collection(s) " +
            "in {Seconds:F1} s, longest '{LongestCollection}' {LongestSeconds:F1} s, {Left} left for the next run",
            TenantId, processed.Count, tenantStopwatch.Elapsed.TotalSeconds, longestCollection ?? "-",
            longest.TotalSeconds, left.Count);
    }

    /// <summary>
    ///     AB#9302: true when a type stored in the collection of <paramref name="collectionRoot" /> - the root, its base
    ///     chain or a descendant - belongs to a model in state <see cref="ModelState.Importing" />.
    /// </summary>
    private static bool InvolvesImportingModel(CkTypeInfo collectionRoot, IEnumerable<CkType> baseTypes) =>
        collectionRoot.ModelState == ModelState.Importing ||
        baseTypes.Any(t => t.ModelState == ModelState.Importing) ||
        collectionRoot.Inheritances.Any(t => t.ModelState == ModelState.Importing);

    private static string CollectionNameOf(CkTypeInfo collectionRoot) =>
        collectionRoot.CkTypeId.ToRtCkId().GetCkTypeCollectionName();

    /// <summary>
    ///     AB#9302: while a waiter marker of <see cref="IndexUpdateLockId" /> is fresh, gives the waiter up to
    ///     <see cref="YieldToWaiterMaxWait" /> to take the lock. Returns whether a waiter was seen. A dead waiter's marker
    ///     expires (<see cref="RepositoryDistributedLockService.WaiterMarkerTimeToLive" />), so the upgrade never waits
    ///     for it longer than that bound.
    /// </summary>
    private async Task<bool> YieldToWaitersAsync(CancellationToken cancellationToken)
    {
        bool waiterSeen;
        try
        {
            waiterSeen = await RepositoryDistributedLockService.HasWaitersAsync(_repository, IndexUpdateLockId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the waiters of '{LockId}' of tenant '{TenantId}'", IndexUpdateLockId,
                TenantId);
            return false;
        }

        if (!waiterSeen)
        {
            return false;
        }

        _logger.LogDebug("Tiebreak index upgrade of tenant '{TenantId}' yields '{LockId}' to a waiting index update",
            TenantId, IndexUpdateLockId);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < YieldToWaiterMaxWait)
        {
            try
            {
                if (await RepositoryDistributedLockService.IsHeldAsync(_repository, IndexUpdateLockId,
                        cancellationToken))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A courtesy probe: on a failed read the upgrade goes on to its normal acquisition.
                _logger.LogDebug(ex, "Could not read '{LockId}' of tenant '{TenantId}' while yielding",
                    IndexUpdateLockId, TenantId);
                break;
            }

            await Task.Delay(100, cancellationToken);
        }

        return true;
    }

    private async Task NotifyUpgradeObserverAsync(IndexUpgradeSectionStage stage, string? collectionName, int section,
        CancellationToken lockLostToken, bool waiterSeen = false)
    {
        if (UpgradeSectionObserver != null)
        {
            await UpgradeSectionObserver(new IndexUpgradeSectionEvent(stage, collectionName, section, lockLostToken,
                _indexStateService.IsTracking, waiterSeen));
        }
    }

    /// <summary>The indexes of the RtAssociations collection and the GridFS owner index (AB#6538).</summary>
    private async Task CreateModelIndependentIndexesAsync(CancellationToken cancellationToken)
    {
        await CreateRtAssociationIndexesAsync(cancellationToken);

        // AB#6538: owner lookup of linked binaries
        if (_repository is MongoRepository mongoRepository)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await GridFsOwnerIndex.EnsureAsync(mongoRepository.Database, cancellationToken);
        }
    }

    /// <summary>The CK metadata an index update works from, read once per update (or per upgrade section).</summary>
    private sealed record IndexUpdatePlan(
        List<CkTypeInfo> CollectionRoots,
        Dictionary<CkId<CkTypeId>, List<CkType>> BaseTypes,
        IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> Attributes,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> Records)
    {
        public List<CkType> BaseTypesOf(CkTypeInfo collectionRoot) =>
            BaseTypes.TryGetValue(collectionRoot.CkTypeId, out var types) ? types : [];
    }

    private async Task<IndexUpdatePlan> ReadIndexUpdatePlanAsync(IOctoSession session,
        bool includeModelsInStateImporting, CkModelId? scopeToModelId, CancellationToken cancellationToken)
    {
        var aggregate = _ckTypes.Aggregate(session);
        aggregate = aggregate.Match(x => x.IsCollectionRoot == true &&
            (includeModelsInStateImporting
                ? (x.ModelState == ModelState.Available || x.ModelState == ModelState.Importing)
                : x.ModelState == ModelState.Available));

        var ckTypeInfoList = await AggregateCkTypeInfo(aggregate).ToListAsync(cancellationToken);
        var collectionRootTypes = ckTypeInfoList.ToList();

        // D3 / review M6: $graphLookup matches base and inheritor ids as strings; a range-retaining model stores
        // its base major-qualified (System@2/Entity-1). Recompute the descendants in memory with bound ids.
        await BindMajorQualifiedInheritancesAsync(session, collectionRootTypes, includeModelsInStateImporting);

        // Pre-fetch all base types for all collection roots to avoid long transactions
        var baseTypesMap =
            await CollectBaseTypesForCollectionRoots(session, collectionRootTypes, includeModelsInStateImporting);

        // When scoped to a specific model, only process collection roots that belong to that model, derive from
        // a type of that model or hold a type of that model. This significantly reduces index update time on
        // large tenants.
        if (scopeToModelId != null)
        {
            var modelName = scopeToModelId.Name;
            collectionRootTypes = collectionRootTypes
                .Where(t => t.CkModelId.Name == modelName ||
                            (baseTypesMap.TryGetValue(t.CkTypeId, out var baseTypes) &&
                             baseTypes.Any(b => b.CkModelId.Name == modelName)) ||
                            t.InheritedTypes.Any(i => i.CkModelId.Name == modelName))
                .ToList();

            _logger.LogInformation(
                "Scoped index update: processing {Count} collection roots for model '{ModelId}'",
                collectionRootTypes.Count, scopeToModelId);
        }

        // Pre-fetch attribute metadata for resolving index attribute paths
        var (allCkAttributes, allCkRecords) = await FetchAttributeMetadataAsync();

        return new IndexUpdatePlan(collectionRootTypes, baseTypesMap, allCkAttributes, allCkRecords);
    }

    private async Task<Dictionary<CkId<CkTypeId>, List<CkType>>> CollectBaseTypesForCollectionRoots(
        IOctoSession session, IEnumerable<CkTypeInfo> collectionRoots, bool includeModelsInStateImporting)
    {
        var result = new Dictionary<CkId<CkTypeId>, List<CkType>>();
        var collectionRootsList = collectionRoots.ToList();

        if (!collectionRootsList.Any())
        {
            return result;
        }

        // Bulk fetch ALL inheritances in one query (D3: major-qualified base ids bound to installed versions)
        var binder = await CreateReferenceBinderAsync(session, includeModelsInStateImporting);
        var allInheritances = await _ckTypeInheritances.FindManyAsync(session, x => true);
        foreach (var inheritance in allInheritances)
        {
            inheritance.BaseCkTypeId = binder.Bind(inheritance.BaseCkTypeId);
        }

        var inheritanceDict = allInheritances.ToLookup(x => x.InheritorCkTypeId, x => x);

        // Collect all type IDs that we might need (all collection roots + their potential base types)
        var allTypeIds = new HashSet<CkId<CkTypeId>>(collectionRootsList.Select(x => x.CkTypeId));

        // Add all potential base type IDs from inheritances
        foreach (var inheritance in allInheritances)
        {
            allTypeIds.Add(inheritance.BaseCkTypeId);
            allTypeIds.Add(inheritance.InheritorCkTypeId);
        }

        // Bulk fetch ALL types that we might need in one query
        var allTypes = await _ckTypes.FindManyAsync(session,
            // Parenthesized (AB#5901): `a && b ? c : d` parses as `(a && b) ? c : d`, which dropped the id
            // filter and fetched every Available type of the tenant.
            x => allTypeIds.Contains(x.CkTypeId) && (includeModelsInStateImporting
                ? (x.ModelState == ModelState.Available || x.ModelState == ModelState.Importing)
                : x.ModelState == ModelState.Available));
        var typeDict = allTypes.ToDictionary(x => x.CkTypeId, x => x);

        // D-G1-3: a missing base type is expected for every type of a model whose dependency was just replaced
        // (e.g. System 2.5.0 -> 2.6.0 with an exact-pinned dependent): the re-validation at the end of the import
        // marks that model ResolveFailed and logs it once. One summary line here instead of one WARN per type.
        var missingBaseTypes = new List<(CkId<CkTypeId> BaseCkTypeId, CkId<CkTypeId> InheritorCkTypeId)>();

        // Build inheritance chains in memory using the fetched data
        foreach (var collectionRoot in collectionRootsList)
        {
            var baseTypes = new List<CkType>();
            var currentTypeId = collectionRoot.CkTypeId;

            // Traverse up the inheritance chain using in-memory data
            while (true)
            {
                var inheritances = inheritanceDict[currentTypeId];
                var inheritance = inheritances.FirstOrDefault();

                if (inheritance != null)
                {
                    if (typeDict.TryGetValue(inheritance.BaseCkTypeId, out var baseType))
                    {
                        baseTypes.Add(baseType);
                        currentTypeId = inheritance.BaseCkTypeId;
                    }
                    else
                    {
                        _logger.LogDebug("Base type '{BaseCkTypeId}' not found for '{InheritorCkTypeId}'",
                            inheritance.BaseCkTypeId, inheritance.InheritorCkTypeId);
                        missingBaseTypes.Add((inheritance.BaseCkTypeId, inheritance.InheritorCkTypeId));
                        break;
                    }
                }
                else
                {
                    // No more base types
                    break;
                }
            }

            // Reverse to get most base first
            baseTypes.Reverse();
            result[collectionRoot.CkTypeId] = baseTypes;
        }

        if (missingBaseTypes.Count > 0)
        {
            _logger.LogInformation(
                "Index update of tenant '{TenantId}': base types not found for {Count} collection root(s), indexes of their " +
                "base types are skipped (missing: {MissingBaseTypes}). Expected for models whose dependency was just " +
                "replaced; they are marked ResolveFailed by the re-validation of the import",
                TenantId, missingBaseTypes.Count,
                string.Join(", ", missingBaseTypes.Select(m => m.BaseCkTypeId.FullName).Distinct().Take(5)));
        }

        return result;
    }

    /// <summary>
    /// Pre-fetches all CkAttributes and CkRecords needed for index attribute path resolution.
    /// </summary>
    private async Task<(IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> attributes,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> records)> FetchAttributeMetadataAsync()
    {
        using var session = await GetSessionAsync();

        // Fetch all CkAttributes
        var allAttributes = await CkAttributes.FindManyAsync(session, _ => true);
        // D3: verbatim major-qualified attribute/record references of range-retaining models resolve via aliases.
        var binder = await CreateReferenceBinderAsync(session, true);
        var attributeDict = binder.WithMajorQualifiedAliases<CkAttributeId, CkAttribute>(
            allAttributes.ToDictionary(a => a.CkAttributeId, a => a));

        // Fetch all CkRecords
        var allRecords = await CkRecords.FindManyAsync(session, _ => true);
        var recordDict = binder.WithMajorQualifiedAliases<CkRecordId, CkRecord>(
            allRecords.ToDictionary(r => r.CkRecordId, r => r));

        return (attributeDict, recordDict);
    }

    /// <summary>
    ///     The index update of one collection root, inside the caller's lock section. AB#9302: a lost lock
    ///     (<paramref name="lockLostToken" />) aborts the collection before its next index operation (an index build
    ///     already started finishes); its index states are then not persisted - the lock section is over and another
    ///     holder may work on the collection; the next run reconciles them.
    /// </summary>
    private async Task UpdateIndexesForCollectionRoot(CkTypeInfo collectionRootType, List<CkType> baseTypes,
        IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> allCkAttributes,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> allCkRecords, bool upgradeTiebreakIndexes,
        CancellationToken lockLostToken)
    {
        lockLostToken.ThrowIfCancellationRequested();

        var name = collectionRootType.CkTypeId.ToRtCkId().GetCkTypeCollectionName();
        var mapper = new RtEntityMongoDataSourceMapper<RtEntity>();
        var collection = _repository.GetCollection(mapper, name);

        _logger.LogDebug("Updating indexes for '{CkTypeId}'", collectionRootType.CkTypeId);

        // Review M-M1 (G3): every type stored in this collection — base chain, root, all descendants. They share
        // the attribute fields (attributes.<name>), so the Hidden backstop must consider all of them: a sibling's
        // or a base's Hidden assignment would otherwise be indexed through another type's index or the merged
        // text index. AB#9391: their index states are reconciled with the indexes of the collection at the end of the run.
        var collectionTypes = baseTypes.Cast<CkType>().Append(collectionRootType)
            .Concat(collectionRootType.Inheritances).DistinctBy(t => t.CkTypeId).ToList();

        // Begin tracking index operations
        _indexStateService.BeginTracking(collection.CollectionName, collectionTypes.Select(t => t.CkTypeId));

        // AB#9391: every definition of the collection was processed (then a Failed state no definition expects is removed)
        var runCompleted = false;

        try
        {
            if (_upgradeSection is { } upgradeSection)
            {
                await NotifyUpgradeObserverAsync(IndexUpgradeSectionStage.TrackingStarted, name, upgradeSection,
                    lockLostToken);
            }

            // We need to merge text indexes from inherited types, because MongoDB does not support more than one text index
            Dictionary<CkType, List<CkTypeIndex>> regularIndices = new();
            CkTypeIndex? textIndex = null;

            // Process base types from most base to most derived (already collected and ordered)
            foreach (var baseType in baseTypes)
            {
                _logger.LogDebug("Analyzing base type '{CkTypeId}' for collection root '{CollectionRootCkTypeId}'",
                    baseType.CkTypeId, collectionRootType.CkTypeId);
                AnalyseIndex(baseType, regularIndices, ref textIndex);
            }

            // Analyze the collection root type itself
            AnalyseIndex(collectionRootType, regularIndices, ref textIndex);

            // Then analyze the inherited types (descendants), to merge text indexes
            var inheritTypes = collectionRootType.Inheritances.ToDictionary(k => k.CkTypeId, v => v);
            foreach (var ckInheritedTypeInfo in collectionRootType.InheritedTypes
                         .DistinctBy(x => x.InheritorCkTypeId)
                         .OrderByDescending(x => x.BaseTypeDepthIndex))
            {
                if (!inheritTypes.TryGetValue(ckInheritedTypeInfo.InheritorCkTypeId, out var inheritCkTypeInfo))
                {
                    _logger.LogWarning("Inherited type '{CkTypeId}' not found in inheritances for '{BaseCkTypeId}'",
                        ckInheritedTypeInfo.InheritorCkTypeId, ckInheritedTypeInfo.BaseCkTypeId);
                    continue;
                }

                AnalyseIndex(inheritCkTypeInfo, regularIndices, ref textIndex);
            }

            // When there is no index defined, we drop all indexes for the collection in case
            // an index was removed in the CK model.
            if (regularIndices.Count == 0 && textIndex == null)
            {
                lockLostToken.ThrowIfCancellationRequested();
                _logger.LogDebug("Dropping all indexes for '{CkTypeId}'", collectionRootType.CkTypeId);
                await collection.DropAllIndexesAsync(name);

                // Clear index states for affected types
                using var session = await GetSessionAsync();
                var affectedTypeIds = new List<CkId<CkTypeId>> { collectionRootType.CkTypeId };
                affectedTypeIds.AddRange(regularIndices.Keys.Select(k => k.CkTypeId));
                await _indexStateService.ClearIndexStatesForCollectionAsync(session, collection.CollectionName,
                    affectedTypeIds);
                runCompleted = true;
                return;
            }

            // Now, we compare the existing indexes with the defined indexes in the CK model.
            var repositoryIndices = await collection.GetIndexListAsync();

            // AB#9342: the indexes kept or built in this collection run, by key spec. Two types of the collection that
            // index the same attributes share one index (see SharedIndexKeySpecs).
            var keySpecs = new SharedIndexKeySpecs();

            foreach (var keyValuePair in regularIndices)
            {
                int uniqueIndexNumber = 0;

                foreach (CkTypeIndex ckTypeIndex in keyValuePair.Value)
                {
                    lockLostToken.ThrowIfCancellationRequested();
                    await PrepareAndCreateIndex(keyValuePair.Key, ckTypeIndex, repositoryIndices, collection,
                        uniqueIndexNumber, collectionRootType, allCkAttributes, allCkRecords, collectionTypes,
                        upgradeTiebreakIndexes, keySpecs);
                    uniqueIndexNumber++;
                }

                // Let's create the text index if it exists.
                if (keyValuePair.Key == collectionRootType)
                {
                    if (textIndex != null)
                    {
                        lockLostToken.ThrowIfCancellationRequested();
                        await PrepareAndCreateIndex(keyValuePair.Key, textIndex, repositoryIndices, collection,
                            uniqueIndexNumber, collectionRootType, allCkAttributes, allCkRecords, collectionTypes,
                            upgradeTiebreakIndexes, keySpecs);
                    }
                    else
                    {
                        var repositoryTextIndex =
                            repositoryIndices.SingleOrDefault(i => i.IndexType == IndexTypes.Text);
                        if (repositoryTextIndex != null)
                        {
                            lockLostToken.ThrowIfCancellationRequested();
                            _logger.LogDebug("Dropping text index '{IndexName}' for '{CkTypeId}'",
                                repositoryTextIndex.Name, keyValuePair.Key.CkTypeId);
                            await DropIndexWithTracking(collection, repositoryTextIndex.Name,
                                keyValuePair.Key.CkTypeId);
                            // Remove from repositoryIndices so it won't be processed again in the cleanup loop
                            repositoryIndices.Remove(repositoryTextIndex);
                        }
                    }
                }
            }

            // Create system-level index for rtState (supports filtering archived entities)
            var rtStateIndex = new CkTypeIndex
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames = [nameof(RtEntity.RtState).ToCamelCase()]
                    }
                ]
            };
            lockLostToken.ThrowIfCancellationRequested();
            await CreateOrUpdateIndex(collection.CollectionName, rtStateIndex, repositoryIndices, collection,
                uniqueIndexNumber: 9000, keySpecs: keySpecs);

            // System-level compound index for (ckTypeId, rtCreatedBy) — supports the OwnedOnly
            // data-permission filter (AB#4971)
            var rtCreatedByIndex = new CkTypeIndex
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtEntity.CkTypeId).ToCamelCase(),
                            nameof(RtEntity.RtCreatedBy).ToCamelCase()
                        ]
                    }
                ]
            };
            lockLostToken.ThrowIfCancellationRequested();
            await CreateOrUpdateIndex(collection.CollectionName, rtCreatedByIndex, repositoryIndices, collection,
                uniqueIndexNumber: 9001, keySpecs: keySpecs);

            // Drop any remaining indexes that are no longer needed. A legacy index replaced by its tiebreak-shape
            // successor (AB#8960) is among them: it is dropped only now, after the successor was built.
            foreach (var repositoryIndex in repositoryIndices.Where(i => !keySpecs.IsKeptAfterFailedBuild(i.Name)))
            {
                lockLostToken.ThrowIfCancellationRequested();
                _logger.LogInformation("Dropping obsolete index '{IndexName}' for '{CollectionName}'",
                    repositoryIndex.Name, collection.CollectionName);
                await collection.DropIndexAsync(repositoryIndex.Name);
            }

            runCompleted = true;
        }
        finally
        {
            // The paging gate and the index hint of this process re-read the indexes of the collection on the next
            // query (AB#8960, AB#9138).
            CollectionIndexCache.Invalidate(collection.GetMongoCollection());

            try
            {
                if (lockLostToken.IsCancellationRequested)
                {
                    // AB#9302: the lock section is over; persisting now could overwrite the states of another holder.
                    _logger.LogWarning(
                        "Lost '{LockId}' while updating the indexes of '{CollectionName}'; its index states are not " +
                        "persisted, the next index update reconciles them", IndexUpdateLockId, collection.CollectionName);
                }
                else
                {
                    // AB#9302: the failures of the run get their names only now, after every definition was processed
                    // (a later definition may be served by the index under a failed definition's expected name).
                    _indexStateService.ResolveFailures();

                    // AB#9391: the index states of the collection's types name exactly the indexes that exist, whatever
                    // dropped an index (the cleanup above, a replaced text index, an earlier run aborted before it
                    // persisted its states). Read once, after the cleanup; without the list the states are persisted as
                    // tracked.
                    try
                    {
                        var finalIndexes = await collection.GetIndexListAsync();
                        _indexStateService.Reconcile(finalIndexes.Select(i => i.Name), runCompleted);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not read the indexes of '{CollectionName}' to reconcile the index states of its types",
                            collection.CollectionName);
                    }

                    // AB#9302: checked again after the read. A token check cannot fence a write already started, and a
                    // loss is noticed only by the heartbeat (every 15 s): the states are written with $set on IndexStates
                    // only, and the next run reconciles them.
                    if (lockLostToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Lost '{LockId}' while updating the indexes of '{CollectionName}'; its index states are not " +
                            "persisted, the next index update reconciles them", IndexUpdateLockId,
                            collection.CollectionName);
                    }
                    else
                    {
                        // Always save tracked index states before ending tracking
                        try
                        {
                            // AB#9302: the token is checked again after the session and the read of the stored states
                            // and before each write.
                            using var updateSession = await GetSessionAsync();
                            if (!await _indexStateService.BulkUpdateIndexStatesAsync(updateSession, lockLostToken))
                            {
                                _logger.LogWarning(
                                    "Lost '{LockId}' while persisting the index states of '{CollectionName}'; the " +
                                    "remaining states are not persisted, the next index update reconciles them",
                                    IndexUpdateLockId, collection.CollectionName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Failed to persist index states for collection of CkTypeId '{CkTypeId}'",
                                collectionRootType.CkTypeId);
                        }
                    }
                }
            }
            finally
            {
                // Always end tracking
                _indexStateService.EndTracking();
            }
        }
    }


    /// <summary>
    /// Prepares index configuration by deduplicating attribute paths and then delegates
    /// to the actual index creation method.
    /// </summary>
    /// <param name="indexDefiningType">The type that defines this index</param>
    /// <param name="ckTypeIndex">The index configuration</param>
    /// <param name="repositoryIndices">Existing indexes in the repository</param>
    /// <param name="collection">The MongoDB collection</param>
    /// <param name="uniqueIndexNumber">Unique number for index naming</param>
    /// <param name="collectionRootType">Optional collection root type info</param>
    /// <param name="keySpecs">The indexes kept or built in this collection run (AB#9342)</param>
    private async Task PrepareAndCreateIndex(CkType indexDefiningType, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<OctoObjectId, RtEntity> collection,
        int uniqueIndexNumber, CkTypeInfo? collectionRootType = null,
        IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute>? allCkAttributes = null,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord>? allCkRecords = null,
        IReadOnlyCollection<CkType>? collectionTypes = null, bool upgradeTiebreakIndexes = false,
        SharedIndexKeySpecs? keySpecs = null)
    {
        if (ckTypeIndex.IndexType == IndexTypes.None)
        {
            return;
        }

        // Build a local CkTypeIndex with resolved/normalized fields. The input ckTypeIndex
        // instance is reused across multiple collection roots (base-type indexes like Entity's
        // RtBlueprintSource), so mutating it — including reassigning Fields — corrupts every
        // subsequent call. Each subsequent call would re-read the already-resolved paths and,
        // when re-resolution fails, prepend another "attributes." via the fallback, producing
        // "attributes.attributes...rtBlueprintSource".
        var localIndex = new CkTypeIndex
        {
            IndexType = ckTypeIndex.IndexType,
            Language = ckTypeIndex.Language,
            Fields = ckTypeIndex.Fields.Select(fields => new CkIndexFields
            {
                Weight = fields.Weight,
                AttributeNames = fields.AttributeNames.ToList()
            }).ToList()
        };

        // Resolve attribute paths to fully qualified MongoDB field paths.
        // e.g. "TimeRange.From" → "attributes.timeRange.attributes.from"
        if (allCkAttributes != null && allCkRecords != null)
        {
            var metadataProvider = new DatabaseAttributeMetadataProvider(
                indexDefiningType.Attributes, allCkAttributes, allCkRecords, isRecordContext: false);
            // Review M-M1: the Hidden backstop looks at every type of the collection (see UpdateIndexesForCollectionRoot).
            var hiddenProviders = (collectionTypes ?? [indexDefiningType])
                .Append(indexDefiningType)
                .Select(t => new DatabaseAttributeMetadataProvider(t.Attributes, allCkAttributes, allCkRecords,
                    isRecordContext: false))
                .ToList();

            foreach (var fields in localIndex.Fields)
            {
                // AB#5533: never index a Secret attribute (incl. text indexes). The CK compiler
                // already rejects it (message 70-77); a model compiled by an older compiler is
                // guarded here - the field is skipped with a warning instead of indexing ciphertext.
                fields.AttributeNames = MongoDbAttributePathResolver.WithoutSecretAttributePaths(
                    fields.AttributeNames, metadataProvider,
                    name => _logger.LogWarning(
                        "Skipping Secret attribute '{AttributePath}' in a {IndexType} index on type '{CkTypeId}': " +
                        "Secret attributes are never indexed",
                        name, localIndex.IndexType, indexDefiningType.CkTypeId))
                    // CK v2 F1.3-S4 (review N6): never index a Hidden attribute either. Unlike Secret, Hidden has
                    // no runtime backstop (the value is stored in plain text); a unique index would reveal values
                    // through duplicate-key errors, a text index through search hits. The compiler rejects it
                    // (message 106); this guards hand-edited or older compiled models and the case-insensitive
                    // match the database applies to field names.
                    .Where(name =>
                    {
                        if (Constants.IsSystemAttribute(name) ||
                            !hiddenProviders.Any(provider => provider.ReachesHiddenAttribute(name)))
                        {
                            return true;
                        }

                        _logger.LogError(
                            "Skipping Hidden attribute '{AttributePath}' in a {IndexType} index on type '{CkTypeId}': " +
                            "Hidden attributes are never indexed (a type stored in the same collection assigns it Hidden)",
                            name, localIndex.IndexType, indexDefiningType.CkTypeId);
                        return false;
                    }).Select(name =>
                {
                    if (Constants.IsSystemAttribute(name))
                    {
                        return name;
                    }

                    var resolved = MongoDbAttributePathResolver.ResolveToMongoDbFieldPath(name, metadataProvider);
                    if (resolved != null)
                    {
                        return resolved;
                    }

                    _logger.LogWarning(
                        "Could not resolve attribute path '{AttributePath}' for index on type '{CkTypeId}', using fallback",
                        name, indexDefiningType.CkTypeId);
                    return Constants.AttributesName + Constants.PathSeparator + name.ToCamelCase();
                }).ToList();
            }
        }

        // Ensure that attributes are not multiple times in the index. If an attribute is defined multiple times, we remove duplicates.
        HashSet<string> attributePaths = new();
        foreach (CkIndexFields fields in localIndex.Fields.OrderBy(f => f.Weight))
        {
            var fieldAttributePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string attributeName in fields.AttributeNames)
            {
                if (attributePaths.Add(attributeName))
                {
                    fieldAttributePaths.Add(attributeName);
                }
            }

            fields.AttributeNames = fieldAttributePaths.ToList();
        }

        // AB#5533: an index whose only fields were Secret attributes is not created at all.
        localIndex.Fields = localIndex.Fields.Where(f => f.AttributeNames.Any()).ToList();
        if (localIndex.Fields.Count == 0)
        {
            _logger.LogWarning("Index on type '{CkTypeId}' has no indexable field left and is not created",
                indexDefiningType.CkTypeId);
            return;
        }

        // Prepend ckTypeId as first field and append rtState as last field to Ascending indexes.
        // Queries always filter on ckTypeId (via $in) first, so it should lead the compound index
        // for optimal selectivity. rtState goes last because $ne filters have low selectivity.
        if (localIndex.IndexType == IndexTypes.Ascending)
        {
            var ckTypeIdFieldName = nameof(RtEntity.CkTypeId).ToCamelCase();
            var rtStateFieldName = nameof(RtEntity.RtState).ToCamelCase();
            var allAttributeNames = localIndex.Fields
                .SelectMany(f => f.AttributeNames)
                .ToList();

            // Prepend ckTypeId as first field if not already present
            if (!allAttributeNames.Any(a =>
                    string.Equals(a, ckTypeIdFieldName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, nameof(RtEntity.CkTypeId), StringComparison.OrdinalIgnoreCase)))
            {
                var firstFieldGroup = localIndex.Fields.First();
                firstFieldGroup.AttributeNames = firstFieldGroup.AttributeNames
                    .Prepend(ckTypeIdFieldName).ToList();
            }

            // Append rtState as last field if not already present
            if (!allAttributeNames.Any(a =>
                    string.Equals(a, rtStateFieldName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, nameof(RtEntity.RtState), StringComparison.OrdinalIgnoreCase)))
            {
                var lastFieldGroup = localIndex.Fields.Last();
                lastFieldGroup.AttributeNames = lastFieldGroup.AttributeNames
                    .Append(rtStateFieldName).ToList();
            }

            // AB#8960: _id right after the attribute fields (before rtState, wherever it stands), so that the index
            // provides the order of a page (the caller's keys, then _id). The index is named <Type>_<n>_id; the
            // legacy shape without _id (<Type>_<n>) is kept until UpgradeTiebreakIndexesAsync replaces it. An index
            // that holds _id already keeps its shape and name.
            if (!localIndex.Fields.Any(g => g.AttributeNames.Contains(Constants.IdField)))
            {
                var legacyIndex = new CkTypeIndex
                {
                    IndexType = localIndex.IndexType,
                    Language = localIndex.Language,
                    Fields = localIndex.Fields
                        .Select(g => new CkIndexFields { Weight = g.Weight, AttributeNames = g.AttributeNames.ToList() })
                        .ToList()
                };
                var group = localIndex.Fields.FirstOrDefault(g => g.AttributeNames.Any(a =>
                                string.Equals(a, rtStateFieldName, StringComparison.OrdinalIgnoreCase)))
                            ?? localIndex.Fields.Last();
                group.AttributeNames = TiebreakIndexShape.WithIdTiebreak(group.AttributeNames)!;
                await CreateOrUpdateIndexWithTracking(collection.CollectionName, localIndex, repositoryIndices,
                    collection, uniqueIndexNumber, collectionRootType, indexDefiningType,
                    new LegacyIndexRule(legacyIndex, upgradeTiebreakIndexes), keySpecs);
                return;
            }
        }

        await CreateOrUpdateIndexWithTracking(collection.CollectionName, localIndex, repositoryIndices,
            collection, uniqueIndexNumber, collectionRootType, indexDefiningType, keySpecs: keySpecs);
    }

    /// <summary>
    ///     The legacy shape of a CK Ascending index that the tiebreak shape replaces (AB#8960), and whether this update
    ///     replaces it (<see cref="Upgrade" />) or keeps it.
    /// </summary>
    private sealed record LegacyIndexRule(CkTypeIndex LegacyIndex, bool Upgrade);

    /// <summary>
    ///     The outcome of <see cref="CreateOrUpdateIndex{TKey,TDocument}" /> when the definition is served by an index
    ///     other than an existing one under the expected name: the name of that index and the existing indexes it replaces
    ///     (AB#8960: the one with the expected name, the slot's index under its alternate name, a unique index with another
    ///     type set) - dropped by the cleanup at the end of the collection, after the build.
    /// </summary>
    /// <param name="Name">The index that serves the definition.</param>
    /// <param name="Replaces">The existing indexes the build replaces (empty when nothing was built).</param>
    /// <param name="Built">
    ///     True when the index was built now; false when an existing index under another name (adopted) or an index
    ///     kept or built earlier in the same collection run for another type (shared, AB#9342) serves it.
    /// </param>
    private sealed record IndexCreation(string Name, IReadOnlyList<string> Replaces, bool Built = true);

    /// <summary>
    ///     AB#9342: the non-unique Ascending indexes kept or built in one collection run, by key spec. A CK Ascending
    ///     index has the keys <c>{ckTypeId, attr…, _id, rtState}</c> - the field <c>ckTypeId</c>, not the type value -,
    ///     so two types of one collection that index the same attributes produce the same key spec, and MongoDB holds
    ///     only one non-unique index per key spec and options (a second one under another name is refused with 85
    ///     "Index already exists with a different name"). One index serves both types: queries filter
    ///     <c>ckTypeId $in</c>, the paging gate and the index hint decide by keys. The first type of the run keeps or
    ///     builds the index; every later definition with the same keys is served by it, whatever the order of the types.
    ///     The same holds for a legacy index (AB#8960) kept by an update without the upgrade. Unique and UniqueNotDeleted
    ///     indexes keep their per-type partial filter and text indexes are merged per collection: neither is recorded.
    ///     Indexes with options the engine never sets are never recorded either (they are not ours).
    /// </summary>
    private sealed class SharedIndexKeySpecs
    {
        private readonly Dictionary<string, (string Name, string HolderType)> _holders =
            new(StringComparer.OrdinalIgnoreCase);

        private static string? KeySpec(CkTypeIndex index) => index.IndexType == IndexTypes.Ascending
            ? string.Join("\u0001", index.Fields.SelectMany(f => f.AttributeNames))
            : null;

        /// <summary>
        ///     Records that <paramref name="name" /> holds the keys of <paramref name="index" />; the first wins.
        ///     <paramref name="holderType" /> names the type the index was built for in the log: its id,
        ///     <see cref="SystemHolder" /> or <see cref="UnknownHolder" />.
        /// </summary>
        public void Remember(CkTypeIndex index, string name, string holderType)
        {
            if (KeySpec(index) is { } keySpec)
            {
                _holders.TryAdd(keySpec, (name, holderType));
            }
        }

        /// <summary>The index of this run that holds the keys of <paramref name="index" />, or null.</summary>
        public (string Name, string HolderType)? Holder(CkTypeIndex index) =>
            KeySpec(index) is { } keySpec && _holders.TryGetValue(keySpec, out var holder) ? holder : null;

        private readonly HashSet<string> _keptAfterFailedBuild = new(StringComparer.Ordinal);

        /// <summary>
        ///     AB#9302: the index <paramref name="name" /> stays because the build of its replacement failed. It remains a
        ///     candidate for the later definitions of the run (one of them may have its definition and adopt it) and the
        ///     cleanup at the end of the collection does not drop it.
        /// </summary>
        public void KeepAfterFailedBuild(string name) => _keptAfterFailedBuild.Add(name);

        /// <summary>AB#9302: true when <see cref="KeepAfterFailedBuild" /> kept <paramref name="name" />.</summary>
        public bool IsKeptAfterFailedBuild(string name) => _keptAfterFailedBuild.Contains(name);

        /// <summary>
        ///     AB#9302: a later build of the run replaced the kept index <paramref name="name" />: the cleanup drops it
        ///     after that build, as any replaced index.
        /// </summary>
        public void Replaced(string name) => _keptAfterFailedBuild.Remove(name);

        /// <summary>The holder of a system-level index (no defining type).</summary>
        public const string SystemHolder = "(system)";

        /// <summary>AB#9391: the holder of an index no stored index state identifies.</summary>
        public const string UnknownHolder = "(unknown)";

        public static string HolderOf(CkType? indexDefiningType) =>
            indexDefiningType?.CkTypeId.ToString() ?? SystemHolder;
    }

    private const int IndexOptionsConflictCode = 85;
    private const int IndexKeySpecsConflictCode = 86;

    /// <summary>
    /// Creates or updates an index in MongoDB without leaving the collection without it (AB#8960, AC 8):
    /// <list type="bullet">
    ///     <item>an existing index with the definition is kept, also under another name (adopted: no drop, no rebuild;
    ///     MongoDB can neither rename an index nor hold the same keys under two names);</item>
    ///     <item>an existing index with the expected name but another definition stays until the end of the collection:
    ///     the new index is built next to it under <see cref="AlternateIndexName" />, the cleanup drops the old one
    ///     after the build. If the build fails, the old index stays.</item>
    ///     <item>AB#9342: a non-unique Ascending definition whose keys an index kept or built earlier in the same
    ///     collection run holds (another type of the collection indexes the same attributes) is served by that index:
    ///     no createIndexes call, the shared index name is returned (<see cref="SharedIndexKeySpecs" />).</item>
    /// </list>
    /// </summary>
    /// <param name="collectionName">Name of the MongoDB collection</param>
    /// <param name="ckTypeIndex">The index configuration to create</param>
    /// <param name="repositoryIndices">Existing indexes in the repository (modified to track processed indexes)</param>
    /// <param name="collection">The MongoDB collection interface</param>
    /// <param name="uniqueIndexNumber">Unique number for index naming</param>
    /// <param name="collectionRootType">Optional collection root type info (required for Unique/UniqueNotDeleted)</param>
    /// <param name="indexDefiningType">Optional type that defines this index (required for Unique/UniqueNotDeleted)</param>
    /// <param name="nameSuffix">Appended to the index name (AB#8960: <see cref="TiebreakIndexShape.NameSuffix" />)</param>
    /// <param name="keySpecs">The indexes kept or built in this collection run (AB#9342); null: no sharing</param>
    /// <returns>
    ///     The index built, adopted under another name or shared with another type of the collection; null when an
    ///     existing index under the expected name has the definition.
    /// </returns>
    private async Task<IndexCreation?> CreateOrUpdateIndex<TKey, TDocument>(string collectionName,
        CkTypeIndex ckTypeIndex, ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<TKey, TDocument> collection, int uniqueIndexNumber,
        CkTypeInfo? collectionRootType = null, CkType? indexDefiningType = null, string nameSuffix = "",
        SharedIndexKeySpecs? keySpecs = null)
        where TDocument : class, new()
        where TKey : notnull
    {
        var indexName = IndexBaseName(collectionName, uniqueIndexNumber, indexDefiningType) + nameSuffix;

        // AB#9342: another type of the collection indexes the same attributes, and its index (kept or built earlier in
        // this run) holds these keys. MongoDB holds one non-unique index per key spec; it serves this type too.
        if (keySpecs?.Holder(ckTypeIndex) is { } holder)
        {
            _logger.LogInformation(
                "Index '{IndexName}' of type '{CkTypeId}' in '{CollectionName}' has the keys of index '{SharedName}' of " +
                "type '{HolderCkTypeId}'; the type is served by that index (AB#9342)",
                indexName, SharedIndexKeySpecs.HolderOf(indexDefiningType), collectionName, holder.Name,
                holder.HolderType);
            return new IndexCreation(holder.Name, [], Built: false);
        }

        // We check if the index already exists in the repository,
        // by comparing type, the fields' weight and the attribute paths
        // The fields are compared case-insensitive, so we use the attribute names directly.
        // Several indexes can share one definition: two types in the same collection that declare the same
        // index differ only in their name and their type filter, e.g. after a type moved to another model.
        // The one carrying the expected name is ours; a differently named one is taken over only when it is
        // the single candidate, otherwise it belongs to another type or is obsolete and dropped at the end.
        // A unique index filters its types (the defining type and its descendants): an index with the keys but another
        // type set (a descendant was added, the type moved to another model) is not ours and is replaced (AB#8960).
        var uniqueTypes = ckTypeIndex.IndexType is IndexTypes.Unique or IndexTypes.UniqueNotDeleted
            ? CollectTypeIdsForIndex(collectionRootType, indexDefiningType, ckTypeIndex.IndexType.ToString())
            : null;
        var typeIds = uniqueTypes?.Select(t => t.ToString()).ToHashSet(StringComparer.Ordinal);
        var repositoryIndex = FindMatchingIndex(repositoryIndices, ckTypeIndex, indexName, typeIds);
        if (repositoryIndex != null)
        {
            // Remove the matching index from the list so it won't be dropped later
            repositoryIndices.Remove(repositoryIndex);
            keySpecs?.Remember(ckTypeIndex, repositoryIndex.Name, SharedIndexKeySpecs.HolderOf(indexDefiningType));

            if (repositoryIndex.Name != indexName)
            {
                // AB#8960: kept under its name. Dropping it to rebuild it under the expected name would leave the
                // collection without the index for the time of the build.
                _logger.LogInformation(
                    "Index '{ActualName}' of '{CollectionName}' has the definition of '{ExpectedName}' of type '{CkTypeId}' " +
                    "and is kept under its name; it serves that type (AB#9342)",
                    repositoryIndex.Name, collectionName, indexName,
                    indexDefiningType?.CkTypeId.ToString() ?? "(system)");
                return new IndexCreation(repositoryIndex.Name, [], Built: false);
            }

            _logger.LogDebug("Index '{IndexName}' already exists for '{CollectionName}', skipping creation",
                indexName, collectionName);
            return null;
        }

        // A collection holds at most one text index: a changed text index replaces the existing one (whatever its name -
        // it may be kept under the name of an earlier slot), which has to go before the new one can be built. A text
        // index with options the engine never sets is not ours and stays; ours is then refused.
        if (ckTypeIndex.IndexType == IndexTypes.Text)
        {
            var oldText = repositoryIndices.FirstOrDefault(i => i.IndexType == IndexTypes.Text && !i.HasForeignOptions);
            if (oldText != null)
            {
                repositoryIndices.Remove(oldText);
                _logger.LogInformation(
                    "Text index '{OldName}' of '{CollectionName}' exists with another definition, recreating as " +
                    "'{IndexName}' (a collection holds only one text index)", oldText.Name, collectionName, indexName);
                await collection.DropIndexAsync(oldText.Name);
            }
        }

        // The index this definition replaces stays in repositoryIndices until the new one is built; the cleanup at the end
        // of the collection drops it after the build, and a failed build keeps it (AB#8960): the index holding the
        // expected name, the index of this slot under its alternate name (built there by an earlier update while the
        // expected name was taken), and for a unique index the one with its keys but another type set, whatever its name.
        var occupant = repositoryIndices.FirstOrDefault(i => i.Name == indexName);
        var replaced = typeIds == null
            ? null
            : repositoryIndices.FirstOrDefault(i => !i.HasForeignOptions && SameDefinition(i, ckTypeIndex));
        var previous = occupant != null
            ? null
            : repositoryIndices.FirstOrDefault(i => !i.HasForeignOptions && i.IndexType != IndexTypes.Text &&
                                                    TiebreakIndexShape.IsAlternateNameOf(i.Name, indexName));
        var targetName = indexName;
        if (occupant != null)
        {
            targetName = AlternateIndexName(indexName, ckTypeIndex, typeIds);
            _logger.LogInformation(
                "Index '{IndexName}' of '{CollectionName}' exists with another definition; building the new one as " +
                "'{TargetName}' before the old one is dropped",
                indexName, collectionName, targetName);
        }

        try
        {
            try
            {
                await CreateIndexAsync(collection, targetName, ckTypeIndex, uniqueTypes);
            }
            catch (MongoCommandException ex) when (occupant == null &&
                                                   ex.Code is IndexOptionsConflictCode or IndexKeySpecsConflictCode)
            {
                // An index with the same keys but options the engine never sets (e.g. sparse or hidden) blocks ours:
                // MongoDB refuses a second index on the same keys that differs only in such options. It stays - an index
                // is dropped only once its replacement is built (AB#8960, decided in the design review) - and ours is
                // not applied (tracked as failed by the caller).
                var twin = repositoryIndices.FirstOrDefault(i => i.HasForeignOptions && SameDefinition(i, ckTypeIndex));
                if (ex.Code == IndexOptionsConflictCode && twin != null)
                {
                    repositoryIndices.Remove(twin);
                    _logger.LogWarning(
                        "Index '{IndexName}' of '{CollectionName}' was not built: index '{OtherName}' has the same keys " +
                        "with other options and is kept",
                        indexName, collectionName, twin.Name);
                    throw;
                }

                // The expected name belongs to an index that is no longer in repositoryIndices because it was kept for
                // another definition of this collection: build under the alternate name.
                targetName = AlternateIndexName(indexName, ckTypeIndex, typeIds);
                _logger.LogInformation(
                    "Index name '{IndexName}' of '{CollectionName}' is taken by a kept index; building as '{TargetName}'",
                    indexName, collectionName, targetName);
                await CreateIndexAsync(collection, targetName, ckTypeIndex, uniqueTypes);
            }
        }
        catch (MongoCommandException)
        {
            // The new index was not built: the index it replaces stays. AB#9302: in a CK collection run it stays in
            // repositoryIndices, protected from the cleanup, so a later definition with its keys still finds and adopts it
            // (two definitions whose existing indexes hold each other's keys, the failing one processed first).
            foreach (var kept in new[] { occupant, replaced, previous }.OfType<CkTypeIndexWithName>())
            {
                if (keySpecs != null)
                {
                    keySpecs.KeepAfterFailedBuild(kept.Name);
                }
                else
                {
                    repositoryIndices.Remove(kept);
                }
            }

            throw;
        }

        keySpecs?.Remember(ckTypeIndex, targetName, SharedIndexKeySpecs.HolderOf(indexDefiningType));
        var replacedNames = new[] { occupant, previous, replaced }.OfType<CkTypeIndexWithName>().Select(i => i.Name)
            .Distinct().ToList();
        foreach (var name in replacedNames)
        {
            keySpecs?.Replaced(name);
        }

        return new IndexCreation(targetName, replacedNames);
    }

    private static async Task CreateIndexAsync<TKey, TDocument>(IMongoDbDataSourceCollection<TKey, TDocument> collection,
        string indexName, CkTypeIndex ckTypeIndex, List<RtCkId<CkTypeId>>? uniqueTypes)
        where TDocument : class, new()
        where TKey : notnull
    {
        switch (ckTypeIndex.IndexType)
        {
            case IndexTypes.Ascending:
                await collection.CreateAscendingIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames));
                break;
            case IndexTypes.Text:
                await collection.CreateTextIndexAsync(indexName, ckTypeIndex.Language ?? "en",
                    ckTypeIndex.Fields);
                break;
            case IndexTypes.Unique:
                await collection.CreateUniqueIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames), uniqueTypes!);
                break;
            case IndexTypes.UniqueNotDeleted:
                await collection.CreateUniqueNotDeletedIndexAsync(indexName,
                    ckTypeIndex.Fields.SelectMany(x => x.AttributeNames), uniqueTypes!);
                break;
            default:
                throw OperationFailedException.IndexTypeNotSupported(ckTypeIndex.IndexType);
        }
    }

    /// <summary>
    ///     The name an index is built under while an index with another definition holds its expected name (AB#8960):
    ///     <c>&lt;expected name&gt;_&lt;6 hex digits&gt;</c>, the digits from the SHA-256 of the definition (type, language,
    ///     the key paths with their weights and, for a unique index, its type set). Deterministic, so a repeated update
    ///     finds the same name.
    /// </summary>
    internal static string AlternateIndexName(string indexName, CkTypeIndex ckTypeIndex,
        IReadOnlySet<string>? typeIds = null)
    {
        var definition = ckTypeIndex.IndexType + "|" + ckTypeIndex.Language + "|" + string.Join(";",
                             ckTypeIndex.Fields.Select(f => f.Weight + ":" + string.Join(",", f.AttributeNames))) +
                         "|" + string.Join(",", (typeIds ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(definition));
        return TiebreakIndexShape.AlternateName(indexName, Convert.ToHexStringLower(hash));
    }

    /// <summary>
    ///     The existing index with <paramref name="definition" />: the one named <paramref name="expectedName" />,
    ///     otherwise the only one with that definition (several types of one collection can declare the same index),
    ///     otherwise null. With <paramref name="typeIds" /> (a unique index) its partial filter must name exactly these
    ///     types.
    ///     <para>
    ///         Sharing rule (AB#9342): a non-unique index is defined by its keys, and MongoDB holds one per key spec. The
    ///         first type of a collection run that finds (or builds) the index takes it out of
    ///         <paramref name="repositoryIndices" />; every later type with the same keys is served by that index through
    ///         <see cref="SharedIndexKeySpecs" />, not by this lookup, so the result does not depend on the order of the
    ///         types.
    ///     </para>
    /// </summary>
    private static CkTypeIndexWithName? FindMatchingIndex(IEnumerable<CkTypeIndexWithName> repositoryIndices,
        CkTypeIndex definition, string expectedName, IReadOnlySet<string>? typeIds = null)
    {
        // An index with options the engine never sets is not ours, whatever its keys (AB#8960: it is never adopted).
        var matching = repositoryIndices.Where(i => !i.HasForeignOptions && SameDefinition(i, definition) &&
                                                    (typeIds == null || (i.PartialFilterTypeIds != null &&
                                                                         i.PartialFilterTypeIds.SetEquals(typeIds))))
            .ToList();
        return matching.FirstOrDefault(i => i.Name == expectedName) ?? (matching.Count == 1 ? matching[0] : null);
    }

    /// <summary>
    ///     True when an existing index has <paramref name="definition" />. A text index compares its field groups
    ///     (weights); every other index its keys in order: the engine builds a definition with several field groups as
    ///     one key list, and <c>listIndexes</c> reads it back as one group (AB#8960).
    /// </summary>
    private static bool SameDefinition(CkTypeIndexWithName index, CkTypeIndex definition)
    {
        if (index.IndexType != definition.IndexType)
        {
            return false;
        }

        if (definition.IndexType == IndexTypes.Text)
        {
            // The language defaults to "en" when the index is built (CreateTextIndexAsync) and listIndexes reports it;
            // the paths and their weights are a set - listIndexes groups them by weight, not by the CK field groups.
            static HashSet<string> Weighted(CkTypeIndex i) => i.Fields
                .SelectMany(f => f.AttributeNames.Select(a => a.ToLowerInvariant() + ":" + (f.Weight ?? 1)))
                .ToHashSet(StringComparer.Ordinal);
            return string.Equals(index.Language ?? "en", definition.Language ?? "en", StringComparison.Ordinal) &&
                   Weighted(index).SetEquals(Weighted(definition));
        }

        return index.Fields.SelectMany(f => f.AttributeNames)
            .SequenceEqual(definition.Fields.SelectMany(f => f.AttributeNames), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The index name without suffix: the type's collection name and the number for an index a type defines, the
    ///     collection name and the number for a system index (like the RtAssociation indexes).
    /// </summary>
    private static string IndexBaseName(string collectionName, int uniqueIndexNumber, CkType? indexDefiningType) =>
        indexDefiningType != null
            ? indexDefiningType.CkTypeId.ToRtCkId().GetCkTypeCollectionName() + "_" + uniqueIndexNumber
            : collectionName + "_" + uniqueIndexNumber;

    /// <summary>
    /// Wrapper for CreateOrUpdateIndex that adds index state tracking
    /// </summary>
    /// <param name="legacyRule">
    ///     AB#8960: <paramref name="ckTypeIndex" /> is in the tiebreak shape and replaces this legacy shape. When the
    ///     tiebreak shape does not exist yet but the legacy one does, the legacy index is kept (no upgrade) or the
    ///     tiebreak index is built next to it and the legacy one is left in <paramref name="repositoryIndices" />, so
    ///     that the cleanup at the end of the collection drops it after the build (upgrade). If the build fails, the
    ///     legacy index stays.
    /// </param>
    /// <param name="keySpecs">
    ///     AB#9342: the indexes kept or built in this collection run. A type whose index another type of the collection
    ///     already holds (same keys) is served by it - also a legacy index kept by an update without the upgrade - and
    ///     gets the state <c>Applied</c> under that index's name; the state under its own name is removed (a
    ///     <c>Failed</c> one only when the serving index is in the target shape, AB#9391).
    /// </param>
    private async Task CreateOrUpdateIndexWithTracking<TKey, TDocument>(string collectionName, CkTypeIndex ckTypeIndex,
        ICollection<CkTypeIndexWithName> repositoryIndices,
        IMongoDbDataSourceCollection<TKey, TDocument> collection, int uniqueIndexNumber,
        CkTypeInfo? collectionRootType = null, CkType? indexDefiningType = null, LegacyIndexRule? legacyRule = null,
        SharedIndexKeySpecs? keySpecs = null)
        where TDocument : class, new()
        where TKey : notnull
    {
        var baseName = IndexBaseName(collectionName, uniqueIndexNumber, indexDefiningType);
        var nameSuffix = legacyRule != null ? TiebreakIndexShape.NameSuffix : "";
        var indexName = baseName + nameSuffix;

        // AB#9391: a Failed state of the definition - under the expected name, or under its alternate name when another
        // definition of the type held the expected name (see the catch below) - stays until an index in the target shape
        // serves the definition, also through runs that keep a legacy index.
        var alternateName = indexDefiningType == null
            ? indexName
            : AlternateIndexName(indexName, ckTypeIndex,
                UniqueTypeIdStrings(ckTypeIndex, collectionRootType, indexDefiningType));
        if (indexDefiningType != null)
        {
            _indexStateService.ExpectIndex(indexDefiningType.CkTypeId, indexName, collection.CollectionName);
            _indexStateService.ExpectIndex(indexDefiningType.CkTypeId, alternateName, collection.CollectionName);
        }

        CkTypeIndexWithName? legacy = null;
        if (legacyRule != null &&
            !repositoryIndices.Any(i => !i.HasForeignOptions && SameDefinition(i, ckTypeIndex)))
        {
            legacy = FindMatchingIndex(repositoryIndices, legacyRule.LegacyIndex, baseName);

            // AB#9342: when an index in the tiebreak shape with these keys was kept or built earlier in this run (for
            // another type), it serves this type; the legacy index stays in the list and the cleanup drops it after the
            // collection's other indexes - as for a single type whose tiebreak index already exists. Nothing is built.
            if (legacy != null && !legacyRule.Upgrade && keySpecs?.Holder(ckTypeIndex) == null)
            {
                var ownLegacy = string.Equals(legacy.Name, baseName, StringComparison.Ordinal);
                var holderType = SharedIndexKeySpecs.HolderOf(indexDefiningType);
                if (ownLegacy)
                {
                    _logger.LogDebug(
                        "Keeping legacy index '{IndexName}' of '{CollectionName}' until the tiebreak index upgrade (AB#8960)",
                        legacy.Name, collection.CollectionName);
                }
                else
                {
                    // AB#9342: the only legacy index with these keys carries the name of another type's slot; it serves
                    // this type too (MongoDB holds one index per key spec). AB#9391: the type it was built for is named
                    // when a stored index state identifies it.
                    holderType = await FindStoredHolderForLogAsync(legacy.Name, collection.CollectionName);
                    _logger.LogInformation(
                        "Legacy index '{SharedName}' of type '{HolderCkTypeId}' in '{CollectionName}' has the keys of the " +
                        "index of type '{CkTypeId}'; it is kept and serves the type until the tiebreak index upgrade (AB#9342)",
                        legacy.Name, holderType, collection.CollectionName,
                        SharedIndexKeySpecs.HolderOf(indexDefiningType));
                }

                repositoryIndices.Remove(legacy);
                keySpecs?.Remember(legacyRule.LegacyIndex, legacy.Name, holderType);
                TrackServedBy(indexDefiningType, legacy.Name, indexName, collection.CollectionName,
                    targetShape: false);
                return;
            }

            // AB#9342: another type of the collection kept the legacy index with these keys in this run (an update
            // without the upgrade) and no index in the tiebreak shape is there for them: the legacy index serves this
            // type too, until the upgrade replaces it by one tiebreak index. Building a tiebreak index for this type
            // here would create the mixed state the upgrade could not resolve.
            if (legacy == null && !legacyRule.Upgrade && keySpecs != null && keySpecs.Holder(ckTypeIndex) == null &&
                keySpecs.Holder(legacyRule.LegacyIndex) is { } legacyHolder)
            {
                if (string.Equals(legacyHolder.Name, baseName, StringComparison.Ordinal))
                {
                    // The type's own legacy index, kept earlier in this run by another type that it serves too.
                    _logger.LogDebug(
                        "Keeping legacy index '{IndexName}' of '{CollectionName}' until the tiebreak index upgrade (AB#8960)",
                        legacyHolder.Name, collection.CollectionName);
                }
                else
                {
                    _logger.LogInformation(
                        "Legacy index '{SharedName}' of type '{HolderCkTypeId}' in '{CollectionName}' has the keys of the " +
                        "index of type '{CkTypeId}'; the type is served by it until the tiebreak index upgrade (AB#9342)",
                        legacyHolder.Name, legacyHolder.HolderType, collection.CollectionName,
                        SharedIndexKeySpecs.HolderOf(indexDefiningType));
                }

                TrackServedBy(indexDefiningType, legacyHolder.Name, indexName, collection.CollectionName,
                    targetShape: false);
                return;
            }
        }

        try
        {
            // Call the original method and check if index was actually created
            var created = await CreateOrUpdateIndex(collectionName, ckTypeIndex, repositoryIndices, collection,
                uniqueIndexNumber, collectionRootType, indexDefiningType, nameSuffix, keySpecs);

            // Track only a change (not an index that already existed under its expected name), under the name of the
            // index that serves the definition; an index it replaces is dropped by the cleanup after the build (AB#8960).
            if (created != null && indexDefiningType != null)
            {
                TrackServedBy(indexDefiningType, created.Name, indexName, collection.CollectionName,
                    targetShape: true);
                foreach (var replacedName in created.Replaces)
                {
                    _indexStateService.RemoveIndexState(indexDefiningType.CkTypeId, replacedName,
                        collection.CollectionName);
                }
            }
            else if (indexDefiningType != null)
            {
                // AB#9391: the index exists under the expected name with the definition (built by an earlier run, also
                // one aborted before it persisted its states, or by another process): Applied under it, which replaces a
                // failure an earlier run recorded there. An unchanged state is not written again.
                TrackServedBy(indexDefiningType, indexName, indexName, collection.CollectionName, targetShape: true);
            }

            // AB#9391: the definition is served in its target shape: a failure an earlier run recorded under its
            // alternate name is over (an Applied state there, the index built under that name, is kept).
            if (indexDefiningType != null && created?.Name != alternateName)
            {
                _indexStateService.RemoveIndexState(indexDefiningType.CkTypeId, alternateName,
                    collection.CollectionName, IndexState.Failed);
            }

            if (legacy != null)
            {
                keySpecs?.Replaced(legacy.Name);
                _logger.LogInformation(
                    "Index '{IndexName}' of '{CollectionName}' is in the tiebreak shape; the legacy index '{LegacyName}' " +
                    "is dropped after the collection's other indexes (AB#8960)",
                    created?.Name ?? indexName, collection.CollectionName, legacy.Name);
                if (indexDefiningType != null)
                {
                    _indexStateService.RemoveIndexState(indexDefiningType.CkTypeId, legacy.Name,
                        collection.CollectionName);
                }
            }
        }
        catch (MongoCommandException ex)
        {
            if (legacy != null)
            {
                // The tiebreak index was not built: the collection keeps its legacy index.
                repositoryIndices.Remove(legacy);
            }

            // Track failed index creation
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (indexDefiningType != null && indexName != null)
            {
                // AB#9391: the legacy index the upgrade could not replace still serves the definition - the type's own
                // or one another type kept earlier in this run after its build failed too: Applied under it, next to the
                // failure of the build.
                if (legacy != null && legacyRule != null)
                {
                    keySpecs?.Remember(legacyRule.LegacyIndex, legacy.Name,
                        string.Equals(legacy.Name, baseName, StringComparison.Ordinal)
                            ? SharedIndexKeySpecs.HolderOf(indexDefiningType)
                            : SharedIndexKeySpecs.UnknownHolder);
                }

                var servingLegacy = legacy?.Name ??
                                    (legacyRule != null ? keySpecs?.Holder(legacyRule.LegacyIndex)?.Name : null);
                if (servingLegacy != null)
                {
                    TrackServedBy(indexDefiningType, servingLegacy, indexName, collection.CollectionName,
                        targetShape: false);
                }

                // AB#9391: a failure never replaces the Applied state of an index that exists. When the index under this
                // definition's expected name serves the type in this run - the legacy index that keeps serving it (above,
                // CodeRabbit on PR #269), or another definition of the type (two definitions whose existing indexes hold
                // each other's keys), also one processed after this one (AB#9302) - the failure is recorded under the
                // alternate name the definition's index gets while the expected name is taken, the name a later successful
                // build uses. The name is chosen after every definition of the collection was processed.
                _indexStateService.TrackFailure(indexDefiningType.CkTypeId, indexName, alternateName,
                    collection.CollectionName, ex.Message);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Index creation failed but cannot track (indexDefiningType={TypeId}, indexName={IndexName})",
                    indexDefiningType?.CkTypeId.ToString() ?? "NULL", indexName ?? "NULL");
            }

            _logger.LogWarning(ex,
                "Failed to create index '{IndexName}' of type {IndexType} for '{CollectionName}': {ErrorMessage}",
                indexName, ckTypeIndex.IndexType, collection.CollectionName, ex.Message);

            // Don't rethrow - continue with other indexes
        }
    }

    /// <summary>
    ///     The types a Unique or UniqueNotDeleted index filters, as the strings <see cref="AlternateIndexName" /> hashes;
    ///     null for every other index type.
    /// </summary>
    private HashSet<string>? UniqueTypeIdStrings(CkTypeIndex ckTypeIndex, CkTypeInfo? collectionRootType,
        CkType? indexDefiningType) =>
        ckTypeIndex.IndexType is IndexTypes.Unique or IndexTypes.UniqueNotDeleted
            ? CollectTypeIdsForIndex(collectionRootType, indexDefiningType, ckTypeIndex.IndexType.ToString())
                .Select(t => t.ToString()).ToHashSet(StringComparer.Ordinal)
            : null;

    /// <summary>
    ///     AB#9391: the type a legacy index another type shares was built for, for the log line only: the type a stored
    ///     index state identifies, else <see cref="SharedIndexKeySpecs.UnknownHolder" />. A failed read never stops the
    ///     collection run.
    /// </summary>
    private async Task<string> FindStoredHolderForLogAsync(string indexName, string collectionName)
    {
        try
        {
            using var session = await GetSessionAsync();
            return (await _indexStateService.FindStoredHolderAsync(session, indexName, collectionName))?.ToString()
                   ?? SharedIndexKeySpecs.UnknownHolder;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the index states naming '{IndexName}' of '{CollectionName}'",
                indexName, collectionName);
            return SharedIndexKeySpecs.UnknownHolder;
        }
    }

    /// <summary>
    ///     Records that <paramref name="servedBy" /> serves the index of <paramref name="indexDefiningType" />: state
    ///     <c>Applied</c> under that name (an unchanged state is not written again, AB#9391). When it is not the type's
    ///     own expected name, the state under the expected name is removed:
    ///     <list type="bullet">
    ///         <item><paramref name="targetShape" /> true - the index has the definition (adopted under another name, or
    ///         shared with another type of the collection, AB#9342): every state, also a <c>Failed</c> one an earlier run
    ///         left there; the definition is served in its target shape.</item>
    ///         <item><paramref name="targetShape" /> false - a legacy index (AB#8960) kept by an update without the upgrade:
    ///         only an <c>Applied</c> state. A <c>Failed</c> state (a tiebreak build of the upgrade that failed) stays
    ///         visible until an index in the target shape serves the definition (AB#9391).</item>
    ///     </list>
    ///     An <c>Applied</c> state this run recorded under the expected name is never removed (see
    ///     <see cref="IndexStateService.RemoveIndexState" />).
    /// </summary>
    private void TrackServedBy(CkType? indexDefiningType, string servedBy, string expectedName, string collectionName,
        bool targetShape)
    {
        if (indexDefiningType == null)
        {
            return;
        }

        _indexStateService.TrackIndexOperation(indexDefiningType.CkTypeId, servedBy, collectionName, IndexState.Applied);
        if (!string.Equals(servedBy, expectedName, StringComparison.Ordinal))
        {
            if (targetShape)
            {
                _indexStateService.RemoveIndexState(indexDefiningType.CkTypeId, expectedName, collectionName);
            }
            else
            {
                _indexStateService.RemoveIndexState(indexDefiningType.CkTypeId, expectedName, collectionName,
                    IndexState.Applied);
            }
        }
    }

    /// <summary>
    /// Wrapper for DropIndexAsync that adds index state tracking
    /// </summary>
    private async Task DropIndexWithTracking<TKey, TDocument>(IMongoDbDataSourceCollection<TKey, TDocument> collection,
        string indexName, CkId<CkTypeId> typeId)
        where TDocument : class, new()
        where TKey : notnull
    {
        await collection.DropIndexAsync(indexName);
        _indexStateService.RemoveIndexState(typeId, indexName, collection.CollectionName);
    }

    private List<RtCkId<CkTypeId>> CollectTypeIdsForIndex(CkTypeInfo? collectionRootType, CkType? indexDefiningType,
        string indexTypeName)
    {
        if (collectionRootType == null || indexDefiningType == null)
        {
            throw DatabaseCkModelRepositoryException.IndexTypeNeedsCkTypeInfoAndIndexDefiningTypes(indexTypeName);
        }

        // Collect type IDs: the index-defining type + types that derive from it
        var typeIds = new List<RtCkId<CkTypeId>> { indexDefiningType.CkTypeId.ToRtCkId() };

        // Find all types that derive from the index-defining type (not the collection root)
        var derivedTypeIds = GetDerivedTypeIds(indexDefiningType.CkTypeId.ToRtCkId(), collectionRootType);
        typeIds.AddRange(derivedTypeIds);

        return typeIds;
    }

    private IEnumerable<RtCkId<CkTypeId>> GetDerivedTypeIds(RtCkId<CkTypeId> baseRtCkTypeId, CkTypeInfo ckTypeInfo)
    {
        // Get all inheritance relationships where the base type is our target type
        var directDerived = ckTypeInfo.InheritedTypes
            .Where(inheritance => inheritance.BaseCkTypeId.Equals(baseRtCkTypeId))
            .Select(inheritance => inheritance.InheritorCkTypeId.ToRtCkId())
            .ToList();

        var allDerived = new HashSet<RtCkId<CkTypeId>>(directDerived);

        // Recursively find derived types of the derived types
        foreach (var derivedType in directDerived)
        {
            var nestedDerived = GetDerivedTypeIds(derivedType, ckTypeInfo);
            foreach (var nestedType in nestedDerived)
            {
                allDerived.Add(nestedType);
            }
        }

        return allDerived;
    }

    private void AnalyseIndex(CkType ckTypeInfo, Dictionary<CkType, List<CkTypeIndex>> regularIndices,
        ref CkTypeIndex? textIndex)
    {
        var indices = new List<CkTypeIndex>();
        regularIndices.Add(ckTypeInfo, indices);

        if (ckTypeInfo.Indexes != null)
        {
            // Add regular indexes (Ascending, Unique, UniqueNotDeleted)
            indices.AddRange(
                ckTypeInfo.Indexes.Where(i =>
                    i.IndexType == IndexTypes.Ascending ||
                    i.IndexType == IndexTypes.Unique ||
                    i.IndexType == IndexTypes.UniqueNotDeleted).ToList());

            // Handle text indexes separately (only one text index allowed per collection)
            foreach (var index in ckTypeInfo.Indexes.Where(i => i.IndexType == IndexTypes.Text))
            {
                if (textIndex == null)
                {
                    textIndex = index;
                    continue;
                }

                if (textIndex.Language != index.Language)
                {
                    _logger.LogWarning(
                        "Text index for '{CkTypeId}' has different language '{Language}' than existing text index '{ExistingLanguage}'",
                        ckTypeInfo.CkTypeId.ToString(), index.Language, textIndex.Language);
                }

                textIndex.Fields = textIndex.Fields.Union(index.Fields).ToList();
            }
        }

        // AB#4978: a declared owner attribute gets an implicit ascending index — the OwnedOnly
        // data-permission predicate filters (ckTypeId, attributes.<owner>) on every read.
        // PrepareAndCreateIndex prepends ckTypeId, so the synthesized index matches that shape.
        // Appended after the declared indexes so their uniqueIndexNumber-based names stay stable;
        // synthesized only at the declaring type — derived types share the collection's index.
        if (!string.IsNullOrWhiteSpace(ckTypeInfo.OwnerAttributePath))
        {
            indices.Add(new CkTypeIndex
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields { AttributeNames = [ckTypeInfo.OwnerAttributePath] }
                ]
            });
        }
    }

    public async Task<IOctoSession> CreateSessionAsync()
    {
        return await _repositoryClient.GetSessionAsync();
    }

    public Task CreateRtAssociationIndexesAsync() => CreateRtAssociationIndexesAsync(CancellationToken.None);

    /// <summary>
    ///     AB#9302: the RtAssociations indexes; <paramref name="lockLostToken" /> (an index update's lost lock) stops
    ///     before the next create or drop.
    /// </summary>
    private async Task CreateRtAssociationIndexesAsync(CancellationToken lockLostToken)
    {
        _logger.LogDebug("Creating indexes for RtAssociations collection");

        var collection = RtMongoDbDataSourceAssociations;

        // Get existing indexes to check if they already exist
        var existingIndexes = await collection.GetIndexListAsync();

        var ckTypeIndices = new List<CkTypeIndex>
        {
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.OriginRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.OriginRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.TargetCkTypeId).ToCamelCase(),
                            nameof(RtAssociation.TargetRtId).ToCamelCase()
                        ]
                    }
                ]
            },
            // Index 9: Optimizes $lookup with foreignField "targetRtId" + pipeline filter
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.TargetRtId).ToCamelCase(),
                            nameof(RtAssociation.AssociationRoleId).ToCamelCase(),
                            nameof(RtAssociation.OriginCkTypeId).ToCamelCase()
                        ]
                    }
                ]
            },
            // Index 10: Supports filtering archived associations (rtState != Archived)
            new()
            {
                IndexType = IndexTypes.Ascending,
                Fields =
                [
                    new CkIndexFields
                    {
                        AttributeNames =
                        [
                            nameof(RtAssociation.RtState).ToCamelCase()
                        ]
                    }
                ]
            },
        };

        int uniqueIndexNumber = 0;

        foreach (CkTypeIndex ckTypeIndex in ckTypeIndices)
        {
            lockLostToken.ThrowIfCancellationRequested();
            _ = await CreateOrUpdateIndex(collection.CollectionName, ckTypeIndex, existingIndexes, collection,
                uniqueIndexNumber++);
        }

        // We cleanup old indexes that are not defined anymore
        foreach (var repositoryIndex in existingIndexes)
        {
            lockLostToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Dropping old index '{IndexName}' for RtAssociations collection",
                repositoryIndex.Name);
            await collection.DropIndexAsync(repositoryIndex.Name);
        }
    }

    private async Task<InstalledModelReferenceBinder> CreateReferenceBinderAsync(IOctoSession session,
        bool includeModelsInStateImporting)
    {
        var models = await CkModels.FindManyAsync(session,
            m => m.ModelState == ModelState.Available ||
                 (includeModelsInStateImporting && m.ModelState == ModelState.Importing));
        return new InstalledModelReferenceBinder(models.Select(m => m.Id));
    }

    /// <summary>
    ///     D3 / review M6: recomputes <see cref="CkTypeInfo.InheritedTypes" /> and <see cref="CkTypeInfo.Inheritances" />
    ///     of the collection roots in memory — like the <c>$graphLookup</c> (depth 0 = direct descendants) but on
    ///     inheritance rows whose major-qualified base ids are bound to the installed versions. Only runs when such a
    ///     row exists, so tenants with classic models only keep the database result unchanged.
    /// </summary>
    private async Task BindMajorQualifiedInheritancesAsync(IOctoSession session, List<CkTypeInfo> collectionRoots,
        bool includeModelsInStateImporting)
    {
        var allInheritances = await _ckTypeInheritances.FindManyAsync(session, _ => true);
        if (collectionRoots.Count == 0 || !allInheritances.Any(i => i.BaseCkTypeId.ModelId.IsMajorQualified))
        {
            return;
        }

        var binder = await CreateReferenceBinderAsync(session, includeModelsInStateImporting);
        foreach (var inheritance in allInheritances)
        {
            inheritance.BaseCkTypeId = binder.Bind(inheritance.BaseCkTypeId);
        }

        var byBase = allInheritances.ToLookup(i => i.BaseCkTypeId);
        var typesById = (await _ckTypes.FindManyAsync(session, _ => true)).ToDictionary(t => t.CkTypeId, t => t);

        foreach (var root in collectionRoots)
        {
            var inherited = new List<CkInheritedTypeInfo>();
            var visited = new HashSet<CkId<CkTypeId>> { root.CkTypeId };
            var frontier = new List<CkId<CkTypeId>> { root.CkTypeId };
            for (var depth = 0; frontier.Count > 0; depth++)
            {
                var next = new List<CkId<CkTypeId>>();
                foreach (var row in frontier.SelectMany(t => byBase[t]))
                {
                    if (!visited.Add(row.InheritorCkTypeId))
                    {
                        continue;
                    }

                    inherited.Add(new CkInheritedTypeInfo
                    {
                        InheritanceId = row.InheritanceId,
                        CkModelId = row.CkModelId,
                        ModelState = row.ModelState,
                        BaseCkTypeId = row.BaseCkTypeId,
                        InheritorCkTypeId = row.InheritorCkTypeId,
                        BaseTypeDepthIndex = depth
                    });
                    next.Add(row.InheritorCkTypeId);
                }

                frontier = next;
            }

            root.InheritedTypes = inherited;
            root.Inheritances = inherited
                .Select(i => typesById.TryGetValue(i.InheritorCkTypeId, out var type) ? type : null)
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();
        }
    }

    private IAggregateFluent<CkTypeInfo> AggregateCkTypeInfo(IAggregateFluent<CkType> aggregate)
    {
        return aggregate.GraphLookup(_ckTypeInheritances.GetMongoCollection(),
                x => x.InheritorCkTypeId,
                x => x.BaseCkTypeId,
                x => x.CkTypeId,
                (CkTypeInfo x) => x.InheritedTypes, (CkInheritedTypeInfo i) => i.BaseTypeDepthIndex)
            .Lookup<CkTypeInfo, CkTypeInfo>(CkTypes.CollectionName,
                "inheritedTypes.inheritorCkTypeId",
                "_id",
                "Inheritances");
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle> AcquireModelImportLockAsync(string modelName,
        CancellationToken cancellationToken = default)
    {
        var lockId = $"{ModelImportLockIdPrefix}{modelName}";
        _logger.LogInformation("Acquiring model import lock for '{ModelName}' with lock ID '{LockId}'", modelName, lockId);

        var lockService = new RepositoryDistributedLockService(_repositoryClient, _repository, _logger, lockId);
        try
        {
            await lockService.AcquireLockAsync(cancellationToken);
        }
        catch
        {
            await lockService.DisposeAsync();
            throw;
        }

        // AB#8947: readers in this process see the import with their next resolve, not only after the cache TTL.
        CkModelImportLockCache.Shared.NotifyImportLockChanged(this);
        return new CacheNotifyingLockHandle(lockService, this);
    }

    /// <summary>AB#8947: releases the import lock and drops the process's cached answer of the running imports.</summary>
    private sealed class CacheNotifyingLockHandle(IDistributedLockHandle inner, ICkMongoDbRepositoryDataSource dataSource)
        : IDistributedLockHandle
    {
        public CancellationToken LockLostToken => inner.LockLostToken;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync();
            }
            finally
            {
                CkModelImportLockCache.Shared.NotifyImportLockChanged(dataSource);
            }
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetModelsBeingImportedAsync(CancellationToken cancellationToken = default)
    {
        return RepositoryDistributedLockService.FindHeldLockIdSuffixesAsync(_repository, ModelImportLockIdPrefix,
            cancellationToken);
    }

    internal QueryResultCacheService CreateQueryResultCacheService()
    {
        return new QueryResultCacheService(_repository);
    }

    /// <inheritdoc />
    public async Task<CollectionCleanupResult> CleanupEmptyAbstractTypeCollectionsAsync(IOctoSession session)
    {
        _logger.LogInformation("Starting cleanup of empty abstract type collections for tenant '{TenantId}'", TenantId);

        var result = new CollectionCleanupResult();
        const string rtEntityPrefix = "RtEntity_";

        // Step 1: Get all RtEntity collections
        var allCollections = await _repository.ListCollectionNamesAsync(rtEntityPrefix);
        result.TotalAnalyzed = allCollections.Count;

        _logger.LogDebug("Found {Count} RtEntity collections to analyze", allCollections.Count);

        // Step 2: Build set of valid collection suffixes (collection roots)
        var validTypes = await _ckTypes.FindManyAsync(session,
            t => t.IsCollectionRoot && t.ModelState == ModelState.Available);

        var validCollectionSuffixes = new HashSet<string>(
            validTypes.Select(t => t.CkTypeId.ToRtCkId().GetCkTypeCollectionName()));

        _logger.LogDebug("Found {Count} valid collection root types", validTypes.Count);

        // Step 3: Analyze each collection - find orphaned collections
        foreach (var collectionName in allCollections)
        {
            var suffix = collectionName.Substring(rtEntityPrefix.Length);

            // Skip if this is a valid collection root
            if (validCollectionSuffixes.Contains(suffix))
            {
                continue;
            }

            _logger.LogDebug("Analyzing orphaned collection '{CollectionName}'", collectionName);

            // Step 4: Check if the collection is empty
            var documentCount = await _repository.GetCollectionDocumentCountAsync(collectionName);

            if (documentCount > 0)
            {
                _logger.LogWarning(
                    "Collection '{CollectionName}' is orphaned but contains {DocumentCount} documents - skipping",
                    collectionName, documentCount);

                result.SkippedCollections.Add(new CollectionSkipInfo
                {
                    CollectionName = collectionName,
                    DocumentCount = documentCount,
                    Reason = $"Collection contains {documentCount} documents"
                });
                continue;
            }

            // Step 5: Delete the empty orphaned collection
            _logger.LogInformation("Deleting empty orphaned collection '{CollectionName}'", collectionName);

            await _repository.DropCollectionAsync(collectionName);
            result.DeletedCollections.Add(collectionName);
        }

        _logger.LogInformation(
            "Cleanup completed for tenant '{TenantId}': Analyzed={Analyzed}, Deleted={Deleted}, Skipped={Skipped}",
            TenantId, result.TotalAnalyzed, result.DeletedCollections.Count, result.SkippedCollections.Count);

        return result;
    }
}
