using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     D3 / review M6 (CK v2 range retention): range-retaining models persist references into their
///     dependencies major-qualified (<c>System@2/Entity-1</c>, verbatim, so the read-back round-trips). Code that
///     works on the persisted CK rows directly — index maintenance during an import, before the CK cache exists —
///     binds them to the installed version of that model and major here. Concrete references pass through
///     unchanged, so classic (exact-pinned) tenants see no difference.
/// </summary>
internal sealed class InstalledModelReferenceBinder
{
    private readonly Dictionary<(string Name, int Major), CkModelId> _installed = new();

    public InstalledModelReferenceBinder(IEnumerable<CkModelId> installedModelIds)
    {
        foreach (var modelId in installedModelIds.Where(m => !m.IsMajorQualified))
        {
            var key = (modelId.Name, modelId.Version.Major);
            if (!_installed.TryGetValue(key, out var existing) || existing.Version.CompareTo(modelId.Version) < 0)
            {
                _installed[key] = modelId;
            }
        }
    }

    /// <summary>The concrete model a major-qualified id binds to, or <c>null</c>.</summary>
    public CkModelId? Resolve(CkModelId modelId)
    {
        return modelId.IsMajorQualified &&
               _installed.TryGetValue((modelId.Name, modelId.Version.Major), out var bound)
            ? bound
            : null;
    }

    /// <summary>Binds a major-qualified element id; any other id is returned unchanged.</summary>
    public CkId<T> Bind<T>(CkId<T> id) where T : IComparable<T>, ICkElementId
    {
        var bound = id.ModelId == null! ? null : Resolve(id.ModelId);
        return bound == null ? id : new CkId<T>(bound, id.ElementId);
    }

    /// <summary>
    ///     Adds a major-qualified alias key (<c>System@2/Name-1</c>) for every element of an installed model, so
    ///     lookups by a verbatim persisted reference find the installed element.
    /// </summary>
    public Dictionary<CkId<T>, TValue> WithMajorQualifiedAliases<T, TValue>(IReadOnlyDictionary<CkId<T>, TValue> source)
        where T : IComparable<T>, ICkElementId
    {
        var result = source.ToDictionary(k => k.Key, v => v.Value);
        foreach (var (id, value) in source)
        {
            if (id.ModelId == null! || id.ModelId.IsMajorQualified)
            {
                continue;
            }

            var alias = id.ModelId.ToMajorQualified();
            if (Resolve(alias) == id.ModelId)
            {
                result.TryAdd(new CkId<T>(alias, id.ElementId), value);
            }
        }

        return result;
    }
}
