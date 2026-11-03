using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7359: the gas-day start offset (<c>DayStartOffsetMs</c>, System.StreamData 1.22.0) is persisted by
/// <see cref="MongoRollupArchiveRuntimeStore"/> and projected into <see cref="RollupArchiveSnapshot.DayStartOffsetMs"/>;
/// a rollup stored without it behaves as before (midnight days).
/// </summary>
public class MongoRollupArchiveRuntimeStoreDayStartOffsetTests
{
    private const long SixHoursMs = 6 * 3_600_000L;

    private static RtRollupArchive Build(long? dayStartOffsetMs, bool passOffset = true)
    {
        IReadOnlyList<RollupSourceReference> sources = [new RollupSourceReference(new OctoObjectId("aa00000000000000000002a0"))];
        IReadOnlyList<CkRollupAggregationSpec> aggregations = [new CkRollupAggregationSpec("Value", CkRollupFunction.Count, null)];
        return passOffset
            ? MongoRollupArchiveRuntimeStore.BuildInsertEntity("gasDay", new RtCkId<CkTypeId>("Demo/Meter"), sources, TimeSpan.FromDays(1),
                TimeSpan.Zero, aggregations, [], BucketAlignment.CalendarDay, "Europe/Vienna", null, dayStartOffsetMs)
            : MongoRollupArchiveRuntimeStore.BuildInsertEntity("gasDay", new RtCkId<CkTypeId>("Demo/Meter"), sources, TimeSpan.FromDays(1),
                TimeSpan.Zero, aggregations, [], BucketAlignment.CalendarDay, "Europe/Vienna", null);
    }

    [Fact]
    public void Insert_WithOffset_PersistsDayStartOffsetMs()
    {
        var entity = Build(SixHoursMs);

        Assert.Equal(SixHoursMs, entity.DayStartOffsetMs);
    }

    [Fact]
    public void Insert_WithoutOffset_LeavesTheAttributeUnset_SoTheRowIsTheSameAsBefore()
    {
        var withoutParameter = Build(null, passOffset: false);
        var explicitNull = Build(null);
        var zero = Build(0);

        Assert.Null(withoutParameter.DayStartOffsetMs);
        Assert.Null(explicitNull.DayStartOffsetMs);
        Assert.Null(zero.DayStartOffsetMs);
    }

    [Fact]
    public void MapToSnapshot_ProjectsTheOffset_AndALegacyEntityLoadsWithoutOne()
    {
        var withOffset = MongoRollupArchiveRuntimeStore.MapToSnapshot(Build(SixHoursMs));
        var legacy = MongoRollupArchiveRuntimeStore.MapToSnapshot(Build(null));

        Assert.Equal(SixHoursMs, withOffset.DayStartOffsetMs);
        Assert.True(legacy.DayStartOffsetMs is null or 0);
        // BucketCalendar.For turns an unset offset into a midnight day.
        Assert.Equal(TimeSpan.FromHours(6), Meshmakers.Octo.Runtime.Engine.StreamData.BucketCalendar.For(withOffset).DayStart);
        Assert.Equal(TimeSpan.Zero, Meshmakers.Octo.Runtime.Engine.StreamData.BucketCalendar.For(legacy).DayStart);
    }
}
