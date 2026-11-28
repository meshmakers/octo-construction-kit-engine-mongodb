using System;
using System.Collections.Generic;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#9933: a row whose <c>rtcreationdatetime</c> or <c>rtchangeddatetime</c> is NULL (restored from a dump that named its
/// columns, or written before the column had its default) is read, not refused with an InvalidCastException.
/// </summary>
public class CrateDatabaseClientRowMappingTests
{
    private static readonly DateTime Timestamp = new(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RowWithNullTimestamps_IsRead_AndLeavesThemAtTheirDefault()
    {
        var row = new Dictionary<string, object?>
        {
            [Constants.Timestamp] = Timestamp,
            [Constants.RtCreationDateTime] = null,
            [Constants.RtChangedDateTime] = null
        };

        var dataPoint = CrateDatabaseClient.ToDataPointDto(row);

        Assert.Equal(Timestamp, dataPoint.Timestamp);
        Assert.Equal(default, dataPoint.RtCreationDateTime);
        Assert.Equal(default, dataPoint.RtChangedDateTime);
    }

    [Fact]
    public void RowWithTimestamps_KeepsThem()
    {
        var created = Timestamp.AddDays(-2);
        var changed = Timestamp.AddDays(-1);
        var row = new Dictionary<string, object?>
        {
            [Constants.Timestamp] = Timestamp,
            [Constants.RtCreationDateTime] = created,
            [Constants.RtChangedDateTime] = changed
        };

        var dataPoint = CrateDatabaseClient.ToDataPointDto(row);

        Assert.Equal(created, dataPoint.RtCreationDateTime);
        Assert.Equal(changed, dataPoint.RtChangedDateTime);
    }
}
