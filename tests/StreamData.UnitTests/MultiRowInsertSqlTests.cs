using System.Text.RegularExpressions;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Archive writes go out as ONE multi-row `VALUES (…), (…)` statement per sub-batch instead of an
// NpgsqlBatch of single-row commands. Every value is still its own typed bind parameter (the AB#4773
// requirement), but CrateDB analyses the statement once instead of once per row. With the
// ConflictPrecedence guard every statement is several KB, and the per-row analysis made CrateDB the
// bottleneck of the EDA replay: 10,000 rows took 7.4 s as single-row commands, 0.8 s as multi-row
// statements.
//
// The parameter helpers bind positionally in column order, so what these tests pin is the contract
// between the rendered placeholders and that order.
public class MultiRowInsertSqlTests
{
    private static readonly string[] Columns = ["amountvalue", "dataquality", "sourcedocumentdate"];

    private static readonly ArchiveConflictKey[] QualityThenDate =
    [
        new("dataquality", ConflictKeyOrder.LowerWins),
        new("sourcedocumentdate", ConflictKeyOrder.HigherWins)
    ];

    [Fact]
    public void PlaceholdersAreNumberedContinuouslyAcrossRows()
    {
        Assert.Equal("($1, $2, $3), ($4, $5, $6)", CrateDatabaseClient.BuildValuesList(3, 2));
    }

    [Fact]
    public void ARowSuffixLandsInsideEveryRow()
    {
        // The rollup generation is a literal, not a parameter: it must not shift the numbering and it
        // must be written for every row, not only the first.
        Assert.Equal("($1, $2, 0), ($3, $4, 0)", CrateDatabaseClient.BuildValuesList(2, 2, ", 0"));
    }

    [Fact]
    public void ATimeRangeStatementCarriesOneTuplePerRow()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns, rowCount: 3);

        // 5 standard columns + 3 user columns = 8 parameters per row.
        Assert.Contains("VALUES ($1, $2, $3, $4, $5, $6, $7, $8), ($9,", sql);
        Assert.Contains("$24) ON CONFLICT", sql);
        Assert.DoesNotContain("$25", sql);
    }

    [Fact]
    public void ARawStatementCarriesOneTuplePerRow()
    {
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", Columns, rowCount: 2);

        // 4 standard columns + 3 user columns = 7 parameters per row.
        Assert.Contains("VALUES ($1, $2, $3, $4, $5, $6, $7), ($8, $9, $10, $11, $12, $13, $14) ON CONFLICT", sql);
    }

    [Fact]
    public void GenerationTrackedRowsEachWriteGenerationZero()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
            generationTracked: true, rowCount: 3);

        Assert.Equal(3, Regex.Matches(sql, @", 0\)").Count);
        Assert.Contains("\"generation\")", sql);
    }

    [Fact]
    public void TheGuardIsRenderedOncePerStatementNotOncePerRow()
    {
        // The point of the change: the expensive part of the statement does not grow with the rows.
        var one = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
            conflictPrecedence: QualityThenDate);
        var many = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
            conflictPrecedence: QualityThenDate, rowCount: 1000);

        var guardCount = Regex.Matches(one, "CASE WHEN").Count;
        Assert.Equal(guardCount, Regex.Matches(many, "CASE WHEN").Count);
    }

    [Theory]
    [InlineData(8, 1000)]
    [InlineData(65, 1000)]
    [InlineData(66, 992)]
    [InlineData(200, 327)]
    [InlineData(70000, 1)]
    public void RowsPerStatementStaysWithinTheProtocolParameterLimit(int parametersPerRow, int expected)
    {
        var rows = CrateDatabaseClient.RowsPerStatement(parametersPerRow);

        Assert.Equal(expected, rows);
        Assert.True(rows == 1 || rows * parametersPerRow <= 65535);
    }
}
