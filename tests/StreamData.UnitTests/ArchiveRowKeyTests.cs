using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     AB#8679: the row key the write path counts and groups by is the key CrateDB compares, timestamps at millisecond
///     precision. Behaviour against CrateDB: <c>StreamDataWriteLoss/ArchiveWriteRepeatedKeyIntegrationTests</c>.
/// </summary>
public class ArchiveRowKeyTests
{
    private static readonly DateTime At = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RowKey_TwoPointsLessThanAMillisecondApart_AreOneKey()
    {
        Assert.Equal(
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At.AddTicks(1_000), null),
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At.AddTicks(9_000), null));
    }

    [Fact]
    public void RowKey_PointsOneMillisecondApart_AreTwoKeys()
    {
        Assert.NotEqual(
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At, null),
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At.AddMilliseconds(1), null));
    }

    [Fact]
    public void RowKey_Window_ComparesStartAndEndAtMillisecondPrecision()
    {
        var end = At.AddMinutes(15);
        Assert.Equal(
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At.AddTicks(5_000), end.AddTicks(5_000)),
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At, end));
        Assert.NotEqual(
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At, end),
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At, end.AddMilliseconds(1)));
    }

    [Fact]
    public void RowKey_IsTheKeyConflictOutcomeMatchesReturnedRowsWith()
    {
        Assert.Equal(new StoredRowKey("meter", "Test/Meter", At, null).Value,
            CrateDatabaseClient.RowKey("meter", "Test/Meter", At.AddTicks(3_000), null));
    }
}
