using System;
using System.Collections.Generic;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8666: the one helper both archive insert paths use to expand record values into their dotted member paths.
/// </summary>
public class ArchiveRecordFlatteningTests
{
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Timestamp = new(2026, 3, 1, 7, 30, 0, DateTimeKind.Utc);

    private static RtRecord Record(string id, Dictionary<string, object?> members) =>
        new(new RtCkId<CkRecordId>("Test", new CkRecordId(id)), members);

    private static StreamDataPoint Raw(IReadOnlyDictionary<string, object?> attributes) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = MeterType,
        Timestamp = Timestamp,
        RtWellKnownName = "meter-1",
        RtCreationDateTime = Timestamp.AddDays(-1),
        RtChangedDateTime = Timestamp,
        Attributes = attributes,
        Origin = ArchiveRowOrigin.Manual,
        ManualWrittenBy = "jane.doe",
        ManualReason = "meter swap"
    };

    [Fact]
    public void RecordMembers_BecomeDottedPaths_ValueAndNullAlike_AndAMissingMemberIsNotNamed()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["Power"] = 1.5,
            ["NamePlate"] = Record("NamePlate", new() { ["SerialNumber"] = "SN-1", ["ManufacturerName"] = null })
        };

        var flattened = ArchiveRecordFlattening.Flatten(attributes);

        Assert.Equal(3, flattened.Count);
        Assert.Equal(1.5, flattened["Power"]);
        Assert.Equal("SN-1", flattened["NamePlate.SerialNumber"]);
        Assert.True(flattened.ContainsKey("NamePlate.ManufacturerName"));
        Assert.Null(flattened["NamePlate.ManufacturerName"]);
        Assert.False(flattened.ContainsKey("NamePlate"));
    }

    [Fact]
    public void NestedRecords_AreExpandedRecursively_AndAnEmptyRecordNamesNothing()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["Outer"] = Record("Outer", new() { ["Inner"] = Record("Inner", new() { ["Value"] = 7 }) }),
            ["Empty"] = Record("Empty", new())
        };

        var flattened = ArchiveRecordFlattening.Flatten(attributes);

        Assert.Equal(7, Assert.Single(flattened, kv => kv.Key == "Outer.Inner.Value").Value);
        Assert.Single(flattened);
    }

    [Fact]
    public void AttributesWithoutRecords_AreReturnedAsTheyAre_SoDottedRestKeysAreNotTouched()
    {
        var attributes = new Dictionary<string, object?> { ["NamePlate.SerialNumber"] = "SN-REST", ["Power"] = null };
        IReadOnlyList<StreamDataPoint> points = [Raw(attributes)];

        Assert.Same(attributes, ArchiveRecordFlattening.Flatten(attributes));
        Assert.Same(points, ArchiveRecordFlattening.Flatten(points));
    }

    [Fact]
    public void APointWithARecord_IsCopiedWithEveryOtherPropertyKept()
    {
        var plain = Raw(new Dictionary<string, object?> { ["Power"] = 1d });
        var withRecord = Raw(new Dictionary<string, object?>
        {
            ["NamePlate"] = Record("NamePlate", new() { ["SerialNumber"] = "SN-PIPE" })
        });

        var result = ArchiveRecordFlattening.Flatten([plain, withRecord]);

        Assert.Same(plain, result[0]);
        var copy = result[1];
        Assert.NotSame(withRecord, copy);
        Assert.Equal("SN-PIPE", copy.Attributes["NamePlate.SerialNumber"]);
        Assert.Equal(withRecord.RtId, copy.RtId);
        Assert.Equal(withRecord.CkTypeId, copy.CkTypeId);
        Assert.Equal(withRecord.Timestamp, copy.Timestamp);
        Assert.Equal(withRecord.RtWellKnownName, copy.RtWellKnownName);
        Assert.Equal(withRecord.RtCreationDateTime, copy.RtCreationDateTime);
        Assert.Equal(withRecord.RtChangedDateTime, copy.RtChangedDateTime);
        Assert.Equal(withRecord.Origin, copy.Origin);
        Assert.Equal(withRecord.ManualWrittenBy, copy.ManualWrittenBy);
        Assert.Equal(withRecord.ManualReason, copy.ManualReason);
    }

    [Fact]
    public void TimeRangePoints_AreFlattenedTheSameWay()
    {
        var point = new TimeRangeStreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = MeterType,
            From = Timestamp,
            To = Timestamp.AddMinutes(15),
            Attributes = new Dictionary<string, object?>
            {
                ["Amount"] = Record("Amount", new() { ["Value"] = 3d, ["Unit"] = null })
            },
            Origin = ArchiveRowOrigin.Manual,
            ManualWrittenBy = "jane.doe",
            ManualReason = "fix"
        };

        var copy = Assert.Single(ArchiveRecordFlattening.Flatten([point]));

        Assert.Equal(3d, copy.Attributes["Amount.Value"]);
        Assert.Null(copy.Attributes["Amount.Unit"]);
        Assert.Equal(point.From, copy.From);
        Assert.Equal(point.To, copy.To);
        Assert.Equal(point.ManualReason, copy.ManualReason);
    }
}
