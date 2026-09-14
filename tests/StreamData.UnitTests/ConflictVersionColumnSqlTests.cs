using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Opt-in conflict resolution (System.StreamData 1.10.0 — Archive.ConflictVersionColumn).
//
// Every archive write is an upsert on the row key, and until this feature the DO UPDATE was
// unconditional: whichever delivery arrived LAST won, regardless of which document was newer. A
// re-delivered or out-of-order data point therefore replaced newer stored values with older ones,
// silently — measured on a real EDA replay, 16 of 565 days kept a superseded value.
//
// These tests pin the generated SQL rather than the behaviour against a live CrateDB, because the
// SQL is where the semantics live and a builder regression is invisible until data is already
// wrong. The four-way null/order semantics were verified against CrateDB 5.10.10 by hand before
// this shape was chosen; the predicate pinned below is that verified shape.
public class ConflictVersionColumnSqlTests
{
    private static readonly string[] Columns = ["amountvalue", "dataquality", "sourcedate"];

    [Fact]
    public void WithoutAVersionColumn_TheTimeRangeUpdateStaysUnconditional()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns);

        // The pre-1.10.0 shape, byte for byte: no archive changes behaviour until it opts in.
        Assert.Contains("\"amountvalue\" = EXCLUDED.\"amountvalue\"", sql);
        Assert.DoesNotContain("CASE WHEN", sql);
    }

    [Fact]
    public void WithoutAVersionColumn_TheRawUpdateStaysUnconditional()
    {
        var sql = CrateDatabaseClient.BuildSingleRowInsertSql("\"t\".\"archive_a\"", Columns);

        Assert.Contains("\"amountvalue\" = EXCLUDED.\"amountvalue\"", sql);
        Assert.DoesNotContain("CASE WHEN", sql);
    }

    [Fact]
    public void TheGuardIsNullWhenNoVersionColumnIsConfigured()
    {
        Assert.Null(CrateDatabaseClient.BuildConflictGuard(null, Columns));
        Assert.Null(CrateDatabaseClient.BuildConflictGuard("   ", Columns));
    }

    [Fact]
    public void TheGuardTreatsAStoredNullAsReplaceableAndAnIncomingNullAsLosing()
    {
        var guard = CrateDatabaseClient.BuildConflictGuard("sourcedate", Columns);

        // Read the predicate as three decisions:
        //   stored IS NULL                  -> replace (rows predating the opt-in carry no version,
        //                                      and refusing to ever update them would freeze history)
        //   incoming IS NULL                -> keep    (a point that cannot prove it is newer loses)
        //   otherwise incoming >= stored    -> replace (>= so re-delivering the same document is
        //                                      idempotent rather than a silent no-op)
        Assert.Equal(
            "(\"sourcedate\" IS NULL OR (EXCLUDED.\"sourcedate\" IS NOT NULL AND EXCLUDED.\"sourcedate\" >= \"sourcedate\"))",
            guard);
    }

    [Fact]
    public void AVersionColumnGuardsEveryUserColumnOfTheTimeRangeUpdate()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictVersionColumn: "sourcedate");

        foreach (var column in Columns)
        {
            Assert.Contains(
                $"\"{column}\" = CASE WHEN (\"sourcedate\" IS NULL OR (EXCLUDED.\"sourcedate\" IS NOT NULL " +
                $"AND EXCLUDED.\"sourcedate\" >= \"sourcedate\")) THEN EXCLUDED.\"{column}\" ELSE \"{column}\" END",
                sql);
        }
    }

    [Fact]
    public void ALosingWriteLeavesNoTraceOnTheBookkeepingColumns()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictVersionColumn: "sourcedate");

        // was_updated is the archive's "ever updated" signal and rtchangeddatetime feeds the
        // retroactive-write detection. If a write that changed nothing still flipped them, both
        // would report activity that never happened.
        Assert.Contains("THEN CURRENT_TIMESTAMP ELSE \"rtchangeddatetime\" END", sql);
        Assert.Contains("THEN TRUE ELSE \"was_updated\" END", sql);
    }

    [Fact]
    public void AVersionColumnAlsoGuardsTheRawArchiveUpdate()
    {
        // Raw archives carry the same unconditional upsert and therefore the same defect; the
        // opt-in lives on the Archive base type so both storage shapes can use it.
        var sql = CrateDatabaseClient.BuildSingleRowInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictVersionColumn: "sourcedate");

        Assert.Contains("\"amountvalue\" = CASE WHEN", sql);
        Assert.Contains("THEN CURRENT_TIMESTAMP ELSE \"rtchangeddatetime\" END", sql);
    }

    [Fact]
    public void TheVersionColumnIsGuardedAgainstItself()
    {
        // The version column is an ordinary user column, so it is updated under the same guard —
        // otherwise a losing write would still advance the stored version and permanently lock the
        // row against the value that should have won.
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictVersionColumn: "sourcedate");

        Assert.Contains("\"sourcedate\" = CASE WHEN", sql);
    }

    [Fact]
    public void AVersionColumnThatIsNotAnArchiveColumnIsRefused()
    {
        // The guard reads EXCLUDED."v", so a name outside the INSERT column list would produce SQL
        // that fails on every write. Refusing to build it turns a misconfiguration into one clear
        // error instead of a storm of storage-layer failures.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CrateDatabaseClient.BuildTimeRangeInsertSql(
                "\"t\".\"archive_a\"", Columns, conflictVersionColumn: "documentdate"));

        Assert.Contains("documentdate", ex.Message);
        Assert.Contains("amountvalue", ex.Message);
    }

    [Fact]
    public void GenerationTrackedTablesKeepTheirConflictTargetWhenGuarded()
    {
        // Rollup tables key their PK with `generation` (AB#4773) and CrateDB rejects a conflict
        // target that does not name the full PK. The guard must not disturb that.
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, generationTracked: true, conflictVersionColumn: "sourcedate");

        Assert.Contains("\"generation\")", sql);
        Assert.Contains("CASE WHEN", sql);
    }

    [Fact]
    public void AnArchiveWithNoUserColumnsProducesNoUpdateList()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", []);

        Assert.Contains("DO UPDATE SET", sql);
        Assert.DoesNotContain("EXCLUDED.", sql);
    }
}
