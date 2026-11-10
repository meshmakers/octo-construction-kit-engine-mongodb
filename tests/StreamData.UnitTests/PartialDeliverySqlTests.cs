using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8622: a write updates only the columns the point names. <see cref="ArchiveColumnSets" /> splits a batch by the
/// set of named columns, and the insert statements name exactly those columns in their INSERT and DO UPDATE SET lists.
/// The behaviour against CrateDB is proven by <c>PartialDeliveryIntegrationTests</c>.
/// </summary>
public class PartialDeliverySqlTests
{
    private static readonly string[] Columns = ["meterreading", "voltage", "quality"];

    private static IReadOnlyList<ArchiveColumnSetGroup> Group(
        IReadOnlyList<(string Key, Dictionary<string, object?> Attributes)> rows,
        IReadOnlyList<ArchiveConflictKey>? precedence = null)
        => ArchiveColumnSets.Group(rows.Count, i => rows[i].Attributes, i => rows[i].Key, Columns, precedence);

    [Fact]
    public void ABatchWhoseRowsNameTheSameColumns_IsOneGroup()
    {
        var groups = Group([
            ("a", new() { ["meterreading"] = 1d, ["voltage"] = 2d }),
            ("b", new() { ["meterreading"] = 3d, ["voltage"] = null })
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(["meterreading", "voltage"], group.InsertColumns);
        Assert.Equal(["meterreading", "voltage"], group.UpdatedColumns);
        Assert.Equal([0, 1], group.Indexes);
    }

    [Fact]
    public void AnExplicitNull_CountsAsNamed_AndAnUnknownAttributeIsIgnored()
    {
        var group = Assert.Single(Group([("a", new() { ["voltage"] = null, ["notacolumn"] = 1 })]));

        Assert.Equal(["voltage"], group.UpdatedColumns);
    }

    [Fact]
    public void RowsWithDifferentColumnSets_GoIntoOneGroupPerSet_InTheOrderTheSetsFirstAppear()
    {
        var groups = Group([
            ("a", new() { ["meterreading"] = 1d }),
            ("b", new() { ["voltage"] = 2d }),
            ("c", new() { ["meterreading"] = 3d }),
            ("d", new())
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Equal([0, 2], groups[0].Indexes);
        Assert.Equal(["meterreading"], groups[0].UpdatedColumns);
        Assert.Equal([1], groups[1].Indexes);
        Assert.Equal(["voltage"], groups[1].UpdatedColumns);
        Assert.Equal([3], groups[2].Indexes);
        Assert.Empty(groups[2].UpdatedColumns);
    }

    [Fact]
    public void TheSameRowKeyWithAnotherColumnSet_StartsANewSegment_SoBothWritesRunInTheirOrder()
    {
        var groups = Group([
            ("a", new() { ["meterreading"] = 1d }),
            ("b", new() { ["voltage"] = 2d }),
            ("c", new() { ["meterreading"] = 3d }),
            ("a", new() { ["voltage"] = 4d }), // a again, other set: must run after index 0
            ("d", new() { ["meterreading"] = 5d })
        ]);

        var order = groups.SelectMany(g => g.Indexes).ToList();
        Assert.True(order.IndexOf(0) < order.IndexOf(3));
        Assert.Equal(4, groups.Count);
        Assert.Equal([0, 2], groups[0].Indexes);
        Assert.Equal([1], groups[1].Indexes);
        Assert.Equal([3], groups[2].Indexes);
        Assert.Equal([4], groups[3].Indexes);
        Assert.Equal(5, order.Distinct().Count());
    }

    [Fact]
    public void TheSameRowKeyWithTheSameColumnSet_StaysInItsGroup()
    {
        var groups = Group([
            ("a", new() { ["meterreading"] = 1d }),
            ("b", new() { ["voltage"] = 2d }),
            ("a", new() { ["meterreading"] = 3d })
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal([0, 2], groups[0].Indexes);
    }

    [Fact]
    public void ARankingColumn_IsAlwaysInserted_ButOnlyReplacedWhenNamed()
    {
        // The guard reads EXCLUDED."quality", so it is in the INSERT list (bound NULL) even when not named.
        var group = Assert.Single(Group([("a", new() { ["meterreading"] = 1d })],
            [new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
             new ArchiveConflictKey("quality", ConflictKeyOrder.LowerWins)]));

        Assert.Equal(["meterreading", "quality"], group.InsertColumns);
        Assert.Equal(["meterreading"], group.UpdatedColumns);
    }

    [Fact]
    public void TheRawStatement_ReplacesOnlyTheUpdatedColumns()
    {
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", ["meterreading", "quality"],
            [new ArchiveConflictKey("quality", ConflictKeyOrder.LowerWins)], rowCount: 1, updatedColumnNames: ["meterreading"]);

        Assert.Contains("\"meterreading\", \"quality\") VALUES ", sql);
        Assert.Contains("\"meterreading\" = CASE WHEN", sql);
        Assert.DoesNotContain("\"quality\" = CASE WHEN", sql);
        Assert.DoesNotContain("\"voltage\"", sql);
    }

    [Fact]
    public void AnUnsentRankingColumn_IsComparedOnTheMergedRow_ASentOneOnTheIncomingValue()
    {
        // AB#8622: an unsent ranking column keeps its stored value, so the guard reads COALESCE(EXCLUDED."k", "k"),
        // which equals the stored key and lets the next key decide. A sent key is compared as before.
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", ["meterreading", "quality", "documentdate"],
            [new ArchiveConflictKey("quality", ConflictKeyOrder.LowerWins),
             new ArchiveConflictKey("documentdate", ConflictKeyOrder.HigherWins)],
            rowCount: 1, updatedColumnNames: ["meterreading", "quality"]);

        Assert.Contains("COALESCE(EXCLUDED.\"documentdate\", \"documentdate\")", sql);
        Assert.DoesNotContain("COALESCE(EXCLUDED.\"quality\"", sql);
        Assert.Contains("EXCLUDED.\"quality\" < \"quality\"", sql);
        Assert.Contains("\"quality\" = CASE WHEN", sql);
        Assert.DoesNotContain("\"documentdate\" = CASE WHEN", sql);
    }

    [Fact]
    public void WithoutUpdatedColumns_EveryRankingColumnCountsAsSent()
    {
        var guard = CrateDatabaseClient.BuildConflictGuard(
            [new ArchiveConflictKey("quality", ConflictKeyOrder.LowerWins)], ["meterreading", "quality"]);

        Assert.DoesNotContain("COALESCE", guard);
    }

    [Fact]
    public void TheTimeRangeStatement_ComparesAnUnsentRankingColumnOnTheMergedRow()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", ["voltage", "quality"], rowCount: 1,
            conflictPrecedence: [new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
                new ArchiveConflictKey("quality", ConflictKeyOrder.LowerWins)],
            updatedColumnNames: ["voltage"]);

        Assert.Contains("COALESCE(EXCLUDED.\"quality\", \"quality\")", sql);
        Assert.DoesNotContain("\"quality\" = CASE WHEN", sql);
    }

    [Fact]
    public void TheTimeRangeStatement_ReplacesOnlyTheUpdatedColumns_AndStillWritesWasUpdatedFalse()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", ["voltage"], rowCount: 2,
            updatedColumnNames: ["voltage"]);

        // AB#6647: was_updated is named with the literal FALSE in every row of every column-set statement.
        Assert.Contains("\"voltage\", \"was_updated\") VALUES ", sql);
        Assert.Contains("FALSE), (", sql);
        Assert.Contains("\"voltage\" = EXCLUDED.\"voltage\"", sql);
        Assert.Contains("\"row_origin\" = EXCLUDED.\"row_origin\"", sql);
        Assert.DoesNotContain("meterreading", sql);
    }

    [Fact]
    public void WithoutUpdatedColumns_TheStatementReplacesEveryInsertedColumn_AsBefore()
    {
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", Columns);

        foreach (var column in Columns)
        {
            Assert.Contains($"\"{column}\" = EXCLUDED.\"{column}\"", sql);
        }
    }
}
