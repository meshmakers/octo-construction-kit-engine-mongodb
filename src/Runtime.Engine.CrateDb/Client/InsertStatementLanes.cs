using System.Runtime.ExceptionServices;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// Runs the statements of one statement shape of an archive write in lanes (AB#8875): every row key belongs to exactly one
/// lane, the lanes run at the same time, and each lane runs its statements one after another.
/// </summary>
/// <remarks>
/// <para>
/// Two statements that run at the same time never carry the same row key, and the rows of one key keep the order they
/// were sent in, so the conflict guard decides every row against the same stored row as in a sequential write. The number
/// of lanes is the limit of statements in flight; a write that fits into one statement has one lane and runs as before.
/// </para>
/// <para>
/// Failure contract of <c>Parallel.ForEachAsync</c>: after the first failed statement no lane starts another one, the
/// statements in flight finish, and the first failure is rethrown unchanged. Which other rows were already written can
/// then differ from a sequential write; all of them are idempotent upserts, so a retry of the same write converges.
/// </para>
/// </remarks>
internal static class InsertStatementLanes
{
    /// <summary>
    /// Splits the rows of one statement shape into lanes of statements.
    /// </summary>
    /// <param name="indexes">Positions of the rows, ascending.</param>
    /// <param name="keyOf">The row key of a position, as CrateDB compares it.</param>
    /// <param name="rowsPerStatement">Most rows one statement carries.</param>
    /// <param name="maxConcurrentStatements">Most statements in flight; not positive means one.</param>
    /// <returns>One list of statements per lane, each statement as ascending row positions.</returns>
    public static IReadOnlyList<IReadOnlyList<int[]>> Plan(IReadOnlyList<int> indexes, Func<int, string> keyOf,
        int rowsPerStatement, int maxConcurrentStatements)
    {
        var statements = (indexes.Count + rowsPerStatement - 1) / rowsPerStatement;
        var laneCount = Math.Max(1, Math.Min(statements, maxConcurrentStatements));
        if (laneCount == 1)
        {
            return [Chunk(indexes, rowsPerStatement)];
        }

        var lanes = new List<int>[laneCount];
        for (var i = 0; i < laneCount; i++)
        {
            lanes[i] = new List<int>(indexes.Count / laneCount + 1);
        }

        foreach (var index in indexes)
        {
            lanes[(int)(StableHash(keyOf(index)) % (uint)laneCount)].Add(index);
        }

        return lanes.Where(l => l.Count > 0).Select(l => Chunk(l, rowsPerStatement)).ToList();
    }

    /// <summary>
    /// Runs the lanes at the same time and the statements of each lane one after another.
    /// </summary>
    /// <param name="lanes">The lanes of <see cref="Plan" />.</param>
    /// <param name="writeStatement">Writes one statement and returns the rows the conflict guard skipped.</param>
    /// <returns>The skipped rows of every statement, ordered by row position.</returns>
    public static async Task<IReadOnlyList<SkippedArchiveRow>> RunAsync(IReadOnlyList<IReadOnlyList<int[]>> lanes,
        Func<int[], Task<IReadOnlyList<SkippedArchiveRow>>> writeStatement)
    {
        var skipped = new List<SkippedArchiveRow>();
        if (lanes.Count == 1)
        {
            foreach (var statement in lanes[0])
            {
                skipped.AddRange(await writeStatement(statement));
            }

            return skipped;
        }

        Exception? firstFailure = null;
        var laneResults = await Task.WhenAll(lanes.Select(async lane =>
        {
            var laneSkipped = new List<SkippedArchiveRow>();
            foreach (var statement in lane)
            {
                if (Volatile.Read(ref firstFailure) is not null)
                {
                    break;
                }

                try
                {
                    laneSkipped.AddRange(await writeStatement(statement));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    break;
                }
            }

            return laneSkipped;
        }));

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        foreach (var laneSkipped in laneResults)
        {
            skipped.AddRange(laneSkipped);
        }

        skipped.Sort((a, b) => a.Index.CompareTo(b.Index));
        return skipped;
    }

    private static IReadOnlyList<int[]> Chunk(IReadOnlyList<int> indexes, int rowsPerStatement)
    {
        var statements = new List<int[]>((indexes.Count + rowsPerStatement - 1) / rowsPerStatement);
        for (var offset = 0; offset < indexes.Count; offset += rowsPerStatement)
        {
            var statement = new int[Math.Min(rowsPerStatement, indexes.Count - offset)];
            for (var i = 0; i < statement.Length; i++)
            {
                statement[i] = indexes[offset + i];
            }

            statements.Add(statement);
        }

        return statements;
    }

    private static uint StableHash(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
