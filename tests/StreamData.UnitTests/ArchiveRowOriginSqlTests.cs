using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// AB#5356: every raw and time-range row records whether it was entered manually or delivered
// automatically (column row_origin), and an archive can rank competing writes by it through the
// reserved conflict key "$Origin". A manual value is permanent: an automated delivery never replaces
// it, whatever its other keys say; only another manual write changes it. The behaviour against a real
// CrateDB is pinned by ArchiveRowOriginIntegrationTests (engine-mongodb integration tests).
public class ArchiveRowOriginSqlTests
{
    private static readonly string[] Columns = ["amountvalue", "dataquality", "sourcedocumentdate"];

    /// <summary>The energy community rule after AB#5356: origin first, then quality, then document date.</summary>
    private static readonly ArchiveConflictKey[] OriginThenQualityThenDate =
    [
        new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
        new("dataquality", ConflictKeyOrder.LowerWins),
        new("sourcedocumentdate", ConflictKeyOrder.HigherWins)
    ];

    [Fact]
    public void TheRawInsertNamesTheOriginColumnAndBindsItPerRow()
    {
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", Columns, rowCount: 2);

        // Named in the INSERT list, never left to a DEFAULT (AB#6647): eleven parameters per row.
        Assert.Contains("(\"rtid\", \"cktypeid\", \"timestamp\", \"rtwellknownname\", \"row_origin\", " +
                        "\"manual_written_by\", \"manual_written_at\", \"manual_reason\", \"amountvalue\"", sql);
        Assert.Contains("($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11), ($12, ", sql);
    }

    [Fact]
    public void WithoutPrecedenceTheOriginFollowsTheLastWrite()
    {
        // An archive that does not rank by origin behaves as before: the last write wins, and the row
        // then says where that write came from.
        var sql = CrateDatabaseClient.BuildRawInsertSql("\"t\".\"archive_a\"", Columns);

        Assert.Contains("\"row_origin\" = EXCLUDED.\"row_origin\"", sql);
        Assert.Contains("\"manual_written_by\" = EXCLUDED.\"manual_written_by\"", sql);
    }

    [Fact]
    public void AColumnPrecedenceGuardsTheOriginLikeEveryValue()
    {
        // A losing write must not leave its origin behind on the row it lost against.
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns,
            conflictPrecedence: [new ArchiveConflictKey("dataquality", ConflictKeyOrder.LowerWins)]);

