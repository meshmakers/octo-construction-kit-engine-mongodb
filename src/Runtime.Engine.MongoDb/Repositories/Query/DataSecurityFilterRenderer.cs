using System.Security.Cryptography;
using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>
///     Renders the caller-specific data-permission filter (AB#4973) into a MongoDB predicate that
///     applies to any entity document: a row passes when its type is unprotected, allowed for the
///     caller, or owned-only and created by the caller.
/// </summary>
internal static class DataSecurityFilterRenderer
{
    /// <summary>
    ///     Builds the predicate for entity documents of type <typeparamref name="TEntity" />, or null
    ///     when the filter enforces nothing.
    /// </summary>
    internal static FilterDefinition<TEntity>? Build<TEntity>(RtDataSecurityQueryFilter? securityFilter)
        where TEntity : RtEntity
    {
        if (securityFilter is not { HasEnforcement: true })
        {
            return null;
        }

        var protectedIds = ToRtCkIds(securityFilter.ProtectedCkTypeIds);
        var orFilters = new List<FilterDefinition<TEntity>>
        {
            Builders<TEntity>.Filter.Nin(x => x.CkTypeId, protectedIds)
        };

        if (securityFilter.AllowedCkTypeIds.Count > 0)
        {
            orFilters.Add(Builders<TEntity>.Filter.In(x => x.CkTypeId, ToRtCkIds(securityFilter.AllowedCkTypeIds)));
        }

        if (securityFilter.OwnedOnlyCkTypeIds.Count > 0 && securityFilter.SubjectId != null)
        {
            orFilters.Add(Builders<TEntity>.Filter.And(
                Builders<TEntity>.Filter.In(x => x.CkTypeId, ToRtCkIds(securityFilter.OwnedOnlyCkTypeIds)),
                Builders<TEntity>.Filter.Eq(x => x.RtCreatedBy, securityFilter.SubjectId)));
        }

        return orFilters.Count == 1 ? orFilters[0] : Builders<TEntity>.Filter.Or(orFilters);
    }

    /// <summary>
    ///     Short stable hash of the filter for the query-result-cache key ("|sec:" segment). Empty for
    ///     unrestricted callers so their cache keys stay byte-identical to the pre-permission behavior.
    /// </summary>
    internal static string ComputeCacheKeySegment(RtDataSecurityQueryFilter? securityFilter)
    {
        if (securityFilter is not { HasEnforcement: true })
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(securityFilter.ComputeCacheSegment()));
        return "|sec:" + Convert.ToHexStringLower(bytes)[..16];
    }

    private static List<RtCkId<CkTypeId>?> ToRtCkIds(IEnumerable<string> fullNames)
    {
        return fullNames.Select(n => (RtCkId<CkTypeId>?)new RtCkId<CkTypeId>(n)).ToList();
    }
}
