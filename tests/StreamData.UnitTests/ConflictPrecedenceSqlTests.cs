using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Opt-in conflict resolution (System.StreamData 1.13.0 — Archive.ConflictPrecedence).
//
// Every archive write is an upsert on the row key, and until this feature the DO UPDATE was
// unconditional: whichever delivery arrived LAST won, regardless of the data. A re-delivered or
// out-of-order data point therefore replaced newer stored values with older ones, silently.
//
// The keys are compared LEXICOGRAPHICALLY, and that is the whole point rather than a detail. It is a
// total order over the data, so the surviving value is its maximum — the same value whichever write
// lands first. Read as a conjunction ("better rank AND newer") the result would still depend on
// arrival order, which is the defect the feature exists to remove; the older-but-better case below
// is what separates the two readings.
//
// The predicate pinned here was verified by hand against CrateDB 5.10.10 on a populated table before
// this shape was chosen, including the four ordering cases the energy-metering rule turns on.
public class ConflictPrecedenceSqlTests
{
    private static readonly string[] Columns = ["amountvalue", "dataquality", "sourcedocumentdate"];

    /// <summary>The energy-metering rule: a measurement is never displaced by a later estimate.</summary>
    private static readonly ArchiveConflictKey[] QualityThenDate =
    [
        new("dataquality", ConflictKeyOrder.LowerWins),        // 1 = L1 measured beats 3 = L3 estimated
        new("sourcedocumentdate", ConflictKeyOrder.HigherWins) // equal quality: the newer document
    ];

    private static readonly ArchiveConflictKey[] DateOnly =
    [
        new("sourcedocumentdate", ConflictKeyOrder.HigherWins)
    ];

