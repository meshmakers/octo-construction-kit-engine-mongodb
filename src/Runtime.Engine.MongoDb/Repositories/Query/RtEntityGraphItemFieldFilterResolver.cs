using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class RtEntityGraphItemFieldFilterResolver(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeGraph ckTypeGraph)
    : RtEntityFieldFilterResolver<RtEntityGraphItem>(ckCacheService, tenantId, ckTypeGraph)
{
    private readonly CkTypeGraph _ckTypeGraph = ckTypeGraph;
    internal override bool IsAttributePathValid(string attributePath)
    {
        var navigationPair = RtPathEvaluator.TokenizeAndGetNavigationPair(_ckCacheService, _tenantId, _ckTypeGraph.CkTypeId, attributePath);
        if (navigationPair == null)
        {
            return base.IsAttributePathValid(attributePath);
        }

        while (navigationPair.InnerNavigationPairs.Count > 0)
        {
            navigationPair = navigationPair.InnerNavigationPairs[0];
        }

        if (!navigationPair.SubPathTerms.Any() && !navigationPair.SubPathTerms.First().Any())
        {
            throw OperationFailedException.AttributePathInvalid(attributePath);
        }

        var targetCkTypeGraph = _ckCacheService.GetRtCkType(_tenantId, navigationPair.TargetCkTypeId);
        RtEntityFieldFilterResolver<RtEntity> resolver = new(_ckCacheService, _tenantId, targetCkTypeGraph);
        return resolver.IsAttributePathValid(RtPathEvaluator.GetPath(navigationPair.SubPathTerms.First()));
    }

    internal override string? ResolveAttributePath(string attributePath)
    {
        var navigationPair = RtPathEvaluator.TokenizeAndGetNavigationPair(_ckCacheService, _tenantId, _ckTypeGraph.CkTypeId, attributePath);
        if (navigationPair == null)
        {
            return base.ResolveAttributePath(attributePath);
        }

        while (navigationPair.InnerNavigationPairs.Count > 0)
        {
            navigationPair = navigationPair.InnerNavigationPairs[0];
        }

        if (!navigationPair.SubPathTerms.Any() && !navigationPair.SubPathTerms.First().Any())
        {
            throw OperationFailedException.AttributePathInvalid(attributePath);
        }

        var targetCkTypeGraph = _ckCacheService.GetRtCkType(_tenantId, navigationPair.TargetCkTypeId);
        RtEntityFieldFilterResolver<RtEntity> resolver = new(_ckCacheService, _tenantId, targetCkTypeGraph);
        return resolver.ResolveAttributePath(RtPathEvaluator.GetPath(navigationPair.SubPathTerms.First()));

    }
}
