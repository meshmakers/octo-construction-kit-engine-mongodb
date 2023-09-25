using System.Diagnostics;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.Ck;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Microsoft.Extensions.Logging;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

/// <summary>
/// Implements a CK model repository that stores the CK model in a (octo) database.
/// </summary>
public class DatabaseCkModelRepository : IDatabaseCkModelRepository
{
    private readonly ILogger<DatabaseCkModelRepository> _logger;
    private readonly ICkValidationService _ckValidationService;

    /// <summary>
    /// Creates a new instance of the <see cref="DatabaseCkModelRepository"/> class.
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
    public async Task<bool> LookupModelIdAsync(CkModelId modelId, object? sourceIdentifier = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        var ckModel = await sourceIdentifierObject.DatabaseContext.CkModels
            .FindSingleOrDefaultAsync(sourceIdentifierObject.Session, e => e.Id == modelId);

        return ckModel != null;
    }

    /// <inheritdoc />
    public async Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId, OperationResult operationResult, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantDatabaseSourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        var ckModel = await sourceIdentifierObject.DatabaseContext.CkModels
            .FindSingleOrDefaultAsync(sourceIdentifierObject.Session, e => e.Id == modelId);
        if (ckModel == null)
        {
            throw ModelRepositoryException.ModelNotFound(modelId, RepositoryName);
        }
        
        var ckEnums = await sourceIdentifierObject.DatabaseContext.CkEnums
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckRecords = await sourceIdentifierObject.DatabaseContext.CkRecords
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckRecordInheritances = await sourceIdentifierObject.DatabaseContext.CkRecordInheritances
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckAttributes = await sourceIdentifierObject.DatabaseContext.CkAttributes
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckTypes = await sourceIdentifierObject.DatabaseContext.CkTypes
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckTypeInheritances = await sourceIdentifierObject.DatabaseContext.CkTypeInheritances
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckTypeAssociations = await sourceIdentifierObject.DatabaseContext.CkTypeAssociations
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        var ckAssociationRoles = await sourceIdentifierObject.DatabaseContext.CkAssociationRoles
            .FindManyAsync(sourceIdentifierObject.Session, e => e.CkModelId == modelId.ModelId);
        
