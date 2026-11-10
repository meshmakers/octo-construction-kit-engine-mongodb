using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// AB#8622 (decided by Reimar 2026-11-09): a ranking column cannot be null. A point written to a raw or time-range
/// archive with a <c>ConflictPrecedence</c> that sends a ranking column as an explicit <c>null</c> is refused.
/// </summary>
/// <remarks>
/// <para>
/// Ranking columns are the column keys of the precedence; <c>$Origin</c> is not a column and is derived from the
/// point's origin. They follow the partial-delivery rule with one exception: a ranking column the point does not send
/// keeps its stored value, also for the comparison (the guard judges the point against the merged row, so the next key
/// decides), and a ranking column it sends with a value is compared and written as usual. An explicit null would
/// otherwise be a rank that never beats a stored value (AB#5247) and, when an earlier key decides, a NULL written into
/// a ranking column; neither is a meaningful statement about a value, so it is refused instead.
/// </para>
/// <para>
/// Keys are matched like the insert path maps them to the storage column (<see cref="ColumnNameMapper" />). Checked for
/// automated and manual points alike, before anything is read or written; one such point refuses the whole batch with an
/// <see cref="ArgumentException" /> (asset repository: 400) naming every such point and its null ranking columns. Points
/// of another CK type are skipped by the repository and not checked. The import path does not apply the precedence and
/// is not checked.
/// </para>
/// </remarks>
internal static class ArchiveRankingColumnValidator
{
    /// <summary>At most this many points are listed in the message; the count names the rest.</summary>
    private const int MaxReportedPoints = 10;

    /// <summary>
    /// Raw-archive points; a point is named by its index in the caller's order, its rtId and its timestamp. The attributes
    /// are flattened (<see cref="ArchiveRecordFlattening" />, AB#8666), so a ranking column on a record member is checked.
    /// </summary>
    public static void Validate(ArchiveSnapshot snapshot, IReadOnlyList<StreamDataPoint> points)
        => ValidateCore(snapshot, points, p => p.CkTypeId, p => p.Attributes,
            static (index, p) =>
                $"point {index} (rtId '{p.RtId}', timestamp {p.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)})",
            nameof(points));

    /// <summary>
    /// Time-range points. Like the raw points they arrive flattened (<see cref="ArchiveRecordFlattening" />, AB#8666), so a
    /// null ranking column inside a record is found as well.
    /// </summary>
    public static void Validate(ArchiveSnapshot snapshot, IReadOnlyList<TimeRangeStreamDataPoint> points)
        => ValidateCore(snapshot, points, p => p.CkTypeId, p => p.Attributes,
            static (index, p) =>
                $"point {index} (rtId '{p.RtId}', from {p.From.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)})",
            nameof(points));

    /// <summary>The ranking columns of the archive as authored (CK attribute paths), without <c>$Origin</c>.</summary>
    internal static IReadOnlyList<string> RankingColumns(ArchiveSnapshot snapshot)
        => snapshot.RollupAggregations is not null
            ? []
            : snapshot.ConflictPrecedence.Where(k => !k.IsOrigin).Select(k => k.Column).ToList();

    private static void ValidateCore<TPoint>(ArchiveSnapshot snapshot, IReadOnlyList<TPoint> points,
        Func<TPoint, RtCkId<CkTypeId>> ckTypeIdOf,
        Func<TPoint, IReadOnlyDictionary<string, object?>> attributesOf,
        Func<int, TPoint, string> describePoint,
        string parameterName)
    {
        var ranking = RankingColumns(snapshot);
        if (ranking.Count == 0)
        {
            return;
        }

        var rankingByStorage = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in ranking)
        {
            rankingByStorage.TryAdd(ColumnNameMapper.PathToColumnName(column), column);
        }

        var refused = new List<string>();
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (ckTypeIdOf(point) != snapshot.TargetCkTypeId)
            {
                continue;
            }

            var nulls = new List<string>();
            foreach (var (name, value) in attributesOf(point))
            {
                if (value is null or DBNull
                    && rankingByStorage.TryGetValue(ColumnNameMapper.PathToColumnName(name), out var column)
                    && !nulls.Contains($"'{column}'"))
                {
                    nulls.Add($"'{column}'");
                }
            }

            if (nulls.Count > 0)
            {
                refused.Add($"{describePoint(index, point)} sends null for {string.Join(", ", nulls)}");
            }
        }

        if (refused.Count == 0)
        {
            return;
        }

        var listed = string.Join("; ", refused.Take(MaxReportedPoints));
        var more = refused.Count > MaxReportedPoints ? $" (and {refused.Count - MaxReportedPoints} more)" : string.Empty;
        throw new ArgumentException(
            $"Archive '{snapshot.RtId}' ranks conflicting writes by {string.Join(", ", ranking.Select(c => $"'{c}'"))}: " +
            "a ranking column cannot be null; send a value, or leave the column out to keep the stored value. " +
            $"{refused.Count} point(s) send null, nothing was written: {listed}{more}.",
            parameterName);
    }
}
