using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Derives CrateDB column types for a rollup archive's <see cref="CkArchiveColumnSpec"/> list. The
/// column names are storage identifiers produced by <see cref="RollupColumnGenerator"/> (e.g.
/// <c>temperature_avg_sum</c>), so the CK-type-path resolver (<see cref="ArchivePathTypeResolver"/>)
/// does not apply — the SQL type is determined by the aggregation function instead.
/// Rollup-archives concept §4.
/// </summary>
/// <remarks>
/// Type rules:
/// <list type="bullet">
/// <item><c>COUNT</c> and the <c>_count</c> half of <c>AVG</c> → <c>BIGINT</c> (CrateDB's
/// <c>count(*)</c> return type).</item>
/// <item><c>SUM</c>, <c>MIN</c>, <c>MAX</c>, and the <c>_sum</c> half of <c>AVG</c> → <c>DOUBLE
/// PRECISION</c>. Rollups operate over numeric source columns; DOUBLE represents both integer and
/// floating-point sources without truncation. Precision-loss for integer MIN/MAX is accepted as
/// an MVP trade-off (the alternative is full source-path resolution, which doesn't compose for
/// chained rollups where the source path is itself a storage identifier).</item>
/// </list>
/// All columns are emitted as <c>Indexed = true</c>, <c>Required = false</c> — matching
/// <see cref="RollupColumnGenerator"/>'s output and the orchestrator's upsert semantics (a missing
/// bucket row is not an error; null aggregation results are normal for empty buckets).
/// </remarks>
internal static class RollupColumnTypeResolver
{
    public static IReadOnlyList<ArchiveColumnDdl> Resolve(
        IReadOnlyList<CkArchiveColumnSpec> columns,
        IReadOnlyList<CkRollupAggregationSpec> aggregations)
    {
        if (columns.Count == 0) return Array.Empty<ArchiveColumnDdl>();

        // Walk the aggregations in the same order RollupColumnGenerator did so we can match each
        // emitted column to its source function. The generator emits column names verbatim into the
        // CkArchiveColumnSpec.Path slot, so name-based lookup is safe and order-independent — but
        // we use position to keep the linkage explicit and detect drift.
        var typeByColumn = new Dictionary<string, CrateColumnType>(StringComparer.Ordinal);
        foreach (var spec in aggregations)
        {
            foreach (var columnName in RollupColumnGenerator.TargetColumnNamesFor(spec))
            {
                typeByColumn[columnName] = TypeFor(spec.Function, columnName);
            }
        }

        var resolved = new List<ArchiveColumnDdl>(columns.Count);
        foreach (var column in columns)
        {
            if (column.IsComputed)
            {
                // Rollup-internal computed columns (concept §11): no aggregation backs them — the
                // type comes from the declared ResultType, the same as raw / time-range computed
                // columns. Always nullable.
                resolved.Add(ComputedColumnDdl.Build(column));
                continue;
            }

            if (!typeByColumn.TryGetValue(column.Path, out var crateType))
            {
                // Defensive: should never happen since both lists come from the same generator
                // and are built from the same aggregations source. Surface as a clear error rather
                // than silently emitting a wrong type.
                throw new UnresolvableArchivePathException(column.Path,
                    "rollup column has no matching aggregation spec — column derivation is out of sync with the aggregation list.");
            }
            resolved.Add(new ArchiveColumnDdl(column.Path, crateType, column.Required, column.Indexed));
        }
        return resolved;
    }

    private static CrateColumnType TypeFor(CkRollupFunction function, string columnName)
    {
        if (function == CkRollupFunction.Count)
        {
            return new CrateColumnType.Primitive("BIGINT");
        }

        if (function == CkRollupFunction.Avg)
        {
            // AVG emits {base}_sum (DOUBLE) and {base}_count (BIGINT). Distinguish by suffix; both
            // names come from RollupColumnGenerator so the suffix check is the canonical fork.
            return columnName.EndsWith("_count", StringComparison.Ordinal)
                ? new CrateColumnType.Primitive("BIGINT")
                : new CrateColumnType.Primitive("DOUBLE PRECISION");
        }

        // SUM, MIN, MAX — all carry numeric values; DOUBLE is the safe envelope.
        return new CrateColumnType.Primitive("DOUBLE PRECISION");
    }
}
