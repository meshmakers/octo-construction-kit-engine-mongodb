using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Renders the per-window active-generation predicate of a rollup archive (AB#4184, Phase 6):
/// <c>"generation" = CASE WHEN &lt;range&gt; THEN &lt;gen&gt; … ELSE 0 END</c>, or the baseline
/// <c>"generation" = 0</c> when no pointer entry applies. One renderer for every reader of a rollup
/// table — the query path (<see cref="CrateQueryCompiler"/>) and the aggregation that reads a rollup
/// as the source of the next level (<see cref="RollupAggregationSqlBuilder"/>) — so both select the
/// same rows.
/// </summary>
/// <remarks>
/// The predicate is never omitted for a rollup table, not even with an empty range list: a recompute
/// writes the next generation's rows into the live table before it flips the pointer, and without
/// the baseline those uncommitted rows would be read next to the committed ones. Columns are
/// referenced unqualified; ranges are ordered newest generation first so an overlapping
/// re-recompute wins.
/// </remarks>
internal static class GenerationFilterSql
{
    /// <summary>Renders the predicate for the given pointer entries (without a leading AND).</summary>
    public static string Render(IEnumerable<GenerationRange> ranges)
    {
        var ordered = ranges.OrderByDescending(r => r.Generation).ToList();
        if (ordered.Count == 0)
        {
            return $"\"{Constants.Generation}\" = 0";
        }

        var sb = new StringBuilder();
        sb.Append('"').Append(Constants.Generation).Append("\" = CASE");
        foreach (var range in ordered)
        {
            sb.Append(" WHEN (\"").Append(Constants.WindowStart).Append("\" >= ").Append(range.StartMs)
              .Append(" AND \"").Append(Constants.WindowStart).Append("\" < ").Append(range.EndMs).Append(')');
            if (!string.IsNullOrEmpty(range.Scope))
            {
                sb.Append(" AND \"").Append(Constants.RtId).Append("\" = '").Append(range.Scope.Replace("'", "''")).Append('\'');
            }
            sb.Append(" THEN ").Append(range.Generation);
        }
        sb.Append(" ELSE 0 END");
        return sb.ToString();
    }

    /// <summary>
    /// The pointer entries that can decide a row of the half-open bucket
    /// <c>[bucketStart, bucketEnd)</c>: those whose range overlaps it. Entries outside the bucket
    /// cannot match any of its windows, so leaving them out keeps the statement small however many
    /// entries the generation map holds, without changing which rows are selected.
    /// </summary>
    public static IReadOnlyList<GenerationRange> Overlapping(
        IEnumerable<GenerationRange> ranges, DateTime bucketStart, DateTime bucketEnd)
    {
        var startMs = ToEpochMs(bucketStart);
        var endMs = ToEpochMs(bucketEnd);
        return ranges.Where(r => r.StartMs < endMs && r.EndMs > startMs).ToList();
    }

    private static long ToEpochMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }
}
