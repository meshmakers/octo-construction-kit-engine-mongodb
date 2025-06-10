using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

public class AutoIncrementModifier(
    ICkCacheService ckCacheService)
    : IPreDocumentModification<RtEntity>
{
    private readonly static CkId<CkTypeId> AutoIncrementCkTypeId =
        new(SystemCkIds.ModelId, SystemCkIds.AutoIncrementTypeId);

    public async Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        var autoIncrementGraphType = ckCacheService.GetCkType(repositoryDataSource.TenantId, AutoIncrementCkTypeId);
        var autoIncrementCollection = repositoryDataSource.GetRtCollection<RtAutoIncrement>(autoIncrementGraphType);

        var documentList = documents.ToArray();
        var ckTypeIds = documentList.GroupBy(d => d.GetCkTypeId());
        HashSet<string> autoIncrementReferences = new();
        foreach (IGrouping<CkId<CkTypeId>, RtEntity> ckTypeId in ckTypeIds)
        {
            var ckTypeGraph = ckCacheService.GetCkType(repositoryDataSource.TenantId, ckTypeId.Key);
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.CkTypeIdNotFound(repositoryDataSource.TenantId, ckTypeId.Key);
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
            var ckTypeGraph = ckCacheService.GetCkType(repositoryDataSource.TenantId, rtEntity.GetCkTypeId());
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.CkTypeIdNotFound(repositoryDataSource.TenantId, rtEntity.GetCkTypeId());
            }

            var typeIncrements = ckTypeGraph.AllAttributes.Values
                .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
            if (!typeIncrements.Any())
            {
                return;
            }

            foreach (var autoIncrementReference in typeIncrements)
            {
                var ckTypeAttributeGraph = ckTypeGraph.AllAttributes[autoIncrementReference.AttributeName];
                if (ckTypeAttributeGraph == null)
                {
                    throw InvalidAttributeException.AttributeNotFound(rtEntity.GetCkTypeId(),
                        autoIncrementReference.AttributeName);
                }

                var autoIncrement = autoIncrementerSet.FirstOrDefault(x =>
                    x.RtWellKnownName == autoIncrementReference.AutoIncrementReference);
                if (autoIncrement == null)
                {
                    throw InvalidAttributeException.AutoIncrementReferenceNotFound(rtEntity.GetCkTypeId(),
                        autoIncrementReference.AutoIncrementReference);
                }

                rtEntity.SetAttributeValue(autoIncrementReference.AttributeName,
                    ckTypeAttributeGraph.ValueType,
                    await ExecuteAutoIncrementAsync(session, repositoryDataSource, autoIncrement));
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

        var ckTypeGraph = ckCacheService.GetCkType(repositoryDataSource.TenantId,
            autoIncrement.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined());

        autoIncrement.CurrentValue = currentValue;
        await repositoryDataSource.GetRtCollection<RtEntity>(ckTypeGraph)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }
}
