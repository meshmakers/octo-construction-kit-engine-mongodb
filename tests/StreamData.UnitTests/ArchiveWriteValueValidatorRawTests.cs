using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8606: the AB#7643 value check for raw-archive points (<see cref="StreamDataPoint" /> overload of
/// <see cref="ArchiveWriteValueValidator" />) and its use in <see cref="CrateDbStreamDataRepository.InsertAsync(OctoObjectId, IEnumerable{StreamDataPoint})" />.
/// </summary>
public class ArchiveWriteValueValidatorRawTests
{
    private const string Tenant = "tenant-x";
    private static readonly OctoObjectId ArchiveRtId = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private static readonly RtCkId<CkTypeId> OtherType = new("Test", new CkTypeId("Other"));
    private static readonly CkId<CkEnumId> StateEnumId = new("Test-1.0.0/State-1");
    private static readonly DateTime Timestamp = new(2026, 3, 1, 7, 30, 0, DateTimeKind.Utc);

    private static readonly ArchiveSnapshot Snapshot = new(ArchiveRtId, MeterType, CkArchiveStatus.Activated, "meter",
    [
        new CkArchiveColumnSpec("Value", Indexed: false, Required: false),
        new CkArchiveColumnSpec("Count", Indexed: false, Required: false),
        new CkArchiveColumnSpec("Active", Indexed: false, Required: false),
        new CkArchiveColumnSpec("ReadAt", Indexed: false, Required: false),
        new CkArchiveColumnSpec("State", Indexed: false, Required: false),
        new CkArchiveColumnSpec("Label", Indexed: false, Required: false)
    ]);

    private readonly ICkCacheService _cache = MeterCache();

    private static ICkCacheService MeterCache()
    {
        CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto type, CkId<CkEnumId>? enumId = null) =>
            new(new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null, type, null, enumId, null, null, null,
                isOptional: true, description: null);

