using System.Collections;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class RtEntityFieldFilterResolver<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeGraph ckTypeGraph)
    : RtFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph)
    where TEntity : RtEntity, new()
{
    internal override string GetEntityName()
    {
        return ckTypeGraph.CkTypeId.FullName;
    }

    internal override bool IsAttributePathValid(string attributePath)
    {
        if (base.IsAttributePathValid(attributePath))
        {
            return true;
        }

        return attributePath.ToPascalCase() == nameof(RtEntity.RtId) ||
               attributePath.ToPascalCase() == nameof(RtEntity.CkTypeId) ||
               attributePath.ToPascalCase() == nameof(RtEntity.RtCreationDateTime) ||
               attributePath.ToPascalCase() == nameof(RtEntity.RtChangedDateTime) ||
               attributePath.ToPascalCase() == nameof(RtEntity.RtVersion) ||
               attributePath.ToPascalCase() == nameof(RtEntity.RtWellKnownName);
    }

    internal override string? ResolveAttributePath(string attributePath)
    {
        var r = base.ResolveAttributePath(attributePath);
        if (!string.IsNullOrWhiteSpace(r))
        {
            return r;
        }

        return attributePath.ToPascalCase() switch
        {
            nameof(RtEntity.RtId) => Constants.IdField,
            nameof(RtEntity.CkTypeId) => nameof(RtEntity.CkTypeId).ToCamelCase(),
            nameof(RtEntity.RtCreationDateTime) => nameof(RtEntity.RtCreationDateTime).ToCamelCase(),
            nameof(RtEntity.RtChangedDateTime) => nameof(RtEntity.RtChangedDateTime).ToCamelCase(),
            nameof(RtEntity.RtVersion) => nameof(RtEntity.RtVersion).ToCamelCase(),
            nameof(RtEntity.RtWellKnownName) => nameof(RtEntity.RtWellKnownName).ToCamelCase(),
            _ => null
        };
    }

    internal override object? ResolveSearchAttributeValue(string attributePath, object? searchTerm, out bool isEnum)
    {
        isEnum = false;
        if (searchTerm == null)
        {
            return base.ResolveSearchAttributeValue(attributePath, searchTerm, out isEnum);
        }

        return attributePath.ToPascalCase() switch
        {
            nameof(RtEntity.RtId) => Get(attributePath, searchTerm, GetAsOctoObjectId),
            nameof(RtEntity.CkTypeId) => Get(attributePath, searchTerm, GetAsCkTypeId),
            nameof(RtEntity.RtCreationDateTime) => Get(attributePath, searchTerm, GetAsDateTime),
            nameof(RtEntity.RtChangedDateTime) => Get(attributePath, searchTerm, GetAsDateTime),
            nameof(RtEntity.RtVersion) => Get(attributePath, searchTerm, GetAsInteger),
            nameof(RtEntity.RtWellKnownName) => Get(attributePath, searchTerm, GetAsString),
            _ => base.ResolveSearchAttributeValue(attributePath, searchTerm, out isEnum)
        };
    }

    private static object Get(string attributePath, object searchTeam, Func<string, object, object> f)
    {
        if (searchTeam is ICollection collection)
        {
            var result = new List<object>();
            foreach (var item in collection)
            {
                result.Add(f(attributePath, item));
            }
            return result;
        }
        return f(attributePath, searchTeam);
    }

    private static object GetAsOctoObjectId(string attributePath, object searchTerm)
    {
        return searchTerm is OctoObjectId octoObjectId
            ? octoObjectId
            : OctoObjectId.Parse(GetAsString(attributePath, searchTerm));
    }

    private static object GetAsCkTypeId(string attributePath, object searchTerm)
    {
        return searchTerm is CkId<CkTypeId> ckTypeId
            ? ckTypeId
            : new CkId<CkTypeId>(GetAsString(attributePath, searchTerm));
    }

    private static object GetAsDateTime(string attributePath, object searchTerm)
    {
        return searchTerm is DateTime dateTime
            ? dateTime
            : DateTime.Parse(GetAsString(attributePath, searchTerm));
    }

    private static object GetAsInteger(string attributePath, object searchTerm)
    {
        return searchTerm is int integer
            ? integer
            : int.Parse(GetAsString(attributePath, searchTerm));
    }

    private static string GetAsString(string attributePath, object searchTerm)
    {
        return searchTerm.ToString() ??
               throw OperationFailedException.CannotConvertToObjectId(attributePath);
    }
}
