using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
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

internal class ImportCkModel
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly CkModelValidation _ckModelValidation;
    private readonly IDatabaseContext _databaseContext;
    private readonly TransientCkModel _transientCkModel;

    public ImportCkModel(IDatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
        _ckModelValidation = new CkModelValidation(databaseContext);
        _transientCkModel = new TransientCkModel();
    }

    public async Task ImportText(IOctoSession session, string jsonText, ScopeIds scopeId,
        CancellationToken? cancellationToken = null)
    {
        Logger.Info("Reading CK model....");
        var model = CkSerializer.Deserialize(jsonText);

        await ExecuteImport(session, model, scopeId, cancellationToken);
    }

    public async Task Import(IOctoSession session, string filePath, ScopeIds scopeId,
        CancellationToken? cancellationToken = null)
    {
        Logger.Info("Reading CK model....");
        CkModelRoot model;
        using (var streamReader = new StreamReader(filePath))
        {
            model = CkSerializer.Deserialize(streamReader);
        }

        Logger.Info("Executing import of CK model....");

        await ExecuteImport(session, model, scopeId, cancellationToken);

        Logger.Info("Import of CK model completed.");
    }

    private async Task ExecuteImport(IOctoSession session, CkModelRoot model, ScopeIds scopeId,
        CancellationToken? cancellationToken)
    {
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
                await _databaseContext.CkAttributes.BulkImportAsync(session,
                    _transientCkModel.CkAttributes.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntities.Any())
        {
            ValidateAndThrow(
                await _databaseContext.CkEntities.BulkImportAsync(session, _transientCkModel.CkEntities.ToArray()));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntityAssociations.Any())
        {
            ValidateAndThrow(
                await _databaseContext.CkEntityAssociations.BulkImportAsync(session,
                    _transientCkModel.CkEntityAssociations));
            if (CheckCancellation(cancellationToken))
            {
                return;
            }
        }

        if (_transientCkModel.CkEntityInheritances.Any())
        {
            ValidateAndThrow(
                await _databaseContext.CkEntityInheritances.BulkImportAsync(session,
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
        foreach (var ckAttribute in await _databaseContext.CkAttributes
                     .FindManyAsync(session, x => x.ScopeId == scopeId))
        {
            await _databaseContext.CkAttributes.DeleteOneAsync(session, ckAttribute.AttributeId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntity in await _databaseContext.CkEntities
                     .FindManyAsync(session, x => x.ScopeId == scopeId))
        {
            await _databaseContext.CkEntities.DeleteOneAsync(session, ckEntity.CkId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityAssociation in await _databaseContext.CkEntityAssociations
                     .FindManyAsync(session, x => x.ScopeId == scopeId))
        {
            await _databaseContext.CkEntityAssociations.DeleteOneAsync(session, ckEntityAssociation.AssociationId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        foreach (var ckEntityInheritance in await _databaseContext.CkEntityInheritances
                     .FindManyAsync(session, x => x.ScopeId == scopeId))
        {
            await _databaseContext.CkEntityInheritances.DeleteOneAsync(session, ckEntityInheritance.InheritanceId);
        }

        if (CheckCancellation(cancellationToken))
        {
            return true;
        }

        return false;
    }

    private async Task CreateCollections(IOctoSession session)
    {
        await _databaseContext.UpdateCollectionsAsync(session);
    }

    private async Task CreateIndex(IOctoSession session)
    {
        await _databaseContext.UpdateIndexAsync(session);
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
                    { Key = sv.Key, Name = sv.Name }).ToList(),
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
                        .ToList()
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

    private void ValidateAndThrow(BulkImportResult bulkImportResult)
    {
        if (bulkImportResult.HasError())
        {
            throw new OperationFailedException(
                "Write operation was not acknowledged by database.");
        }
    }
}
