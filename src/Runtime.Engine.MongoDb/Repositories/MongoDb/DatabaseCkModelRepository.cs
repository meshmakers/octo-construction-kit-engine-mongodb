using System.Diagnostics;
using System.Text.RegularExpressions;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.Repository;
using Meshmakers.Octo.Runtime.Contracts;
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
    private readonly ILogger<DatabaseCkModelRepository> _logger;

    /// <summary>
    ///     Creates a new instance of the <see cref="DatabaseCkModelRepository" /> class.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="repositoryModelResolver"></param>
    public DatabaseCkModelRepository(ILogger<DatabaseCkModelRepository> logger,
        IRepositoryModelResolver repositoryModelResolver)
    {
        _logger = logger;
        _repositoryModelResolver = repositoryModelResolver;
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

        var ckModels = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels.FindManyAsync(session,
            e => e.ModelId == modelIdVersionRange.Name && e.ModelState == ModelState.Available);

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
            operationResult, sourceIdentifier, cancellationToken);
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

        var ckModel = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels
            .FindSingleOrDefaultAsync(session, e => e.Id == ckModelId && e.ModelState == ModelState.Available);
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
                IsAbstract = r.IsAbstract,
                IsFinal = r.IsFinal,
                Attributes = r.Attributes.Select(a => new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional
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
                IsDataStream = a.IsDataStream,
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
                Attributes = t.Attributes.Select(a => new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional
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
                    IsOptional = a.IsOptional
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
        CancellationToken? cancellationToken)
    {
        _logger.LogInformation("Executing import of CK model '{CkModelId}' to database", compiledModel.ModelId);

        // Acquire distributed lock to prevent parallel imports of the same model
        await using var importLock = await mongoDbRepositoryDataSource.AcquireModelImportLockAsync(compiledModel.ModelId.Name);

        // Insert model with Importing state (now safe because we have the lock)
        await InsertModelWithImportingState(compiledModel, mongoDbRepositoryDataSource);

        try
        {
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
            await mongoDbRepositoryDataSource.UpdateCollectionsAsync(session);
            CheckCancellation(cancellationToken);

            _logger.LogDebug("Committing model import transaction");
            await session.CommitTransactionAsync();

            _logger.LogDebug("Pos-work of CK model import");
            using var indexUpdateSession = await mongoDbRepositoryDataSource.CreateSessionAsync();
            indexUpdateSession.StartTransaction();

            // Attention! This operation is critical. It forces an exclusive write lock on the database.
            _logger.LogDebug("Updating index");
            await mongoDbRepositoryDataSource.UpdateIndexAsync(indexUpdateSession,  true);
            CheckCancellation(cancellationToken);

            await indexUpdateSession.CommitTransactionAsync();

            using var sessionComplete = await mongoDbRepositoryDataSource.CreateSessionAsync();
            sessionComplete.StartTransaction();

            _logger.LogDebug("Updating model state");
            await UpdateModelStateAsync(sessionComplete, mongoDbRepositoryDataSource, compiledModel.ModelId,
                ModelState.Available);

            _logger.LogDebug("Validating dependencies of other CK models");
            await ValidateDependencies(sessionComplete, mongoDbRepositoryDataSource);
            CheckCancellation(cancellationToken);

            await sessionComplete.CommitTransactionAsync();

            _logger.LogInformation("Import of CK model {CkModelId} to database succeeded", compiledModel.ModelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import of CK model {CkModelId}  to database failed", compiledModel.ModelId);

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

    private async Task ValidateDependencies(IOctoSession session,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        var sourceIdentifier = new TenantDatabaseSourceIdentifier(session, mongoDbRepositoryDataSource);
        OperationResult operationResult = new();
        var ckModels =
            await mongoDbRepositoryDataSource.CkModels.FindManyAsync(session,
                m => m.ModelState == ModelState.Available);
        var originFileResolver = new OriginFileResolver("-");
        var resolveResult = await _repositoryModelResolver.SoftResolveAsync(ckModels.Select(x => x.Id).ToList(),
            originFileResolver, operationResult, sourceIdentifier);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            throw OperationFailedException.ValidateFailed(operationResult);
        }

        if (resolveResult.SkippedModelIds.Any())
        {
            foreach (CkModelId skippedModelId in resolveResult.SkippedModelIds)
            {
                await UpdateModelStateAsync(session, mongoDbRepositoryDataSource, skippedModelId, ModelState.ResolveFailed);
            }
        }
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
                    IsDataStream = ckAttributeDto.IsDataStream,
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
}
