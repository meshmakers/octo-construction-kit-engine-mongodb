using System.Diagnostics;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.Ck;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Microsoft.Extensions.Logging;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Engine;

public class DatabaseCkModelRepository : ICkModelRepository
{
    private readonly ILogger<DatabaseCkModelRepository> _logger;
    private readonly ICkValidationService _ckValidationService;

    public DatabaseCkModelRepository(ILogger<DatabaseCkModelRepository> logger, ICkValidationService ckValidationService)
    {
        _logger = logger;
        _ckValidationService = ckValidationService;
    }

    /// <inheritdoc />
    public bool IsSupportingSourceIdentifier(object? sourceIdentifier = null)
    {
        return sourceIdentifier is TenantCkModelRepositorySourceIdentifier;
    }

    public Task<bool> LookupModelIdAsync(CkModelId modelId, object? sourceIdentifier = null)
    {
        var tenantCkModelRepositorySourceIdentifier =
            ArgumentValidation.ValidateAndCastToObject<TenantCkModelRepositorySourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);


        throw new NotImplementedException();
    }

    public Task<CkCompiledModelRoot> GetModelAsync(CkModelId modelId, OperationResult operationResult, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var tenantCkModelRepositorySourceIdentifier =
            ArgumentValidation.ValidateAndCastToObject<TenantCkModelRepositorySourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        throw new NotImplementedException();
    }

    public async Task PublishModelAsync(CkCompiledModelRoot ckCompiledModel, bool force = false, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        var sourceIdentifierObject =
            ArgumentValidation.ValidateAndCastToObject<TenantCkModelRepositorySourceIdentifier>(nameof(sourceIdentifier), sourceIdentifier);

        OperationResult operationResult = new();
        var transientCkModel = new TransientCkModel(new Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkModel()
        {
            Id = ckCompiledModel.ModelId,
            Dependencies = ckCompiledModel.Dependencies?.ToArray()
        });
        await ExecuteImport(sourceIdentifierObject.Session, ckCompiledModel, transientCkModel,
            sourceIdentifierObject.TenantCkModelRepository,
            operationResult, cancellationToken);
    }

    public async Task UpdateModelAsync(CkCompiledModelRoot ckCompiledModel, object? sourceIdentifier = null,
        CancellationToken? cancellationToken = null)
    {
        await PublishModelAsync(ckCompiledModel, false, sourceIdentifier, cancellationToken);
    }

    public int Order => 5;
    public string RepositoryName => InternalConstants.CkModelRepositoryName;
    public string Description => "Repository for OctoMesh tenant models.";
    public bool CanWrite => true;

    private async Task ExecuteImport(IOctoSession session, CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel,
        ITenantCkModelRepository ckModelRepository, OperationResult operationResult,
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

        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        ProcessCkModel(compiledModel, transientCkModel);

        // Transform to entities
        ProcessCkAttributes(compiledModel, transientCkModel);


        ProcessCkAssociations(compiledModel, transientCkModel);


        ProcessCkEntitiesAndAssociations(compiledModel, transientCkModel);


        // ValidateAsync
        Debug.Assert(_ckValidationService != null, nameof(_ckValidationService) + " != null");


        // Delete the old version
        if (await DeleteOldVersion(session, compiledModel.ModelId, ckModelRepository, cancellationToken))
        {
            return;
        }

        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        // ValidateAsync the Model
        if (transientCkModel.CkAttributes.Any())
        {
            ValidateAndThrow(
                await ckModelRepository.BulkImportCkAttributesAsync(session,
                    transientCkModel.CkAttributes.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (transientCkModel.CkAssociationRoles.Any())
        {
            ValidateAndThrow(
                await ckModelRepository.BulkImportCkAssociationRoleAsync(session,
                    transientCkModel.CkAssociationRoles.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (transientCkModel.CkEntities.Any())
        {
            ValidateAndThrow(
                await ckModelRepository.BulkImportCkEntitiesAsync(session, transientCkModel.CkEntities.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (transientCkModel.CkEntityAssociations.Any())
        {
            ValidateAndThrow(
                await ckModelRepository.BulkImportCkEntityAssociationsAsync(session,
                    transientCkModel.CkEntityAssociations));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (transientCkModel.CkEntityInheritances.Any())
        {
            ValidateAndThrow(
                await ckModelRepository.BulkImportCkEntityInheritancesAsync(session,
                    transientCkModel.CkEntityInheritances));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        await ckModelRepository.InsertCkModelAsync(session, transientCkModel.CkModel);

        await CreateCollections(session, ckModelRepository);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        await CreateIndex(session, ckModelRepository);
    }

    private void ProcessCkAssociations(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.AssociationRoles != null)
        {
            foreach (var modelAssociationRole in compiledModel.AssociationRoles)
            {
                var associationRole = new CkAssociationRole
                {
                    RoleId = new CkId<CkAssociationRoleId>(compiledModel.ModelId, modelAssociationRole.AssociationRoleId),
                    InboundName = modelAssociationRole.InboundName,
                    OutboundName = modelAssociationRole.OutboundName,
                    InboundMultiplicity = (Multiplicities)modelAssociationRole.InboundMultiplicity,
                    OutboundMultiplicity = (Multiplicities)modelAssociationRole.OutboundMultiplicity,
                };
                transientCkModel.CkAssociationRoles.Add(associationRole);
            }
        }
    }

    private void ProcessCkModel(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
    }

    private async Task<bool> DeleteOldVersion(IOctoSession session, CkModelId ckModelId, ITenantCkModelRepository ckModelRepository,
        CancellationToken? cancellationToken)
    {
        foreach (var ckAttribute in await ckModelRepository.GetCkAttributesByModelAsync(session, ckModelId))
        {
            await ckModelRepository.DeleteCkAttributesOneAsync(session, ckAttribute.AttributeId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckAssociationRole in await ckModelRepository.GetCkAssociationRolesByModelAsync(session, ckModelId))
        {
            await ckModelRepository.DeleteCkAssociationRoleOneAsync(session, ckAssociationRole.RoleId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntity in await ckModelRepository.GetCkEntitiesByModelAsync(session, ckModelId))
        {
            await ckModelRepository.DeleteCkEntitiesOneAsync(session, ckEntity.CkTypeId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityAssociation in await ckModelRepository.GetCkEntityAssociationsByModelAsync(session, ckModelId))
        {
            await ckModelRepository.DeleteCkEntityAssociationsOneAsync(session, ckEntityAssociation.AssociationId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityInheritance in await ckModelRepository.GetCkEntityInheritancesByModelAsync(session, ckModelId))
        {
            await ckModelRepository.DeleteOneCkEntityInheritancesAsync(session, ckEntityInheritance.InheritanceId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        await ckModelRepository.DeleteCkModelOneAsync(session, ckModelId);

        return false;
    }

    private async Task CreateCollections(IOctoSession session, ITenantCkModelRepository ckModelRepository)
    {
        await ckModelRepository.UpdateCollectionsAsync(session);
    }

    private async Task CreateIndex(IOctoSession session, ITenantCkModelRepository ckModelRepository)
    {
        await ckModelRepository.UpdateIndexAsync(session);
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }

    private void ProcessCkAttributes(CkCompiledModelRoot compiledModel, TransientCkModel transientCkModel)
    {
        if (compiledModel.Attributes != null)
        {
            foreach (var modelCkAttribute in compiledModel.Attributes)
            {
                var ckAttribute = new CkAttribute
                {
                    AttributeId = new CkId<CkAttributeId>(compiledModel.ModelId, modelCkAttribute.AttributeId),
                    AttributeValueType = (AttributeValueTypes)modelCkAttribute.ValueType,
                    // SelectionValues = modelCkAttribute.SelectionValues?.Select(sv => new CkSelectionValue
                    //     { Key = sv.Key, Name = sv.Name }).ToList(),
                    DefaultValues = modelCkAttribute.DefaultValues
                };
                transientCkModel.CkAttributes.Add(ckAttribute);
            }
        }
    }

    private void ProcessCkEntitiesAndAssociations(CkCompiledModelRoot compiledModel,
        TransientCkModel transientCkModel)
    {
        if (compiledModel.Types == null)
        {
            return;
        }

        foreach (var entity in compiledModel.Types)
        {
            var ckEntityAttributes = new List<CkEntityAttribute>();
            if (entity.Attributes != null)
            {
                foreach (var attribute in entity.Attributes)
                {
                    var ckEntityAttribute = new CkEntityAttribute
                    {
                        AttributeId = attribute.CkAttributeId,
                        AttributeName = attribute.AttributeName,
                        AutoCompleteFilter = attribute.AutoCompleteFilter,
                        AutoCompleteLimit = attribute.AutoCompleteLimit,
                        IsAutoCompleteEnabled = attribute.IsAutoCompleteEnabled,
                        AutoIncrementReference = attribute.AutoIncrementReference
                    };

                    ckEntityAttributes.Add(ckEntityAttribute);
                }
            }

            var textSearchDefinitions = new List<CkEntityIndex>();
            if (entity.Indexes != null)
            {
                foreach (var entityIndexDto in entity.Indexes)
                {
                    var entityIndex = new CkEntityIndex
                    {
                        IndexType = (IndexTypes)entityIndexDto.IndexType,
                        Language = entityIndexDto.Language,
                        Fields = entityIndexDto.Fields
                            .Select(x => new CkIndexFields { Weight = x.Weight, AttributeNames = x.AttributeNames })
                            .ToList()
                    };

                    textSearchDefinitions.Add(entityIndex);
                }
            }


            var ckEntity = new CkEntity
            {
                CkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, entity.TypeId),
                IsFinal = entity.IsFinal,
                IsAbstract = entity.IsAbstract,
                Attributes = ckEntityAttributes,
                Indexes = textSearchDefinitions
            };

            if (entity.DerivedFromCkTypeId != null)
            {
                var ckEntityInheritance = new CkEntityInheritance
                {
                    OriginCkTypeId = entity.DerivedFromCkTypeId.Value,
                    TargetCkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, entity.TypeId)
                };
                transientCkModel.CkEntityInheritances.Add(ckEntityInheritance);
            }

            if (entity.Associations != null)
            {
                foreach (var association in entity.Associations)
                {
                    var ckEntityAssociation = new CkEntityAssociation
                    {
                        RoleId = association.CkRoleId,
                        OriginCkTypeId = new CkId<CkTypeId>(compiledModel.ModelId, ckEntity.CkTypeId.Key),
                        TargetCkTypeId = association.TargetCkTypeId,
                    };
                    transientCkModel.CkEntityAssociations.Add(ckEntityAssociation);
                }
            }

            transientCkModel.CkEntities.Add(ckEntity);
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