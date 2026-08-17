using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DisplayRules;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     Computes the read-only system fields <see cref="RtEntity.RtDisplayName" /> /
///     <see cref="RtEntity.RtDisplayDescription" /> before persistence from the CK type's effective
///     display rules (declared or inherited, see <c>CkTypeGraph.DisplayNameRule</c>). Runs in the
///     pre-document-modification pipeline, i.e. on Insert and Replace. A type without rules (or a
///     rule whose referenced attributes are all empty) yields null — the read layer falls back to
///     "ckTypeId@rtId". Evaluation semantics are shared via <see cref="RtDisplayRuleEvaluator" />.
/// </summary>
public class DisplayNameModifier(ICkCacheService ckCacheService) : IPreDocumentModification<RtEntity>
{
    public Task RunAsync(IOctoSession session, IRepositoryDataSource repositoryDataSource,
        IEnumerable<RtEntity> documents)
    {
        foreach (var rtEntity in documents)
        {
            var ckTypeGraph = ckCacheService.GetRtCkType(repositoryDataSource.TenantId, rtEntity.GetRtCkTypeId());
            if (ckTypeGraph == null)
            {
                throw InvalidCkTypeIdException.RtCkTypeIdNotFound(repositoryDataSource.TenantId,
                    rtEntity.GetRtCkTypeId());
            }

            rtEntity.RtDisplayName = RtDisplayRuleEvaluator.ComputeValue(ckTypeGraph.DisplayNameRule, rtEntity);
            rtEntity.RtDisplayDescription =
                RtDisplayRuleEvaluator.ComputeValue(ckTypeGraph.DisplayDescriptionRule, rtEntity);
        }

        return Task.CompletedTask;
    }
}
