using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     Tells new from already stored entities inside a pre-document modification (AB#6209). The modification pipeline
///     runs on insert and on replace (and on the upsert of a bulk import) without saying which, so a modification that
///     must act only on new entities - auto-increment numbering today, number allocation and numbering on release
///     later (AB#9753, AB#9758) - asks the database: one read by <c>RtId</c> (the indexed <c>_id</c>) per CK type, in the
///     session (and so the transaction) of the write. Callers read only for types that need it.
/// </summary>
internal static class StoredRtEntities
{
    /// <summary>
    ///     The already stored entities among <paramref name="entities" /> (all of CK type <paramref name="ckTypeGraph" />),
    ///     by <c>RtId</c>. Entities without an <c>RtId</c> are new by definition and cause no read; an empty input reads
    ///     nothing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<OctoObjectId, RtEntity>> GetByRtIdAsync(IOctoSession session,
        IRepositoryDataSource repositoryDataSource, CkTypeGraph ckTypeGraph, IEnumerable<RtEntity> entities)
    {
        var rtIds = entities.Select(e => e.RtId).Where(id => id != OctoObjectId.Empty).Distinct().ToList();
        if (rtIds.Count == 0)
        {
            return new Dictionary<OctoObjectId, RtEntity>();
        }

        var stored = await repositoryDataSource.GetRtCollection<RtEntity>(ckTypeGraph)
            .FindManyAsync(session, e => rtIds.Contains(e.RtId));
        return stored.ToDictionary(e => e.RtId);
    }
}