        var ckCompiledModelRoot = new CkCompiledModelRoot
        {
            ModelId = ckModel.Id,
            Dependencies = ckModel.Dependencies?.ToList(),
            Enums = ckEnums.Select(e=> new CkEnumDto
            {
                EnumId = e.CkEnumId.Key,
                UseFlags = e.UseFlags,
                Values = e.Values.Select(v=> new CkEnumValueDto
                {
                    Key = v.Key,
                    Name = v.Name,
                    Description = v.Description
                }).ToList()
            }).ToList(),
            Records = ckRecords.Select(r=> new CkRecordDto
            {
                RecordId = r.CkRecordId.Key,
                IsAbstract = r.IsAbstract,
                IsFinal = r.IsFinal,
                Attributes = r.Attributes.Select(a=> new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional
                }).ToList(),
                DerivedFromCkRecordId = ckRecordInheritances.FirstOrDefault(x=> x.InheritorCkRecordId == r.CkRecordId)?.BaseCkRecordId
            }).ToList(),
            Attributes = ckAttributes.Select(a=> new CkAttributeDto
            {
                AttributeId = a.AttributeId.Key,
                ValueType = a.AttributeValueType,
                ValueCkEnumId = a.ValueCkEnumId,
                ValueCkRecordId = a.ValueCkRecordId,
                DefaultValues = a.DefaultValues?.ToList(),
                Description = a.Description
            }).ToList(),
            Types = ckTypes.Select(t=> new CkTypeDto
            {
                TypeId = t.CkTypeId.Key,
                IsAbstract = t.IsAbstract,
                IsFinal = t.IsFinal,
                Attributes = t.Attributes.Select(a=> new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional
                }).ToList(),
                Associations = ckTypeAssociations.Select(a=> new CkTypeAssociationDto
                {
                    CkRoleId = a.RoleId,
                    TargetCkTypeId = a.TargetCkTypeId,
                    TargetAttributes = a.TargetAttributes?.ToList()
                }).ToList(),
                DerivedFromCkTypeId = ckTypeInheritances.FirstOrDefault(x=> x.InheritorCkTypeId == t.CkTypeId)?.BaseCkTypeId
            }).ToList(),
            AssociationRoles = ckAssociationRoles.Select(ar=> new CkAssociationRoleDto
            {
                AssociationRoleId = ar.RoleId.Key,
                InboundMultiplicity = ar.InboundMultiplicity,
                OutboundMultiplicity = ar.OutboundMultiplicity,
                InboundName = ar.InboundName,
                OutboundName = ar.OutboundName,
                Attributes = ar.Attributes.Select(a=> new CkTypeAttributeDto
                {
                    AttributeName = a.AttributeName,
                    CkAttributeId = a.AttributeId,
                    AutoCompleteValues = a.AutoCompleteValues?.ToList(),
                    AutoIncrementReference = a.AutoIncrementReference,
                    IsOptional = a.IsOptional
                }).ToList(),
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
        var transientCkModel = new TransientCkModel(new Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkModel()
        {
            Id = ckCompiledModel.ModelId,
            Dependencies = ckCompiledModel.Dependencies?.ToArray()
        });
        await ExecuteImport(sourceIdentifierObject.Session, ckCompiledModel, transientCkModel,
            sourceIdentifierObject.DatabaseContext,
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

    private async Task ExecuteImport(IOctoSession session, CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel,
        ICkDatabaseContext databaseContext, OperationResult operationResult,
        CancellationToken? cancellationToken)
    {
        _logger.LogInformation("Validating of CK model");
        await _ckValidationService.ValidateAsync(compiledModel, operationResult);
        if (operationResult.HasErrors)
        {
            _logger.LogInformation("Import of CK model failed, model is not valid");
            operationResult.WriteMessagesToLogger(_logger);
            throw OperationFailedException.ValidationErrors();
        }

        CheckCancellation(cancellationToken);

        ProcessCkRecords(compiledModel, transientCkModel);
        ProcessCkEnums(compiledModel, transientCkModel);
        ProcessCkAttributes(compiledModel, transientCkModel);
        ProcessCkAssociationRoles(compiledModel, transientCkModel);
        ProcessCkTypesAndAssociations(compiledModel, transientCkModel);


        // ValidateAsync
        Debug.Assert(_ckValidationService != null, nameof(_ckValidationService) + " != null");


        // Delete the old version
        await DeletePreviousVersion(session, compiledModel.ModelId, databaseContext, cancellationToken);

        CheckCancellation(cancellationToken);

        if (transientCkModel.CkEnums.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkEnums.BulkImportAsync(session,
                    transientCkModel.CkEnums.ToArray()));
            CheckCancellation(cancellationToken);
        }
        
        if (transientCkModel.CkRecords.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkRecords.BulkImportAsync(session,
                    transientCkModel.CkRecords.ToArray()));
            CheckCancellation(cancellationToken);
        }
        
        if (transientCkModel.CkAttributes.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkAttributes.BulkImportAsync(session,
                    transientCkModel.CkAttributes.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkAssociationRoles.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkAssociationRoles.BulkImportAsync(session,
                    transientCkModel.CkAssociationRoles.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypes.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkTypes.BulkImportAsync(session, transientCkModel.CkTypes.ToArray()));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypeAssociations.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkTypeAssociations.BulkImportAsync(session,
                    transientCkModel.CkTypeAssociations));
            CheckCancellation(cancellationToken);
        }

        if (transientCkModel.CkTypeInheritances.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkTypeInheritances.BulkImportAsync(session,
                    transientCkModel.CkTypeInheritances));
            CheckCancellation(cancellationToken);
        }
        
        if (transientCkModel.CkRecordInheritances.Any())
        {
            ValidateAndThrow(
                await databaseContext.CkRecordInheritances.BulkImportAsync(session,
                    transientCkModel.CkRecordInheritances));
            CheckCancellation(cancellationToken);
        }

        await databaseContext.CkModels.InsertAsync(session, transientCkModel.CkModel);

        await CreateCollections(session, databaseContext);
        CheckCancellation(cancellationToken);


        await CreateIndex(session, databaseContext);
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
                        BaseCkRecordId = ckRecordDto.DerivedFromCkRecordId.Value,
                        InheritorCkRecordId = new CkId<CkRecordId>(compiledModel.ModelId, ckRecordDto.RecordId)
                    };
                    transientCkModel.CkRecordInheritances.Add(ckTypeInheritance);
                }

                var recordDto = new CkRecord
                {
                    CkModelId = compiledModel.ModelId,
                    CkRecordId = new CkId<CkRecordId>(compiledModel.ModelId, ckRecordDto.RecordId),
                    IsFinal = ckRecordDto.IsFinal,
                    IsAbstract = ckRecordDto.IsAbstract,
                    Attributes = ckTypeAttributes,
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
                    AutoIncrementReference = attribute.AutoIncrementReference
                };

                ckTypeAttributes.Add(ckTypeAttribute);
            }
        }

        return ckTypeAttributes;
    }

    private async Task DeletePreviousVersion(IOctoSession session, CkModelId ckModelId, ICkDatabaseContext databaseContext,
        CancellationToken? cancellationToken)
    {
        var existingModelId =
            await databaseContext.CkModels.FindSingleOrDefaultAsync(session, model => model.Id.ModelId == ckModelId.ModelId);
        if (existingModelId == null)
        {
            return;
        }
        
        foreach (var ckRecord in await databaseContext.CkRecords.FindManyAsync(session, x => x.CkRecordId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkRecords.DeleteOneAsync(session, ckRecord.CkRecordId);
        }
        
        foreach (var ckEnum in await databaseContext.CkEnums.FindManyAsync(session, x => x.CkEnumId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkEnums.DeleteOneAsync(session, ckEnum.CkEnumId);
        }
        
        foreach (var ckAttribute in await databaseContext.CkAttributes.FindManyAsync(session, x => x.AttributeId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkAttributes.DeleteOneAsync(session, ckAttribute.AttributeId);
        }
        
        CheckCancellation(cancellationToken);

        foreach (var ckAssociationRole in await databaseContext.CkAssociationRoles.FindManyAsync(session, x => x.RoleId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkAssociationRoles.DeleteOneAsync(session, ckAssociationRole.RoleId);
        }

        CheckCancellation(cancellationToken);

        foreach (var ckType in await databaseContext.CkTypes.FindManyAsync(session, x => x.CkTypeId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkTypes.DeleteOneAsync(session, ckType.CkTypeId);
        }

        CheckCancellation(cancellationToken);

        foreach (var ckTypeAssociation in await databaseContext.CkTypeAssociations.FindManyAsync(session, x => x.RoleId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkTypeAssociations.DeleteOneAsync(session, ckTypeAssociation.AssociationId);
        }

        CheckCancellation(cancellationToken);

        foreach (var ckTypeInheritance in await databaseContext.CkTypeInheritances.FindManyAsync(session,  x => x.InheritorCkTypeId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkTypeInheritances.DeleteOneAsync(session, ckTypeInheritance.InheritanceId);
        }
        
        foreach (var ckRecordInheritance in await databaseContext.CkRecordInheritances.FindManyAsync(session,  x => x.InheritorCkRecordId.ModelId == ckModelId.ModelId))
        {
            await databaseContext.CkRecordInheritances.DeleteOneAsync(session, ckRecordInheritance.InheritanceId);
        }

        CheckCancellation(cancellationToken);

        await databaseContext.CkModels.DeleteOneAsync(session, ckModelId);
    }

    private async Task CreateCollections(IOctoSession session, ICkDatabaseContext databaseContext)
    {
        await databaseContext.UpdateCollectionsAsync(session);
    }

    private async Task CreateIndex(IOctoSession session, ICkDatabaseContext databaseContext)
    {
        await databaseContext.UpdateIndexAsync(session);
    }

    private static void CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
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
                    AttributeId = new CkId<CkAttributeId>(compiledModel.ModelId, ckAttributeDto.AttributeId),
                    AttributeValueType = ckAttributeDto.ValueType,
                    ValueCkEnumId = ckAttributeDto.ValueCkEnumId,
                    ValueCkRecordId = ckAttributeDto.ValueCkRecordId,
                    DefaultValues = ckAttributeDto.DefaultValues,
                    Description = ckAttributeDto.Description
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
                IsFinal = ckTypeDto.IsFinal,
                IsAbstract = ckTypeDto.IsAbstract,
                Attributes = ckTypeAttributes,
                Indexes = textSearchDefinitions
            };

            if (ckTypeDto.DerivedFromCkTypeId != null)
            {
                var ckTypeInheritance = new CkTypeInheritance
                {
                    CkModelId = compiledModel.ModelId,
                    BaseCkTypeId = ckTypeDto.DerivedFromCkTypeId.Value,
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
                        TargetAttributes = association.TargetAttributes
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