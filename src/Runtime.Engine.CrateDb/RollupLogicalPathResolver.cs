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
/// <para>
/// For a rollup directly on a raw or time-range archive the answer is trivial — the spec's
/// <see cref="CkRollupAggregationSpec.SourcePath"/> is already the CK attribute path. For
/// cascade rollups (rollup-over-rollup) the spec's source path is a *physical* storage column
/// on the parent rollup (e.g. <c>amountvalue_sum</c>); we reverse-map it through the parent's
/// aggregation specs using <see cref="RollupAggregationColumns.Resolve"/> until we hit a raw /
/// time-range archive where the path is finally logical.
/// </para>
/// <para>
/// Multi-source rollups (AB#5157): a rollup may list several time-disjoint sources. At each level
/// the walker climbs exactly one parent, picked by (1) the source from which the requested base
/// archive is reachable — the source itself, or a rollup source whose own sources transitively
/// contain it, (2) the single source whose materialised columns contain the current
/// source path (the reverse-map discriminator), (3) the first source in declaration order. All
/// sources of one rollup target the same CK type, so the logical paths agree whichever branch is
/// climbed; the discriminator only matters when the branches materialise different columns.
/// </para>
/// <para>
/// Concept-time-range §7. Returned paths are de-duplicated and order-preserving by first
/// occurrence — the studio picker uses them as-is to populate the column selector for stream-
/// data queries.
/// </para>
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
    /// inconsistency, chain too deep, no sources declared) are silently dropped so a single broken
    /// spec doesn't blank the entire picker.
    /// </summary>
    /// <param name="rollup">The rollup archive snapshot to resolve paths for.</param>
    /// <param name="getArchive">Loader that returns any archive (raw / time-range / rollup) by rtId. Returns null if missing.</param>
    /// <param name="getRollup">Loader that returns a rollup snapshot by rtId. Returns null if the archive isn't a rollup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="requestedBaseRtId">
    /// Optional base archive the caller wants the chain resolved through (AB#5157). Wherever it is
    /// reachable from a level's sources — directly declared there, or further up a rollup source's
    /// own source chain — that branch is climbed in preference to the column discriminator;
    /// elsewhere the default selection applies.
    /// </param>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        RollupArchiveSnapshot rollup,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken = default,
        OctoObjectId? requestedBaseRtId = null)
    {
        // Preserve first-seen order so the picker UI is deterministic across reloads.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var spec in rollup.Aggregations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await ResolveSpecPathAsync(
                spec.SourcePath, rollup.Sources, requestedBaseRtId, getArchive, getRollup, cancellationToken);
            if (path != null && seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static async Task<string?> ResolveSpecPathAsync(
        string sourcePath,
        IReadOnlyList<RollupSourceReference> sources,
        OctoObjectId? requestedBaseRtId,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources.Count == 0)
            {
                return null; // no source declared — nothing to climb
            }

            var parent = await PickParentAsync(
                    sourcePath, sources, requestedBaseRtId, getArchive, getRollup, cancellationToken)
                .ConfigureAwait(false);
            if (parent is not var (parentRtId, parentSnapshot))
            {
                return null; // chain broken — every candidate parent archive missing
            }

            if (parentSnapshot.RollupAggregations is null)
            {
                // Source is raw or time-range. sourcePath addresses a CK attribute on that archive,
                // which by construction matches the original CK type's attribute graph.
                return sourcePath;
            }

            // Source is a rollup. The sourcePath is a physical storage column on that parent
            // rollup's table — reverse-map it through the parent's aggregation specs to recover
            // *its* logical sourcePath, then continue climbing.
            var sourceRollup = await getRollup(parentRtId).ConfigureAwait(false);
            if (sourceRollup is null)
            {
                // ArchiveSnapshot says it's a rollup but the rollup store can't find it — store
                // inconsistency, treat as unresolvable so we don't surface garbage to the picker.
                return null;
            }

            var parentSourcePath = ReverseMapThroughRollup(sourceRollup.Aggregations, sourcePath);
            if (parentSourcePath is null)
            {
                // sourcePath didn't match any of the parent's materialised target columns —
                // chain is malformed (or the parent was edited after this rollup was provisioned).
                return null;
            }

            sourcePath = parentSourcePath;
            sources = sourceRollup.Sources;
        }
        return null; // recursion cap hit — defensive against cycles
    }

    /// <summary>
    /// Picks the one parent to climb at this level: the source from which the requested base is
    /// reachable, else the single source whose materialised columns contain <paramref name="sourcePath"/>,
    /// else the first source (in declaration order) whose archive snapshot exists.
    /// </summary>
    private static async Task<(OctoObjectId RtId, ArchiveSnapshot Snapshot)?> PickParentAsync(
        string sourcePath,
        IReadOnlyList<RollupSourceReference> sources,
        OctoObjectId? requestedBaseRtId,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> getArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken)
    {
        // (1) Explicit base request wins on whichever branch leads to it — the base may sit
        // several levels above this one, so we test reachability rather than direct declaration.
        // A base reachable from no source leaves rules (2)/(3) in charge (fail-soft).
        if (requestedBaseRtId is { } requested)
        {
            var anyBranchReaches = false;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reaches = await ReachesAsync(source.SourceArchiveRtId, requested, getRollup, cancellationToken)
                    .ConfigureAwait(false);
                if (!reaches)
                {
                    continue;
                }

                // Diamond: several branches lead to the base — keep declaration order among them.
                anyBranchReaches = true;
                var requestedSnapshot = await getArchive(source.SourceArchiveRtId).ConfigureAwait(false);
                if (requestedSnapshot is not null)
                {
                    return (source.SourceArchiveRtId, requestedSnapshot);
                }
            }

            if (anyBranchReaches)
            {
                // The requested branch exists but its archive is gone — climbing another branch
                // would answer a different question, so report the chain as broken.
                return null;
            }
        }

        // Single source (the overwhelmingly common shape) — no discrimination needed.
        if (sources.Count == 1)
        {
            var onlyRtId = sources[0].SourceArchiveRtId;
            var onlySnapshot = await getArchive(onlyRtId).ConfigureAwait(false);
            return onlySnapshot is null ? null : (onlyRtId, onlySnapshot);
        }

        var candidates = new List<(OctoObjectId RtId, ArchiveSnapshot Snapshot)>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await getArchive(source.SourceArchiveRtId).ConfigureAwait(false);
            if (snapshot is not null)
            {
                candidates.Add((source.SourceArchiveRtId, snapshot));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // (2) The single source whose columns materialise the current path.
        (OctoObjectId RtId, ArchiveSnapshot Snapshot)? materialising = null;
        var materialisingCount = 0;
        foreach (var candidate in candidates)
        {
            if (Materialises(candidate.Snapshot, sourcePath))
            {
                materialising ??= candidate;
                materialisingCount++;
            }
        }

        if (materialisingCount == 1)
        {
            return materialising;
        }

        // (3) First in declaration order.
        return candidates[0];
    }

    /// <summary>
    /// True when <paramref name="target"/> is <paramref name="candidate"/> itself or lies on
    /// <paramref name="candidate"/>'s own source chain (rollup over rollup over …). Bounded by
    /// <see cref="MaxChainDepth"/> and a visited set, so a cyclic store state terminates with
    /// "not reachable" instead of spinning.
    /// </summary>
    private static async Task<bool> ReachesAsync(
        OctoObjectId candidate,
        OctoObjectId target,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> getRollup,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<OctoObjectId>();
        var pending = new Stack<(OctoObjectId RtId, int Depth)>();
        pending.Push((candidate, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (rtId, depth) = pending.Pop();
            if (rtId == target)
            {
                return true;
            }

            if (depth >= MaxChainDepth || !visited.Add(rtId))
            {
                continue;
            }

            var rollup = await getRollup(rtId).ConfigureAwait(false);
            if (rollup is null)
            {
                continue; // raw / time-range archive or missing — end of this branch
            }

            foreach (var source in rollup.Sources)
            {
                pending.Push((source.SourceArchiveRtId, depth + 1));
            }
        }

        return false;
    }

    /// <summary>
    /// True when the archive exposes <paramref name="sourcePath"/> as one of its columns: for a
    /// rollup, a materialised target column of one of its aggregation specs (physical name); for a
    /// raw / time-range archive, a declared column path.
    /// </summary>
    private static bool Materialises(ArchiveSnapshot snapshot, string sourcePath)
    {
        if (snapshot.RollupAggregations is { } aggregations)
        {
            return ReverseMapThroughRollup(aggregations, sourcePath) is not null;
        }

        foreach (var column in snapshot.Columns)
        {
            if (string.Equals(column.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reverse-maps a physical storage column name on a rollup table to the logical
    /// <see cref="CkRollupAggregationSpec.SourcePath"/> of the spec that materialises it, or null
    /// when no spec of that rollup produces the column.
    /// </summary>
    private static string? ReverseMapThroughRollup(IReadOnlyList<CkRollupAggregationSpec> aggregations, string sourcePath)
    {
        foreach (var parentSpec in aggregations)
        {
            var (_, targets) = RollupAggregationColumns.Resolve(parentSpec);
            foreach (var target in targets)
            {
                if (string.Equals(target.ColumnName, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return parentSpec.SourcePath;
                }
            }
        }

        return null;
    }
}
