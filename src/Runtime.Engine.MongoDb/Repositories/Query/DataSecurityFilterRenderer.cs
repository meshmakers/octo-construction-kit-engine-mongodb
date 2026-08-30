using System.Security.Cryptography;
using System.Text;

using Meshmakers.Common.Shared;
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
            // AB#4978: types whose CK model declares an owner attribute compare that attribute's
            // stored value against the subject; all remaining owned-only types use the stamped
            // rtCreatedBy. One Or-branch per distinct owner attribute (deterministic order).
            var ownerAttributes = securityFilter.OwnedOnlyOwnerAttributes;
            var defaultOwnedTypeIds = ownerAttributes is { Count: > 0 }
                ? securityFilter.OwnedOnlyCkTypeIds.Where(t => !ownerAttributes.ContainsKey(t)).ToList()
                : (IReadOnlyCollection<string>)securityFilter.OwnedOnlyCkTypeIds;

            if (defaultOwnedTypeIds.Count > 0)
            {
                orFilters.Add(Builders<TEntity>.Filter.And(
                    Builders<TEntity>.Filter.In(x => x.CkTypeId, ToRtCkIds(defaultOwnedTypeIds)),
                    Builders<TEntity>.Filter.Eq(x => x.RtCreatedBy, securityFilter.SubjectId)));
            }

            if (ownerAttributes is { Count: > 0 })
            {
                foreach (var attributeGroup in ownerAttributes
                             .GroupBy(x => x.Value, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    orFilters.Add(Builders<TEntity>.Filter.And(
                        Builders<TEntity>.Filter.In(x => x.CkTypeId,
                            ToRtCkIds(attributeGroup.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal))),
                        Builders<TEntity>.Filter.Eq(ToMongoFieldPath(attributeGroup.Key),
                            securityFilter.SubjectId)));
                }
            }
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

    /// <summary>
    ///     Translates a CK owner attribute path ("AssigneeId", "Owner.UserId") to the stored BSON
    ///     field path. Scalar values sit directly at attributes.{camelCase}
    ///     (RtAttributeDictionarySerializer — no .value wrapper); each Record hop nests another
    ///     attributes document, mirroring MongoDbAttributePathResolver ("Owner.UserId" →
    ///     "attributes.owner.attributes.userId"). CK compile-time validation guarantees the shape:
    ///     single-valued Record segments with a String terminal.
    /// </summary>
    private static string ToMongoFieldPath(string ownerAttributePath)
    {
        var segments = ownerAttributePath.Split('.').Select(s => s.ToCamelCase());
        return Constants.AttributesName + Constants.PathSeparator +
               string.Join(Constants.PathSeparator + Constants.AttributesName + Constants.PathSeparator, segments);
    }

    private static List<RtCkId<CkTypeId>?> ToRtCkIds(IEnumerable<string> fullNames)
    {
        return fullNames.Select(n => (RtCkId<CkTypeId>?)new RtCkId<CkTypeId>(n)).ToList();
    }
}
