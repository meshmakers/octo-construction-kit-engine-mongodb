using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Message contract of the AB#4255 disable refusal: deterministic order, kind labels from the
/// snapshot discriminators, runtime id as the fallback name.
/// </summary>
public class StreamDataDisableBlockedExceptionTests
{
    private static readonly RtCkId<CkTypeId> Target = new("Test/MeasuringPoint");

    [Fact]
    public void Create_NamesEveryArchive_OrderedByNameThenRtId_WithItsKind()
    {
        var raw = Snapshot("temps");
        var rollup = Snapshot("temps-1h") with { RollupAggregations = Array.Empty<CkRollupAggregationSpec>() };
        var window = Snapshot("shifts") with { IsTimeRange = true };

        var ex = StreamDataDisableBlockedException.Create("acme", [rollup, raw, window]);

        Assert.Equal(
            "Stream data cannot be disabled for tenant 'acme' while the following archives are still activated: " +
            "TimeRangeArchive 'shifts' (Activated), RawArchive 'temps' (Activated), RollupArchive 'temps-1h' (Activated). " +
            "Disable them (data is kept) or delete them - rollups before their source archive - and retry.",
            ex.Message);
        Assert.Equal([window.RtId, raw.RtId, rollup.RtId], ex.ActivatedArchives.Select(a => a.RtId));
    }

    [Fact]
    public void Create_OrdersArchivesWithTheSameName_ByRtId()
    {
        var a = Snapshot("dup");
        var b = Snapshot("dup");
        var expected = new[] { a, b }.OrderBy(s => s.RtId.ToString(), StringComparer.Ordinal).Select(s => s.RtId).ToList();

        var ex = StreamDataDisableBlockedException.Create("acme", [b, a]);

        Assert.Equal(expected, ex.ActivatedArchives.Select(s => s.RtId));
    }

    [Fact]
    public void DescribeArchive_FallsBackToTheRuntimeId_WithoutAWellKnownName()
    {
        var nameless = Snapshot(null);

        Assert.Equal($"RawArchive '{nameless.RtId}' (Activated)", StreamDataDisableBlockedException.DescribeArchive(nameless));
    }

    [Fact]
    public void Create_RejectsAnEmptyArchiveList()
    {
        Assert.Throws<ArgumentException>(() => StreamDataDisableBlockedException.Create("acme", []));
    }

    private static ArchiveSnapshot Snapshot(string? name) =>
        new(OctoObjectId.GenerateNewId(), Target, CkArchiveStatus.Activated, name, Array.Empty<CkArchiveColumnSpec>());
}
