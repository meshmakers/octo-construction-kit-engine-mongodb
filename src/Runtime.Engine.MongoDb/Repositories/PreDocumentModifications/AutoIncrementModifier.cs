using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     Numbers the auto-increment attributes (<c>autoIncrementReference</c>) of NEW entities from their
///     <c>System/AutoIncrement</c> counter. The pre-document-modification pipeline runs on insert and on replace and
///     does not say which (AB#6209), so the modifier tells them apart by the stored state:
///     <list type="bullet">
///         <item>An entity that is not stored yet gets a number only for an attribute without a value; a supplied
///         value (ImportRt, restore, blueprint seed) is kept and does not advance the counter.</item>
///         <item>An entity that is already stored (replace, or a bulk import of an existing entity) is never
///         renumbered; when its payload has no value for the attribute, the stored number is carried over, so a
///         replace never clears it either.</item>
///     </list>
///     Every entity of a mixed batch is handled; an entity or type without auto-increment attributes is skipped.
/// </summary>
public class AutoIncrementModifier(
    ICkCacheService ckCacheService)
    : IPreDocumentModification<RtEntity>
{
    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        var documentList = documents.ToArray();
        var autoIncrementTypes = new List<AutoIncrementType>();
        HashSet<string> autoIncrementReferences = new();
        foreach (IGrouping<RtCkId<CkTypeId>, RtEntity> ckTypeGroup in documentList.GroupBy(d => d.GetRtCkTypeId()))
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, ckTypeGroup.Key);
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, ckTypeGroup.Key);
            }

            var typeIncrements = ckTypeGraph.AllAttributes.Values
                .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
            if (typeIncrements.Count == 0)
            {
                continue;
            }

            var attributes = new List<(CkTypeAttributeGraph Attribute, string Reference)>();
            foreach (var typeIncrement in typeIncrements)
            {
                if (!ckTypeGraph.AllAttributesByName.TryGetValue(typeIncrement.AttributeName,
                        out CkTypeAttributeGraph? ckTypeAttributeGraph))
                {
                    throw InvalidAttributeException.AttributeNotFoundAtRtCkIdType(ckTypeGroup.Key,
                        typeIncrement.AttributeName);
                }

                attributes.Add((ckTypeAttributeGraph, typeIncrement.AutoIncrementReference!));
                autoIncrementReferences.Add(typeIncrement.AutoIncrementReference!);
            }

            autoIncrementTypes.Add(new AutoIncrementType(ckTypeGroup.Key, ckTypeGraph, attributes,
                ckTypeGroup.ToList()));
        }

        if (autoIncrementTypes.Count == 0)
        {
            return;
        }

        // Looked up VERSION-LESS and only when an auto-increment attribute is actually written (CK v2 F1.0, D-G1-1).
        // The generated SystemCkIds.CkAutoIncrementTypeId carries the System version this service was compiled
        // against (e.g. System-2.5.0/AutoIncrement-1); since the embedded-import downgrade guard keeps a NEWER System
        // in a tenant, that versioned id is not in the tenant's CK cache, and the unconditional lookup made every
        // entity insert of an older service fail.
        var autoIncrementGraphType = ckCacheService.GetRtCkType(repositoryDataSource.TenantId,
            SystemCkIds.CkAutoIncrementTypeId.ToRtCkId());
        var autoIncrementCollection = repositoryDataSource.GetRtCollection<RtAutoIncrement>(autoIncrementGraphType);

        var autoIncrementerSet = await autoIncrementCollection.FindManyAsync(session,
            f => f.RtWellKnownName != null && autoIncrementReferences.Contains(f.RtWellKnownName));

        foreach (var autoIncrementType in autoIncrementTypes)
        {
            // Only auto-increment types are read, in the session (transaction) of the write.
            var storedEntities = await StoredRtEntities.GetByRtIdAsync(session, repositoryDataSource,
                autoIncrementType.CkTypeGraph, autoIncrementType.Entities);

            foreach (var rtEntity in autoIncrementType.Entities)
            {
                storedEntities.TryGetValue(rtEntity.RtId, out var storedEntity);

                foreach (var (ckTypeAttributeGraph, reference) in autoIncrementType.Attributes)
                {
                    var attributeName = ckTypeAttributeGraph.AttributeName;
                    if (HasValue(rtEntity.GetAttributeValueOrDefault(attributeName)))
                    {
                        // A supplied value is kept: never renumber, never advance the counter for it.
                        continue;
                    }

                    if (storedEntity != null)
                    {
                        // Already stored (replace): carry the existing number over instead of numbering again.
                        var storedValue = storedEntity.GetAttributeValueOrDefault(attributeName);
                        if (HasValue(storedValue))
                        {
                            rtEntity.SetAttributeValue(attributeName, ckTypeAttributeGraph.ValueType, storedValue);
                        }

                        continue;
                    }

                    var autoIncrement = autoIncrementerSet.FirstOrDefault(x => x.RtWellKnownName == reference);
                    if (autoIncrement == null)
                    {
                        throw InvalidAttributeException.AutoIncrementReferenceNotFound(autoIncrementType.CkTypeId,
                            reference);
                    }

                    var value = await ExecuteAutoIncrementAsync(session, repositoryDataSource, autoIncrement);
                    if (!string.IsNullOrEmpty(autoIncrement.Format) &&
                        ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.String)
                    {
                        rtEntity.SetAttributeValue(attributeName, ckTypeAttributeGraph.ValueType,
                            string.Format(autoIncrement.Format, value));
                    }
                    else
                    {
                        rtEntity.SetAttributeValue(attributeName, ckTypeAttributeGraph.ValueType, value);
                    }
                }
            }
        }
    }

    private static bool HasValue(object? value) =>
        value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    private async Task<long> ExecuteAutoIncrementAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        RtAutoIncrement autoIncrement)
    {
        var end = autoIncrement.End;

        var currentValue = autoIncrement.CurrentValue;

        currentValue++;

        if (currentValue > end)
        {
            throw AutoIncrementFailedException.AutoIncrementEndReached(autoIncrement.RtId);
        }

        var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId,
            autoIncrement.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined());

        autoIncrement.CurrentValue = currentValue;
        await repositoryDataSource.GetRtCollection<RtEntity>(ckTypeGraph)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }

    private sealed record AutoIncrementType(
        RtCkId<CkTypeId> CkTypeId,
        CkTypeGraph CkTypeGraph,
        IReadOnlyList<(CkTypeAttributeGraph Attribute, string Reference)> Attributes,
        IReadOnlyList<RtEntity> Entities);
}
