using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Builds the <see cref="ArchiveColumnDdl"/> for a computed column from its declared
/// <see cref="CkArchiveColumnSpec.ResultType"/>. Shared by the raw / time-range resolver
/// (<see cref="ArchivePathTypeResolver"/>) and the rollup resolver
/// (<see cref="RollupColumnTypeResolver"/>): a computed column has no source path or aggregation,
/// so its CrateDB type comes from the declared result type, its name from <c>Name</c> (lower-cased
/// via <see cref="ColumnNameMapper"/>), and it is always nullable. Concept §4 / §11.
/// </summary>
internal static class ComputedColumnDdl
{
    public static ArchiveColumnDdl Build(CkArchiveColumnSpec column)
    {
        if (string.IsNullOrWhiteSpace(column.Name))
        {
            throw new UnresolvableArchivePathException(column.Formula ?? string.Empty,
                "computed column requires a Name.");
        }

        if (column.ResultType is null)
        {
            throw new UnresolvableArchivePathException(column.Name,
                "computed column requires a ResultType.");
        }

        var crateType = MapResultType(column.ResultType.Value, column.Name);
        var columnName = ColumnNameMapper.PathToColumnName(column.Name!);
        return new ArchiveColumnDdl(string.Empty, crateType, Required: false, column.Indexed, columnName);
    }

    private static CrateColumnType MapResultType(FormulaResultType resultType, string columnName) => resultType switch
    {
        FormulaResultType.Boolean => new CrateColumnType.Primitive("BOOLEAN"),
        FormulaResultType.Int => new CrateColumnType.Primitive("INTEGER"),
        FormulaResultType.Int64 => new CrateColumnType.Primitive("BIGINT"),
        FormulaResultType.Double => new CrateColumnType.Primitive("DOUBLE PRECISION"),
        FormulaResultType.DateTime => new CrateColumnType.Primitive("TIMESTAMP WITH TIME ZONE"),
        _ => throw new UnresolvableArchivePathException(columnName,
            $"computed column ResultType '{resultType}' has no CrateDB mapping."),
    };
}
