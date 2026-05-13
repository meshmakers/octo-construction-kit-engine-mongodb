using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Walks a rollup archive's source-archive chain to recover the *logical* CK-attribute paths the
/// rollup ultimately aggregates over. Companion of <see cref="RollupQueryAggregationResolver"/>:
/// the query resolver maps (logicalPath, function) → SQL, whereas this resolver answers
/// "given this rollup, which logical paths can the operator query against?".
/// </summary>
/// <remarks>
/// For a rollup directly on a raw or time-range archive the answer is trivial — the spec's
/// <see cref="CkRollupAggregationSpec.SourcePath"/> is already the CK attribute path. For
/// cascade rollups (rollup-over-rollup) the spec's source path is a *physical* storage column
/// on the parent rollup (e.g. <c>amountvalue_sum</c>); we reverse-map it through the parent's
/// aggregation specs using <see cref="RollupAggregationColumns.Resolve"/> until we hit a raw /
/// time-range archive where the path is finally logical.
///
/// Concept-time-range §7. Returned paths are de-duplicated and order-preserving by first
/// occurrence — the studio picker uses them as-is to populate the column selector for stream-
/// data queries.
/// </remarks>
public static class RollupLogicalPathResolver
{
    /// <summary>
    /// Maximum chain depth the walker will descend before giving up. Defends against
    /// pathological cycles caused by store inconsistency — well-formed chains are short
    /// (typically 1–3 levels: raw → daily → monthly → yearly).
    /// </summary>
    private const int MaxChainDepth = 8;

    /// <summary>
    /// Resolves the rollup's aggregation specs to the distinct logical CK-attribute paths they
    /// ultimately aggregate. Specs whose chain can't be resolved (missing parent, store
    /// inconsistency, chain too deep) are silently dropped so a single broken spec doesn't
    /// blank the entire picker.
    /// </summary>
    /// <param name="rollup">The rollup archive snapshot to resolve paths for.</param>
    /// <param name="getArchive">Loader that returns any archive (raw / time-range / rollup) by rtId. Returns null if missing.</param>
    /// <param name="getRollup">Loader that returns a rollup snapshot by rtId. Returns null if the archive isn't a rollup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        RollupArchiveSnapshot rollup,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken = default)
    {
        // Preserve first-seen order so the picker UI is deterministic across reloads.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var spec in rollup.Aggregations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await ResolveSpecPathAsync(
                spec.SourcePath, rollup.SourceArchiveRtId, getArchive, getRollup, cancellationToken);
            if (path != null && seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static async Task<string?> ResolveSpecPathAsync(
        string sourcePath,
        OctoObjectId sourceArchiveRtId,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceSnapshot = await getArchive(sourceArchiveRtId).ConfigureAwait(false);
            if (sourceSnapshot is null)
            {
                return null; // chain broken — parent archive missing
            }

            if (sourceSnapshot.RollupAggregations is null)
            {
                // Source is raw or time-range. sourcePath addresses a CK attribute on that archive,
                // which by construction matches the original CK type's attribute graph.
                return sourcePath;
            }

            // Source is a rollup. The sourcePath is a physical storage column on that parent
            // rollup's table — reverse-map it through the parent's aggregation specs to recover
            // *its* logical sourcePath, then continue climbing.
            var sourceRollup = await getRollup(sourceArchiveRtId).ConfigureAwait(false);
            if (sourceRollup is null)
            {
                // ArchiveSnapshot says it's a rollup but the rollup store can't find it — store
                // inconsistency, treat as unresolvable so we don't surface garbage to the picker.
                return null;
            }

            string? parentSourcePath = null;
            foreach (var parentSpec in sourceRollup.Aggregations)
            {
                var (_, targets) = RollupAggregationColumns.Resolve(parentSpec);
                foreach (var target in targets)
                {
                    if (string.Equals(target.ColumnName, sourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        parentSourcePath = parentSpec.SourcePath;
                        break;
                    }
                }
                if (parentSourcePath != null)
                {
                    break;
                }
            }

            if (parentSourcePath is null)
            {
                // sourcePath didn't match any of the parent's materialised target columns —
                // chain is malformed (or the parent was edited after this rollup was provisioned).
                return null;
            }

            sourcePath = parentSourcePath;
            sourceArchiveRtId = sourceRollup.SourceArchiveRtId;
        }
        return null; // recursion cap hit — defensive against cycles
    }
}