    [Fact]
    public void WithoutPrecedenceTheTimeRangeUpdateStaysUnconditional()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns);

        // The pre-1.13.0 shape, byte for byte: no archive changes behaviour until it opts in.
        Assert.Contains("\"amountvalue\" = EXCLUDED.\"amountvalue\"", sql);
        Assert.DoesNotContain("CASE WHEN", sql);
    }

    [Fact]
    public void WithoutPrecedenceTheRawUpdateStaysUnconditional()
    {
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", Columns);

        Assert.Contains("\"amountvalue\" = EXCLUDED.\"amountvalue\"", sql);
        Assert.DoesNotContain("CASE WHEN", sql);
    }

    [Fact]
    public void AnEmptyPrecedenceIsNoGuard()
    {
        Assert.Null(CrateDatabaseClient.BuildConflictGuard([], Columns));
    }

    [Fact]
    public void ASingleKeyOrdersByThatColumnAndTreatsNullsAsymmetrically()
    {
        var guard = CrateDatabaseClient.BuildConflictGuard(DateOnly, Columns);

        // Read the predicate as three decisions:
        //   stored IS NULL                  -> replace (rows predating the opt-in carry no keys, and
        //                                      refusing to ever update them would freeze history)
        //   incoming IS NULL                -> keep    (a point that cannot prove it is better loses)
        //   otherwise compare               -> the better value wins, equality accepted so that
        //                                      re-delivering the identical document is idempotent
        Assert.Equal(
            "(((\"sourcedocumentdate\" IS NULL AND EXCLUDED.\"sourcedocumentdate\" IS NOT NULL) "
            + "OR (\"sourcedocumentdate\" IS NOT NULL AND EXCLUDED.\"sourcedocumentdate\" IS NOT NULL "
            + "AND EXCLUDED.\"sourcedocumentdate\" > \"sourcedocumentdate\")) "
            + "OR ((\"sourcedocumentdate\" IS NULL AND EXCLUDED.\"sourcedocumentdate\" IS NULL) "
            + "OR (\"sourcedocumentdate\" IS NOT NULL AND EXCLUDED.\"sourcedocumentdate\" IS NOT NULL "
            + "AND EXCLUDED.\"sourcedocumentdate\" = \"sourcedocumentdate\")))",
            guard);
    }

    [Fact]
    public void LowerWinsFlipsTheComparisonForRankLikeCodes()
    {
        // Quality codes are numbered best-first (1 = measured, 3 = estimated), so "better" is smaller.
        // Getting this direction wrong is not a cosmetic error: it makes every estimate outrank every
        // measurement, which is precisely the bug the rule exists to prevent.
        var guard = CrateDatabaseClient.BuildConflictGuard(QualityThenDate, Columns)!;

        Assert.Contains("EXCLUDED.\"dataquality\" < \"dataquality\"", guard);
        Assert.DoesNotContain("EXCLUDED.\"dataquality\" > \"dataquality\"", guard);
    }

    [Fact]
    public void TheSecondKeyOnlyAppliesWhenTheFirstIsATie()
    {
        // Lexicographic, not conjunctive: the date comparison sits INSIDE the quality-equal branch.
        // If the two were ANDed at the top level, an older measurement would lose to a newer estimate
        // that happened to arrive first and the outcome would stay order-dependent.
        var guard = CrateDatabaseClient.BuildConflictGuard(QualityThenDate, Columns)!;

        var qualityEqual = guard.IndexOf("EXCLUDED.\"dataquality\" = \"dataquality\"", StringComparison.Ordinal);
        var dateCompared = guard.IndexOf("EXCLUDED.\"sourcedocumentdate\" >", StringComparison.Ordinal);

        Assert.True(qualityEqual >= 0 && dateCompared > qualityEqual,
            "the date key must be nested behind the quality tie, not ANDed with it");
        Assert.Contains(" AND (((\"sourcedocumentdate\"", guard);
    }

    [Fact]
    public void EveryUserColumnIsGuardedIncludingTheKeysThemselves()
    {
        // A key is an ordinary user column, so it is updated under the same guard — otherwise a losing
        // write would still advance the stored key and permanently lock the row against the value that
        // should have won.
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictPrecedence: QualityThenDate);

        foreach (var column in Columns)
        {
            Assert.Contains($"\"{column}\" = CASE WHEN ", sql);
        }
    }

    [Fact]
    public void ALosingWriteLeavesNoTraceOnTheBookkeepingColumns()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictPrecedence: QualityThenDate);

        // was_updated is the archive's "ever updated" signal and rtchangeddatetime feeds the
        // retroactive-write detection. If a write that changed nothing still flipped them, both would
        // report activity that never happened.
        Assert.Contains("THEN CURRENT_TIMESTAMP ELSE \"rtchangeddatetime\" END", sql);
        Assert.Contains("THEN TRUE ELSE \"was_updated\" END", sql);
    }

    [Fact]
    public void PrecedenceAlsoGuardsTheRawArchiveUpdate()
    {
        // Raw archives carry the same unconditional upsert and therefore the same defect; the opt-in
        // lives on the Archive base type so both storage shapes can use it.
        var sql = CrateDatabaseClient.BuildRawInsertSql(
            "\"t\".\"archive_a\"", Columns, conflictPrecedence: QualityThenDate);

        Assert.Contains("\"amountvalue\" = CASE WHEN", sql);
        Assert.Contains("THEN CURRENT_TIMESTAMP ELSE \"rtchangeddatetime\" END", sql);
    }

    [Fact]
    public void AKeyThatIsNotAnArchiveColumnIsRefused()
    {
        // The guard reads EXCLUDED."k", so a name outside the INSERT column list would produce SQL
        // that cannot execute. Refusing to build it turns a misconfiguration into one clear error
        // instead of a storm of storage-layer failures.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
                conflictPrecedence: [new ArchiveConflictKey("documentdate", ConflictKeyOrder.HigherWins)]));

        Assert.Contains("documentdate", ex.Message);
        Assert.Contains("amountvalue", ex.Message);
    }

    [Fact]
    public void GenerationTrackedTablesKeepTheirConflictTargetWhenGuarded()
    {
        // Rollup tables key their PK with `generation` (AB#4773) and CrateDB rejects a conflict target
        // that does not name the full PK. The guard must not disturb that.
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
            generationTracked: true, conflictPrecedence: QualityThenDate);

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

    [Fact]
    public void ThreeKeysNestOneInsideTheOther()
    {
        // Nothing caps the key count, and each further key must narrow the tie of all before it.
        var guard = CrateDatabaseClient.BuildConflictGuard(
        [
            new("dataquality", ConflictKeyOrder.LowerWins),
            new("sourcedocumentdate", ConflictKeyOrder.HigherWins),
            new("amountvalue", ConflictKeyOrder.HigherWins)
        ], Columns)!;

        var q = guard.IndexOf("EXCLUDED.\"dataquality\" = ", StringComparison.Ordinal);
        var d = guard.IndexOf("EXCLUDED.\"sourcedocumentdate\" = ", StringComparison.Ordinal);
        var a = guard.IndexOf("EXCLUDED.\"amountvalue\" > ", StringComparison.Ordinal);

        Assert.True(q < d && d < a, "each key must be nested behind the tie of the one before it");
    }
}
