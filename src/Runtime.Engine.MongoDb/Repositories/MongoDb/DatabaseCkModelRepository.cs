using System.Diagnostics;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
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
///     Implements a CK model repository that stores the CK model in a (octo) database.
/// </summary>
public class DatabaseCkModelRepository : IDatabaseCkModelRepository
{
    private readonly ICkValidationService _ckValidationService;
    private readonly ILogger<DatabaseCkModelRepository> _logger;

    /// <summary>
    ///     Creates a new instance of the <see cref="DatabaseCkModelRepository" /> class.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="ckValidationService"></param>
    public DatabaseCkModelRepository(ILogger<DatabaseCkModelRepository> logger, ICkValidationService ckValidationService)
    {
        _logger = logger;
        _ckValidationService = ckValidationService;
    }

    /// <inheritdoc />
    public bool IsSupportingSourceIdentifier(object? sourceIdentifier = null)
    {
        return sourceIdentifier is TenantDatabaseSourceIdentifier;
    }

    /// <inheritdoc />
    public async Task<bool> IsModelIdExistingAsync(CkModelId modelId, object? sourceIdentifier = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        using var session = await sourceIdentifierObject.MongoDbRepositoryDataSource.CreateSessionAsync();
        session.StartTransaction();

        var ckModel = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels
            .FindSingleOrDefaultAsync(session, e => e.Id == modelId && e.ModelState == ModelState.Available);
        await session.CommitTransactionAsync();

        return ckModel != null;
    }

    /// <inheritdoc />
    public async Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId, OperationResult operationResult,
        object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        using var session = await sourceIdentifierObject.MongoDbRepositoryDataSource.CreateSessionAsync();
        session.StartTransaction();

        var ckModel = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkModels
            .FindSingleOrDefaultAsync(session, e => e.Id == modelId && e.ModelState == ModelState.Available);
        if (ckModel == null)
        {
            throw ModelRepositoryException.ModelNotFound(modelId, RepositoryName);
        }

