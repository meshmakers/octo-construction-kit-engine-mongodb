using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CrateConstants = Meshmakers.Octo.Runtime.Engine.CrateDb.Constants;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#7616 against a real CrateDB: readers see the origin of every time-range row and its manual audit, filter
///     and group by it, and a row written before the origin was recorded (NULL in <c>row_origin</c>) reads as
///     automated on every path.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveRowOriginReadIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("DataQuality", new CrateColumnType.Primitive("INTEGER"), false, false)
    ];

    private sealed record Archive(string Table, OctoObjectId RtId, OctoObjectId Meter, CrateDbStreamDataRepository Repository);

    /// <summary>
    ///     A meter archive provisioned before AB#5356 with one delivered row (window 0), then a manual correction
    ///     (window 1) and an automated delivery (window 2) written through the repository, which adds the columns.
    /// </summary>
    private async Task<Archive> ArrangeAsync()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = OctoObjectId.GenerateNewId();
        var meter = OctoObjectId.GenerateNewId();
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(archiveRtId)).Returns(new ArchiveSnapshot(archiveRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15),
            ConflictPrecedence = [new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins), new("DataQuality", ConflictKeyOrder.LowerWins)]
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());
        var archive = new Archive(TenantSchema.QualifiedArchiveTable(tenant, archiveRtId.ToString()), archiveRtId, meter, repository);

        await fixture.SqlAsync(
            $"CREATE TABLE {archive.Table} (\"window_start\" TIMESTAMP WITH TIME ZONE NOT NULL, \"window_end\" TIMESTAMP WITH TIME ZONE NOT NULL, " +
            "\"rtid\" TEXT NOT NULL, \"cktypeid\" TEXT NOT NULL, \"rtcreationdatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, " +
            "\"rtchangeddatetime\" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP, \"rtwellknownname\" TEXT, \"was_updated\" BOOLEAN NOT NULL DEFAULT FALSE, " +
            "\"value\" DOUBLE PRECISION, \"dataquality\" INTEGER, " +
            "PRIMARY KEY (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\")) CLUSTERED INTO 1 SHARDS WITH (number_of_replicas = 0)");
        await fixture.SqlAsync(
            $"INSERT INTO {archive.Table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"value\", \"dataquality\") " +
            $"VALUES ('{Day:O}', '{Day.AddMinutes(15):O}', '{meter}', '{Type.SemanticVersionedFullName}', FALSE, 1, 1)");

        var ct = TestContext.Current.CancellationToken;
        await repository.InsertTimeRangeAsync(archiveRtId, [Point(archive, 1, 2, 2, ArchiveRowOrigin.Manual, "user-1", "meter exchanged")], ct);
        await repository.InsertTimeRangeAsync(archiveRtId, [Point(archive, 2, 4, 1, ArchiveRowOrigin.Automated)], ct);
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        return archive;
    }

    private static TimeRangeStreamDataPoint Point(Archive archive, int window, double value, int quality, ArchiveRowOrigin origin,
        string? writtenBy = null, string? reason = null) => new()
    {
        RtId = archive.Meter,
        CkTypeId = Type,
        From = Day.AddMinutes(15 * window),
        To = Day.AddMinutes(15 * (window + 1)),
        Attributes = new Dictionary<string, object?> { ["Value"] = value, ["DataQuality"] = quality },
        Origin = origin,
        ManualWrittenBy = writtenBy,
        ManualReason = reason
    };

    private static readonly string[] OriginColumns =
        ["Value", CrateConstants.RowOrigin, CrateConstants.ManualWrittenBy, CrateConstants.ManualWrittenAt, CrateConstants.ManualReason];

    private Task<StreamDataQueryResult> QueryAsync(Archive archive, params FieldFilter[] filters)
        => archive.Repository.ExecuteQueryAsync(archive.RtId, StreamDataQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithColumns(OriginColumns)
            .WithTimeRange(Day, Day.AddDays(1))
            .WithFieldFilters(filters.Length == 0 ? null : filters)
            .WithSortOrders([new SortOrderItem("Value", SortOrders.Ascending)]));

    [Fact]
    public async Task EveryRowReportsItsOrigin_AndAManualRowItsAudit_ARowWrittenBeforeTheOriginReadsAutomated()
    {
        var archive = await ArrangeAsync();

        var rows = (await QueryAsync(archive)).Rows;

        Assert.Equal(3, rows.Count);
        Assert.Equal(["automated", "manual", "automated"], rows.Select(r => r.Values[CrateConstants.RowOrigin]));
        Assert.Null(rows[0].Values[CrateConstants.ManualWrittenBy]);
        Assert.Equal("user-1", rows[1].Values[CrateConstants.ManualWrittenBy]);
        Assert.Equal("meter exchanged", rows[1].Values[CrateConstants.ManualReason]);
        Assert.NotNull(rows[1].Values[CrateConstants.ManualWrittenAt]);
        Assert.Null(rows[2].Values[CrateConstants.ManualWrittenBy]);
    }

    [Theory]
    [InlineData("automated", new[] { 1d, 4d })]
    [InlineData("manual", new[] { 2d })]
    public async Task AFilterOnTheOrigin_CountsARowWrittenBeforeTheOriginAsAutomated(string origin, double[] expectedValues)
    {
        var archive = await ArrangeAsync();

        var rows = (await QueryAsync(archive, new FieldFilter(CrateConstants.RowOrigin, FieldFilterOperator.Equals, origin))).Rows;

        Assert.Equal(expectedValues, rows.Select(r => Convert.ToDouble(r.Values["value"])));
    }

    [Fact]
    public async Task AGroupedAggregation_CountsManualAndAutomatedIntervals_InOneStep()
    {
        var archive = await ArrangeAsync();

        var result = await archive.Repository.ExecuteGroupedAggregationQueryAsync(archive.RtId,
            StreamDataGroupedAggregationQueryOptions.Create()
                .WithCkTypeId(Type)
                .WithTimeRange(Day, Day.AddDays(1))
                .WithGroupByColumns([CrateConstants.RtId, "DataQuality", CrateConstants.RowOrigin])
                .WithAggregationColumns([new AggregationColumn("Value", AggregationFunction.Sum),
                    new AggregationColumn("Value", AggregationFunction.Count)]));

        var groups = result.Rows
            .Select(r => (Origin: (string?)r.Values[CrateConstants.RowOrigin], Quality: Convert.ToInt32(r.Values["dataquality"]),
                Sum: Convert.ToDouble(r.Values["value_sum"]), Count: Convert.ToInt64(r.Values["value_count"])))
            .OrderBy(g => g.Origin).ThenBy(g => g.Quality)
            .ToList();
        Assert.Equal([("automated", 1, 5d, 2L), ("manual", 2, 2d, 1L)], groups);
    }
}
