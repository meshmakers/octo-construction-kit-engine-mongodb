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
        var skipped = ConflictOutcome.FindSkipped([Sent(1)], [Returned(1L, "automated")], Precedence, true, true);

        Assert.Empty(skipped);
    }

    [Fact]
    public void ARowThatLostOnQuality_IsLowerPrecedence()
    {
        var skipped = ConflictOutcome.FindSkipped([Sent(3)], [Returned(1, "automated")], Precedence, true, true);

        var s = Assert.Single(skipped);
        Assert.Equal(0, s.Index);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, s.Reason);
    }

    [Fact]
    public void AnAutomatedRowAgainstAStoredManualValue_IsManualValueProtected_EvenWithEqualQuality()
    {
        var skipped = ConflictOutcome.FindSkipped([Sent(1)], [Returned(1, "manual")], Precedence, true, true);

        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void AnAutomatedRowThatLostAgainstAManualValue_WithoutOriginRanking_IsLowerPrecedence()
    {
        // AB#7656: an archive that ignores the origin ranks by its columns only; the stored row being manual is incidental.
        ArchiveConflictKey[] qualityOnly = [new("dataquality", ConflictKeyOrder.LowerWins)];

        var skipped = ConflictOutcome.FindSkipped([Sent(3)], [Returned(1, "manual")], qualityOnly, true, true);

        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(skipped).Reason);
    }

    [Fact]
    public void TimestampsCompareAtMillisecondPrecision()
    {
        // CrateDB stores milliseconds; ticks below them are not a different row.
        var skipped = ConflictOutcome.FindSkipped([Sent(1, at: T0.AddTicks(4))], [Returned(1, "automated")], Precedence, true, true);

        Assert.Empty(skipped);
    }

    [Fact]
    public void TwoRowsForOneKey_AreMatchedOneToOne()
    {
        // The second of two writes for one key in one statement lost; exactly one of them is reported.
        var skipped = ConflictOutcome.FindSkipped([Sent(1), Sent(3)],
            [Returned(1, "automated"), Returned(1, "automated")], Precedence, true, true);

        Assert.Equal(1, Assert.Single(skipped).Index);
    }

    [Fact]
    public void ARowCrateDbReturnedNothingFor_IsNeverCountedAsStored()
    {
        var skipped = ConflictOutcome.FindSkipped([Sent(1)], [], Precedence, true, true);

        Assert.Single(skipped);
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
}
