using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataOrigin;

/// <summary>
///     AB#8637 against a real CrateDB, for raw and time-range archives: the reason of a skipped write names the ranking
///     key that decided. <c>ManualValueProtected</c> only when <c>$Origin</c> decided — first by default, or placed
///     after a column key that tied; <c>LowerPrecedence</c> when a column key before <c>$Origin</c> decided, also against
///     a stored manual row; never <c>ManualValueProtected</c> when the archive ignores the origin.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class SkipReasonDecidingKeyIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false),
        new("Quality", new CrateColumnType.Primitive("INTEGER"), false, false)
    ];

    private static readonly ArchiveConflictKey Quality = new("Quality", ConflictKeyOrder.LowerWins);
    private static readonly ArchiveConflictKey Origin = new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins);
    private static readonly ArchiveConflictKey OriginIgnored = new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.Ignore);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Archive(string Tenant, OctoObjectId RtId, bool TimeRange, CrateDbStreamDataRepository Repository)
    {
        public string Table => TenantSchema.QualifiedArchiveTable(Tenant, RtId.ToString());
    }

    private async Task<Archive> CreateArchiveAsync(bool timeRange, IReadOnlyList<ArchiveConflictKey> precedence)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, "measurements",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray())
        {
            IsTimeRange = timeRange,
            Period = timeRange ? TimeSpan.FromMinutes(15) : null,
            ConflictPrecedence = precedence
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store, A.Fake<IFormulaEngine>());
        var archive = new Archive(tenant, rtId, timeRange, repository);
        await fixture.SqlAsync(timeRange
            ? ArchiveDdlGenerator.GenerateCreateWindowedTable(archive.Table, ColumnDdl, 1, 0)
            : ArchiveDdlGenerator.GenerateCreateTable(archive.Table, ColumnDdl, 1, 0));
        return archive;
    }

    private async Task<StreamDataWriteResult> WriteAsync(Archive archive, OctoObjectId meter, double value, int quality,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Automated)
    {
        var attributes = new Dictionary<string, object?> { ["Value"] = value, ["Quality"] = quality };
        var writtenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null;
        var result = archive.TimeRange
            ? await archive.Repository.InsertTimeRangeAsync(archive.RtId,
            [
                new TimeRangeStreamDataPoint
                {
                    RtId = meter, CkTypeId = Type, From = Start, To = Start.AddMinutes(15), Attributes = attributes,
                    Origin = origin, ManualWrittenBy = writtenBy
                }
            ], Ct)
            : await archive.Repository.InsertAsync(archive.RtId,
            [
                new StreamDataPoint
                {
                    RtId = meter, CkTypeId = Type, Timestamp = Start, Attributes = attributes, Origin = origin,
                    ManualWrittenBy = writtenBy
                }
            ]);
        await fixture.SqlAsync($"REFRESH TABLE {archive.Table}");
        return result;
    }

    private async Task<(double Value, string? Origin)> RowAsync(Archive archive, OctoObjectId meter)
    {
        var row = Assert.Single(await fixture.QueryAsync(
            $"SELECT \"value\", \"row_origin\" FROM {archive.Table} WHERE \"rtid\" = '{meter}'"));
        return (Convert.ToDouble(row["value"]), row["row_origin"] as string);
    }

    /// <summary>Stores a manual row with quality 2 and delivers an automated point with <paramref name="quality" />.</summary>
    private async Task<(StreamDataWriteResult Result, (double Value, string? Origin) Row)> DeliverAgainstManualAsync(
        bool timeRange, IReadOnlyList<ArchiveConflictKey> precedence, int quality)
    {
        var archive = await CreateArchiveAsync(timeRange, precedence);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, meter, 12, 2, ArchiveRowOrigin.Manual);

        var result = await WriteAsync(archive, meter, 10, quality);
        return (result, await RowAsync(archive, meter));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    [InlineData(true, 1)]
    public async Task WithTheOriginFirstByDefault_TheOriginDecides_WhateverTheQuality(bool timeRange, int quality)
    {
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality], quality);

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        Assert.Equal((12d, "manual"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginAfterQuality_AWorseQuality_IsLowerPrecedence_AndTheManualRowStays(bool timeRange)
    {
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality, Origin], 3);

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(result.Skipped).Reason);
        Assert.Equal((12d, "manual"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginAfterQuality_ATiedQuality_IsDecidedByTheOrigin_AsManualValueProtected(bool timeRange)
    {
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality, Origin], 2);

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, Assert.Single(result.Skipped).Reason);
        Assert.Equal((12d, "manual"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginAfterQuality_ABetterQuality_ReplacesTheManualRow(bool timeRange)
    {
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality, Origin], 1);

        Assert.Equal(1, result.Stored);
        Assert.Empty(result.Skipped);
        Assert.Equal((10d, "automated"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginIgnored_AWorseQualityAgainstAManualRow_IsLowerPrecedence(bool timeRange)
    {
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality, OriginIgnored], 3);

        Assert.Equal(0, result.Stored);
        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(result.Skipped).Reason);
        Assert.Equal((12d, "manual"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginIgnored_ATiedQuality_ReplacesTheManualRow(bool timeRange)
    {
        // The last key admits equality; no origin key protects the manual row.
        var (result, row) = await DeliverAgainstManualAsync(timeRange, [Quality, OriginIgnored], 2);

        Assert.Equal(1, result.Stored);
        Assert.Equal((10d, "automated"), row);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithTheOriginAfterQuality_AManualWriteThatLosesOnQuality_FailsWithLowerPrecedence(bool timeRange)
    {
        var archive = await CreateArchiveAsync(timeRange, [Quality, Origin]);
        var meter = OctoObjectId.GenerateNewId();
        await WriteAsync(archive, meter, 10, 1);

        var error = await Assert.ThrowsAsync<ManualStreamDataNotAppliedException>(() =>
            WriteAsync(archive, meter, 12, 3, ArchiveRowOrigin.Manual));

        Assert.Equal(StreamDataSkipReason.LowerPrecedence, Assert.Single(error.Result.Skipped).Reason);
        Assert.Equal((10d, "automated"), await RowAsync(archive, meter));
    }
}
