using System.Diagnostics;
using System.Text.RegularExpressions;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.Repository;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.CkCache;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Wrapper for session lifecycle management.
///     Only disposes the session if it was created by this scope (not passed from outside).
///     Also manages transactions - only starts/commits if we own the session.
/// </summary>
internal readonly struct SessionScope : IAsyncDisposable
{
    private readonly IOctoSession _session;
    private readonly bool _ownsSession;

    private SessionScope(IOctoSession session, bool ownsSession)
    {
        _session = session;
        _ownsSession = ownsSession;
    }

    public IOctoSession Session => _session;

    /// <summary>
    ///     Indicates whether this scope owns the session (created it).
    ///     If true, the session will be disposed when this scope is disposed.
    ///     Transaction management should only be done when this is true.
    /// </summary>
    public bool OwnsSession => _ownsSession;

    public static async Task<SessionScope> CreateAsync(TenantDatabaseSourceIdentifier sourceIdentifier)
    {
        if (sourceIdentifier.Session != null)
        {
            return new SessionScope(sourceIdentifier.Session, ownsSession: false);
        }

        var session = await sourceIdentifier.MongoDbRepositoryDataSource.CreateSessionAsync();
        return new SessionScope(session, ownsSession: true);
    }

    /// <summary>
    ///     Starts a transaction if this scope owns the session.
    /// </summary>
    public void StartTransactionIfOwned()
    {
        if (_ownsSession)
        {
            _session.StartTransaction();
        }
    }

    /// <summary>
    ///     Commits the transaction if this scope owns the session.
    /// </summary>
    public async Task CommitTransactionIfOwnedAsync()
    {
        if (_ownsSession)
        {
            await _session.CommitTransactionAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsSession)
        {
            _session.Dispose();
        }

        await ValueTask.CompletedTask;
    }
}

/// <summary>
///     Implements a CK model repository that stores the CK model in a (octo) database.
/// </summary>
public class DatabaseCkModelRepository : IDatabaseCkModelRepository
{
    private readonly IRepositoryModelResolver _repositoryModelResolver;
    private readonly ICkModelImportAuditTrail _importAuditTrail;
    private readonly ILogger<DatabaseCkModelRepository> _logger;

    /// <summary>
    ///     Creates a new instance of the <see cref="DatabaseCkModelRepository" /> class.
    /// </summary>
    public DatabaseCkModelRepository(ILogger<DatabaseCkModelRepository> logger,
        IRepositoryModelResolver repositoryModelResolver,
        ICkModelImportAuditTrail importAuditTrail)
    {
        _logger = logger;
        _repositoryModelResolver = repositoryModelResolver;
        _importAuditTrail = importAuditTrail;
    }

    /// <inheritdoc />
    public async Task<ModelExistingResult> IsExistingAsync(CkModelIdVersionRange modelIdVersionRange,
        object? sourceIdentifier = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);

        await using var scope = await SessionScope.CreateAsync(sourceIdentifierObject);
        var session = scope.Session;

        // AB#5901: the post-import re-validation also resolves ResolveFailed models (and their dependencies).
        var includeResolveFailed = sourceIdentifierObject.IncludeResolveFailedModels;
        var ckModels = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels.FindManyAsync(session,
            e => e.ModelId == modelIdVersionRange.Name && (e.ModelState == ModelState.Available ||
                                                           (includeResolveFailed &&
                                                            e.ModelState == ModelState.ResolveFailed)));

        var satisfiedModels = ckModels
            .Where(m => modelIdVersionRange.IsSatisfiedBy(m.Id))
            .ToList();

        if (!satisfiedModels.Any())
        {
            return new ModelExistingResult { Exists = false };
        }

        // Return the latest satisfied version
        var latestSatisfiedModel = satisfiedModels
            .OrderByDescending(m => m.Id.Version)
            .First();

