using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// AB#8622 (decided by Odin 2026-11-09, option C): a point written to a raw or time-range archive with a
/// <c>ConflictPrecedence</c> must send every ranking column of that list. Whoever claims precedence states their rank.
/// </summary>
/// <remarks>
/// <para>
/// Why: a partial point keeps the columns it does not send, but the guard compares the sent value of every ranking column
/// (<c>EXCLUDED."k"</c>). An unsent ranking column was bound NULL and kept its stored value, so a point could win on an
/// earlier key while the write result compared the NULL it sent against the value that was kept: an automated point
/// was reported as skipped and a manual point failed the write (REST 409) although the row was written. Judging the
/// unsent keys on the merged row instead would let an automated point without a quality overwrite the value of a stored
/// L1 measurement while the row still says L1.
/// </para>
/// <para>
/// "Sends" means the key is in the point's attributes, with any value: an explicit <c>null</c> is a stated rank (it never
/// beats a stored value, AB#5247). <c>$Origin</c> is not a column and is derived from the point's origin. Keys are
/// matched like the insert path maps them to the storage column (<see cref="ColumnNameMapper" />). Checked for automated
/// and manual points alike, before anything is read or written; one point without its ranking columns refuses the whole
/// batch with an <see cref="ArgumentException" /> (asset repository: 400) naming every such point and its missing
/// columns. Points of another CK type are skipped by the repository and not checked. The import path does not apply the
/// precedence and is not checked.
/// </para>
/// </remarks>
internal static class ArchiveRankingColumnValidator
{
    /// <summary>At most this many points are listed in the message; the count names the rest.</summary>
    private const int MaxReportedPoints = 10;

    /// <summary>Raw-archive points; a point is named by its index in the caller's order, its rtId and its timestamp.</summary>
    public static void Validate(ArchiveSnapshot snapshot, IReadOnlyList<StreamDataPoint> points)
        => ValidateCore(snapshot, points, p => p.CkTypeId, p => p.Attributes,
            static (index, p) =>
                $"point {index} (rtId '{p.RtId}', timestamp {p.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)})",
            nameof(points));

    /// <summary>
    /// Time-range points; <paramref name="flatten" /> expands record values into dotted column paths, as the insert path
    /// does, so a ranking column inside a record counts as sent when the record carries it.
    /// </summary>
    public static void Validate(ArchiveSnapshot snapshot, IReadOnlyList<TimeRangeStreamDataPoint> points,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> flatten)
        => ValidateCore(snapshot, points, p => p.CkTypeId, p => flatten(p.Attributes),
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

        var rankingStorage = ranking.Select(c => (Path: c, Storage: ColumnNameMapper.PathToColumnName(c))).ToList();
        var refused = new List<string>();
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (ckTypeIdOf(point) != snapshot.TargetCkTypeId)
            {
                continue;
            }

            var sent = new HashSet<string>(attributesOf(point).Keys.Select(ColumnNameMapper.PathToColumnName),
                StringComparer.Ordinal);
            var missing = rankingStorage.Where(c => !sent.Contains(c.Storage)).Select(c => $"'{c.Path}'").ToList();
            if (missing.Count > 0)
            {
                refused.Add($"{describePoint(index, point)} does not send {string.Join(", ", missing)}");
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
            $"every point must send each of these columns (null is allowed). {refused.Count} point(s) do not, nothing was " +
            $"written: {listed}{more}.",
            parameterName);
    }
}
