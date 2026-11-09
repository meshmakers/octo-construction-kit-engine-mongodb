using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// AB#7598: which rows of a guarded upsert were stored, decided from what CrateDB RETURNs. The behaviour against a
// real CrateDB (single and multi-row statements) is pinned by ArchiveWriteResultIntegrationTests.
public class ConflictOutcomeTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey[] Precedence =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("dataquality", ConflictKeyOrder.LowerWins),
    ];

    private static ConflictOutcome.SentRow Sent(int quality, ArchiveRowOrigin origin = ArchiveRowOrigin.Automated, DateTime? at = null)
        => new("rt1", "Test/Meter", at ?? T0, T0.AddMinutes(15), new Dictionary<string, object?> { ["dataquality"] = quality }, origin);

    private static Dictionary<string, object?> Returned(object quality, string? origin, DateTime? at = null) => new()
    {
        ["rtid"] = "rt1", ["cktypeid"] = "Test/Meter", ["window_start"] = at ?? T0, ["window_end"] = T0.AddMinutes(15),
        ["dataquality"] = quality, ["row_origin"] = origin
    };

    private static IReadOnlyList<SkippedArchiveRow> FindSkipped(IReadOnlyList<ConflictOutcome.SentRow> sent,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> returned, IReadOnlyList<ArchiveConflictKey> precedence,
        bool includeOrigin, bool windowed)
    {
        var outcome = ConflictOutcome.Decide(sent, returned, precedence, includeOrigin, windowed);
        Assert.Empty(outcome.Missing);
        return outcome.Skipped;
    }

    [Fact]
    public void TheReturningClauseNamesTheKeyTheRankingColumnsAndTheOrigin()
    {
        Assert.Equal(" RETURNING \"rtid\", \"cktypeid\", \"window_start\", \"window_end\", \"dataquality\", \"row_origin\"",
            ConflictOutcome.ReturningClause(windowed: true, Precedence, includeOrigin: true));
        Assert.Equal(" RETURNING \"rtid\", \"cktypeid\", \"timestamp\", \"dataquality\"",
            ConflictOutcome.ReturningClause(windowed: false, Precedence, includeOrigin: false));
    }

    [Fact]
    public void ARowWhoseValuesCrateDbHolds_IsStored_EvenWithAnotherNumericType()
    {
        // The writer sent an int, the driver returns a long or a short: equal by value.
        var skipped = FindSkipped([Sent(1)], [Returned(1L, "automated")], Precedence, true, true);

        Assert.Empty(skipped);
    }

    [Fact]
    public void ARowThatLostOnQuality_IsLowerPrecedence()
    {
        var skipped = FindSkipped([Sent(3)], [Returned(1, "automated")], Precedence, true, true);

        var s = Assert.Single(skipped);
        Assert.Equal(0, s.Index);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, s.Reason);
    }

    [Fact]
    public void ARankingColumnTheRowDoesNotSend_IsNotCompared_TheRowCountsAsStored()
    {
        // AB#8622: an unsent ranking column keeps its stored value; the guard compared it on the merged row.
        var sent = new ConflictOutcome.SentRow("rt1", "Test/Meter", T0, T0.AddMinutes(15),
            new Dictionary<string, object?> { ["consumption"] = 12d }, ArchiveRowOrigin.Manual);

        Assert.Empty(FindSkipped([sent], [Returned(1, "manual")], Precedence, true, true));
        Assert.Equal(StreamDataSkipReason.LowerPrecedence,
            Assert.Single(FindSkipped([sent], [Returned(1, "automated")], Precedence, true, true)).Reason);
    }

    [Fact]
    public void AnAutomatedRowAgainstAStoredManualValue_IsManualValueProtected_EvenWithEqualQuality()
    {
        var skipped = FindSkipped([Sent(1)], [Returned(1, "manual")], Precedence, true, true);

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AnAutomatedRowThatLostAgainstAManualValue_WithoutOriginRanking_IsLowerPrecedence()
    {
        // AB#7656: an archive that ignores the origin ranks by its columns only; the stored row being manual is incidental.
        ArchiveConflictKey[] qualityOnly = [new("dataquality", ConflictKeyOrder.LowerWins)];

        var skipped = FindSkipped([Sent(3)], [Returned(1, "manual")], qualityOnly, true, true);

        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(skipped).Reason);
    }

    // AB#8637: the reason names the key that decided, the first key in which the stored row differs from the sent one.
    private static readonly ArchiveConflictKey[] QualityBeforeOrigin =
    [
        new("dataquality", ConflictKeyOrder.LowerWins),
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
    ];

    [Fact]
    public void WithTheOriginFirst_AnAutomatedRowAgainstAManualValue_IsManualValueProtected_AlsoWhenItsQualityIsWorse()
    {
        var skipped = FindSkipped([Sent(3)], [Returned(1, "manual")], Precedence, true, true);

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void WithQualityBeforeTheOrigin_AnAutomatedRowWithWorseQualityAgainstAManualValue_IsLowerPrecedence()
    {
        var skipped = FindSkipped([Sent(3)], [Returned(2, "manual")], QualityBeforeOrigin, true, true);

        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void WithQualityBeforeTheOrigin_AnAutomatedRowWithEqualQualityAgainstAManualValue_IsManualValueProtected()
    {
        var skipped = FindSkipped([Sent(2)], [Returned(2L, "manual")], QualityBeforeOrigin, true, true);

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void WithQualityBeforeTheOrigin_AnAutomatedRowWithoutQualityAgainstAManualValue_IsManualValueProtected()
    {
        // AB#8622 + AB#8637: an unsent ranking column keeps its stored value and ties, so the origin decides.
        var sent = new ConflictOutcome.SentRow("rt1", "Test/Meter", T0, T0.AddMinutes(15),
            new Dictionary<string, object?> { ["consumption"] = 12d }, ArchiveRowOrigin.Automated);

        var skipped = FindSkipped([sent], [Returned(2, "manual")], QualityBeforeOrigin, true, true);

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void WithQualityBeforeTheOrigin_AManualRowThatLostOnQuality_IsLowerPrecedence()
    {
        // The reason a manual row that was not stored carries in ManualStreamDataNotAppliedException.
        var skipped = FindSkipped([Sent(3, ArchiveRowOrigin.Manual)], [Returned(1, "automated")],
            QualityBeforeOrigin, true, true);

        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void TimestampsCompareAtMillisecondPrecision()
    {
        // CrateDB stores milliseconds; ticks below them are not a different row.
        var skipped = FindSkipped([Sent(1, at: T0.AddTicks(4))], [Returned(1, "automated")], Precedence, true, true);

        Assert.Empty(skipped);
    }

    [Fact]
    public void TwoRowsForOneKey_AreMatchedOneToOne()
    {
        // The second of two writes for one key in one statement lost; exactly one of them is reported.
        var skipped = FindSkipped([Sent(1), Sent(3)],
            [Returned(1, "automated"), Returned(1, "automated")], Precedence, true, true);

        Assert.Equal(1, Assert.Single(skipped).Index);
    }

    [Fact]
    public void ARowCrateDbReturnedNothingFor_IsNotWritten_NeitherStoredNorSkipped()
    {
        // AB#8679: an upsert returns a row for every row it processed, the guard's losers included. No row back means
        // CrateDB dropped the row (a tripped memory breaker), not that the guard decided against it.
        var outcome = ConflictOutcome.Decide([Sent(1)], [], Precedence, true, true);

        Assert.Empty(outcome.Skipped);
        Assert.Equal(0, Assert.Single(outcome.Missing));
    }

    [Fact]
    public void InAStatementWithMissingRows_TheReturnedRowsAreStillDecided()
    {
        // Rows 0 and 2 came back (row 2 lost against a stored L1), row 1 did not.
        var at1 = T0.AddMinutes(15);
        var at2 = T0.AddMinutes(30);
        var outcome = ConflictOutcome.Decide([Sent(1), Sent(1, at: at1), Sent(3, at: at2)],
            [Returned(1, "automated"), Returned(1, "automated", at2)], Precedence, true, true);

        Assert.Equal(1, Assert.Single(outcome.Missing));
        var skipped = Assert.Single(outcome.Skipped);
        Assert.Equal(2, skipped.Index);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, skipped.Reason);
    }

    [Fact]
    public void ARowForAKeyNamedTwice_WithOnlyOneRowReturned_IsMissing()
    {
        var outcome = ConflictOutcome.Decide([Sent(1), Sent(1)], [Returned(1, "automated")], Precedence, true, true);

        Assert.Empty(outcome.Skipped);
        Assert.Equal(1, Assert.Single(outcome.Missing));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 1, false)]
    [InlineData(2.5, 2.5f, true)]
    [InlineData("a", "a", true)]
    [InlineData("a", "A", false)]
    public void ValuesCompareAsCrateDbStoresThem(object? stored, object? sent, bool equal)
    {
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(stored, sent));
    }

    [Theory]
    [InlineData("2026-10-21T10:00:00Z", true)]
    [InlineData("2026-10-21T12:00:00+02:00", true)]
    [InlineData("2026-10-21T10:00:00", true)]
    [InlineData("2026-10-21T10:15:00Z", false)]
    [InlineData("no date", false)]
    public void ValuesEqual_ATimeSentAsString_ComparesWithTheStoredTime(string sent, bool equal)
    {
        var stored = new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(equal, ConflictOutcome.ValuesEqual(stored, sent));
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(new DateTimeOffset(stored), sent));
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(new DateTimeOffset(stored).ToUnixTimeMilliseconds(), sent));
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("3.0", true)]
    [InlineData("3.5", false)]
    [InlineData("three", false)]
    public void ValuesEqual_ANumberSentAsString_ComparesWithTheStoredNumber(string sent, bool equal)
    {
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(3, sent));
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(3L, sent));
        Assert.Equal(equal, ConflictOutcome.ValuesEqual(3.0, sent));
    }
}
