using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts.DisplayRules;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
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
///     "ckTypeId@rtId".
/// </summary>
public class DisplayNameModifier(ICkCacheService ckCacheService) : IPreDocumentModification<RtEntity>
{
    /// <summary>
    ///     Rules are validated at model compile time; parsing is memoized by rule text (identical
    ///     rules across types and tenants share one entry).
    /// </summary>
    private static readonly ConcurrentDictionary<string, DisplayRuleParseResult> ParsedRules = new();

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

            rtEntity.RtDisplayName = EvaluateRule(ckTypeGraph.DisplayNameRule, rtEntity);
            rtEntity.RtDisplayDescription = EvaluateRule(ckTypeGraph.DisplayDescriptionRule, rtEntity);
        }

        return Task.CompletedTask;
    }

    private static string? EvaluateRule(string? rule, RtEntity rtEntity)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return null;
        }

        var parseResult = ParsedRules.GetOrAdd(rule!, DisplayRuleParser.Parse);
        if (!parseResult.IsValid)
        {
            // Rules are validated at compile time; a stale invalid rule must not block the save.
            return null;
        }

        return parseResult.Evaluate(path => ResolveValue(rtEntity, path));
    }

    /// <summary>
    ///     Resolves a rule path against the entity's attributes; dot-separated segments traverse
    ///     record values (<see cref="RtRecord" />).
    /// </summary>
    private static object? ResolveValue(RtEntity rtEntity, string path)
    {
        RtTypeWithAttributes current = rtEntity;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var value = current.GetAttributeValueOrDefault(segments[i]);
            if (i == segments.Length - 1)
            {
                return value;
            }

            if (value is not RtTypeWithAttributes nested)
            {
                return null;
            }

            current = nested;
        }

        return null;
    }
}
