using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// Rows of one insert batch that name the same user columns (AB#8622), with the columns their statement inserts
/// and the columns its <c>ON CONFLICT DO UPDATE</c> replaces.
/// </summary>
/// <param name="InsertColumns">
/// The user columns of the <c>INSERT</c> list, in archive order: the named columns plus every ranking column of the
/// conflict precedence (bound as NULL when the row does not name it), because the guard reads <c>EXCLUDED."k"</c>.
/// The repository refuses such points before the write (<c>ArchiveRankingColumnValidator</c>, AB#8622 option C); the
/// binding stays so that a direct caller of the client still produces valid SQL.
/// </param>
/// <param name="UpdatedColumns">The user columns a winning write replaces: exactly the named columns.</param>
/// <param name="Indexes">Positions of the rows in the batch, ascending.</param>
internal sealed record ArchiveColumnSetGroup(
    IReadOnlyList<string> InsertColumns, IReadOnlyList<string> UpdatedColumns, IReadOnlyList<int> Indexes);

/// <summary>
/// Splits an insert batch by the set of user columns each row names (AB#8622).
/// </summary>
/// <remarks>
/// <para>
/// The rule: a column the row does not name keeps its stored value; a column named with <c>null</c> is set to
/// NULL; a new row stores NULL for every column it does not name. "Named" means the attribute dictionary of the
/// row has the key, whatever the value. One statement can only update the columns of its <c>DO UPDATE SET</c>
/// list, so rows that name different columns go into different statements.
/// </para>
/// <para>
/// The order of the batch is kept for every row key: the groups of a segment are disjoint in their row keys, and a
/// row whose key already sits in another group of the current segment starts a new segment. Groups are emitted
/// segment by segment, so two writes of the same key always run in the order they were sent. A batch whose rows all
/// name the same columns (the usual pipeline delivery) is one group and costs no key lookups.
/// </para>
/// </remarks>
internal static class ArchiveColumnSets
{
    /// <summary>
    /// Groups the <paramref name="count" /> rows of a batch.
    /// </summary>
    /// <param name="count">Rows in the batch.</param>
    /// <param name="attributesOf">The attribute dictionary of row <c>i</c> (physical column names as keys).</param>
    /// <param name="keyOf">The row key of row <c>i</c> (the conflict target); only read for mixed batches.</param>
    /// <param name="userColumnNames">The user columns of the archive, in archive order.</param>
    /// <param name="conflictPrecedence">The archive's conflict precedence; its column keys are always inserted.</param>
    public static IReadOnlyList<ArchiveColumnSetGroup> Group(
        int count,
        Func<int, IReadOnlyDictionary<string, object?>?> attributesOf,
        Func<int, object> keyOf,
        IReadOnlyList<string> userColumnNames,
        IReadOnlyList<ArchiveConflictKey>? conflictPrecedence)
    {
        if (count == 0)
        {
            return [];
        }

        var rankingColumns = conflictPrecedence is { Count: > 0 }
            ? conflictPrecedence.Where(k => !k.IsOrigin).Select(k => k.Column).ToHashSet(StringComparer.Ordinal)
            : null;

        var masks = new string[count];
        var uniform = true;
        for (var i = 0; i < count; i++)
        {
            masks[i] = Mask(attributesOf(i), userColumnNames);
            uniform &= string.Equals(masks[i], masks[0], StringComparison.Ordinal);
        }

        if (uniform)
        {
            return [CreateGroup(masks[0], Enumerable.Range(0, count).ToList(), userColumnNames, rankingColumns)];
        }

        var result = new List<ArchiveColumnSetGroup>();
        var segment = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var segmentOrder = new List<string>();
        var maskByKey = new Dictionary<object, string>();
        for (var i = 0; i < count; i++)
        {
            var key = keyOf(i);
            if (maskByKey.TryGetValue(key, out var seenMask) && !string.Equals(seenMask, masks[i], StringComparison.Ordinal))
            {
                Flush();
            }

            maskByKey[key] = masks[i];
            if (!segment.TryGetValue(masks[i], out var indexes))
            {
                indexes = [];
                segment[masks[i]] = indexes;
                segmentOrder.Add(masks[i]);
            }

            indexes.Add(i);
        }

        Flush();
        return result;

        void Flush()
        {
            foreach (var mask in segmentOrder)
            {
                result.Add(CreateGroup(mask, segment[mask], userColumnNames, rankingColumns));
            }

            segment.Clear();
            segmentOrder.Clear();
            maskByKey.Clear();
        }
    }

    /// <summary>One character per user column: <c>1</c> when the row names it, <c>0</c> when it does not.</summary>
    private static string Mask(IReadOnlyDictionary<string, object?>? attributes, IReadOnlyList<string> userColumnNames)
    {
        return string.Create(userColumnNames.Count, (attributes, userColumnNames), static (span, state) =>
        {
            for (var c = 0; c < span.Length; c++)
            {
                span[c] = state.attributes != null && state.attributes.ContainsKey(state.userColumnNames[c]) ? '1' : '0';
            }
        });
    }

    private static ArchiveColumnSetGroup CreateGroup(string mask, IReadOnlyList<int> indexes,
        IReadOnlyList<string> userColumnNames, HashSet<string>? rankingColumns)
    {
        var insert = new List<string>(userColumnNames.Count);
        var updated = new List<string>(userColumnNames.Count);
        for (var c = 0; c < userColumnNames.Count; c++)
        {
            var named = mask[c] == '1';
            if (named)
            {
                updated.Add(userColumnNames[c]);
            }

            if (named || rankingColumns?.Contains(userColumnNames[c]) == true)
            {
                insert.Add(userColumnNames[c]);
            }
        }

        return new ArchiveColumnSetGroup(insert, updated, indexes);
    }
}
