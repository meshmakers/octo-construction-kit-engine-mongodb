using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8622: a ranking column of the conflict precedence cannot be null (<see cref="ArchiveRankingColumnValidator" />),
/// raw and time-range, automated and manual; an unsent one is allowed. The repository refuses the batch before it reads
/// or writes anything.
/// </summary>
public class ArchiveRankingColumnValidatorTests
{
    private static readonly OctoObjectId ArchiveRtId = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private static readonly RtCkId<CkTypeId> OtherType = new("Test", new CkTypeId("Other"));
    private static readonly DateTime Timestamp = new(2026, 3, 1, 7, 30, 0, DateTimeKind.Utc);

    private static readonly ArchiveConflictKey Quality = new("DataQuality", ConflictKeyOrder.LowerWins);
    private static readonly ArchiveConflictKey Recency = new("Document.Date", ConflictKeyOrder.HigherWins);
    private static readonly ArchiveConflictKey Origin = new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins);

    private static ArchiveSnapshot Snapshot(params ArchiveConflictKey[] precedence) =>
        new(ArchiveRtId, MeterType, CkArchiveStatus.Activated, "meter",
        [
            new CkArchiveColumnSpec("Value", Indexed: false, Required: false),
            new CkArchiveColumnSpec("DataQuality", Indexed: false, Required: false),
            new CkArchiveColumnSpec("Document.Date", Indexed: false, Required: false)
        ])
        {
            ConflictPrecedence = precedence
        };

    private static StreamDataPoint Raw(Dictionary<string, object?> attributes, RtCkId<CkTypeId>? type = null,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Automated) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = type ?? MeterType,
        Timestamp = Timestamp,
        Attributes = attributes,
        Origin = origin,
        ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
    };

    private static TimeRangeStreamDataPoint Window(Dictionary<string, object?> attributes,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Automated) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = MeterType,
        From = Timestamp,
        To = Timestamp.AddMinutes(15),
        Attributes = attributes,
        Origin = origin,
        ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
    };

    private static IReadOnlyDictionary<string, object?> NoFlatten(IReadOnlyDictionary<string, object?> a) => a;

    [Fact]
    public void APointWithoutRankingColumns_Passes_ItKeepsTheStoredValues()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency), [Raw(new() { ["Value"] = 1d })]);
        ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency), [Window(new() { ["Value"] = 1d })], NoFlatten);
    }

    [Fact]
    public void APointThatSendsRankingColumnsWithValues_Passes()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency),
            [Raw(new() { ["DataQuality"] = 1, ["Document.Date"] = Timestamp })]);
    }

    [Fact]
    public void AnExplicitNullOnANonRankingColumn_Passes()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency), [Raw(new() { ["Value"] = null, ["DataQuality"] = 1 })]);
    }

    [Theory]
    [InlineData(ArchiveRowOrigin.Automated)]
    [InlineData(ArchiveRowOrigin.Manual)]
    public void ARawPointWithANullRankingColumn_RefusesTheBatch_NamingThePointAndTheColumn(ArchiveRowOrigin origin)
    {
        var good = Raw(new() { ["Value"] = 1d, ["DataQuality"] = 1 });
        var bad = Raw(new() { ["Value"] = 2d, ["DataQuality"] = null }, origin: origin);

        var error = Assert.Throws<ArgumentException>(() =>
            ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency), [good, bad]));

        Assert.Equal("points", error.ParamName);
        Assert.Contains($"Archive '{ArchiveRtId}' ranks conflicting writes by 'DataQuality', 'Document.Date'", error.Message);
        Assert.Contains("a ranking column cannot be null", error.Message);
        Assert.Contains("1 point(s) send null, nothing was written", error.Message);
        Assert.Contains($"point 1 (rtId '{bad.RtId}', timestamp 2026-03-01T07:30:00.0000000Z) sends null for 'DataQuality'",
            error.Message);
        Assert.DoesNotContain("point 0", error.Message);
    }

    [Theory]
    [InlineData(ArchiveRowOrigin.Automated)]
    [InlineData(ArchiveRowOrigin.Manual)]
    public void ATimeRangePointWithNullRankingColumns_IsRefused_ListingEveryNullColumn(ArchiveRowOrigin origin)
    {
        var bad = Window(new() { ["Value"] = 2d, ["DataQuality"] = null, ["Document.Date"] = DBNull.Value }, origin);

        var error = Assert.Throws<ArgumentException>(() =>
            ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Origin, Recency), [bad], NoFlatten));

        Assert.Contains($"point 0 (rtId '{bad.RtId}', from 2026-03-01T07:30:00.0000000Z) sends null for 'DataQuality', 'Document.Date'",
            error.Message);
        Assert.DoesNotContain(ArchiveConflictKey.OriginKey, error.Message);
    }

    [Fact]
    public void TheOriginKey_IsNotAColumnAndNeverChecked()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(Origin), [Raw(new() { ["Value"] = null })]);
    }

    [Fact]
    public void AnArchiveWithoutPrecedence_ChecksNothing()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(), [Raw(new() { ["DataQuality"] = null })]);
        ArchiveRankingColumnValidator.Validate(Snapshot(), [Window(new() { ["DataQuality"] = null })], NoFlatten);
    }

    [Fact]
    public void KeysAreMatchedLikeTheStorageColumn_CaseAndDotsDoNotMatter()
    {
        var error = Assert.Throws<ArgumentException>(() => ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency),
            [Raw(new() { ["dataquality"] = 2, ["documentDate"] = null })]));

        Assert.Contains("sends null for 'Document.Date'", error.Message);
    }

    [Fact]
    public void ANullRankingColumnInsideARecord_IsFoundOnTheTimeRangePath_AfterTheRecordIsExpanded()
    {
        var point = Window(new() { ["DataQuality"] = 1, ["Document"] = "record" });
        IReadOnlyDictionary<string, object?> Flatten(IReadOnlyDictionary<string, object?> a) =>
            a.ContainsKey("Document")
                ? new Dictionary<string, object?> { ["DataQuality"] = a["DataQuality"], ["Document.Date"] = null }
                : a;

        var error = Assert.Throws<ArgumentException>(() =>
            ArchiveRankingColumnValidator.Validate(Snapshot(Quality, Recency), [point], Flatten));

        Assert.Contains("sends null for 'Document.Date'", error.Message);
    }

    [Fact]
    public void PointsOfAnotherCkType_AreNotChecked()
    {
        ArchiveRankingColumnValidator.Validate(Snapshot(Quality), [Raw(new() { ["DataQuality"] = null }, OtherType)]);
    }

    [Fact]
    public void EveryRefusedPointIsCounted_AndAtMostTenAreListed()
    {
        var points = Enumerable.Range(0, 12).Select(_ => Raw(new() { ["DataQuality"] = null })).ToArray();

        var error = Assert.Throws<ArgumentException>(() => ArchiveRankingColumnValidator.Validate(Snapshot(Quality), points));

        Assert.Contains("12 point(s) send null", error.Message);
        Assert.Contains("point 9 (", error.Message);
        Assert.DoesNotContain("point 10 (", error.Message);
        Assert.Contains("(and 2 more)", error.Message);
    }

    // ── Repository wiring: refused before anything is read or written ────────────────────

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static (CrateDbStreamDataRepository Repository, IStreamDataDatabaseClient Db) NewRepository(ArchiveSnapshot snapshot)
    {
        var db = A.Fake<IStreamDataDatabaseClient>();
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(ArchiveRtId)).Returns(snapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), db, A.Fake<IStreamDataDatabaseManagementClient>(),
            Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }), "tenant-x", store, Formula);
        return (repository, db);
    }

    [Theory]
    [InlineData(ArchiveRowOrigin.Automated)]
    [InlineData(ArchiveRowOrigin.Manual)]
    public async Task InsertAsync_RefusesANullRankingColumn_BeforeTheDatabaseIsTouched(ArchiveRowOrigin origin)
    {
        var (repository, db) = NewRepository(Snapshot(Quality, Recency));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => repository.InsertAsync(ArchiveRtId,
        [
            Raw(new() { ["Value"] = 1d }),
            Raw(new() { ["Value"] = 2d, ["DataQuality"] = null }, origin: origin)
        ]));

        Assert.Contains("point 1 (", error.Message);
        A.CallTo(db).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(ArchiveRowOrigin.Automated)]
    [InlineData(ArchiveRowOrigin.Manual)]
    public async Task InsertTimeRangeAsync_RefusesANullRankingColumn_BeforeTheDatabaseIsTouched(ArchiveRowOrigin origin)
    {
        var (repository, db) = NewRepository(Snapshot(Quality, Recency) with { IsTimeRange = true, Period = TimeSpan.FromMinutes(15) });

        var error = await Assert.ThrowsAsync<ArgumentException>(() => repository.InsertTimeRangeAsync(ArchiveRtId,
            [Window(new() { ["Value"] = 2d, ["DataQuality"] = null }, origin)], TestContext.Current.CancellationToken));

        Assert.Contains("sends null for 'DataQuality'", error.Message);
        A.CallTo(db).MustNotHaveHappened();
    }
}
