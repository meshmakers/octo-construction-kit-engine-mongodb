using System.Diagnostics;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;
using AttributeValueTypes = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.AttributeValueTypes;
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
    private TransientCkModel? _transientCkModel;
    private ITenantCkModelRepository? _ckModelRepository;

    public ImportCkModelCommand()
    {
    }

    public async Task ImportTextAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string jsonText, ScopeIds scopeId,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _ckModelRepository = ckModelRepository;
            _ckModelValidation = new CkModelValidation(_ckModelRepository);
            _transientCkModel = new TransientCkModel();

            Logger.Info("Reading CK model....");
            var model = CkSerializer.Deserialize(jsonText);

            Logger.Info("Executing import of CK model....");

            await ExecuteImport(session, model, scopeId, cancellationToken);

            Logger.Info("Import of CK model completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of CK model failed.");
            throw;
        }
    }

    public async Task ImportAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string filePath, ScopeIds scopeId,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _ckModelRepository = ckModelRepository;
            _ckModelValidation = new CkModelValidation(_ckModelRepository);
            _transientCkModel = new TransientCkModel();

            Logger.Info("Reading CK model....");
            CkModelRoot? model;
            using (var streamReader = new StreamReader(filePath))
            {
                model = CkSerializer.Deserialize(streamReader);
            }

            Logger.Info("Executing import of CK model....");

            await ExecuteImport(session, model, scopeId, cancellationToken);

            await session.CommitTransactionAsync();

            Logger.Info("Import of CK model completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of CK model failed.");
            throw;
        }
    }

    private async Task ExecuteImport(IOctoSession session, CkModelRoot model, ScopeIds scopeId,
        CancellationToken? cancellationToken)
    {
        Debug.Assert(_ckModelRepository != null, nameof(_ckModelRepository) + " != null");

        // Transform to entities
        ProcessCkAttributes(model, scopeId);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        ProcessCkEntitiesAndAssociations(model, scopeId);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        // ValidateAsync
        Debug.Assert(_ckModelValidation != null, nameof(_ckModelValidation) + " != null");
        Debug.Assert(_transientCkModel != null, nameof(_transientCkModel) + " != null");
        await _ckModelValidation.Validate(session, _transientCkModel, scopeId, cancellationToken);

        // Delete the old version
        if (await DeleteOldVersion(session, scopeId, cancellationToken))
        {
            return;
        }

        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        // ValidateAsync the Model
        if (_transientCkModel.CkAttributes.Any())
        {
            ValidateAndThrow(
                await _ckModelRepository.BulkImportCkAttributesAsync(session,
                    _transientCkModel.CkAttributes.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntities.Any())
        {
            ValidateAndThrow(
                await _ckModelRepository.BulkImportCkEntitiesAsync(session, _transientCkModel.CkEntities.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntityAssociations.Any())
        {
            ValidateAndThrow(
                await _ckModelRepository.BulkImportCkEntityAssociationsAsync(session,
                    _transientCkModel.CkEntityAssociations));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntityInheritances.Any())
        {
            ValidateAndThrow(
                await _ckModelRepository.BulkImportCkEntityInheritancesAsync(session,
                    _transientCkModel.CkEntityInheritances));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        await CreateCollections(session);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        await CreateIndex(session);
    }

    private async Task<bool> DeleteOldVersion(IOctoSession session, ScopeIds scopeId,
        CancellationToken? cancellationToken)
    {
        Debug.Assert(_ckModelRepository != null, nameof(_ckModelRepository) + " != null");

        foreach (var ckAttribute in await _ckModelRepository.GetCkAttributesByScopeAsync(session, scopeId))
        {
            await _ckModelRepository.DeleteCkAttributesOneAsync(session, ckAttribute.AttributeId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntity in await _ckModelRepository.GetCkEntitiesByScopeAsync(session, scopeId))
        {
            await _ckModelRepository.DeleteCkEntitiesOneAsync(session, ckEntity.CkId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityAssociation in await _ckModelRepository.GetCkEntityAssociationsByScopeAsync(session, scopeId))
        {
            await _ckModelRepository.DeleteCkEntityAssociationsOneAsync(session, ckEntityAssociation.AssociationId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityInheritance in await _ckModelRepository.GetCkEntityInheritancesByScopeAsync(session, scopeId))
        {
            await _ckModelRepository.DeleteOneCkEntityInheritancesAsync(session, ckEntityInheritance.InheritanceId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        return false;
    }

    private async Task CreateCollections(IOctoSession session)
    {
        Debug.Assert(_ckModelRepository != null, nameof(_ckModelRepository) + " != null");

        await _ckModelRepository.UpdateCollectionsAsync(session);
    }

    private async Task CreateIndex(IOctoSession session)
    {
        Debug.Assert(_ckModelRepository != null, nameof(_ckModelRepository) + " != null");
        await _ckModelRepository.UpdateIndexAsync(session);
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }

    private void ProcessCkAttributes(CkModelRoot model, ScopeIds scopeId)
    {
        foreach (var modelCkAttribute in model.CkAttributes)
        {
            var ckAttribute = new CkAttribute
            {
                AttributeId = modelCkAttribute.AttributeId,
                ScopeId = scopeId,
                AttributeValueType = (AttributeValueTypes)modelCkAttribute.ValueType,
                SelectionValues = modelCkAttribute.SelectionValues?.Select(sv => new CkSelectionValue
                    { Key = sv.Key, Name = sv.Name }).ToList<ICkSelectionValue>(),
                DefaultValue = modelCkAttribute.DefaultValue,
                DefaultValues = modelCkAttribute.DefaultValues
            };
            _transientCkModel.CkAttributes.Add(ckAttribute);
        }
    }

    private void ProcessCkEntitiesAndAssociations(CkModelRoot model, ScopeIds scopeId)
    {
        var associationRoleDefinitions = model.CkAssociationRoles.ToDictionary(k => k.RoleId, v => v);

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
            foreach (var entityIndexDto in entity.Indexes)
            {
                var entityIndex = new CkEntityIndex
                {
                    IndexType = (IndexTypes)entityIndexDto.IndexType,
                    Language = entityIndexDto.Language,
                    Fields = entityIndexDto.Fields
                        .Select(x => new CkIndexFields { Weight = x.Weight, AttributeNames = x.AttributeNames })
                        .ToList<ICkIndexFields>()
                };

                textSearchDefinitions.Add(entityIndex);
            }


            var ckEntity = new CkEntity
            {
                CkId = entity.CkId,
                ScopeId = scopeId,
                IsFinal = entity.IsFinal,
                IsAbstract = entity.IsAbstract,
                Attributes = ckEntityAttributes,
                Indexes = textSearchDefinitions
            };

            if (!string.IsNullOrWhiteSpace(entity.CkDerivedId))
            {
                var ckEntityInheritance = new CkEntityInheritance
                {
                    ScopeId = scopeId,
                    OriginCkId = entity.CkDerivedId,
                    TargetCkId = entity.CkId
                };
                _transientCkModel.CkEntityInheritances.Add(ckEntityInheritance);
            }

            foreach (var association in entity.Associations)
            {
                var ckEntityAssociation = new CkEntityAssociation
                {
                    RoleId = association.RoleId,
                    ScopeId = scopeId,
                    InboundMultiplicity = (Multiplicities)association.InboundMultiplicity,
                    OutboundMultiplicity = (Multiplicities)association.OutboundMultiplicity,
                    OriginCkId = ckEntity.CkId,
                    TargetCkId = association.TargetCkId,
                    InboundName = associationRoleDefinitions[association.RoleId].InboundName,
                    OutboundName = associationRoleDefinitions[association.RoleId].OutboundName
                };
                _transientCkModel.CkEntityAssociations.Add(ckEntityAssociation);
            }

            _transientCkModel.CkEntities.Add(ckEntity);
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