        var attributes = new[]
        {
            Attribute("Value", AttributeValueTypesDto.Double),
            Attribute("Count", AttributeValueTypesDto.Integer64),
            Attribute("Active", AttributeValueTypesDto.Boolean),
            Attribute("ReadAt", AttributeValueTypesDto.DateTime),
            Attribute("State", AttributeValueTypesDto.Enum, StateEnumId),
            Attribute("Label", AttributeValueTypesDto.String)
        }.ToDictionary(a => a.CkAttributeId, a => a);
        var graph = new CkTypeGraph(new CkId<CkTypeId>("Test-1.0.0/Meter-1"), isAbstract: false, isFinal: false,
            isCollectionRoot: true, [], null, null, [], [], attributes, [], new CkGraphDirectedAssociations([]), "meter",
            enableChangeStreamPreAndPostImages: false);
        var stateEnum = new CkEnumGraph(StateEnumId, useFlags: false, isExtensible: false,
            [new CkEnumValueDto { Key = 0, Name = "Off" }, new CkEnumValueDto { Key = 1, Name = "On" }], "state");

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, MeterType)).Returns(graph);
        CkEnumGraph? ignored;
        A.CallTo(() => cache.TryGetCkEnum(A<string>._, StateEnumId, out ignored)).Returns(true).AssignsOutAndRefParameters(stateEnum);
        return cache;
    }

    private static StreamDataPoint Point(Dictionary<string, object?> attributes, RtCkId<CkTypeId>? type = null,
        ArchiveRowOrigin origin = ArchiveRowOrigin.Manual) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = type ?? MeterType,
        Timestamp = Timestamp,
        RtWellKnownName = "meter-1",
        RtCreationDateTime = Timestamp.AddDays(-1),
        RtChangedDateTime = Timestamp,
        Attributes = attributes,
        Origin = origin,
        ManualWrittenBy = "jane.doe",
        ManualReason = "meter exchanged"
    };

    private IReadOnlyList<StreamDataPoint> Normalize(params StreamDataPoint[] points)
        => ArchiveWriteValueValidator.Normalize(_cache, Tenant, Snapshot, points);

    [Fact]
    public void ValuesThatFitTheirColumns_PassUnchanged_AsTheSameInstances()
    {
        var points = new[]
        {
            Point(new() { ["Value"] = 12.5, ["Count"] = 3L, ["Active"] = true, ["ReadAt"] = Timestamp, ["State"] = 1, ["Label"] = "x" }),
            Point(new() { ["Value"] = 7, ["Count"] = 4.0, ["Label"] = null })
        };

        var result = Normalize(points);

        Assert.Same(points[0], result[0]);
        Assert.Same(points[1], result[1]);
    }

    [Fact]
    public void AnEnumName_BecomesItsKey_AndThePointKeepsEverythingElse()
    {
        var point = Point(new() { ["State"] = "on", ["Value"] = 1.0 });

        var normalized = Assert.Single(Normalize(point));

        Assert.NotSame(point, normalized);
        Assert.Equal(1, normalized.Attributes["State"]);
        Assert.Equal(1.0, normalized.Attributes["Value"]);
        Assert.Equal(point.RtId, normalized.RtId);
        Assert.Equal(point.CkTypeId, normalized.CkTypeId);
        Assert.Equal(point.Timestamp, normalized.Timestamp);
        Assert.Equal(point.RtWellKnownName, normalized.RtWellKnownName);
        Assert.Equal(point.RtCreationDateTime, normalized.RtCreationDateTime);
        Assert.Equal(point.RtChangedDateTime, normalized.RtChangedDateTime);
        Assert.Equal(point.Origin, normalized.Origin);
        Assert.Equal(point.ManualWrittenBy, normalized.ManualWrittenBy);
        Assert.Equal(point.ManualReason, normalized.ManualReason);
    }

    [Theory]
    [InlineData("Value", "twelve", "a number")]
    [InlineData("Count", 1.5, "an integer")]
    [InlineData("Active", "yes", "true or false")]
    [InlineData("ReadAt", "2026-03-01", "a date and time (ISO 8601)")]
    [InlineData("State", "Broken", "a value of enum 'Test-1.0.0/State-1': Off (0), On (1)")]
    [InlineData("State", 7, "a value of enum 'Test-1.0.0/State-1': Off (0), On (1)")]
    public void AValueThatDoesNotFit_IsRefused_NamingPointColumnAndExpectedType(string column, object value, string expected)
    {
        var good = Point(new() { ["Value"] = 1.0 });
        var bad = Point(new() { [column] = value });

        var error = Assert.Throws<ArgumentException>(() => Normalize(good, bad));

        Assert.Contains($"Archive '{ArchiveRtId}': 1 value(s) do not fit their column, nothing was written", error.Message);
        Assert.Contains($"point 1 (rtId '{bad.RtId}', timestamp 2026-03-01T07:30:00.0000000Z)", error.Message);
        Assert.Contains($"column '{column}' expects {expected}", error.Message);
        Assert.Equal("points", error.ParamName);
    }

    [Fact]
    public void EveryMismatchOfTheBatchIsCounted_AndAtMostTenAreListed()
    {
        var points = Enumerable.Range(0, 12).Select(_ => Point(new() { ["Value"] = "x" })).ToArray();

        var error = Assert.Throws<ArgumentException>(() => Normalize(points));

        Assert.Contains("12 value(s) do not fit", error.Message);
        Assert.Contains("point 9 (", error.Message);
        Assert.DoesNotContain("point 10 (", error.Message);
        Assert.Contains("(and 2 more)", error.Message);
    }

    [Fact]
    public void PointsOfAnotherCkType_AndKeysThatAreNoColumn_AreNotChecked()
    {
        var other = Point(new() { ["Value"] = "not checked" }, OtherType);
        var unknownKey = Point(new() { ["NoSuchColumn"] = "x" });

        var result = Normalize(other, unknownKey);

        Assert.Same(other, result[0]);
        Assert.Same(unknownKey, result[1]);
    }

    [Fact]
    public void AutomatedPoints_AreCheckedTheSameWay()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Normalize(Point(new() { ["Value"] = "x" }, origin: ArchiveRowOrigin.Automated)));

        Assert.Contains("column 'Value' expects a number", error.Message);
    }

    [Fact]
    public void AnArchiveWithoutCheckableColumns_ReturnsTheBatchAsItIs()
    {
        var snapshot = Snapshot with { Columns = [] };
        var points = new[] { Point(new() { ["Value"] = "x" }) };

        Assert.Same(points, ArchiveWriteValueValidator.Normalize(_cache, Tenant, snapshot, points));
        A.CallTo(() => _cache.GetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._)).MustNotHaveHappened();
    }

    // ── InsertAsync wiring ─────────────────────────────────────────────────────────────

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private (CrateDbStreamDataRepository Repository, IStreamDataDatabaseClient Db) NewRepository()
    {
        var db = A.Fake<IStreamDataDatabaseClient>();
        A.CallTo(() => db.InsertDataAsync(Tenant, A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._,
                A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(ArchiveRtId)).Returns(Snapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, db,
            A.Fake<IStreamDataDatabaseManagementClient>(),
            Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }), Tenant, store, Formula);
        return (repository, db);
    }

    [Fact]
    public async Task InsertAsync_WithAValueThatDoesNotFit_ThrowsBeforeAnythingIsSentToCrateDb()
    {
        var (repository, db) = NewRepository();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => repository.InsertAsync(ArchiveRtId,
            [Point(new() { ["Value"] = 1.0 }), Point(new() { ["Value"] = "twelve" })]));

        Assert.Contains("point 1 (", error.Message);
        A.CallTo(() => db.InsertDataAsync(A<string>._, A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._,
            A<IReadOnlyList<ArchiveConflictKey>?>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task InsertAsync_WritesTheEnumKey_ForAnEnumName()
    {
        var (repository, db) = NewRepository();
        IEnumerable<DataPointDto>? sent = null;
        A.CallTo(() => db.InsertDataAsync(Tenant, A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._,
                A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Invokes((string _, string _, IReadOnlyList<string> _, IEnumerable<DataPointDto> dtos, IReadOnlyList<ArchiveConflictKey>? _) =>
                sent = dtos.ToList())
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));

        await repository.InsertAsync(ArchiveRtId, [Point(new() { ["State"] = "Off" })]);

        var dto = Assert.Single(sent!);
        Assert.Equal(0, dto.Attributes!["state"]);
    }
}
