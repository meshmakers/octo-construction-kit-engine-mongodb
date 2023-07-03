using System.Text;
using CkModel;
using CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class CkModelValidation
{
    private readonly ITenantCkModelRepository _tenantCkModelRepository;

    private List<CkAttribute>? _availableAttributes;
    private TransientCkModel? _transientCkModel;

    public CkModelValidation(ITenantCkModelRepository tenantCkModelRepository)
    {
        _tenantCkModelRepository = tenantCkModelRepository;
    }

    public async Task Validate(IOctoSession session, TransientCkModel transientCkModel, ScopeIds scopeId,
        CancellationToken? cancellationToken)
    {
        _transientCkModel = transientCkModel;

        var dbAttributes = await _tenantCkModelRepository.GetCkAttributesByScopeAsync(session, scopeId);
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
            throw ModelValidationException.DuplicateAttributeIds(duplicateAttributes.Select(a => a.Key));
        }
    }

    private async Task ValidateEntitiesAndInheritance(IOctoSession session, ScopeIds scopeId)
    {
        var dbEntities = (await _tenantCkModelRepository.GetCkEntitiesByScopeAsync(session, scopeId))
            .ToList();
        var dbEntityInheritances = await _tenantCkModelRepository.GetCkEntityInheritancesByScopeAsync(session, scopeId);
        var availableEntitiesIds = dbEntities.Select(x => x.CkId.ToString())
            .Union(_transientCkModel.CkEntities.Select(x => x.CkId)).ToList();
        var availableEntities = dbEntities.Union(_transientCkModel.CkEntities).ToList();
        var availableEntityInheritances = dbEntityInheritances.Union(_transientCkModel.CkEntityInheritances).ToList();

        foreach (var ckEntityInheritance in _transientCkModel.CkEntityInheritances)
        {
            if (!availableEntitiesIds.Contains(ckEntityInheritance.OriginCkId))
            {
                throw ModelValidationException.UnknownCkIdForInheritance(ckEntityInheritance.OriginCkId);
            }

            if (!availableEntitiesIds.Contains(ckEntityInheritance.TargetCkId))
            {
                throw ModelValidationException.UnknownCkIdForInheritance(ckEntityInheritance.TargetCkId);
            }
        }

        foreach (var ckEntity in _transientCkModel.CkEntities)
        {
            if (dbEntities.Any(x => x.CkId == ckEntity.CkId))
            {
                throw ModelValidationException.CkIdAlreadyExistsInDatabase(ckEntity.CkId);
            }

            foreach (var attribute in ckEntity.Attributes)
            {
                if (_availableAttributes.All(a => a.AttributeId != attribute.AttributeId))
                {
                    throw ModelValidationException.UnknownAttributeOfCkIdInSource(ckEntity.CkId, attribute.AttributeId);
                }
            }

            var attributes = GetAllDerivedAttributes(availableEntityInheritances, availableEntities, ckEntity).ToList();

            var systemReservedAttributeNames = attributes
                .Where(attr => CkModelCommon.SystemReservedAttributeNames.Contains(attr.AttributeName)).ToList();
            if (systemReservedAttributeNames.Count > 0)
            {
                throw ModelValidationException.CkIdUsingSystemReservedAttributeNames(ckEntity.CkId,
                    systemReservedAttributeNames.Select(x => x.AttributeName));
            }

            var duplicateAttributeNames = attributes.GroupBy(a => a.AttributeName).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeNames.Count > 0)
            {
                throw ModelValidationException.DuplicateAttributeNamesInCkEntity(ckEntity.CkId,
                    duplicateAttributeNames.Select(a => a.Key));
            }

            var duplicateAttributeIds = attributes.GroupBy(a => a.AttributeId).Where(a => a.Count() > 1).ToList();
            if (duplicateAttributeIds.Count > 0)
            {
                throw ModelValidationException.DuplicateAttributeIdsInCkEntity(ckEntity.CkId,
                    duplicateAttributeIds.Select(a => a.Key));
            }

            ValidateTextSearchLanguage(ckEntity, attributes);
        }
    }

    private static void ValidateTextSearchLanguage(CkEntity ckEntity, List<ICkEntityAttribute> attributes)
    {
        var errorMessageStringBuilder = new StringBuilder();

        var missingAttributes = new List<string>();
        var unknownAnalyzers = new List<string>();
        foreach (var ckEntityIndex in ckEntity.Indexes)
        {
            if (ckEntityIndex.IndexType == IndexTypes.Text &&
                !CkModelCommon.KnownAnalyzerLanguages.Contains(ckEntityIndex.Language))
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
            throw ModelValidationException.CommonValidationFailed(errorMessageStringBuilder.ToString());
        }
    }

    private IEnumerable<ICkEntityAttribute> GetAllDerivedAttributes(IList<CkEntityInheritance> ckEntityInheritances,
        IList<CkEntity> availableEntities, CkEntity ckEntity)
    {
        var attributeList = new List<ICkEntityAttribute>();
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
