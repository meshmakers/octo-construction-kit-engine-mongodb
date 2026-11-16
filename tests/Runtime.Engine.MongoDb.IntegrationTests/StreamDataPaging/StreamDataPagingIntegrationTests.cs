using System.Globalization;
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

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataPaging;

/// <summary>
///     AB#9110 against a real CrateDB: paging over a stream-data result returns every row exactly once, also without a
///     sort order. The archives are sharded (4 shards), so CrateDB really collects the rows from several shards, and all
///     anchors share the same timestamps / windows, so a time-only order would still leave ties. Every page is a new
///     query, as the GraphQL connection runs it, and the whole paging is repeated three times.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class StreamDataPagingIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Day = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private const int Anchors = 8;
    private const int PointsPerAnchor = 48;
    private const int TotalRows = Anchors * PointsPerAnchor;
    private const int PageSize = 50;
    private const int Shards = 4;

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Bucket", new CrateColumnType.Primitive("INTEGER"), false, false)
    ];

    private sealed record Archive(OctoObjectId RtId, CrateDbStreamDataRepository Repository);

    /// <summary>
    ///     A raw (<paramref name="timeRange"/> false) or time-range archive of <see cref="Anchors"/> meters with
    ///     <see cref="PointsPerAnchor"/> rows each at the same 15-minute timestamps / windows. <c>Value</c> is the same
    ///     on every row (a sort on it is all ties), <c>Bucket</c> takes 6 values (groups of the grouped aggregation).
    /// </summary>
    private async Task<Archive> ArrangeAsync(bool timeRange)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, archiveRtId.ToString());
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(archiveRtId)).Returns(new ArchiveSnapshot(archiveRtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());

        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(table, ColumnDdl, Shards, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(table, ColumnDdl, Shards, 0));

        var start = new DateTimeOffset(Day).ToUnixTimeMilliseconds();
        var timeColumns = timeRange ? "\"window_start\", \"window_end\", \"was_updated\"" : "\"timestamp\"";
        var timeValues = timeRange
            ? $"({start} + w::bigint * 900000)::timestamp, ({start} + (w::bigint + 1) * 900000)::timestamp, FALSE"
            : $"({start} + w::bigint * 900000)::timestamp";
        await fixture.SqlAsync(
            $"INSERT INTO {table} ({timeColumns}, \"rtid\", \"cktypeid\", \"value\", \"bucket\") " +
            $"SELECT {timeValues}, '{AnchorPrefix}' || m, '{Type.SemanticVersionedFullName}', 1.0, w % 6 " +
            $"FROM generate_series(0, {PointsPerAnchor - 1}) AS t1(w) CROSS JOIN generate_series(10, {10 + Anchors - 1}) AS t2(m)");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        Assert.Equal(TotalRows, await fixture.CountAsync($"SELECT COUNT(*) FROM {table}"));
        return new Archive(archiveRtId, repository);
    }

    // 22 hex characters + a two-digit anchor number = a valid 24-hex rtId.
    private const string AnchorPrefix = "aa00000000000000009110";

    private static StreamDataQueryOptions Page(int offset, IReadOnlyList<SortOrderItem>? sortOrders) => StreamDataQueryOptions.Create()
        .WithCkTypeId(Type)
        .WithColumns(["Value"])
        .WithTimeRange(Day, Day.AddDays(1))
        .WithSortOrders(sortOrders)
        .WithPagination(offset, PageSize);

    public static TheoryData<bool, bool> Cases => new()
    {
        { false, false }, // raw archive, no sort order
        { true, false },  // time-range archive, no sort order
        { false, true },  // raw archive, sort order on a column that is the same on every row
        { true, true }    // time-range archive, the same
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PagingASimpleQuery_ReturnsEveryRowExactlyOnce_InTheSameOrderEveryTime(bool timeRange, bool sortOnTies)
    {
        var archive = await ArrangeAsync(timeRange);
        IReadOnlyList<SortOrderItem>? sortOrders = sortOnTies ? [new SortOrderItem("Value", SortOrders.Descending)] : null;

        List<(OctoObjectId? RtId, DateTime? Timestamp)>? firstRun = null;
        for (var run = 0; run < 3; run++)
        {
            var keys = new List<(OctoObjectId? RtId, DateTime? Timestamp)>();
            for (var offset = 0; offset < TotalRows; offset += PageSize)
            {
                var result = await archive.Repository.ExecuteQueryAsync(archive.RtId, Page(offset, sortOrders));
                Assert.Equal(TotalRows, result.TotalCount);
                keys.AddRange(result.Rows.Select(r => (r.RtId, r.Timestamp)));
            }

            Assert.Equal(TotalRows, keys.Count);
            Assert.Equal(TotalRows, keys.Distinct().Count());
            firstRun ??= keys;
            Assert.Equal(firstRun, keys);
        }

        // Without a sort order the default is the time axis, then rtId; with a sort on a column that is equal on every
        // row the row key breaks every tie, so the order is the same. Checked over all pages.
        var expected = firstRun!.OrderBy(k => k.Timestamp).ThenBy(k => k.RtId!.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(expected, firstRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PagingAGroupedAggregation_ReturnsEveryGroupExactlyOnce_InGroupColumnOrder(bool timeRange)
        => PageGroupsAsync(timeRange, AggregationFunction.Count);

    /// <summary>
    ///     TimeWeightedAverage over a raw archive runs the LOCF statement (<c>TimeWeightedQuerySqlBuilder</c>), not the
    ///     query builder; its groups are ordered the same way.
    /// </summary>
    [Fact]
    public Task PagingAGroupedTimeWeightedAggregation_ReturnsEveryGroupExactlyOnce_InGroupColumnOrder()
        => PageGroupsAsync(timeRange: false, AggregationFunction.TimeWeightedAverage);

    private async Task PageGroupsAsync(bool timeRange, AggregationFunction function)
    {
        var archive = await ArrangeAsync(timeRange);
        const int groupPageSize = 10;
        const int groups = Anchors * 6;

        // The GraphQL connection runs the grouped query again for every page and slices the result.
        List<string>? firstRun = null;
        for (var run = 0; run < 3; run++)
        {
            var keys = new List<string>();
            for (var offset = 0; offset < groups; offset += groupPageSize)
            {
                var result = await archive.Repository.ExecuteGroupedAggregationQueryAsync(archive.RtId,
                    StreamDataGroupedAggregationQueryOptions.Create()
                        .WithCkTypeId(Type)
                        .WithTimeRange(Day, Day.AddDays(1))
                        .WithGroupByColumns([CrateConstants.RtId, "Bucket"])
                        .WithAggregationColumns([new AggregationColumn("Value", function)]));
                Assert.Equal(groups, result.Rows.Count);
                keys.AddRange(result.Rows.Skip(offset).Take(groupPageSize).Select(r =>
                    string.Create(CultureInfo.InvariantCulture, $"{r.Values[CrateConstants.RtId]}|{r.Values["bucket"]}")));
            }

            Assert.Equal(groups, keys.Distinct().Count());
            Assert.Equal(groups, keys.Count);
            firstRun ??= keys;
            Assert.Equal(firstRun, keys);
        }

        var expected = firstRun!
            .Select(k => k.Split('|'))
            .OrderBy(p => p[0], StringComparer.Ordinal).ThenBy(p => int.Parse(p[1], CultureInfo.InvariantCulture))
            .Select(p => string.Join('|', p));
        Assert.Equal(expected, firstRun);
    }
}
