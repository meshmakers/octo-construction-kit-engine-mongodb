using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

internal class CkModelValidation
{
    private readonly IDatabaseContext _databaseContext;

    private List<CkAttribute> _availableAttributes;
    private TransientCkModel _transientCkModel;

    public CkModelValidation(IDatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    public async Task Validate(IOctoSession session, TransientCkModel transientCkModel, ScopeIds scopeId,
        CancellationToken? cancellationToken)
    {
        _transientCkModel = transientCkModel;

        var dbAttributes = (await _databaseContext.CkAttributes.GetAsync(session))
            .Where(x => (int)x.ScopeId < (int)scopeId).ToList();
        _availableAttributes = dbAttributes.Union(_transientCkModel.CkAttributes).ToList();

        // ValidateAsync
        await ValidateEntitiesAndInheritance(session, scopeId);
        if (CheckCancellation(cancellationToken))
        {
            return;
        }

        ValidateAttributes();
    }

    private void ValidateAttributes()
    {
        var duplicateAttributes = _availableAttributes.GroupBy(a => a.AttributeId).Where(a => a.Count() > 1).ToList();
        if (duplicateAttributes.Count > 0)
        {
            var attributeIds = string.Join(", ", duplicateAttributes.Select(x => x.Key));
            throw new ModelImportException($"Following attribute ids are duplicates: '{attributeIds}'");
        }
    }

    private async Task ValidateEntitiesAndInheritance(IOctoSession session, ScopeIds scopeId)
    {
        var dbEntities = (await _databaseContext.CkEntities.GetAsync(session)).Where(x => (int)x.ScopeId < (int)scopeId)
            .ToList();
        var dbEntityInheritances = (await _databaseContext.CkEntityInheritances.GetAsync(session))
            .Where(x => (int)x.ScopeId < (int)scopeId).ToList();
        var availableEntitiesIds = dbEntities.Select(x => x.CkId.ToString())
            .Union(_transientCkModel.CkEntities.Select(x => x.CkId)).ToList();
        var availableEntities = dbEntities.Union(_transientCkModel.CkEntities).ToList();
        var availableEntityInheritances = dbEntityInheritances.Union(_transientCkModel.CkEntityInheritances).ToList();

        foreach (var ckEntityInheritance in _transientCkModel.CkEntityInheritances)
        {
            if (!availableEntitiesIds.Contains(ckEntityInheritance.OriginCkId))
            {
                throw new ModelImportException($"CkId '{ckEntityInheritance.OriginCkId}' is unknown for inheritance.");
            }

            if (!availableEntitiesIds.Contains(ckEntityInheritance.TargetCkId))
            {
                throw new ModelImportException($"CkId '{ckEntityInheritance.TargetCkId}' is unknown for inheritance.");
            }
        }

        foreach (var ckEntity in _transientCkModel.CkEntities)
        {
            if (dbEntities.Any(x => x.CkId == ckEntity.CkId))
            {
                throw new ModelImportException($"CkId '{ckEntity.CkId}' does already exist in database.");
            }

            foreach (var attribute in ckEntity.Attributes)
            {
                if (_availableAttributes.All(a => a.AttributeId != attribute.AttributeId))
                {
                    throw new ModelImportException(
                        $"Attribute Id '{attribute.AttributeId}' of CkId '{ckEntity.CkId}' does not exist.");
                }
            }

            var attributes = GetAllDerivedAttributes(availableEntityInheritances, availableEntities, ckEntity).ToList();

            var systemReservedAttributeNames = attributes
                .Where(attr => Constants.SystemReservedAttributeNames.Contains(attr.AttributeName)).ToList();
            if (systemReservedAttributeNames.Count > 0)
            {
                var attributeNames = string.Join(", ", systemReservedAttributeNames.Select(x => x.AttributeName));
                throw new ModelImportException(
                    $"CkId '{ckEntity.CkId}' using attribute names that are system reserved: '{attributeNames}'");
            }

            var duplicateAttributeNames = attributes.GroupBy(a => a.AttributeName).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeNames.Count > 0)
            {
                var attributeNames = string.Join(", ", duplicateAttributeNames.Select(x => x.Key));
                throw new ModelImportException(
                    $"CkId '{ckEntity.CkId}' has duplicate attribute names: '{attributeNames}'");
            }

            var duplicateAttributeIds = attributes.GroupBy(a => a.AttributeId).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeIds.Count > 0)
            {
                var attributeIds = string.Join(", ", duplicateAttributeIds.Select(x => x.Key));
                throw new ModelImportException($"CkId '{ckEntity.CkId}' has duplicate attribute IDs: '{attributeIds}'");
            }

            ValidateTextSearchLanguage(ckEntity, attributes);
        }
    }

    private static void ValidateTextSearchLanguage(CkEntity ckEntity, List<CkEntityAttribute> attributes)
    {
        var errorMessageStringBuilder = new StringBuilder();

        var missingAttributes = new List<string>();
        var unknownAnalyzers = new List<string>();
        foreach (var ckEntityIndex in ckEntity.Indexes)
        {
            if (ckEntityIndex.IndexType == IndexTypes.Text &&
                !Constants.KnownAnalyzerLanguages.Contains(ckEntityIndex.Language))
            {
                unknownAnalyzers.Add(ckEntityIndex.Language);
            }

            foreach (var ckIndexFields in ckEntityIndex.Fields)
            {
                var missingAttributeList = ckIndexFields.AttributeNames.Where(s =>
                    !attributes.Exists(attribute => attribute.AttributeName == s)).ToArray();
                if (missingAttributeList.Any())
                {
                    missingAttributes.AddRange(missingAttributeList);
                }

                var duplicateAttributeNames =
                    ckIndexFields.AttributeNames.GroupBy(a => a).Where(a => a.Count() > 1).ToList();
                if (duplicateAttributeNames.Count > 0)
                {
                    var attributeNames = string.Join(", ", duplicateAttributeNames.Select(x => x.Key));
                    errorMessageStringBuilder.AppendLine(
                        $"Text search language '{ckEntityIndex.Language}' at CkId '{ckEntity.CkId}' has duplicate attribute names in one definition: '{attributeNames}'");
                }

                if (ckEntityIndex.IndexType == IndexTypes.Text && ckIndexFields.Weight.HasValue &&
                    ckIndexFields.Weight < 1)
                {
                    errorMessageStringBuilder.AppendLine(
                        $"Weight '{ckIndexFields.Weight}' for language '{ckEntityIndex.Language}' at CkId '{ckEntity.CkId}' is invalid.");
                }
            }
        }

        if (missingAttributes.Any())
        {
            var attributeNames = string.Join(", ", missingAttributes);
            errorMessageStringBuilder.AppendLine(
                $"Text search attribute names '{attributeNames}‘ does not exist at CkId: '{ckEntity.CkId}'");
        }

        if (unknownAnalyzers.Any())
        {
            var unknownAnalyzerTerms = string.Join(", ", unknownAnalyzers);
            errorMessageStringBuilder.AppendLine(
                $"Text search languages '{unknownAnalyzerTerms}‘ are unknown at CkId: '{ckEntity.CkId}'");
        }

        if (errorMessageStringBuilder.Length > 0)
        {
            throw new ModelImportException("Validation of Construction Kit Model failed: " + Environment.NewLine +
                                           errorMessageStringBuilder);
        }
    }

    private IEnumerable<CkEntityAttribute> GetAllDerivedAttributes(IList<CkEntityInheritance> ckEntityInheritances,
        IList<CkEntity> availableEntities, CkEntity ckEntity)
    {
        var attributeList = new List<CkEntityAttribute>();
        var currentEntity = ckEntity;
        while (currentEntity != null)
        {
            attributeList.AddRange(currentEntity.Attributes);

            var result = ckEntityInheritances.FirstOrDefault(x => x.TargetCkId == currentEntity.CkId);
            if (result == null)
            {
                break;
            }

            currentEntity = availableEntities.FirstOrDefault(x => x.CkId == result.OriginCkId);
        }

        return attributeList;
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }
}
