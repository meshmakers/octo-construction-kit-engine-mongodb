using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;
using Persistence.Contracts;
using AttributeValueTypes = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.AttributeValueTypes;
using CkAssociationRole = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAssociationRole;
using CkAttribute = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkAttribute;
using CkEntity = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntity;
using CkEntityAssociation = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntityAssociation;
using CkEntityAttribute = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkEntityAttribute;
using CkIndexFields = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkIndexFields;
using CkSelectionValue = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.CkSelectionValue;
using Multiplicities = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.Multiplicities;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ImportCkModelCommand : IImportCkModelCommand
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private CkModelValidation? _ckModelValidation;

    public async Task ImportTextAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string jsonText,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _ckModelValidation = new CkModelValidation(ckModelRepository);


            Logger.Info("Reading CK model....");
            var model = await CkSerializer.DeserializeAsync(jsonText);

            if (model == null)
            {
                Logger.Error("Import of CK model failed, model cannot be deserialized.");
                throw ModelImportException.CannotDeserializeModel();
            }

            Logger.Info("Executing import of CK model....");
            var transientCkModel = new TransientCkModel(new DatabaseEntities.CkModel()
            {
                Id = model.ModelId,
                Dependencies = model.CkDependencies.ToArray()
            });
            await ExecuteImport(session, model, transientCkModel, ckModelRepository, cancellationToken);


            Logger.Info("Import of CK model completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of CK model failed.");
            throw;
        }
    }

    public async Task ImportAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string filePath,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _ckModelValidation = new CkModelValidation(ckModelRepository);


            Logger.Info("Reading CK model....");
            CkModelRoot? model;
            using (var streamReader = new StreamReader(filePath))
            {
                model = await CkSerializer.DeserializeAsync(streamReader);
            }

            if (model == null)
            {
                Logger.Error("Import of CK model failed, model cannot be deserialized.");
                throw ModelImportException.CannotDeserializeModel();
            }

            Logger.Info("Executing import of CK model....");
            var transientCkModel = new TransientCkModel(new DatabaseEntities.CkModel()
            {
                Id = model.ModelId,
                Dependencies = model.CkDependencies.ToArray()
            });
            await ExecuteImport(session, model, transientCkModel, ckModelRepository, cancellationToken);

            Logger.Info("Import of CK model completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of CK model failed.");
            throw;
        }
    }

    private async Task ExecuteImport(IOctoSession session, CkModelRoot model, TransientCkModel transientCkModel,
        ITenantCkModelRepository ckModelRepository,
        CancellationToken? cancellationToken)
    {
        ProcessCkModel(model, transientCkModel);

        // Transform to entities
        ProcessCkAttributes(model, transientCkModel);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        ProcessCkAssociations(model, transientCkModel);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        ProcessCkEntitiesAndAssociations(ckModelRepository, model, transientCkModel);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        // ValidateAsync
        Debug.Assert(_ckModelValidation != null, nameof(_ckModelValidation) + " != null");
        await _ckModelValidation.Validate(session, transientCkModel, cancellationToken);

        // Delete the old version
        if (await DeleteOldVersion(session, model.ModelId, ckModelRepository, cancellationToken))
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

    private void ProcessCkAssociations(CkModelRoot model, TransientCkModel transientCkModel)
    {
        foreach (var modelAssociationRole in model.CkAssociationRoles)
        {
            var associationRole = new CkAssociationRole
            {
                RoleId = new CkId<CkAssociationId>(model.ModelId, modelAssociationRole.RoleId),
                InboundName = modelAssociationRole.InboundName,
                OutboundName = modelAssociationRole.OutboundName,
                InboundMultiplicity = (Multiplicities)modelAssociationRole.InboundMultiplicity,
                OutboundMultiplicity = (Multiplicities)modelAssociationRole.OutboundMultiplicity,
            };
            transientCkModel.CkAssociationRoles.Add(associationRole);
        }
    }

    private void ProcessCkModel(CkModelRoot model, TransientCkModel transientCkModel)
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
            await ckModelRepository.DeleteCkEntitiesOneAsync(session, ckEntity.CkId);
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

    private void ProcessCkAttributes(CkModelRoot model, TransientCkModel transientCkModel)
    {
        foreach (var modelCkAttribute in model.CkAttributes)
        {
            var ckAttribute = new CkAttribute
            {
                AttributeId = new CkId<CkAttributeId>(model.ModelId, modelCkAttribute.AttributeId),
                AttributeValueType = (AttributeValueTypes)modelCkAttribute.ValueType,
                SelectionValues = modelCkAttribute.SelectionValues?.Select(sv => new CkSelectionValue
                    { Key = sv.Key, Name = sv.Name }).ToList(),
                DefaultValue = modelCkAttribute.DefaultValue,
                DefaultValues = modelCkAttribute.DefaultValues
            };
            transientCkModel.CkAttributes.Add(ckAttribute);
        }
    }

    private void ProcessCkEntitiesAndAssociations(ITenantCkModelRepository ckModelRepository, CkModelRoot model,
        TransientCkModel transientCkModel)
    {
        foreach (var entity in model.CkEntities)
        {
            var ckEntityAttributes = new List<CkEntityAttribute>();
            foreach (var attribute in entity.Attributes)
            {
                var ckEntityAttribute = new CkEntityAttribute
                {
                    AttributeId = attribute.AttributeId,
                    AttributeName = attribute.AttributeName,
                    AutoCompleteFilter = attribute.AutoCompleteFilter,
                    AutoCompleteLimit = attribute.AutoCompleteLimit,
                    IsAutoCompleteEnabled = attribute.IsAutoCompleteEnabled,
                    AutoIncrementReference = attribute.AutoIncrementReference
                };

                ckEntityAttributes.Add(ckEntityAttribute);
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
                CkId = new CkId<CkTypeId>(model.ModelId, entity.CkId),
                IsFinal = entity.IsFinal,
                IsAbstract = entity.IsAbstract,
                Attributes = ckEntityAttributes,
                Indexes = textSearchDefinitions
            };

            if (entity.CkDerivedId != null)
            {
                var ckEntityInheritance = new CkEntityInheritance
                {
                    OriginCkId = entity.CkDerivedId.Value,
                    TargetCkId = new CkId<CkTypeId>(model.ModelId, entity.CkId)
                };
                transientCkModel.CkEntityInheritances.Add(ckEntityInheritance);
            }

            if (entity.Associations != null)
            {
                foreach (var association in entity.Associations)
                {
                    var ckEntityAssociation = new CkEntityAssociation
                    {
                        RoleId = association.RoleId,
                        OriginCkId = new CkId<CkTypeId>(model.ModelId, ckEntity.CkId.Key),
                        TargetCkId = association.TargetCkId,
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
            throw new OperationFailedException(
                "Write operation was not acknowledged by database.");
        }
    }
}