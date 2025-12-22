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

public class AutoIncrementModifier(
    ICkCacheService ckCacheService)
    : IPreDocumentModification<RtEntity>
{
    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        var autoIncrementGraphType = ckCacheService.GetCkType(repositoryDataSource.TenantId, SystemCkIds.CkAutoIncrementTypeId);
        var autoIncrementCollection = repositoryDataSource.GetRtCollection<RtAutoIncrement>(autoIncrementGraphType);

        var documentList = documents.ToArray();
        var ckTypeIds = documentList.GroupBy(d => d.GetRtCkTypeId());
        HashSet<string> autoIncrementReferences = new();
        foreach (IGrouping<RtCkId<CkTypeId>, RtEntity> ckTypeId in ckTypeIds)
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, ckTypeId.Key);
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, ckTypeId.Key);
            }

            var typeAttributeGraphs = ckTypeGraph.AllAttributes.Values
                .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference))
                .Select(x => x.AutoIncrementReference!).ToList();
            if (!typeAttributeGraphs.Any())
            {
                return;
            }

            // Add unique auto increment references of typeAttributeGraphs to hashset autoIncrementReferences
            foreach (string typeAttributeGraph in typeAttributeGraphs)
            {
                autoIncrementReferences.Add(typeAttributeGraph);
            }
        }

        if (!autoIncrementReferences.Any())
        {
            return;
        }

        var autoIncrementerSet = await autoIncrementCollection.FindManyAsync(session,
            f => f.RtWellKnownName != null && autoIncrementReferences.Contains(f.RtWellKnownName));

        foreach (RtEntity rtEntity in documentList.AsParallel())
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, rtEntity.GetRtCkTypeId());
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId, rtEntity.GetRtCkTypeId());
            }

            var typeIncrements = ckTypeGraph.AllAttributes.Values
                .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
            if (!typeIncrements.Any())
            {
                return;
            }

            foreach (var autoIncrementReference in typeIncrements)
            {
                if (!ckTypeGraph.AllAttributesByName.TryGetValue(autoIncrementReference.AttributeName, out CkTypeAttributeGraph? ckTypeAttributeGraph))
                {
                    throw InvalidAttributeException.AttributeNotFoundAtRtCkIdType(rtEntity.GetRtCkTypeId(),
                        autoIncrementReference.AttributeName);
                }

                var autoIncrement = autoIncrementerSet.FirstOrDefault(x =>
                    x.RtWellKnownName == autoIncrementReference.AutoIncrementReference);
                if (autoIncrement == null)
                {
                    throw InvalidAttributeException.AutoIncrementReferenceNotFound(rtEntity.GetRtCkTypeId(),
                        autoIncrementReference.AutoIncrementReference);
                }

                var value = await ExecuteAutoIncrementAsync(session, repositoryDataSource, autoIncrement);
                if (!string.IsNullOrEmpty(autoIncrement.Format) &&
                    ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.String)
                {
                    rtEntity.SetAttributeValue(autoIncrementReference.AttributeName,
                        ckTypeAttributeGraph.ValueType, string.Format(autoIncrement.Format, value));
                }
                else
                {
                    rtEntity.SetAttributeValue(autoIncrementReference.AttributeName,
                        ckTypeAttributeGraph.ValueType, value);
                }
            }
        }
    }

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
}