        return new ModelExistingResult { Exists = true, ModelId = latestSatisfiedModel.Id };
    }

    /// <inheritdoc />
    public async Task<bool> IsExistingAsync(CkModelId modelId, object? sourceIdentifier = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);

        await using var scope = await SessionScope.CreateAsync(sourceIdentifierObject);
        var session = scope.Session;

        var ckModel = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels
            .FindSingleOrDefaultAsync(session, e => e.Id == modelId && e.ModelState == ModelState.Available);

        return ckModel != null;
    }

    /// <inheritdoc />
    public async Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel,
        object? sourceIdentifier = null, CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);

        OperationResult operationResult = new();
        var transientCkModel = new TransientCkModel(new CkModel
        {
            Id = ckCompiledModel.ModelId,
            Description = ckCompiledModel.Description,
            Dependencies = ckCompiledModel.Dependencies?.ToArray()
        });
        await ExecuteImport(ckCompiledModel, transientCkModel,
            sourceIdentifierObject.MongoDbRepositoryDataSource,
            operationResult, sourceIdentifier, sourceIdentifierObject.TenantId, cancellationToken);
    }

    public async Task<CkCompiledModelRoot?> TryLookupCkModelAsync(CkModelId ckModelId, OperationResult operationResult,
        object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);

        await using var scope = await SessionScope.CreateAsync(sourceIdentifierObject);
        var session = scope.Session;

        // AB#5901: see IsExistingAsync(CkModelIdVersionRange) — re-validation only.
        var includeResolveFailed = sourceIdentifierObject.IncludeResolveFailedModels;
        var ckModel = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels
            .FindSingleOrDefaultAsync(session, e => e.Id == ckModelId && (e.ModelState == ModelState.Available ||
                                                                          (includeResolveFailed &&
                                                                           e.ModelState == ModelState.ResolveFailed)));
        if (ckModel == null)
        {
            return null;
        }

        var ckEnums = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkEnums
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckRecords = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkRecords
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckRecordInheritances = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkRecordInheritances
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckAttributes = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkAttributes
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckTypes = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypes
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckTypeInheritances = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypeInheritances
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckTypeAssociations = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypeAssociations
            .FindManyAsync(session, e => e.CkModelId == ckModelId);
        var ckAssociationRoles = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkAssociationRoles
            .FindManyAsync(session, e => e.CkModelId == ckModelId);

        var ckCompiledModelRoot = new CkCompiledModelRoot
        {
            ModelId = ckModel.Id,
            Description = ckModel.Description,
            Dependencies = ckModel.Dependencies?.ToList(),
            Enums = ckEnums.Select(e => new CkEnumDto
            {
                EnumId = e.CkEnumId.ElementId,
                Description = e.Description,
                UseFlags = e.UseFlags,
                IsExtensible = e.IsExtensible,
                Values =
                    e.Values.Select(v => new CkEnumValueDto
                    {
                        Key = v.Key, Name = v.Name, Description = v.Description, IsExtension = v.IsExtension
                    }).ToList()
            }).ToList(),
            Records = ckRecords.Select(r => new CkRecordDto
            {
                RecordId = r.CkRecordId.ElementId,
                Description = r.Description,
                // AB#5533: the record key must survive the round-trip into the runtime CK cache.
                RecordKey = r.RecordKey,
                IsAbstract = r.IsAbstract,
                IsFinal = r.IsFinal,
                Attributes = r.Attributes.Select(a => new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional,
                    // AB#5187: per-assignment ownership override; null = inherit from the definition.
                    Ownership = a.Ownership
                }).ToList(),
                DerivedFromCkRecordId = ckRecordInheritances.FirstOrDefault(x => x.InheritorCkRecordId == r.CkRecordId)
                    ?.BaseCkRecordId
            }).ToList(),
            Attributes = ckAttributes.Select(a => new CkAttributeDto
            {
                AttributeId = a.CkAttributeId.ElementId,
                ValueType = a.AttributeValueType,
                ValueCkEnumId = a.ValueCkEnumId,
                ValueCkRecordId = a.ValueCkRecordId,
                DefaultValues = a.DefaultValues?.ToList(),
                Description = a.Description,
                // AB#5187: the declared ownership wins; IsRuntimeState stays the fallback for
                // documents written before it existed (Ownership == null there).
                Ownership = a.Ownership,
                IsRuntimeState = a.IsRuntimeState,
                MetaData = a.MetaData?.Select(m =>
                    new CkAttributeMetaDataDto { Key = m.Key, Value = m.Value, Description = m.Description }).ToList()
            }).ToList(),
            Types = ckTypes.Select(t => new CkCompiledTypeDto
            {
                TypeId = t.CkTypeId.ElementId,
                Description = t.Description,
                IsAbstract = t.IsAbstract,
                IsFinal = t.IsFinal,
                IsCollectionRoot = t.IsCollectionRoot,
                EnableChangeStreamPreAndPostImages = t.EnableChangeStreamPreAndPostImages,
                DisplayNameRule = t.DisplayNameRule,
                DisplayDescriptionRule = t.DisplayDescriptionRule,
                OwnerAttributePath = t.OwnerAttributePath,
                Attributes = t.Attributes.Select(a => new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional,
                    // AB#5187: per-assignment ownership override; null = inherit from the definition.
                    Ownership = a.Ownership
                }).ToList(),
                Associations = ckTypeAssociations.Where(x => x.OriginCkTypeId == t.CkTypeId).Select(a =>
                    new CkTypeAssociationDto
                    {
                        CkRoleId = a.RoleId,
                        TargetCkTypeId = a.TargetCkTypeId,
                        TargetCkAttributeIds = a.TargetCkAttributeIds?.ToList()
                    }).ToList(),
                DerivedFromCkTypeId = ckTypeInheritances.FirstOrDefault(x => x.InheritorCkTypeId == t.CkTypeId)
                    ?.BaseCkTypeId
            }).ToList(),
            AssociationRoles = ckAssociationRoles.Select(ar => new CkAssociationRoleDto
            {
                AssociationRoleId = ar.RoleId.ElementId,
                Description = ar.Description,
                InboundMultiplicity = ar.InboundMultiplicity,
                OutboundMultiplicity = ar.OutboundMultiplicity,
                InboundName = ar.InboundName,
                OutboundName = ar.OutboundName,
                Attributes = ar.Attributes.Select(a => new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional,
                    // AB#5187: per-assignment ownership override; null = inherit from the definition.
                    Ownership = a.Ownership
                }).ToList()
            }).ToList()
        };

        return ckCompiledModelRoot;
    }

    /// <inheritdoc />
    public async Task CustomizeCkEnumAsync(CkId<CkEnumId> ckEnumId, ICollection<CkEnumUpdate> ckEnumUpdates,
        object? sourceIdentifier = null, CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);

        await using var scope = await SessionScope.CreateAsync(sourceIdentifierObject);
        var session = scope.Session;

        try
        {
            scope.StartTransactionIfOwned();
            var dbCkEnum = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkEnums.FindSingleOrDefaultAsync(
                session,
                @enum => @enum.CkEnumId == ckEnumId);

            if (dbCkEnum == null)
            {
                throw DatabaseCkModelRepositoryException.CkEnumNotFound(ckEnumId);
            }

            if (!dbCkEnum.IsExtensible)
            {
                throw DatabaseCkModelRepositoryException.CkEnumNotExtensible(ckEnumId);
            }

            // System enum values cannot be customized, we store it in a separate list to know them
            var systemValues = dbCkEnum.Values.Where(x => !x.IsExtension).ToList();

            // Let's build a new list of enum values
            var newEnumValueList = dbCkEnum.Values.ToList();

            // Remove all enums that are marked for deletion, except system defined.
            foreach (var enumValueToRemove in
                     ckEnumUpdates.Where(x => x.Operation == CkExtensionUpdateOperations.Delete))
            {
                if (systemValues.Any(x => x.Key == enumValueToRemove.Value.Key))
                {
                    throw DatabaseCkModelRepositoryException.CkEnumValueIsSystem(ckEnumId, enumValueToRemove.Value.Key);
                }

                newEnumValueList.RemoveAll(x => x.Key == enumValueToRemove.Value.Key);
            }

            // Add all new enums
            foreach (var enumValueToAdd in ckEnumUpdates.Where(x => x.Operation == CkExtensionUpdateOperations.Insert))
            {
                if (enumValueToAdd.Value.Key < 0)
                {
                    throw DatabaseCkModelRepositoryException.CkEnumValueKeyInvalid(ckEnumId, enumValueToAdd.Value.Key);
                }

                if (newEnumValueList.Any(x => x.Key == enumValueToAdd.Value.Key))
                {
                    throw DatabaseCkModelRepositoryException.CkEnumValueAlreadyExists(ckEnumId,
                        enumValueToAdd.Value.Key);
                }

                if (string.IsNullOrWhiteSpace(enumValueToAdd.Value.Name))
                {
                    throw DatabaseCkModelRepositoryException.CkEnumValueNameCannotBeEmpty(ckEnumId,
                        enumValueToAdd.Value.Key);
                }

                if (!Regex.IsMatch(enumValueToAdd.Value.Name, "^[_a-zA-Z][_a-zA-Z0-9]*$"))
                {
                    throw DatabaseCkModelRepositoryException.CkEnumValueNameInvalid(ckEnumId, enumValueToAdd.Value.Key,
                        enumValueToAdd.Value.Name);
                }

                if (newEnumValueList.Any(x => x.Name == enumValueToAdd.Value.Name))
                {
                    throw DatabaseCkModelRepositoryException.CkEnumNameAlreadyExists(ckEnumId,
                        enumValueToAdd.Value.Key, enumValueToAdd.Value.Name);
                }

                newEnumValueList.Add(new CkEnumValue
                {
                    Key = enumValueToAdd.Value.Key,
                    Name = enumValueToAdd.Value.Name,
                    Description = enumValueToAdd.Value.Description,
                    IsExtension = true
                });
            }

            var updateDefinition = Builders<CkEnum>.Update.Set(x => x.Values, newEnumValueList);
            await sourceIdentifierObject.MongoDbRepositoryDataSource.CkEnums.UpdateOneAsync(session, ckEnumId,
                updateDefinition);

            await scope.CommitTransactionIfOwnedAsync();
        }
        catch (Exception e)
        {
            throw DatabaseCkModelRepositoryException.ErrorDuringUpdateOfCkEnumExtensions(ckEnumId, e);
        }
    }

    private async Task ExecuteImport(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource, OperationResult operationResult,
        object? sourceIdentifier,
        string? tenantId,
        CancellationToken? cancellationToken)
    {
        _logger.LogInformation("Executing import of CK model '{CkModelId}' to database", compiledModel.ModelId);

        // Acquire distributed lock to prevent parallel imports of the same model.
        // Pass the caller's cancellation token so the polling loop is interruptable.
        await using var importLock = await mongoDbRepositoryDataSource.AcquireModelImportLockAsync(
            compiledModel.ModelId.Name, cancellationToken ?? CancellationToken.None);

        // Link the caller's token with the lock's LockLostToken: if the heartbeat detects we no
        // longer own the lock (e.g. our TTL expired under load and another service claimed it),
        // the import must abort to prevent split-brain writes against shared CK collections.
        using var linkedImportCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken ?? CancellationToken.None, importLock.LockLostToken);
        cancellationToken = linkedImportCts.Token;

        var guardedIdentifier = sourceIdentifier as TenantDatabaseSourceIdentifier;
        if (guardedIdentifier is { GuardAgainstDowngrade: true })
        {
            // CK v2 F1.0-S1 (AB#5900, G-H1): an embedded/startup import repeats the by-name decision under the
            // lock. The caller decided before waiting for the lock; meanwhile another service may have installed
            // this or a newer version (parallel stack start, rolling update, fresh tenant set up by two services).
            // Without this check the exact-id re-check below misses the newer row, and InsertModelWithImportingState
            // deletes it — the downgrade the guard exists to prevent.
            var installed = await EmbeddedCkModelImportGuard.FindInstalledAsync(mongoDbRepositoryDataSource,
                compiledModel.ModelId.Name);
            var decision = EmbeddedCkModelImportGuard.Decide(compiledModel.ModelId, installed?.ModelId);
            if (decision != EmbeddedImportDecision.Import)
            {
                EmbeddedCkModelImportGuard.Report(decision, compiledModel.ModelId, installed, tenantId, _logger,
                    underLock: true);
                guardedIdentifier.ImportOutcome.SkippedUnderLock = true;
                guardedIdentifier.ImportOutcome.InstalledModelId = installed!.ModelId;
                return;
            }
        }
        else
        {
            // Re-check after acquiring the lock: another service may have already imported
            // the model while we were waiting for the lock
            using var checkSession = await mongoDbRepositoryDataSource.CreateSessionAsync();
            var existingModel = await mongoDbRepositoryDataSource.CkModels
                .FindSingleOrDefaultAsync(checkSession,
                    e => e.Id == compiledModel.ModelId && e.ModelState == ModelState.Available);
            if (existingModel != null)
            {
                _logger.LogInformation(
                    "CK model '{CkModelId}' was already imported by another service while waiting for the lock, skipping import",
                    compiledModel.ModelId);
                return;
            }
        }

        // Insert model with Importing state (now safe because we have the lock)
        await InsertModelWithImportingState(compiledModel, mongoDbRepositoryDataSource);

        // G-L3: once the element rows are committed, a failure of the post-work must not delete the CkModel row
        // (that would orphan the committed rows). The row then stays Importing; the next import of the model name
        // replaces it.
        var elementsCommitted = false;
        try
        {
            // Pre-validate that all system CkTypeId references in the compiled model
            // match actually installed system model versions.  Compiled models contain
            // exact versioned references (e.g. System-2.0.7/Entity-1) that must exist
            // in the database; a minor version mismatch will cause the import to fail.
            // Skip this check for system models themselves (they are the root models
            // being installed and don't depend on a previously installed version).
            var modelName = compiledModel.ModelId.Name;
            if (modelName != "System" && !modelName.StartsWith("System.", StringComparison.Ordinal))
            {
                await ValidateSystemReferencesAsync(compiledModel, mongoDbRepositoryDataSource);
            }

            _logger.LogInformation("Validating of CK model '{CkModelId}'", compiledModel.ModelId);
            var originFileResolver = new OriginFileResolver("-");
            await _repositoryModelResolver.HardResolveAsync(compiledModel, originFileResolver, operationResult,
                sourceIdentifier);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                _logger.LogInformation("Import of CK model '{CkModelId}' failed, model is not valid",
                    compiledModel.ModelId);
                operationResult.WriteMessagesToLogger(_logger);
                throw OperationFailedException.ValidationErrors();
            }

            _logger.LogInformation("Starting import of CK model '{CkModelId}'", compiledModel.ModelId);

            CheckCancellation(cancellationToken);

            ProcessCkRecords(compiledModel, transientCkModel);
            ProcessCkEnums(compiledModel, transientCkModel);
            ProcessCkAttributes(compiledModel, transientCkModel);
            ProcessCkAssociationRoles(compiledModel, transientCkModel);
            ProcessCkTypesAndAssociations(compiledModel, transientCkModel);

            // ValidateAsync
            Debug.Assert(_repositoryModelResolver != null, nameof(_repositoryModelResolver) + " != null");

            using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
            session.StartTransaction();

            _logger.LogDebug("Preparing import of CK model to database");

            // Create basic collections first (later this method is called again to create CkType document collections)
            await mongoDbRepositoryDataSource.UpdateCollectionsAsync(session);
            CheckCancellation(cancellationToken);

            // Preserve extension values from extensible enums before deleting the old version
            await PreserveExtensibleEnumValues(session, compiledModel.ModelId, mongoDbRepositoryDataSource,
                transientCkModel, tenantId);
            CheckCancellation(cancellationToken);

            _logger.LogDebug("Deleting previous version of CK model");

            // Delete the old version
            await DeletePreviousVersion(session, compiledModel.ModelId, mongoDbRepositoryDataSource, cancellationToken);
            CheckCancellation(cancellationToken);

            _logger.LogDebug("Importing CK model to database");
            if (transientCkModel.CkEnums.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkEnums.BulkImportAsync(session,
                        transientCkModel.CkEnums.ToArray(), BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkRecords.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkRecords.BulkImportAsync(session,
                        transientCkModel.CkRecords.ToArray(), BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkAttributes.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkAttributes.BulkImportAsync(session,
                        transientCkModel.CkAttributes.ToArray(), BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkAssociationRoles.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkAssociationRoles.BulkImportAsync(session,
                        transientCkModel.CkAssociationRoles.ToArray(), BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkTypes.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkTypes.BulkImportAsync(session,
                        transientCkModel.CkTypes.ToArray(), BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkTypeAssociations.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkTypeAssociations.BulkImportAsync(session,
                        transientCkModel.CkTypeAssociations, BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkTypeInheritances.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkTypeInheritances.BulkImportAsync(session,
                        transientCkModel.CkTypeInheritances, BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            if (transientCkModel.CkRecordInheritances.Any())
            {
                ValidateAndThrow(
                    await mongoDbRepositoryDataSource.CkRecordInheritances.BulkImportAsync(session,
                        transientCkModel.CkRecordInheritances, BulkOperationOptions.Default));
                CheckCancellation(cancellationToken);
            }

            _logger.LogDebug("Updating collections");
            // This operation is critical. It forces an exclusive write lock on the database.
            // Skip cleanup on the second call - it was already done in the first call and is
            // expensive on large tenants (counts documents in orphaned collections).
            // includeModelsInStateImporting: the types we just bulk-inserted are still in
            // ModelState.Importing — they flip to Available later in sessionComplete. Without
            // opting in, UpdateCollectionsAsync would filter them out and their RtEntity_*
            // collections would only be auto-created by MongoDB on first insert, without the
            // changeStreamPreAndPostImages option applied.
            await mongoDbRepositoryDataSource.UpdateCollectionsAsync(
                session, includeModelsInStateImporting: true, skipCleanup: true);
            CheckCancellation(cancellationToken);

            _logger.LogDebug("Committing model import transaction");
            await session.CommitTransactionAsync();
            elementsCommitted = true;

            _logger.LogDebug("Pos-work of CK model import");
            using var indexUpdateSession = await mongoDbRepositoryDataSource.CreateSessionAsync();
            indexUpdateSession.StartTransaction();

            // Attention! This operation is critical. It forces an exclusive write lock on the database.
            // Scope to the imported model to avoid re-indexing all collection roots in the tenant.
            _logger.LogDebug("Updating index");
            await mongoDbRepositoryDataSource.UpdateIndexAsync(indexUpdateSession, true, compiledModel.ModelId,
                cancellationToken ?? CancellationToken.None);
            CheckCancellation(cancellationToken);

            await indexUpdateSession.CommitTransactionAsync();

            // State flip + re-validation of all models in one transaction, retried on transient transaction errors
            // (G-L3: parallel imports by several services re-validate the same models and can write-conflict).
            var revalidation = await RunInTransactionWithRetryAsync(mongoDbRepositoryDataSource, async sessionComplete =>
            {
                _logger.LogDebug("Updating model state");
                await UpdateModelStateAsync(sessionComplete, mongoDbRepositoryDataSource, compiledModel.ModelId,
                    ModelState.Available);

                _logger.LogDebug("Validating dependencies of other CK models");
                var result = await ValidateDependencies(sessionComplete, mongoDbRepositoryDataSource);
                CheckCancellation(cancellationToken);
                return result;
            }, $"state update and re-validation after the import of '{compiledModel.ModelId}'");

            ReportRevalidation(revalidation);
            await RestoreCollectionsOfRecoveredModelsAsync(mongoDbRepositoryDataSource, revalidation.Recovered,
                cancellationToken);
            if (guardedIdentifier != null)
            {
                guardedIdentifier.ImportOutcome.RecoveredModelIds = revalidation.Recovered;
            }

            _logger.LogInformation("Import of CK model {CkModelId} to database succeeded", compiledModel.ModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import of CK model {CkModelId}  to database failed", compiledModel.ModelId);

            if (elementsCommitted)
            {
                _logger.LogError(
                    "The elements of CK model '{CkModelId}' were already committed; its CkModel row is kept (state " +
                    "Importing) and is replaced by the next import of the model",
                    compiledModel.ModelId);
                throw;
            }

            using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
            session.StartTransaction();

            _logger.LogDebug("Rolling back CK model import transaction");
            await mongoDbRepositoryDataSource.CkModels.DeleteOneAsync(session, compiledModel.ModelId);

            await session.CommitTransactionAsync();

            throw;
        }
    }

    private static async Task UpdateModelStateAsync(IOctoSession sessionComplete,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        CkModelId ckModelId, ModelState modelState)
    {
        await mongoDbRepositoryDataSource.CkTypeAssociations.UpdateManyAsync(sessionComplete,
            Builders<CkTypeAssociation>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkTypeAssociation>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkRecordInheritances.UpdateManyAsync(sessionComplete,
            Builders<CkRecordInheritance>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkRecordInheritance>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkTypeInheritances.UpdateManyAsync(sessionComplete,
            Builders<CkTypeInheritance>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkTypeInheritance>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkRecords.UpdateManyAsync(sessionComplete,
            Builders<CkRecord>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkRecord>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkTypes.UpdateManyAsync(sessionComplete,
            Builders<CkType>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkType>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkAttributes.UpdateManyAsync(sessionComplete,
            Builders<CkAttribute>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkAttribute>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkEnums.UpdateManyAsync(sessionComplete,
            Builders<CkEnum>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkEnum>.Update.Set(x => x.ModelState, modelState));
        await mongoDbRepositoryDataSource.CkAssociationRoles.UpdateManyAsync(sessionComplete,
            Builders<CkAssociationRole>.Filter.Eq(x => x.CkModelId, ckModelId),
            Builders<CkAssociationRole>.Update.Set(x => x.ModelState, modelState));

        await mongoDbRepositoryDataSource.CkModels.UpdateOneAsync(sessionComplete, ckModelId,
            Builders<CkModel>.Update.Set(x => x.ModelState, modelState));
    }

    /// <summary>
    ///     Outcome of one re-validation pass (CK v2 F1.0-S2, AB#5901). Logged and counted by
    ///     <see cref="ReportRevalidation" /> only after the transaction committed, so a retried transaction is not
    ///     reported twice.
    /// </summary>
    internal sealed record RevalidationResult(
        IReadOnlyCollection<CkModelId> Recovered,
        IReadOnlyCollection<(CkModelId ModelId, string Reason)> NewlyFailed,
        IReadOnlyCollection<(CkModelId ModelId, string Reason)> StillFailed,
        IReadOnlyDictionary<CkModelId, CkModelId[]> Dependencies);

    /// <summary>
    ///     Re-validates every installed model after an import (end of <c>ExecuteImport</c>, inside the transaction
    ///     that flips the imported model to <c>Available</c>) or on its own (<see cref="RevalidateAsync" />).
    ///     CK v2 F1.0-S2 (AB#5901): <c>ResolveFailed</c> models are resolved too, so a model whose dependencies became
    ///     satisfiable again (an upgrade, an explicit downgrade, a missing dependency imported) returns to
    ///     <c>Available</c> without manual action — before, only <c>Available</c> models were checked and
    ///     <c>ResolveFailed</c> never recovered (R2-3). <c>Importing</c> models are never touched.
    /// </summary>
    private async Task<RevalidationResult> ValidateDependencies(IOctoSession session,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        // The resolver may load ResolveFailed models for this call only (see TenantDatabaseSourceIdentifier).
        var sourceIdentifier = new TenantDatabaseSourceIdentifier(session, mongoDbRepositoryDataSource,
            IncludeResolveFailedModels: true);
        OperationResult operationResult = new();
        var ckModels =
            await mongoDbRepositoryDataSource.CkModels.FindManyAsync(session,
                m => m.ModelState == ModelState.Available || m.ModelState == ModelState.ResolveFailed);
        var originFileResolver = new OriginFileResolver("-");
        var resolveResult = await _repositoryModelResolver.SoftResolveAsync(ckModels.Select(x => x.Id).ToList(),
            originFileResolver, operationResult, sourceIdentifier);

        // TODO CK v2 S3 (F1.0-S4 range-retention run, review I2): the missing-dependency seed below and the
        // engine's RepositoryDependencyResolver both use the exact Dependencies. Range-retaining models must be
        // judged by their persisted dependency ranges (CkModel.DependencyRanges, F1.3-S1), otherwise they go
        // ResolveFailed on an additive System bump. ComputeFailedModels takes the dependencies as input so the
        // ranges can be passed in there.
        var failed = ComputeFailedModels(ckModels.Select(m => (m.Id, (IReadOnlyCollection<CkModelId>)(m.Dependencies ?? []))).ToList(),
            resolveResult.SkippedModelIds, resolveResult.FailedModelIds);

        var recovered = new List<CkModelId>();
        var newlyFailed = new List<(CkModelId, string)>();
        var stillFailed = new List<(CkModelId, string)>();
        foreach (var ckModel in ckModels)
        {
            var isFailed = failed.ContainsKey(ckModel.Id);
            if (ckModel.ModelState == ModelState.Available && isFailed)
            {
                await UpdateModelStateAsync(session, mongoDbRepositoryDataSource, ckModel.Id, ModelState.ResolveFailed);
                newlyFailed.Add((ckModel.Id, failed[ckModel.Id]));
            }
            else if (ckModel.ModelState == ModelState.ResolveFailed && !isFailed)
            {
                await UpdateModelStateAsync(session, mongoDbRepositoryDataSource, ckModel.Id, ModelState.Available);
                recovered.Add(ckModel.Id);
            }
            else if (ckModel.ModelState == ModelState.ResolveFailed)
            {
                stillFailed.Add((ckModel.Id, failed[ckModel.Id]));
            }
        }

        return new RevalidationResult(recovered, newlyFailed, stillFailed,
            ckModels.ToDictionary(m => m.Id, m => m.Dependencies ?? []));
    }

    private void ReportRevalidation(RevalidationResult result)
    {
        foreach (var (modelId, reason) in result.NewlyFailed)
        {
            _logger.LogWarning(
                "CK model '{CkModelId}' no longer resolves and is marked as ResolveFailed: {Reason}. " +
                "It is re-validated after every CK model import and returns to Available once it resolves",
                modelId, reason);
        }

        foreach (var modelId in result.Recovered)
        {
            _logger.LogInformation(
                "CK model '{CkModelId}' resolves again (dependencies {Dependencies} satisfied) and is Available",
                modelId, string.Join(", ", result.Dependencies.TryGetValue(modelId, out var d) ? d : []));
            CkModelImportDiagnostics.RecordRevalidation(CkModelImportDiagnostics.ResultRecovered);
        }

        foreach (var (modelId, reason) in result.StillFailed)
        {
            // Still failing: no log spam on every import, the reason was logged when it failed.
            _logger.LogDebug("CK model '{CkModelId}' still does not resolve: {Reason}", modelId, reason);
            CkModelImportDiagnostics.RecordRevalidation(CkModelImportDiagnostics.ResultStillFailed);
        }
    }

    /// <summary>
    ///     G-M2: a recovered model gets its collection roots and indexes back. While it was <c>ResolveFailed</c> its
    ///     types were not part of collection/index maintenance, and the collections of a model that went
    ///     <c>ResolveFailed</c> before they were ever created do not exist; MongoDB would auto-create them on the
    ///     first insert without the model's (unique) indexes and without <c>changeStreamPreAndPostImages</c>.
    /// </summary>
    private async Task RestoreCollectionsOfRecoveredModelsAsync(ICkMongoDbRepositoryDataSource dataSource,
        IReadOnlyCollection<CkModelId> recovered, CancellationToken? cancellationToken)
    {
        if (recovered.Count == 0)
        {
            return;
        }

        using (var session = await dataSource.CreateSessionAsync())
        {
            session.StartTransaction();
            await dataSource.UpdateCollectionsAsync(session, includeModelsInStateImporting: false, skipCleanup: true);
            await session.CommitTransactionAsync();
        }

        foreach (var modelId in recovered)
        {
            using var indexSession = await dataSource.CreateSessionAsync();
            indexSession.StartTransaction();
            await dataSource.UpdateIndexAsync(indexSession, false, modelId, cancellationToken ?? CancellationToken.None);
            await indexSession.CommitTransactionAsync();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<CkModelId>> RevalidateAsync(object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier),
                sourceIdentifier);
        var dataSource = sourceIdentifierObject.MongoDbRepositoryDataSource;

        var result = await RunInTransactionWithRetryAsync(dataSource,
            session => ValidateDependencies(session, dataSource), "re-validation of the CK models");
        ReportRevalidation(result);
        await RestoreCollectionsOfRecoveredModelsAsync(dataSource, result.Recovered, cancellationToken);
        return result.Recovered;
    }

    /// <summary>
    ///     Runs <paramref name="work" /> in its own transaction and retries it (3 attempts) on the MongoDB labels
    ///     <c>TransientTransactionError</c> / <c>UnknownTransactionCommitResult</c> — write conflicts between services
    ///     that import or re-validate in parallel (G-L3).
    /// </summary>
    private async Task<T> RunInTransactionWithRetryAsync<T>(ICkMongoDbRepositoryDataSource dataSource,
        Func<IOctoSession, Task<T>> work, string what)
    {
        const int maxAttempts = 3;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                using var session = await dataSource.CreateSessionAsync();
                session.StartTransaction();
                var result = await work(session);
                await session.CommitTransactionAsync();
                return result;
            }
            catch (MongoException ex) when (attempt < maxAttempts &&
                                            (ex.HasErrorLabel("TransientTransactionError") ||
                                             ex.HasErrorLabel("UnknownTransactionCommitResult")))
            {
                _logger.LogWarning("Transient transaction error during {What} (attempt {Attempt}/{MaxAttempts}), retrying: {Message}",
                    what, attempt, maxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    /// <summary>
    ///     AB#5901: the set of models that do not resolve, with a reason. Seeds: models with an exact dependency
    ///     that is not installed, and the models the resolver skipped (missing dependency) or failed (inheritance).
    ///     Then the failure is propagated to every model with an exact dependency on a failed model, until nothing
    ///     changes — with ResolveFailed models visible to the resolver, a dependent of a model that fails only
    ///     its inheritance would otherwise be judged on a dependency that is not usable.
    /// </summary>
    internal static Dictionary<CkModelId, string> ComputeFailedModels(
        IReadOnlyCollection<(CkModelId Id, IReadOnlyCollection<CkModelId> Dependencies)> models,
        IEnumerable<CkModelId> skippedModelIds, IEnumerable<CkModelId> inheritanceFailedModelIds)
    {
        var installedIds = models.Select(m => m.Id).ToHashSet();
        var failed = new Dictionary<CkModelId, string>();

        foreach (var model in models)
        {
            var missing = model.Dependencies.Where(d => !installedIds.Contains(d)).ToList();
            if (missing.Count > 0)
            {
                failed[model.Id] = "missing dependency " + string.Join(", ", missing.Select(m => m.FullName)) +
                                   InstalledVersions(missing, installedIds);
            }
        }

        foreach (var id in skippedModelIds.Where(installedIds.Contains))
        {
            failed.TryAdd(id, "a dependency does not resolve");
        }

        foreach (var id in inheritanceFailedModelIds.Where(installedIds.Contains))
        {
            failed.TryAdd(id, "inheritance resolution failed (typically a dependency changed its major version)");
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var model in models)
            {
                if (failed.ContainsKey(model.Id))
                {
                    continue;
                }

                var failedDependency = model.Dependencies.FirstOrDefault(failed.ContainsKey);
                if (failedDependency != null)
                {
                    failed[model.Id] = $"dependency '{failedDependency.FullName}' is ResolveFailed";
                    changed = true;
                }
            }
        } while (changed);

        return failed;
    }

    private static string InstalledVersions(IEnumerable<CkModelId> missing, IReadOnlySet<CkModelId> installed)
    {
        var details = missing
            .Select(m => installed.FirstOrDefault(i => i.Name == m.Name))
            .Where(i => i != null)
            .Select(i => i!.FullName)
            .ToList();
        return details.Count == 0 ? " (not installed)" : $" (installed: {string.Join(", ", details)})";
    }

    /// <summary>
    /// Inserts the model with Importing state into the database.
    /// This method should only be called after acquiring the distributed lock.
    /// </summary>
    private async Task InsertModelWithImportingState(CkCompiledModelRoot compiledModel,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        _logger.LogInformation("Inserting CK model '{ModelId}' with Importing state", compiledModel.ModelId);

        using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
        session.StartTransaction();

        // Delete any existing model with the same name (regardless of version)
        await mongoDbRepositoryDataSource.CkModels.TryDeleteOneAsync(session,
            e => e.ModelId == compiledModel.ModelId.Name);

        // Insert the new model with Importing state
        await mongoDbRepositoryDataSource.CkModels.InsertOneAsync(session,
            new CkModel
            {
                Id = compiledModel.ModelId,
                ModelId = compiledModel.ModelId.Name,
                Dependencies = compiledModel.Dependencies?.ToArray(),
                Description = compiledModel.Description,
                ModelState = ModelState.Importing
            });

        await session.CommitTransactionAsync();
        _logger.LogInformation("CK model '{ModelId}' inserted with Importing state", compiledModel.ModelId);
    }

    private void ProcessCkEnums(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.Enums != null)
        {
            foreach (var ckEnumDto in compiledModel.Enums)
            {
                var ckEnumValues = new List<CkEnumValue>();
                foreach (var ckEnumValueDto in ckEnumDto.Values)
                {
                    var ckEnumValue = new CkEnumValue
                    {
                        Key = ckEnumValueDto.Key,
                        Name = ckEnumValueDto.Name,
                        Description = ckEnumValueDto.Description,
                        IsExtension = ckEnumValueDto.IsExtension
                    };

                    ckEnumValues.Add(ckEnumValue);
                }

                var ckEnum = new CkEnum
                {
                    CkModelId = compiledModel.ModelId,
                    ModelState = ModelState.Importing,
                    CkEnumId = new CkId<CkEnumId>(compiledModel.ModelId, ckEnumDto.EnumId),
                    Description = ckEnumDto.Description,
                    UseFlags = ckEnumDto.UseFlags,
                    IsExtensible = ckEnumDto.IsExtensible,
                    Values = ckEnumValues
                };
                transientCkModel.CkEnums.Add(ckEnum);
            }
        }
    }

    private void ProcessCkRecords(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.Records != null)
        {
            foreach (var ckRecordDto in compiledModel.Records)
            {
                var ckTypeAttributes = ProcessCkTypeAttributes(ckRecordDto.Attributes);

                if (ckRecordDto.DerivedFromCkRecordId != null)
                {
                    var ckRecordInheritance = new CkRecordInheritance
                    {
                        CkModelId = compiledModel.ModelId,
                        ModelState = ModelState.Importing,
                        BaseCkRecordId = ckRecordDto.DerivedFromCkRecordId,
                        InheritorCkRecordId = new CkId<CkRecordId>(compiledModel.ModelId, ckRecordDto.RecordId)
                    };
                    transientCkModel.CkRecordInheritances.Add(ckRecordInheritance);
                }

                var recordDto = new CkRecord
                {
                    CkModelId = compiledModel.ModelId,
                    ModelState = ModelState.Importing,
                    CkRecordId = new CkId<CkRecordId>(compiledModel.ModelId, ckRecordDto.RecordId),
                    Description = ckRecordDto.Description,
                    // AB#5533: persist the declared record key (the inherited one is resolved by the graph).
                    RecordKey = string.IsNullOrWhiteSpace(ckRecordDto.RecordKey) ? null : ckRecordDto.RecordKey,
                    IsFinal = ckRecordDto.IsFinal,
                    IsAbstract = ckRecordDto.IsAbstract,
                    Attributes = ckTypeAttributes
                };
                transientCkModel.CkRecords.Add(recordDto);
            }
        }
    }

    private void ProcessCkAssociationRoles(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.AssociationRoles != null)
        {
            foreach (var modelAssociationRole in compiledModel.AssociationRoles)
            {
                var ckTypeAttributes = ProcessCkTypeAttributes(modelAssociationRole.Attributes);

                var associationRole = new CkAssociationRole
                {
                    CkModelId = compiledModel.ModelId,
                    ModelState = ModelState.Importing,
                    RoleId = new CkId<CkAssociationRoleId>(compiledModel.ModelId,
                        modelAssociationRole.AssociationRoleId),
                    Description = modelAssociationRole.Description,
                    InboundName = modelAssociationRole.InboundName,
                    OutboundName = modelAssociationRole.OutboundName,
                    InboundMultiplicity = modelAssociationRole.InboundMultiplicity,
                    OutboundMultiplicity = modelAssociationRole.OutboundMultiplicity,
                    Attributes = ckTypeAttributes
                };
                transientCkModel.CkAssociationRoles.Add(associationRole);
            }
        }
    }

    private static List<CkTypeAttribute> ProcessCkTypeAttributes(List<CkTypeAttributeDto>? typeAttributes)
    {
        var ckTypeAttributes = new List<CkTypeAttribute>();
        if (typeAttributes != null)
        {
            foreach (var attribute in typeAttributes)
            {
                var ckTypeAttribute = new CkTypeAttribute
                {
                    AttributeId = attribute.CkAttributeId,
                    AttributeName = attribute.AttributeName,
                    AutoCompleteValues = attribute.AutoCompleteValues,
                    AutoIncrementReference = attribute.AutoIncrementReference,
                    IsOptional = attribute.IsOptional,
                    // AB#5187: the per-assignment override has NO boolean fallback — dropping it
                    // here would silently disable the override for every type, record and
                    // association-role attribute (they all pass through this one method).
                    Ownership = attribute.Ownership,
                };

                ckTypeAttributes.Add(ckTypeAttribute);
            }
        }

        return ckTypeAttributes;
    }

    /// <summary>
    /// This method deletes the previous version of the model.
    /// </summary>
    /// <remarks>
    /// We want to check if there is a ck model of ANY version is existing here. We retrieve the model version
    /// and delete everything that belongs to this model. This is necessary because we want to be able to
    /// import a model with a different version. 
    /// </remarks>
    /// <param name="session"></param>
    /// <param name="ckModelId"></param>
    /// <param name="mongoDbRepositoryDataSource"></param>
    /// <param name="cancellationToken"></param>
    private async Task DeletePreviousVersion(IOctoSession session, CkModelId ckModelId,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        CancellationToken? cancellationToken)
    {
        var ckModel =
            await mongoDbRepositoryDataSource.CkModels.FindSingleOrDefaultAsync(session, model =>
                model.ModelId == ckModelId.Name);
        if (ckModel == null)
        {
            return;
        }

        await mongoDbRepositoryDataSource.CkRecords.DeleteManyAsync(session,
            Builders<CkRecord>.Filter.Regex(nameof(CkRecord.CkModelId).ToCamelCase(), $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkEnums.DeleteManyAsync(session,
            Builders<CkEnum>.Filter.Regex(nameof(CkEnum.CkModelId).ToCamelCase(), $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkAttributes.DeleteManyAsync(session,
            Builders<CkAttribute>.Filter.Regex(nameof(CkAttribute.CkModelId).ToCamelCase(), $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkAssociationRoles.DeleteManyAsync(session,
            Builders<CkAssociationRole>.Filter.Regex(nameof(CkAssociationRole.CkModelId).ToCamelCase(),
                $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypes.DeleteManyAsync(session,
            Builders<CkType>.Filter.Regex(nameof(CkType.CkModelId).ToCamelCase(), $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypeAssociations.DeleteManyAsync(session,
            Builders<CkTypeAssociation>.Filter.Regex(nameof(CkTypeAssociation.CkModelId).ToCamelCase(),
                $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypeInheritances.DeleteManyAsync(session,
            Builders<CkTypeInheritance>.Filter.Regex(nameof(CkTypeInheritance.CkModelId).ToCamelCase(),
                $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkRecordInheritances.DeleteManyAsync(session,
            Builders<CkRecordInheritance>.Filter.Regex(nameof(CkRecordInheritance.CkModelId).ToCamelCase(),
                $"^{ckModel.ModelId}-.*$"));
        CheckCancellation(cancellationToken);
    }

    /// <summary>
    ///     Preserves extension values from extensible enums before the old version is deleted.
    ///     Extension values (IsExtension = true) are custom values added via the API that should
    ///     survive model imports/updates. Custom values take precedence over construction kit
    ///     defined values with the same key. Key collisions are reported via
    ///     <see cref="ICkModelImportAuditTrail"/> so they surface in the platform event log
    ///     (WI #3324 AC3).
    /// </summary>
    private async Task PreserveExtensibleEnumValues(IOctoSession session, CkModelId ckModelId,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource, TransientCkModel transientCkModel,
        string? tenantId)
    {
        // Find all existing extensible enums for this model
        var existingEnums = await mongoDbRepositoryDataSource.CkEnums.FindManyAsync(session,
            Builders<CkEnum>.Filter.And(
                Builders<CkEnum>.Filter.Regex(nameof(CkEnum.CkModelId).ToCamelCase(), $"^{ckModelId.Name}-.*$"),
                Builders<CkEnum>.Filter.Eq(x => x.IsExtensible, true)));

        if (existingEnums.Count == 0)
        {
            return;
        }

        // Build a dictionary of extension values keyed by enum ID
        var extensionValuesMap = existingEnums.ToDictionary(
            e => e.CkEnumId,
            e => e.Values.Where(v => v.IsExtension).ToList());

        // Merge preserved extension values into the new enums
        // Custom values (extensions) take precedence over CK-defined values with the same key
        foreach (var newEnum in transientCkModel.CkEnums.Where(e => e.IsExtensible))
        {
            if (extensionValuesMap.TryGetValue(newEnum.CkEnumId, out var extensionValues) && extensionValues.Count > 0)
            {
                foreach (var extensionValue in extensionValues)
                {
                    // Check if CK model defines a value with the same key
                    var existingValue = newEnum.Values.FirstOrDefault(v => v.Key == extensionValue.Key);

                    if (existingValue != null)
                    {
                        // Remove the CK-defined value and replace with the custom extension value
                        newEnum.Values.Remove(existingValue);
                        newEnum.Values.Add(extensionValue);
                        await _importAuditTrail.RecordExtensibleEnumValueOverrideAsync(
                            tenantId, ckModelId, newEnum.CkEnumId,
                            existingValue.Name, extensionValue.Name, extensionValue.Key);
                    }
                    else
                    {
                        // Add new extension value
                        newEnum.Values.Add(extensionValue);
                        _logger.LogDebug(
                            "Preserved extension enum value '{EnumValueName}' (key: {EnumValueKey}) for enum '{CkEnumId}'",
                            extensionValue.Name, extensionValue.Key, newEnum.CkEnumId);
                    }
                }
            }
        }
    }

    private static void CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken is { IsCancellationRequested: true })
        {
            cancellationToken.Value.ThrowIfCancellationRequested();
        }
    }

    private void ProcessCkAttributes(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.Attributes != null)
        {
            foreach (var ckAttributeDto in compiledModel.Attributes)
            {
                var ckAttribute = new CkAttribute
                {
                    CkModelId = compiledModel.ModelId,
                    ModelState = ModelState.Importing,
                    CkAttributeId = new CkId<CkAttributeId>(compiledModel.ModelId, ckAttributeDto.AttributeId),
                    AttributeValueType = ckAttributeDto.ValueType,
                    ValueCkEnumId = ckAttributeDto.ValueCkEnumId,
                    ValueCkRecordId = ckAttributeDto.ValueCkRecordId,
                    DefaultValues = ckAttributeDto.DefaultValues?.Select(dv =>
                        AttributeValueConverter.ConvertAttributeValue(ckAttributeDto.ValueType, dv)!).ToList(),
                    Description = ckAttributeDto.Description,
                    // AB#5187: persist BOTH — Ownership is the truth, IsRuntimeState is the
                    // computed mirror (CkAttributeDto.IsRuntimeState == Ownership.IsPreservedOnUpsert())
                    // that an engine which does not know Ownership yet still reads correctly.
                    Ownership = ckAttributeDto.Ownership,
                    IsRuntimeState = ckAttributeDto.IsRuntimeState,
                    MetaData = ckAttributeDto.MetaData?.Select(m =>
                        new CkAttributeMetaData { Key = m.Key, Value = m.Value, Description = m.Description }).ToList()
                };
                transientCkModel.CkAttributes.Add(ckAttribute);
            }
        }
    }

    private void ProcessCkTypesAndAssociations(CkCompiledModelRoot compiledModel,
        TransientCkModel transientCkModel)
    {
        if (compiledModel.Types == null)
        {
            return;
        }

        foreach (var ckTypeDto in compiledModel.Types)
        {
            var ckTypeAttributes = ProcessCkTypeAttributes(ckTypeDto.Attributes);

            var textSearchDefinitions = new List<CkTypeIndex>();
            if (ckTypeDto.Indexes != null)
            {
                foreach (var typeIndexDto in ckTypeDto.Indexes)
                {
                    var typeIndex = new CkTypeIndex
                    {
                        IndexType = (IndexTypes)typeIndexDto.IndexType,
                        Language = typeIndexDto.Language,
                        Fields = typeIndexDto.Fields
                            .Select(x => new CkIndexFields { Weight = x.Weight, AttributeNames = x.AttributePaths })
                            .ToList()
                    };

                    textSearchDefinitions.Add(typeIndex);
                }
            }


            var ckType = new CkType
            {
                CkModelId = compiledModel.ModelId,
                ModelState = ModelState.Importing,
                CkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, ckTypeDto.TypeId),
                Description = ckTypeDto.Description,
                IsFinal = ckTypeDto.IsFinal,
                IsAbstract = ckTypeDto.IsAbstract,
                IsCollectionRoot = ckTypeDto.IsCollectionRoot,
                EnableChangeStreamPreAndPostImages = ckTypeDto.EnableChangeStreamPreAndPostImages,
                DisplayNameRule = ckTypeDto.DisplayNameRule,
                DisplayDescriptionRule = ckTypeDto.DisplayDescriptionRule,
                OwnerAttributePath = ckTypeDto.OwnerAttributePath,
                Attributes = ckTypeAttributes,
                Indexes = textSearchDefinitions
            };

            if (ckTypeDto.DerivedFromCkTypeId != null)
            {
                var ckTypeInheritance = new CkTypeInheritance
                {
                    CkModelId = compiledModel.ModelId,
                    ModelState = ModelState.Importing,
                    BaseCkTypeId = ckTypeDto.DerivedFromCkTypeId,
                    InheritorCkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, ckTypeDto.TypeId)
                };
                transientCkModel.CkTypeInheritances.Add(ckTypeInheritance);
            }

            if (ckTypeDto.Associations != null)
            {
                foreach (var association in ckTypeDto.Associations)
                {
                    var ckTypeAssociation = new CkTypeAssociation
                    {
                        CkModelId = compiledModel.ModelId,
                        ModelState = ModelState.Importing,
                        RoleId = association.CkRoleId,
                        OriginCkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, ckType.CkTypeId.ElementId),
                        TargetCkTypeId = association.TargetCkTypeId,
                        TargetCkAttributeIds = association.TargetCkAttributeIds
                    };
                    transientCkModel.CkTypeAssociations.Add(ckTypeAssociation);
                }
            }

            transientCkModel.CkTypes.Add(ckType);
        }
    }

    private void ValidateAndThrow(IBulkImportResult bulkImportResult)
    {
        if (bulkImportResult.HasError())
        {
            throw OperationFailedException.BulkImportError();
        }
    }

    /// <summary>
    ///     Validates that all system CkTypeId references in the compiled model's types
    ///     match system models that are actually installed in the database.
    ///     Compiled models contain exact versioned references (e.g. System-2.0.7/Entity-1);
    ///     if the installed system model has a different version, the import would fail
    ///     during inheritance resolution with a confusing "unknown CkTypeId" error.
    /// </summary>
    private async Task ValidateSystemReferencesAsync(
        CkCompiledModelRoot compiledModel,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        if (compiledModel.Types == null) return;

        // Collect all distinct system model versions referenced in the compiled types
        var referencedSystemVersions = new Dictionary<string, CkModelId>();
        foreach (var type in compiledModel.Types)
        {
            if (type.DerivedFromCkTypeId?.ModelId == null) continue;

            var refModelId = type.DerivedFromCkTypeId.ModelId;
            if (refModelId.Name == "System" || refModelId.Name.StartsWith("System.", StringComparison.Ordinal))
            {
                referencedSystemVersions.TryAdd(refModelId.Name, refModelId);
            }
        }

        if (referencedSystemVersions.Count == 0) return;

        // Check each referenced system model against the database.
        // Query by exact CkModelId to avoid LINQ serializer issues with sub-properties.
        using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
        foreach (var (name, referencedId) in referencedSystemVersions)
        {
            // Try to find the exact referenced version first
            var exactModel = await mongoDbRepositoryDataSource.CkModels
                .FindSingleOrDefaultAsync(session,
                    e => e.Id == referencedId && e.ModelState == ModelState.Available);

            if (exactModel != null) continue; // Exact version is installed, all good

            // Exact version not found — check if a different version is installed
            // by looking for any available model with the same CkModelId
            var anyModels = await mongoDbRepositoryDataSource.CkModels
                .FindManyAsync(session, e => e.ModelState == ModelState.Available);
            var installedModel = anyModels.FirstOrDefault(m => m.Id.Name == name);

            if (installedModel == null) continue; // Not installed at all, let HardResolveAsync handle it

            throw new ModelValidationException(
                $"Model '{compiledModel.ModelId}' was compiled against '{referencedId.FullName}' " +
                $"but '{name}-{installedModel.Id.Version}' is installed. " +
                $"The model needs to be recompiled against the current system version.");
        }
    }
}
