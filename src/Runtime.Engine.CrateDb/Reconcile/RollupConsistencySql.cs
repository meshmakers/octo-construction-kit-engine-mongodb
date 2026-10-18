using System.Globalization;
using System.Text;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// The statements of the rollup validator and repair (AB#6968). The validator uses counts and MIN/MAX only: no row is read.
/// Windows are compared as epoch milliseconds, like every other statement on a rollup's time axis.
/// </summary>
internal static class RollupConsistencySql
{
    /// <summary>1 when the table exists in the schema of the tenant, else 0.</summary>
    public static string BuildTableExists(string tenantId, string tableName) =>
        "SELECT COUNT(*) FROM information_schema.tables " +
        $"WHERE table_schema = '{TenantSchema.SchemaName(tenantId)}' AND table_name = '{tableName.Replace("'", "''", StringComparison.Ordinal)}';";

    /// <summary>The newest end of any window in the rollup table.</summary>
    public static string BuildMaxWindowEnd(string liveTable) =>
        $"SELECT MAX(\"{Constants.WindowEnd}\") AS max_end FROM {liveTable};";

    /// <summary>First window start and last window end of the table.</summary>
    public static string BuildExtent(string liveTable) =>
        $"SELECT MIN(\"{Constants.WindowStart}\") AS lo, MAX(\"{Constants.WindowEnd}\") AS hi FROM {liveTable};";

    /// <summary>Rows, first and last window of every recomputed generation (generation 0 is the forward aggregation).</summary>
    public static string BuildGenerationSummary(string liveTable) =>
        $"SELECT \"{Constants.Generation}\" AS g, COUNT(*) AS c, MIN(\"{Constants.WindowStart}\") AS lo, MAX(\"{Constants.WindowStart}\") AS hi " +
        $"FROM {liveTable} WHERE \"{Constants.Generation}\" > 0 GROUP BY \"{Constants.Generation}\";";

    /// <summary>The condition "this row lies inside the pointer entry" (range and, for a scoped entry, the entity).</summary>
    public static string EntryCondition(GenerationRange entry)
    {
        var sb = new StringBuilder();
        sb.Append('(').Append('"').Append(Constants.WindowStart).Append("\" >= ").Append(Num(entry.StartMs))
            .Append(" AND \"").Append(Constants.WindowStart).Append("\" < ").Append(Num(entry.EndMs));
        if (entry.Scope.Length > 0)
        {
            sb.Append(" AND \"").Append(Constants.RtId).Append("\" = ").Append(Text(entry.Scope));
        }

        return sb.Append(')').ToString();
    }

    /// <summary>
    /// The rows of <paramref name="generation"/> that no pointer entry of that generation or a higher one covers: no reader selects
    /// them, and no sweep will remove them (a sweep only removes rows <em>below</em> a pointed generation).
    /// </summary>
    public static string OrphanPredicate(long generation, IReadOnlyCollection<GenerationRange> coveringEntries)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(Constants.Generation).Append("\" = ").Append(Num(generation));
        if (coveringEntries.Count > 0)
        {
            sb.Append(" AND NOT (").Append(string.Join(" OR ", coveringEntries.Select(EntryCondition))).Append(')');
        }

        return sb.ToString();
    }

    public static string BuildCountOrphans(string liveTable, long generation, IReadOnlyCollection<GenerationRange> coveringEntries) =>
        $"SELECT COUNT(*) FROM {liveTable} WHERE {OrphanPredicate(generation, coveringEntries)};";

    public static string BuildDeleteOrphans(string liveTable, long generation, IReadOnlyCollection<GenerationRange> coveringEntries) =>
        $"DELETE FROM {liveTable} WHERE {OrphanPredicate(generation, coveringEntries)};";

    /// <summary>Rows of a source archive in <c>[fromMs, toMs)</c> on its time column.</summary>
    public static string BuildCountSourceRows(string sourceTable, string timeColumn, long fromMs, long toMs) =>
        $"SELECT COUNT(*) FROM {sourceTable} WHERE \"{timeColumn}\" >= {Num(fromMs)} AND \"{timeColumn}\" < {Num(toMs)};";

    /// <summary>The first row of a source archive on its time column.</summary>
    public static string BuildMinSourceTime(string sourceTable, string timeColumn) =>
        $"SELECT MIN(\"{timeColumn}\") AS min_time FROM {sourceTable};";

    /// <summary>The part of <paramref name="entry"/> that is not outranked by an entry of a higher generation.</summary>
    /// <remarks>
    /// An entry of a higher generation outranks the entry where their ranges overlap and the higher entry applies to the same
    /// entities (unscoped, or the same scope). An unscoped entry is not outranked by a scoped one in the rows of other entities.
    /// </remarks>
    public static IReadOnlyList<(long FromMs, long ToMs)> GovernedRanges(GenerationRange entry, IReadOnlyList<GenerationRange> all)
    {
        var blockers = all
            .Where(o => o.Generation > entry.Generation
                        && o.StartMs < entry.EndMs && o.EndMs > entry.StartMs
                        && (o.Scope.Length == 0 || o.Scope == entry.Scope))
            .Select(o => (From: Math.Max(o.StartMs, entry.StartMs), To: Math.Min(o.EndMs, entry.EndMs)))
            .OrderBy(b => b.From)
            .ToList();

        var result = new List<(long, long)>();
        var cursor = entry.StartMs;
        foreach (var (from, to) in blockers)
        {
            if (from > cursor)
            {
                result.Add((cursor, from));
            }

            cursor = Math.Max(cursor, to);
        }

        if (cursor < entry.EndMs)
        {
            result.Add((cursor, entry.EndMs));
        }

        return result;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
