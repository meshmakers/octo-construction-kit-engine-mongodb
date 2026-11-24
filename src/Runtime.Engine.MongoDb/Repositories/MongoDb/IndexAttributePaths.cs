using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Resolves the attribute paths of a CK index definition to MongoDB field paths during an index update.
/// </summary>
/// <remarks>
///     AB#9524: a type stored in the database lists only the attributes it assigns itself; inherited attributes
///     (e.g. <c>Name</c> from a base type) are assigned on the base. Paths are therefore resolved against the
///     inheritance chain of the index-defining type, most-base first, so that the type's own assignment wins over
///     an inherited one of the same name. Invariant: every path that resolved against the type's own attributes
///     resolves to the same field path as before; a plain inherited attribute resolves to exactly the string the
///     fallback produced (<c>attributes.</c> + camelCase of the written name), so its key spec does not change.
/// </remarks>
internal static class IndexAttributePaths
{
    /// <summary>
    ///     The inheritance chain of every type stored in one collection: most-base first, ending with the type itself.
    ///     <paramref name="baseTypes" /> is the chain above the collection root (most-base first, as
    ///     <c>CollectBaseTypesForCollectionRoots</c> returns it); descendants are linked through
    ///     <see cref="CkTypeInfo.InheritedTypes" /> (one row per direct inheritance).
    /// </summary>
    public static Dictionary<CkId<CkTypeId>, IReadOnlyList<CkType>> BuildTypeChains(IReadOnlyList<CkType> baseTypes,
        CkTypeInfo collectionRoot)
    {
        var typesById = new Dictionary<CkId<CkTypeId>, CkType>();
        foreach (var type in baseTypes.Append(collectionRoot).Concat(collectionRoot.Inheritances))
        {
            typesById.TryAdd(type.CkTypeId, type);
        }

        var parentOf = new Dictionary<CkId<CkTypeId>, CkId<CkTypeId>>();
        for (var i = 1; i < baseTypes.Count; i++)
        {
            parentOf[baseTypes[i].CkTypeId] = baseTypes[i - 1].CkTypeId;
        }

        if (baseTypes.Count > 0)
        {
            parentOf[collectionRoot.CkTypeId] = baseTypes[^1].CkTypeId;
        }

        foreach (var inheritance in collectionRoot.InheritedTypes)
        {
            parentOf.TryAdd(inheritance.InheritorCkTypeId, inheritance.BaseCkTypeId);
        }

        var chains = new Dictionary<CkId<CkTypeId>, IReadOnlyList<CkType>>();
        foreach (var (typeId, type) in typesById)
        {
            var chain = new List<CkType> { type };
            var visited = new HashSet<CkId<CkTypeId>> { typeId };
            var current = typeId;
            // A parent outside this collection (or a cycle in damaged metadata) ends the chain.
            while (parentOf.TryGetValue(current, out var parentId) && visited.Add(parentId) &&
                   typesById.TryGetValue(parentId, out var parent))
            {
                chain.Add(parent);
                current = parentId;
            }

            chain.Reverse();
            chains[typeId] = chain;
        }

        return chains;
    }

    /// <summary>The chain of <paramref name="type" />, or the type alone when no chain is known.</summary>
    public static IReadOnlyList<CkType> ChainOf(CkType type,
        IReadOnlyDictionary<CkId<CkTypeId>, IReadOnlyList<CkType>>? typeChains) =>
        typeChains != null && typeChains.TryGetValue(type.CkTypeId, out var chain) ? chain : [type];

    /// <summary>
    ///     A metadata provider over the attribute assignments of a type chain (most-base first): a later (more derived)
    ///     assignment of the same name wins, except that a Hidden assignment always wins (see the provider).
    /// </summary>
    public static DatabaseAttributeMetadataProvider ProviderFor(IReadOnlyList<CkType> chain,
        IReadOnlyDictionary<CkId<CkAttributeId>, CkAttribute> allCkAttributes,
        IReadOnlyDictionary<CkId<CkRecordId>, CkRecord> allCkRecords) =>
        new(chain.SelectMany(t => t.Attributes), allCkAttributes, allCkRecords, isRecordContext: false);

    /// <summary>
    ///     Resolves the attribute paths of one index field group: Secret paths (AB#5533) and paths that reach a Hidden
    ///     assignment of any type of the collection (CK v2 F1.3-S4) are left out, system attributes pass unchanged,
    ///     every other path is resolved; a path that does not resolve falls back to <c>attributes.</c> + camelCase.
    /// </summary>
    public static List<string> ResolveFields(IEnumerable<string> attributeNames,
        DatabaseAttributeMetadataProvider provider, IReadOnlyCollection<DatabaseAttributeMetadataProvider> hiddenProviders,
        Action<string> onSecretSkipped, Action<string> onHiddenSkipped, Action<string> onFallback)
    {
        return MongoDbAttributePathResolver.WithoutSecretAttributePaths(attributeNames, provider, onSecretSkipped)
            .Where(name =>
            {
                if (Constants.IsSystemAttribute(name) ||
                    !hiddenProviders.Any(hiddenProvider => hiddenProvider.ReachesHiddenAttribute(name)))
                {
                    return true;
                }

                onHiddenSkipped(name);
                return false;
            })
            .Select(name =>
            {
                if (Constants.IsSystemAttribute(name))
                {
                    return name;
                }

                var resolved = MongoDbAttributePathResolver.ResolveToMongoDbFieldPath(name, provider);
                if (resolved != null)
                {
                    return resolved;
                }

                onFallback(name);
                return FallbackPath(name);
            }).ToList();
    }

    /// <summary>The field path used for an attribute path that does not resolve (unchanged since before AB#9524).</summary>
    public static string FallbackPath(string attributePath) =>
        Constants.AttributesName + Constants.PathSeparator + attributePath.ToCamelCase();
}
