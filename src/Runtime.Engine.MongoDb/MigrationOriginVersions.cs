using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     Selects, per CK model name, the stored version a data migration starts from (AB#6720).
/// </summary>
/// <remarks>
///     A model in state <see cref="ModelState.ResolveFailed" /> still holds the data of its version: it only lost its
///     dependency pins, e.g. a 3.x System.Communication pinning System.Bot exactly to 3.4.0 after System.Bot 3.5.0 was
///     imported. Before AB#6720 only <see cref="ModelState.Available" /> models were listed, so the import of the
///     current model never saw such an origin and its data migration (Pool to DeploymentSite) silently did not run,
///     depending on the order in which services imported their models. An Available version of a model always wins
///     over a ResolveFailed one; among models of the same state the highest version wins. Models that are still
///     <see cref="ModelState.Importing" /> are never an origin.
/// </remarks>
internal static class MigrationOriginVersions
{
    public static IReadOnlyList<(string ModelName, CkVersion Version)> Select(
        IEnumerable<(string ModelName, CkVersion Version, ModelState State)> storedModels)
    {
        return storedModels
            .Where(m => m.State is ModelState.Available or ModelState.ResolveFailed)
            .GroupBy(m => m.ModelName)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(m => m.State == ModelState.Available)
                    .ThenByDescending(m => m.Version)
                    .First();
                return (best.ModelName, best.Version);
            })
            .ToList();
    }
}