        var ckEnums = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkEnums
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckRecords = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkRecords
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckRecordInheritances = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkRecordInheritances
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckAttributes = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkAttributes
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckTypes = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypes
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckTypeInheritances = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypeInheritances
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckTypeAssociations = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkTypeAssociations
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);
        var ckAssociationRoles = await sourceIdentifierObject.MongoDbRepositoryDataSource.CkAssociationRoles
            .FindManyAsync(session, e => e.CkModelId == modelId.ModelId);

        await session.CommitTransactionAsync();

        var ckCompiledModelRoot = new CkCompiledModelRoot
        {
            ModelId = ckModel.Id,
            Description = ckModel.Description,
            Dependencies = ckModel.Dependencies?.ToList(),
            Enums = ckEnums.Select(e => new CkEnumDto
            {
                EnumId = e.CkEnumId.Key,
                Description = e.Description,
                UseFlags = e.UseFlags,
                Values = e.Values.Select(v => new CkEnumValueDto
                {
                    Key = v.Key,
                    Name = v.Name,
                    Description = v.Description
                }).ToList()
            }).ToList(),
            Records = ckRecords.Select(r => new CkRecordDto
            {
                RecordId = r.CkRecordId.Key,
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
                DerivedFromCkRecordId = ckRecordInheritances.FirstOrDefault(x => x.InheritorCkRecordId == r.CkRecordId)?.BaseCkRecordId
            }).ToList(),
            Attributes = ckAttributes.Select(a => new CkAttributeDto
            {
                AttributeId = a.CkAttributeId.Key,
                ValueType = a.AttributeValueType,
                ValueCkEnumId = a.ValueCkEnumId,
                ValueCkRecordId = a.ValueCkRecordId,
                DefaultValues = a.DefaultValues?.ToList(),
                Description = a.Description,
                IsDataStream = a.IsDataStream,
                MetaData = a.MetaData?.Select(m=> 
                    new CkAttributeMetaDataDto {Key = m.Key, Value = m.Value, Description = m.Description }).ToList()
            }).ToList(),
            Types = ckTypes.Select(t => new CkCompiledTypeDto
            {
                TypeId = t.CkTypeId.Key,
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
                Associations = ckTypeAssociations.Where(x=> x.OriginCkTypeId == t.CkTypeId).Select(a => new CkTypeAssociationDto
                {
                    CkRoleId = a.RoleId,
                    TargetCkTypeId = a.TargetCkTypeId,
                    TargetCkAttributeIds = a.TargetCkAttributeIds?.ToList()
                }).ToList(),
                DerivedFromCkTypeId = ckTypeInheritances.FirstOrDefault(x => x.InheritorCkTypeId == t.CkTypeId)?.BaseCkTypeId
            }).ToList(),
            AssociationRoles = ckAssociationRoles.Select(ar => new CkAssociationRoleDto
            {
                AssociationRoleId = ar.RoleId.Key,
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
    public async Task PublishModelAsync(CkCompiledModelRoot ckCompiledModel, bool force = false, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        OperationResult operationResult = new();
        var transientCkModel = new TransientCkModel(new CkModel
        {
            Id = ckCompiledModel.ModelId,
            Description = ckCompiledModel.Description,
            Dependencies = ckCompiledModel.Dependencies?.ToArray()
        });
        await ExecuteImport(ckCompiledModel, transientCkModel,
            sourceIdentifierObject.MongoDbRepositoryDataSource,
            operationResult, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        await PublishModelAsync(ckCompiledModel, false, sourceIdentifier, cancellationToken);
    }

    /// <inheritdoc />
    public int Order => 5;

    /// <inheritdoc />
    public string RepositoryName => InternalConstants.CkModelRepositoryName;

    /// <inheritdoc />
    public string Description => "Repository for OctoMesh tenant models.";

    /// <inheritdoc />
    public bool CanWrite => true;

    private async Task ExecuteImport(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource, OperationResult operationResult,
        CancellationToken? cancellationToken)
    {
        _logger.LogInformation("Executing import of CK model");
        await CheckParallelModelImport(compiledModel, mongoDbRepositoryDataSource);

        _logger.LogInformation("Validating of CK model");
        await _ckValidationService.ValidateAsync(compiledModel, operationResult);
        if (operationResult.HasErrors)
        {
            _logger.LogInformation("Import of CK model failed, model is not valid");
            operationResult.WriteMessagesToLogger(_logger);
            throw OperationFailedException.ValidationErrors();
        }
        _logger.LogDebug("Starting import of CK model");

        CheckCancellation(cancellationToken);

        ProcessCkRecords(compiledModel, transientCkModel);
        ProcessCkEnums(compiledModel, transientCkModel);
        ProcessCkAttributes(compiledModel, transientCkModel);
        ProcessCkAssociationRoles(compiledModel, transientCkModel);
        ProcessCkTypesAndAssociations(compiledModel, transientCkModel);


        // ValidateAsync
        Debug.Assert(_ckValidationService != null, nameof(_ckValidationService) + " != null");

        using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
        session.StartTransaction();

        _logger.LogDebug("Preparing import of CK model to database");

        // Create basic collections first (latter this method is called again to create CkType document collections)
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
                    transientCkModel.CkEnums.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkRecords.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkRecords.BulkImportAsync(session,
                    transientCkModel.CkRecords.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkAttributes.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkAttributes.BulkImportAsync(session,
                    transientCkModel.CkAttributes.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkAssociationRoles.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkAssociationRoles.BulkImportAsync(session,
                    transientCkModel.CkAssociationRoles.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypes.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkTypes.BulkImportAsync(session, transientCkModel.CkTypes.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypeAssociations.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkTypeAssociations.BulkImportAsync(session,
                    transientCkModel.CkTypeAssociations));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypeInheritances.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkTypeInheritances.BulkImportAsync(session,
                    transientCkModel.CkTypeInheritances));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkRecordInheritances.Any())
        {
            ValidateAndThrow(
                await mongoDbRepositoryDataSource.CkRecordInheritances.BulkImportAsync(session,
                    transientCkModel.CkRecordInheritances));
            CheckCancellation(cancellationToken);
        }

        _logger.LogDebug("Updating collections");
        // This operation is critical. It forces an exclusive write lock on the database.
        await mongoDbRepositoryDataSource.UpdateCollectionsAsync(session);
        CheckCancellation(cancellationToken);

        _logger.LogDebug("Committing model import transaction");
        await session.CommitTransactionAsync();
        
        _logger.LogDebug("Pos-work of CK model import");
        using var sessionComplete = await mongoDbRepositoryDataSource.CreateSessionAsync();
        sessionComplete.StartTransaction();

        _logger.LogDebug("Updating index");
        await mongoDbRepositoryDataSource.UpdateIndexAsync(sessionComplete);
        CheckCancellation(cancellationToken);

        _logger.LogDebug("Updating model state");
        var updateDefinition = Builders<CkModel>.Update.Set(x => x.ModelState, ModelState.Available);
        await mongoDbRepositoryDataSource.CkModels.UpdateOneAsync(sessionComplete, compiledModel.ModelId, updateDefinition);

        await sessionComplete.CommitTransactionAsync();
        
        _logger.LogDebug("Import of CK model to database completed");
    }

    private async Task CheckParallelModelImport(CkCompiledModelRoot compiledModel,
        ICkMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
        var queryable = await mongoDbRepositoryDataSource.CkModels.AsQueryableAsync();

        int retries = 5;
        while (true)
        {

            _logger.LogInformation("Checking if CK model '{ModelId}' can be imported", compiledModel.ModelId);
            var currentlyImportingCkModels = queryable.Where(m => m.ModelState == ModelState.Importing && m.Id != compiledModel.ModelId);
            if (currentlyImportingCkModels.Any())
            {
                Debug.Assert(currentlyImportingCkModels.Count() == 1, "currentlyImportingCkModels == 1");

                var importingModels = currentlyImportingCkModels.Select(x => x.ModelId).ToList();
                _logger.LogInformation("CK model(s) '{Models}' are importing, waiting for 1 second (retries left: {Retries})", string.Join(", ", importingModels), retries);
                Interlocked.Decrement(ref retries);
                if (retries <= 0)
                {
                    _logger.LogInformation("Current CK model is importing, tried 5 times to wait for the import to finish");
                    throw OperationFailedException.ModelImportingWaitTimeout();
                }

                await Task.Delay(1000);
            }
            else
            {
                _logger.LogInformation("No CK model is importing, continuing");
                using var session = await mongoDbRepositoryDataSource.CreateSessionAsync();
                session.StartTransaction();
                
                await mongoDbRepositoryDataSource.CkModels.TryDeleteOneAsync(session, compiledModel.ModelId);
                await mongoDbRepositoryDataSource.CkModels.InsertOneAsync(session, new CkModel
                {
                    Id = compiledModel.ModelId,
                    ModelId = compiledModel.ModelId.ModelId,
                    Dependencies = compiledModel.Dependencies?.ToArray(),
                    Description = compiledModel.Description,
                    ModelState = ModelState.Importing
                });
                
                await session.CommitTransactionAsync();
                break;
            }
        }
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
                        Description = ckEnumValueDto.Description
                    };

                    ckEnumValues.Add(ckEnumValue);
                }

                var ckEnum = new CkEnum
                {
                    CkModelId = compiledModel.ModelId,
                    CkEnumId = new CkId<CkEnumId>(compiledModel.ModelId, ckEnumDto.EnumId),
                    Description = compiledModel.Description,
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
                    var ckTypeInheritance = new CkRecordInheritance
                    {
                        CkModelId = compiledModel.ModelId,
                        BaseCkRecordId = ckRecordDto.DerivedFromCkRecordId,
                        InheritorCkRecordId = new CkId<CkRecordId>(compiledModel.ModelId, ckRecordDto.RecordId)
                    };
                    transientCkModel.CkRecordInheritances.Add(ckTypeInheritance);
                }

                var recordDto = new CkRecord
                {
                    CkModelId = compiledModel.ModelId,
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
                    RoleId = new CkId<CkAssociationRoleId>(compiledModel.ModelId, modelAssociationRole.AssociationRoleId),
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
                model.ModelId == ckModelId.ModelId);
        if (ckModel == null)
        {
            return;
        }

        await mongoDbRepositoryDataSource.CkRecords.DeleteManyAsync(session,
                Builders<CkRecord>.Filter.Eq(nameof(CkRecord.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkEnums.DeleteManyAsync(session,
            Builders<CkEnum>.Filter.Eq(nameof(CkEnum.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkAttributes.DeleteManyAsync(session,
            Builders<CkAttribute>.Filter.Eq(nameof(CkAttribute.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkAssociationRoles.DeleteManyAsync(session,
            Builders<CkAssociationRole>.Filter.Eq(nameof(CkAssociationRole.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypes.DeleteManyAsync(session,
            Builders<CkType>.Filter.Eq(nameof(CkType.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypeAssociations.DeleteManyAsync(session,
            Builders<CkTypeAssociation>.Filter.Eq(nameof(CkTypeAssociation.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkTypeInheritances.DeleteManyAsync(session,
            Builders<CkTypeInheritance>.Filter.Eq(nameof(CkTypeInheritance.CkModelId).ToCamelCase(), ckModel.Id));
        CheckCancellation(cancellationToken);

        await mongoDbRepositoryDataSource.CkRecordInheritances.DeleteManyAsync(session,
            Builders<CkRecordInheritance>.Filter.Eq(nameof(CkRecordInheritance.CkModelId).ToCamelCase(), ckModel.Id));
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
                    CkAttributeId = new CkId<CkAttributeId>(compiledModel.ModelId, ckAttributeDto.AttributeId),
                    AttributeValueType = ckAttributeDto.ValueType,
                    ValueCkEnumId = ckAttributeDto.ValueCkEnumId,
                    ValueCkRecordId = ckAttributeDto.ValueCkRecordId,
                    DefaultValues = ckAttributeDto.DefaultValues?.Select(dv =>
                        AttributeValueConverter.ConvertAttributeValue(ckAttributeDto.ValueType, dv)!).ToList(),
                    Description = ckAttributeDto.Description,
                    IsDataStream = ckAttributeDto.IsDataStream,
                    MetaData = ckAttributeDto.MetaData?.Select(m=> 
                        new CkAttributeMetaData{Key = m.Key, Value = m.Value, Description = m.Description }).ToList()
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
                foreach (var entityIndexDto in ckTypeDto.Indexes)
                {
                    var typeIndex = new CkTypeIndex
                    {
                        IndexType = (IndexTypes)entityIndexDto.IndexType,
                        Language = entityIndexDto.Language,
                        Fields = entityIndexDto.Fields
                            .Select(x => new CkIndexFields { Weight = x.Weight, AttributeNames = x.AttributeNames })
                            .ToList()
                    };

                    textSearchDefinitions.Add(typeIndex);
                }
            }


            var ckType = new CkType
            {
                CkModelId = compiledModel.ModelId,
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
                        RoleId = association.CkRoleId,
                        OriginCkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, ckType.CkTypeId.Key),
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