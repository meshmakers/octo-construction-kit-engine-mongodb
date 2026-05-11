using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

public class StreamDataEntityHydratorTests
{
    // Minimal local StreamDataEntity subclass — real ones come from the source generator
    private class SdTestEntity : StreamDataEntity
    {
        public double? Voltage { get; set; }
        public double? Current { get; set; }
    }

    [Fact]
    public void Hydrate_MapsBuiltInFields()
    {
        var rtId = new OctoObjectId("000000000000000000000001");
        var row = new StreamDataRow
        {
            RtId = rtId,
            CkTypeId = new RtCkId<CkTypeId>("Test/Type"),
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            RtWellKnownName = "wkn",
            RtCreationDateTime = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            RtChangedDateTime = new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc),
            Values = new Dictionary<string, object?>()
        };
        var e = StreamDataEntityHydrator.Hydrate<SdTestEntity>(row);
        Assert.Equal(rtId, e.RtId);
        Assert.Equal("Test/Type", e.CkTypeId!.ToString());
        Assert.Equal(row.Timestamp, e.Timestamp);
        Assert.Equal("wkn", e.RtWellKnownName);
        Assert.Equal(row.RtCreationDateTime, e.RtCreationDateTime);
        Assert.Equal(row.RtChangedDateTime, e.RtChangedDateTime);
    }

    [Fact]
    public void Hydrate_MapsTypedPropertiesFromValues()
    {
        var row = new StreamDataRow
        {
            RtId = OctoObjectId.Empty,
            CkTypeId = new RtCkId<CkTypeId>("Test/Type"),
            Timestamp = DateTime.UtcNow,
            Values = new Dictionary<string, object?> { ["Voltage"] = 220.5, ["Current"] = 10.2 }
        };
        var e = StreamDataEntityHydrator.Hydrate<SdTestEntity>(row);
        Assert.Equal(220.5, e.Voltage);
        Assert.Equal(10.2, e.Current);
    }

    [Fact]
    public void Hydrate_UnknownKeys_StayInAttributes()
    {
        var row = new StreamDataRow
        {
            RtId = OctoObjectId.Empty,
            CkTypeId = new RtCkId<CkTypeId>("Test/Type"),
            Timestamp = DateTime.UtcNow,
            Values = new Dictionary<string, object?> { ["UnknownAttr"] = "xyz" }
        };
        var e = StreamDataEntityHydrator.Hydrate<SdTestEntity>(row);
        Assert.True(e.Attributes.ContainsKey("UnknownAttr"));
        Assert.Equal("xyz", e.Attributes["UnknownAttr"]);
    }

    [Fact]
    public void Hydrate_ConvertsCompatibleValueTypes()
    {
        // CrateDB sometimes returns numeric values as int or long even when the typed prop is double
        var row = new StreamDataRow
        {
            RtId = OctoObjectId.Empty,
            CkTypeId = new RtCkId<CkTypeId>("Test/Type"),
            Timestamp = DateTime.UtcNow,
            Values = new Dictionary<string, object?> { ["Voltage"] = 220 }  // int, not double
        };
        var e = StreamDataEntityHydrator.Hydrate<SdTestEntity>(row);
        Assert.Equal(220.0, e.Voltage);
    }

    [Fact]
    public void Hydrate_NullValue_LeavesTypedPropertyAtDefault()
    {
        var row = new StreamDataRow
        {
            RtId = OctoObjectId.Empty,
            CkTypeId = new RtCkId<CkTypeId>("Test/Type"),
            Timestamp = DateTime.UtcNow,
            Values = new Dictionary<string, object?> { ["Voltage"] = null }
        };
        var e = StreamDataEntityHydrator.Hydrate<SdTestEntity>(row);
        Assert.Null(e.Voltage);
        Assert.True(e.Attributes.ContainsKey("Voltage"));
        Assert.Null(e.Attributes["Voltage"]);
    }
}