        Assert.Contains("\"row_origin\" = CASE WHEN ", sql);
        Assert.Contains("ELSE \"row_origin\" END", sql);
    }

    [Fact]
    public void ARollupInsertCarriesNoOrigin()
    {
        var sql = CrateDatabaseClient.BuildTimeRangeInsertSql("\"t\".\"archive_a\"", Columns, generationTracked: true);

        Assert.DoesNotContain("row_origin", sql);
    }

    [Fact]
    public void TheOriginKeyNeedsNoUserColumn()
    {
        // "$Origin" ranks by the platform column; it is not declared in the archive's Columns.
        var guard = CrateDatabaseClient.BuildConflictGuard(
            [new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins)], Columns);

        // Alone: a manual write always wins, an automated one only against an automated row.
        Assert.Equal(
            "(EXCLUDED.\"row_origin\" = 'manual' OR (NOT COALESCE(\"row_origin\", 'automated') = 'manual' AND TRUE))",
            guard);
    }

    [Fact]
    public void TheOriginKeyIsMatchedCaseInsensitively()
    {
        Assert.True(new ArchiveConflictKey("$origin", ConflictKeyOrder.HigherWins).IsOrigin);
        Assert.False(new ArchiveConflictKey("Origin", ConflictKeyOrder.HigherWins).IsOrigin);
    }

    [Fact]
    public void TheOriginKeyFirst_ManualDecidesAndOnlyTwoAutomatedWritesFallThrough()
    {
        var guard = CrateDatabaseClient.BuildConflictGuard(OriginThenQualityThenDate, Columns)!;

        // The quality and date comparison sits inside the "stored row is not manual" branch, so:
        //   incoming manual                       -> wins, whatever the quality (a correction replaces a measurement,
        //                                             and a second manual write fixes a wrong first one);
        //   incoming automated, stored manual     -> loses, whatever the quality (manual is permanent);
        //   both automated                        -> quality, then date, exactly as before.
        Assert.StartsWith("(EXCLUDED.\"row_origin\" = 'manual' OR (NOT COALESCE(\"row_origin\", 'automated') = 'manual' AND ((", guard);
        var storedManual = guard.IndexOf("NOT COALESCE(\"row_origin\"", StringComparison.Ordinal);
        var quality = guard.IndexOf("EXCLUDED.\"dataquality\" < \"dataquality\"", StringComparison.Ordinal);
        Assert.True(storedManual >= 0 && quality > storedManual, "the column keys must be nested behind the origin decision");
    }

    [Fact]
    public void AnOriginKeyAfterAColumnKeyIsOnlyConsultedOnATie()
    {
        var guard = CrateDatabaseClient.BuildConflictGuard(
        [
            new("dataquality", ConflictKeyOrder.LowerWins),
            new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins)
        ], Columns)!;

        var qualityEqual = guard.IndexOf("EXCLUDED.\"dataquality\" = \"dataquality\"", StringComparison.Ordinal);
        var origin = guard.IndexOf("EXCLUDED.\"row_origin\" = 'manual'", StringComparison.Ordinal);
        Assert.True(qualityEqual >= 0 && origin > qualityEqual);
    }

    [Fact]
    public void TheOriginKeyWithLowerWinsIsRefused()
    {
        // LowerWins would let an automated delivery displace a manual value.
        var ex = Assert.Throws<InvalidOperationException>(() => CrateDatabaseClient.BuildConflictGuard(
            [new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.LowerWins)], Columns));

        Assert.Contains("$Origin", ex.Message);
        Assert.Contains("HigherWins", ex.Message);
    }

    [Fact]
    public void IgnoreOnAColumnKeyIsRefused()
    {
        // AB#7656: Ignore is the opt-out of the origin ranking and means nothing on a column.
        var ex = Assert.Throws<InvalidOperationException>(() => CrateDatabaseClient.BuildConflictGuard(
            [new ArchiveConflictKey(Columns[0], ConflictKeyOrder.Ignore)], Columns));

        Assert.Contains("only '$Origin' accepts", ex.Message);
    }

    [Theory]
    [InlineData(ArchiveRowOrigin.Manual, "manual")]
    [InlineData(ArchiveRowOrigin.Automated, "automated")]
    public void TheStoredValueRoundTrips(ArchiveRowOrigin origin, string stored)
    {
        Assert.Equal(stored, ArchiveRowOriginColumns.ToStoredValue(origin));
        Assert.Equal(origin, ArchiveRowOriginColumns.FromStoredValue(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else")]
    public void AnythingButManualReadsAsAutomated(string? stored)
    {
        // NULL is a row written before the origin was recorded.
        Assert.Equal(ArchiveRowOrigin.Automated, ArchiveRowOriginColumns.FromStoredValue(stored));
    }

    [Fact]
    public void RawAndTimeRangeTablesAreCreatedWithTheOriginColumn_RollupTablesWithout()
    {
        var raw = ArchiveDdlGenerator.GenerateCreateTable("\"t\".\"archive_a\"", [], 1, 0);
        var timeRange = ArchiveDdlGenerator.GenerateCreateWindowedTable("\"t\".\"archive_a\"", [], 1, 0);
        var rollup = ArchiveDdlGenerator.GenerateCreateWindowedTable("\"t\".\"archive_a\"", [], 1, 0, includeGeneration: true);

        foreach (var ddl in new[] { raw, timeRange })
        {
            Assert.Contains("\"row_origin\" TEXT,", ddl);
            Assert.Contains("\"manual_written_by\" TEXT,", ddl);
            Assert.Contains("\"manual_written_at\" TIMESTAMP WITH TIME ZONE,", ddl);
            Assert.Contains("\"manual_reason\" TEXT,", ddl);
        }

        Assert.DoesNotContain("row_origin", rollup);
        Assert.DoesNotContain("manual_", rollup);
        // Nullable without a DEFAULT: added by ALTER TABLE to populated tables, and always written by the insert.
        Assert.DoesNotContain("\"row_origin\" TEXT NOT NULL", raw);
        Assert.DoesNotContain("\"row_origin\" TEXT DEFAULT", raw);
    }

    [Fact]
    public void TheOriginColumnsAreQueryableOnRawAndTimeRangeArchives_NotOnRollups()
    {
        // AB#7593: the recorded values are readable through the stream data query API.
        var type = new Meshmakers.Octo.ConstructionKit.Contracts.RtCkId<Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId>(
            "Test", new Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId("Meter"));
        var timeRange = new ArchiveSnapshot(Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.GenerateNewId(), type,
            CkArchiveStatus.Activated, "m", [new CkArchiveColumnSpec("Value", true, false)]) { IsTimeRange = true };
        var rollup = timeRange with { RollupAggregations = [new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null)] };

        var resolver = StreamDataFieldResolver.CreateForArchive(timeRange);
        foreach (var name in new[] { "row_origin", "manual_written_by", "manual_written_at", "manual_reason" })
        {
            Assert.Equal(name, resolver.Resolve(name)?.CrateDbName);
            Assert.Equal(StreamDataFieldCategory.DataStream, resolver.Resolve(name)!.Category); // not in the default projection
            Assert.Null(StreamDataFieldResolver.CreateForArchive(rollup).Resolve(name));
        }
    }
}
