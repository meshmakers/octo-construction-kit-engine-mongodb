using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>How the content of a restored model compares with the catalog's model of the same id.</summary>
internal enum RestoreSystemModelContentMatch
{
    /// <summary>Canonical content identical, the resolved dependency closure included.</summary>
    Identical,

    /// <summary>
    ///     Identical except the resolved dependency closure of a range-retaining model, whose entries moved within the
    ///     declared ranges (<see cref="RestoreSystemModelClosure" />). Genuine once every moved entry names a published
    ///     version.
    /// </summary>
    ClosureMoved,

    /// <summary>Another content: a manipulated model.</summary>
    Differs
}

/// <summary>The comparison of a restored model with the catalog's model of the same id.</summary>
/// <param name="Match">How the two compare.</param>
/// <param name="MovedClosureEntries">
///     With <see cref="RestoreSystemModelContentMatch.ClosureMoved" />: the closure entries of the restored model that
///     differ from the catalog's; each must name a published version. Empty otherwise.
/// </param>
internal sealed record RestoreSystemModelContentComparison(
    RestoreSystemModelContentMatch Match,
    IReadOnlyList<CkModelId> MovedClosureEntries)
{
    /// <summary>True unless the content differs.</summary>
    public bool IsGenuineContent => Match != RestoreSystemModelContentMatch.Differs;
}

/// <summary>
///     AB#10061: the resolved dependency closure (<c>dependencies</c>) of a range-retaining compiled model is written by
///     the compiler from whatever dependency versions the build saw: the catalog keeps the closure of the first publish
///     (System.UI-2.12.0 lists System-2.8.0), a service built later embeds the same model version with System-2.10.0, and
///     the tenant rows carry the closure of the service that imported them. For such a model the closure decides nothing
///     at run time: references are major-qualified and bound when the model loads, the declared ranges and floors
///     (<c>dependencyRanges</c>) decide what resolves. The engine's publish gate treats such a closure move as no change
///     of the model (<c>CkClosureMove</c>, AB#5435); the restore check follows the same rule and validates the closure
///     on its own instead of comparing it exactly.
///     <para>
///         The closure of the restored model is accepted when all of this holds, otherwise the model counts as another
///         content: both models are range-retaining (their declared ranges are compared exactly with the rest of the
///         model); the closure names exactly the same models as the catalog's, each once; every entry that differs is a
///         concrete version of the same major as the catalog's entry and, when the model declares a range for that name,
///         inside the effective range (range with its floor). The caller additionally requires every moved entry to be a
///         published version (reason <c>dependency_unpublished</c>), so the accepted closures are exactly those a build
///         of the published model against published dependencies can write. Classic models (no
///         <c>dependencyRanges</c>) resolve by their exact pins: their closure is compared exactly.
///     </para>
/// </summary>
internal static class RestoreSystemModelClosure
{
    /// <summary>Compares <paramref name="readBack" /> (a tenant's model) with <paramref name="published" /> (the catalog's).</summary>
    internal static RestoreSystemModelContentComparison Compare(CkCompiledModelRoot readBack,
        CkCompiledModelRoot published)
    {
        if (string.Equals(CkCompiledModelCanonicalizer.ToCanonicalJson(readBack),
                CkCompiledModelCanonicalizer.ToCanonicalJson(published), StringComparison.Ordinal))
        {
            return new RestoreSystemModelContentComparison(RestoreSystemModelContentMatch.Identical, []);
        }

        var moved = MovedWithinDeclaredRanges(readBack, published);
        if (moved == null || !string.Equals(CkCompiledModelCanonicalizer.ToCanonicalJson(readBack, true),
                CkCompiledModelCanonicalizer.ToCanonicalJson(published, true), StringComparison.Ordinal))
        {
            return new RestoreSystemModelContentComparison(RestoreSystemModelContentMatch.Differs, []);
        }

        return new RestoreSystemModelContentComparison(RestoreSystemModelContentMatch.ClosureMoved, moved);
    }

    /// <summary>
    ///     The closure entries of <paramref name="readBack" /> that moved within the declared ranges of
    ///     <paramref name="published" />, or null when the closures differ in any other way (see the type remarks). An
    ///     empty list means the closures name the same versions (in another order or form).
    /// </summary>
    internal static IReadOnlyList<CkModelId>? MovedWithinDeclaredRanges(CkCompiledModelRoot readBack,
        CkCompiledModelRoot published)
    {
        if (!readBack.IsRangeRetaining || !published.IsRangeRetaining)
        {
            return null;
        }

        var restored = ByName(readBack.Dependencies);
        var catalog = ByName(published.Dependencies);
        if (restored == null || catalog == null || restored.Count != catalog.Count ||
            restored.Keys.Any(name => !catalog.ContainsKey(name)))
        {
            return null;
        }

        var moved = new List<CkModelId>();
        foreach (var (name, entry) in restored)
        {
            var publishedEntry = catalog[name];
            if (entry.Equals(publishedEntry))
            {
                continue;
            }

            if (entry.IsMajorQualified || publishedEntry.IsMajorQualified ||
                entry.Version.Major != publishedEntry.Version.Major)
            {
                return null;
            }

            var declared = published.DependencyRanges!.FirstOrDefault(d =>
                string.Equals(d.Range.Name, name, StringComparison.Ordinal));
            if (declared != null && !declared.IsSatisfiedBy(entry))
            {
                return null;
            }

            moved.Add(entry);
        }

        return moved;
    }

    /// <summary>The closure by model name; null when a name occurs twice.</summary>
    private static Dictionary<string, CkModelId>? ByName(IReadOnlyCollection<CkModelId>? closure)
    {
        var result = new Dictionary<string, CkModelId>(StringComparer.Ordinal);
        foreach (var entry in closure ?? [])
        {
            if (!result.TryAdd(entry.Name, entry))
            {
                return null;
            }
        }

        return result;
    }
}
